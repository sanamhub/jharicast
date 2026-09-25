using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jharicast.Fetch;

/// <summary>
/// A <see cref="DelegatingHandler"/> that makes an <see cref="HttpClient"/> a guest nobody needs to
/// block (ADR-0007): it identifies itself, obeys robots.txt, keeps one request in flight per host
/// with a minimum interval, retries only what is safe to retry, stops when a host keeps failing,
/// and refuses disabled hosts. It never retries a 401 or 403; a refusal is the site saying no.
/// </summary>
public sealed class PoliteHttpHandler : DelegatingHandler
{
    // ADR-0007: 30 s per request. Applied per attempt, so a Retry-After wait does not eat into it.
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BackoffBase = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RobotsLifetime = TimeSpan.FromHours(24);

    // RFC 9309 section 2.5: parse at least the first 500 KiB, ignore the rest.
    private const int RobotsMaxBytes = 500 * 1024;

    private readonly PoliteHttpOptions _options;
    private readonly ConcurrentDictionary<string, HostGate> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<RobotsEntry>> _robots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _robotsLock = new();

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
    /// <exception cref="SourceUnavailableException">The host is disabled, its robots.txt disallows the path or could not be read, or its circuit breaker is open.</exception>
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

        var gate = _gates.GetOrAdd(host, h => new HostGate(h, _options.IntervalFor(h), _options.TimeProvider));
        if (!string.Equals(uri.AbsolutePath, "/robots.txt", StringComparison.Ordinal))
        {
            var robots = await RobotsFor(uri, gate, cancellationToken).ConfigureAwait(false);
            if (!robots.Policy.IsAllowed(_options.UserAgent, uri.PathAndQuery))
            {
                throw new SourceUnavailableException(robots.Reachable
                    ? $"robots.txt on {host} disallows {uri.AbsolutePath}."
                    : $"robots.txt on {host} could not be read, so nothing is fetched there until it can be (RFC 9309 section 2.3.1.4).");
            }
        }

        return await SendThroughGateAsync(request, gate, cancellationToken).ConfigureAwait(false);
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

    private async Task<HttpResponseMessage> SendThroughGateAsync(HttpRequestMessage request, HostGate gate, CancellationToken cancellationToken)
    {
        var time = _options.TimeProvider;
        var host = gate.Host;
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

    // One robots.txt fetch per host at a time, shared by every request waiting on it, and reused
    // until it expires: 24 h after a read, or one breaker period after a failure.
    private async Task<RobotsEntry> RobotsFor(Uri uri, HostGate gate, CancellationToken cancellationToken)
    {
        Task<RobotsEntry> pending;
        lock (_robotsLock)
        {
            // A fetch that threw something unexpected is retried by the next request, not cached.
            if (!_robots.TryGetValue(gate.Host, out pending!) || pending.IsFaulted)
            {
                pending = _robots[gate.Host] = FetchRobotsAsync(new Uri(uri, "/robots.txt"), gate);
            }
        }

        var entry = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (entry.ValidUntil > _options.TimeProvider.GetUtcNow())
        {
            return entry;
        }

        lock (_robotsLock)
        {
            // Another request may have started the refresh already; share it.
            if (ReferenceEquals(_robots[gate.Host], pending))
            {
                _robots[gate.Host] = FetchRobotsAsync(new Uri(uri, "/robots.txt"), gate);
            }

            pending = _robots[gate.Host];
        }

        return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // Through the same gate, User-Agent and retries as any request. Not cancellable by one caller,
    // because other requests to the host wait on the same fetch.
    private async Task<RobotsEntry> FetchRobotsAsync(Uri robotsUri, HostGate gate)
    {
        var time = _options.TimeProvider;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, robotsUri);
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            using var response = await SendThroughGateAsync(request, gate, CancellationToken.None).ConfigureAwait(false);
            if ((int)response.StatusCode >= 500)
            {
                return Unreachable(time);
            }

            // RFC 9309 treats 401 and 403 as "unavailable", which allows everything. ADR-0007 does
            // not touch a host that answers 401 or 403, so they disallow everything instead.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new RobotsEntry(RobotsPolicy.DisallowAll, Reachable: true, time.GetUtcNow() + RobotsLifetime);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new RobotsEntry(RobotsPolicy.AllowAll, Reachable: true, time.GetUtcNow() + RobotsLifetime);
            }

            var policy = RobotsPolicy.Parse(await ReadLimitedAsync(response.Content).ConfigureAwait(false));
            if (policy.MinInterval(_options.UserAgent) is { } interval)
            {
                gate.RaiseInterval(interval);
            }

            return new RobotsEntry(policy, Reachable: true, time.GetUtcNow() + RobotsLifetime);
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or SourceUnavailableException)
        {
            return Unreachable(time);
        }
    }

    private RobotsEntry Unreachable(TimeProvider time) =>
        new(RobotsPolicy.DisallowAll, Reachable: false, time.GetUtcNow() + _options.BreakerDuration);

    private static async Task<string> ReadLimitedAsync(HttpContent content)
    {
        var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[RobotsMaxBytes];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length)).ConfigureAwait(false)) > 0)
            {
                length += read;
            }

            return Encoding.UTF8.GetString(buffer, 0, length);
        }
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

    private sealed record RobotsEntry(RobotsPolicy Policy, bool Reachable, DateTimeOffset ValidUntil);
}
