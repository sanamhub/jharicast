using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Fetch;

namespace Jharicast.Nepal;

/// <summary>
/// The part every Nepal source shares: one GET, a snapshot, storage, and health (ADR-0008,
/// ADR-0011). Failures become <see cref="SourceStatus.Failing"/> or
/// <see cref="SourceStatus.Disabled"/>, never exceptions, except cancellation by the caller.
/// </summary>
/// <remarks>
/// A snapshot is stored only when its hash differs from the latest stored one for the source. The
/// latest stored snapshot is then the first fetch of the current content, so its fetch time is when
/// the content last changed, which is what staleness is measured from.
/// </remarks>
internal sealed class SourceFetcher(HttpClient http, ISnapshotStore store, TimeProvider timeProvider)
{
    private static readonly string[] KeptHeaders = ["ETag", "Last-Modified", "Content-Type"];

    public TimeProvider TimeProvider { get; } = timeProvider;

    /// <summary>Fetches, scrubs, stores and parses once.</summary>
    /// <param name="request">What to fetch and how to read it.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    public async Task<SourceResult<T>> FetchAsync<T>(SourceRequest<T> request, CancellationToken cancellationToken)
        where T : class
    {
        var (fetched, failure) = await GetAsync(request.Url, cancellationToken).ConfigureAwait(false);
        if (fetched is null)
        {
            return new SourceResult<T>(null, null, failure!);
        }

        Snapshot snapshot;
        try
        {
            // Scrubbed before the snapshot exists, so personal data is never hashed or stored.
            var body = request.Scrub is null ? fetched.Body : request.Scrub(fetched.Body);
            snapshot = new Snapshot(request.SourceId, request.Url, fetched.At, fetched.Status, fetched.Headers, body);
        }
        catch (JsonException e)
        {
            return new SourceResult<T>(null, null, new SourceHealth(SourceStatus.Failing, $"{request.SourceId}: body could not be scrubbed, so it was not stored: {e.Message}", null));
        }

        var previous = await store.LatestAsync(request.SourceId, cancellationToken).ConfigureAwait(false);
        var lastChangeAt = snapshot.FetchedAt;
        if (previous is not null && string.Equals(previous.Sha256, snapshot.Sha256, StringComparison.Ordinal))
        {
            lastChangeAt = previous.FetchedAt;
        }
        else
        {
            await store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        T value;
        try
        {
            value = request.Parse(snapshot.Body, snapshot.FetchedAt);
        }
        catch (JsonException e)
        {
            return new SourceResult<T>(null, snapshot, new SourceHealth(SourceStatus.Failing, $"{request.SourceId}: {e.Message}", lastChangeAt));
        }

        return new SourceResult<T>(value, snapshot, Health(request, value, lastChangeAt));
    }

    /// <summary>Health of a parsed value: drifting when it has drift, else stale or fresh by content age and issue time.</summary>
    public SourceHealth Health<T>(SourceRequest<T> request, T value, DateTimeOffset? lastChangeAt)
        where T : class
    {
        var drift = request.Drift(value);
        if (drift.Count > 0)
        {
            return new SourceHealth(SourceStatus.Drifting, $"{request.SourceId}: unrecognised {string.Join(", ", drift)}", lastChangeAt);
        }

        var now = TimeProvider.GetUtcNow();
        if (request.IssuedAt?.Invoke(value) is { } issued && now - issued > 2 * request.Cadence)
        {
            return new SourceHealth(SourceStatus.Stale, string.Create(CultureInfo.InvariantCulture, $"issued {(now - issued).TotalHours:0.#} h ago, expected every {request.Cadence.TotalHours:0.#} h"), lastChangeAt);
        }

        return SourceHealth.FromLastChange(lastChangeAt, request.Cadence, now);
    }

    /// <summary>One GET through the caller's handler, with failures as health.</summary>
    public async Task<(Fetched? Fetched, SourceHealth? Failure)> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, Failing(string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)response.StatusCode} from {url.Host}")));
            }

            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var headers = KeptHeaders
                .Select(name => (name, value: response.Headers.TryGetValues(name, out var v) || response.Content.Headers.TryGetValues(name, out v) ? string.Join(", ", v) : null))
                .Where(h => h.value is not null)
                .ToDictionary(h => h.name, h => h.value!, StringComparer.OrdinalIgnoreCase);
            return (new Fetched(body, response.StatusCode, headers, TimeProvider.GetUtcNow()), null);
        }
        catch (SourceUnavailableException e)
        {
            // The handler refused before sending: a disabled host or a robots.txt rule is the site
            // or the operator saying no; an open breaker is a failing host.
            var disabled = e.Message.Contains("disabled", StringComparison.Ordinal) || e.Message.Contains("robots.txt", StringComparison.Ordinal);
            return (null, new SourceHealth(disabled ? SourceStatus.Disabled : SourceStatus.Failing, e.Message, null));
        }
        catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return (null, Failing($"{url.Host}: {e.Message}"));
        }
    }

    private static SourceHealth Failing(string detail) => new(SourceStatus.Failing, detail, null);

    /// <summary>A successful response, read.</summary>
    internal sealed record Fetched(ReadOnlyMemory<byte> Body, System.Net.HttpStatusCode Status, IReadOnlyDictionary<string, string> Headers, DateTimeOffset At);
}

/// <summary>What <see cref="SourceFetcher"/> fetches and how it reads the body.</summary>
/// <param name="SourceId">Source id for the snapshot.</param>
/// <param name="Url">What to GET.</param>
/// <param name="Cadence">How often the issuer normally changes it; stale after twice this.</param>
/// <param name="Parse">Parses the stored body. May throw <see cref="JsonException"/> for a structurally wrong body.</param>
/// <param name="Drift">The parsed value's drift list.</param>
internal sealed record SourceRequest<T>(string SourceId, Uri Url, TimeSpan Cadence, Func<ReadOnlyMemory<byte>, DateTimeOffset, T> Parse, Func<T, IReadOnlyList<string>> Drift)
{
    /// <summary>Removes personal data from the body before it is stored, or null when there is none.</summary>
    public Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>? Scrub { get; init; }

    /// <summary>The issuer's own time on the value, or null when it has none; stale when older than twice the cadence.</summary>
    public Func<T, DateTimeOffset?>? IssuedAt { get; init; }
}
