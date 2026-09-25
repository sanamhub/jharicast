using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Jharicast.Fetch;

/// <summary>
/// A <see cref="DelegatingHandler"/> that makes an <see cref="HttpClient"/> a guest nobody needs to
/// block (ADR-0007): it identifies itself, keeps one request in flight per host with a minimum
/// interval, retries only what is safe to retry, stops when a host keeps failing, and refuses
/// disabled hosts. It never retries a 401 or 403; a refusal is the site saying no.
/// </summary>
public sealed class PoliteHttpHandler : DelegatingHandler
{
    // ADR-0007: 30 s per request. Applied per attempt, so a Retry-After wait does not eat into it.
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BackoffBase = TimeSpan.FromSeconds(1);

    private readonly PoliteHttpOptions _options;
    private readonly ConcurrentDictionary<string, HostGate> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the handler. Set <see cref="DelegatingHandler.InnerHandler"/> before use, or let <c>IHttpClientFactory</c> do it.</summary>
    /// <param name="options">Settings; the User-Agent is required.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public PoliteHttpHandler(PoliteHttpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Creates the handler over an inner handler.</summary>
    /// <param name="options">Settings; the User-Agent is required.</param>
    /// <param name="innerHandler">The handler that sends the request, usually a <see cref="SocketsHttpHandler"/>.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public PoliteHttpHandler(PoliteHttpOptions options, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Sends a GET or HEAD request politely.</summary>
    /// <param name="request">The request. Its User-Agent is replaced with <see cref="PoliteHttpOptions.UserAgent"/>.</param>
    /// <param name="cancellationToken">Cancels the wait and the request.</param>
    /// <returns>The last response. A 401, 403 or 404 is returned, not thrown; so is a retryable status once the retries are spent.</returns>
    /// <exception cref="InvalidOperationException">The method is not GET or HEAD, or the URI is not absolute.</exception>
    /// <exception cref="SourceUnavailableException">The host is disabled, or its circuit breaker is open.</exception>
    /// <exception cref="HttpRequestException">The connection failed on every attempt.</exception>
    /// <exception cref="TimeoutException">The last attempt took longer than 30 seconds.</exception>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head)
        {
            throw new InvalidOperationException($"Only GET and HEAD are sent; {request.Method} was refused (ADR-0007).");
        }

        if (request.RequestUri is not { IsAbsoluteUri: true } uri)
        {
            throw new InvalidOperationException("The request needs an absolute URI.");
        }

        var host = uri.IdnHost;
        if (_options.DisabledHosts.Contains(host))
        {
            throw new SourceUnavailableException($"{host} is disabled by configuration.");
        }

        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);

        var time = _options.TimeProvider;
        var gate = _gates.GetOrAdd(host, h => new HostGate(h, _options.IntervalFor(h), time));
        for (var attempt = 0; ; attempt++)
        {
            await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
            if (gate.IsOpen(_options.BreakerFailures))
            {
                gate.Release();
                throw new SourceUnavailableException($"The circuit breaker for {host} is open after {_options.BreakerFailures} consecutive failures.");
            }

            HttpResponseMessage? response = null;
            ExceptionDispatchInfo? error = null;
            try
            {
                using var timeout = new CancellationTokenSource(AttemptTimeout, time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                try
                {
                    response = await base.SendAsync(request, linked.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException e)
                {
                    error = ExceptionDispatchInfo.Capture(e);
                }
                catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
                {
                    error = ExceptionDispatchInfo.Capture(new TimeoutException($"{host} did not answer within {AttemptTimeout.TotalSeconds:0} seconds.", e));
                }
            }
            catch
            {
                // The caller cancelled, or the inner handler threw something unexpected. Nothing
                // was learned about the host, so leave its state alone.
                gate.Release();
                throw;
            }

            var status = response?.StatusCode;
            var retryable = error is not null || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int?)status >= 500;
            var refused = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            var retryAfter = RetryAfter(response, time);

            // A host asking for a longer wait than the breaker's own is not retried now; its
            // Retry-After still holds back the next request.
            var retry = retryable && attempt < _options.MaxRetries && !(retryAfter > _options.BreakerDuration);
            var wait = retry ? retryAfter ?? Backoff(attempt) : retryAfter;
            gate.Exit(
                retryable || refused ? AttemptOutcome.Failure : AttemptOutcome.Success,
                wait is { } w ? time.GetUtcNow() + w : null,
                _options.BreakerFailures,
                _options.BreakerDuration);

            if (!retry)
            {
                error?.Throw();
                return response!;
            }

            response?.Dispose();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var gate in _gates.Values)
            {
                gate.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage? response, TimeProvider time)
    {
        var header = response?.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - time.GetUtcNow() : null);
        return wait is { } w && w < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    // 1, 2, 4 s, each plus up to half again at random, so clients that failed together do not
    // come back together. RandomNumberGenerator because CA5394 flags System.Random; jitter needs
    // no secrecy.
    private static TimeSpan Backoff(int attempt) =>
        BackoffBase * Math.Pow(2, attempt) * (1 + (RandomNumberGenerator.GetInt32(0, 501) / 1000.0));
}
