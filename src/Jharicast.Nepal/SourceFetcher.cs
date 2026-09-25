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
    /// <param name="sourceId">Source id for the snapshot.</param>
    /// <param name="url">What to GET.</param>
    /// <param name="cadence">How often the issuer normally changes it; stale after twice this.</param>
    /// <param name="parse">Parses the stored body. May throw <see cref="JsonException"/> for a structurally wrong body.</param>
    /// <param name="drift">The parsed value's drift list.</param>
    /// <param name="scrub">Removes personal data from the body before it is stored, or null when there is none.</param>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    public async Task<SourceResult<T>> FetchAsync<T>(
        string sourceId,
        Uri url,
        TimeSpan cadence,
        Func<ReadOnlyMemory<byte>, DateTimeOffset, T> parse,
        Func<T, IReadOnlyList<string>> drift,
        Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>>? scrub,
        CancellationToken cancellationToken)
        where T : class
    {
        Snapshot snapshot;
        try
        {
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Failed<T>(string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)response.StatusCode} from {url.Host}"));
            }

            ReadOnlyMemory<byte> body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (scrub is not null)
            {
                body = scrub(body); // before the snapshot exists, so personal data is never hashed or stored
            }

            var headers = KeptHeaders
                .Select(name => (name, value: response.Headers.TryGetValues(name, out var v) || response.Content.Headers.TryGetValues(name, out v) ? string.Join(", ", v) : null))
                .Where(h => h.value is not null)
                .ToDictionary(h => h.name, h => h.value!, StringComparer.OrdinalIgnoreCase);
            snapshot = new Snapshot(sourceId, url, TimeProvider.GetUtcNow(), response.StatusCode, headers, body);
        }
        catch (SourceUnavailableException e)
        {
            // The handler refused before sending: a disabled host or a robots.txt rule is the site
            // or the operator saying no; an open breaker is a failing host.
            var disabled = e.Message.Contains("disabled", StringComparison.Ordinal) || e.Message.Contains("robots.txt", StringComparison.Ordinal);
            return new SourceResult<T>(null, null, new SourceHealth(disabled ? SourceStatus.Disabled : SourceStatus.Failing, e.Message, null));
        }
        catch (JsonException e)
        {
            return Failed<T>($"{sourceId}: body could not be scrubbed, so it was not stored: {e.Message}");
        }
        catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return Failed<T>($"{url.Host}: {e.Message}");
        }

        var previous = await store.LatestAsync(sourceId, cancellationToken).ConfigureAwait(false);
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
            value = parse(snapshot.Body, snapshot.FetchedAt);
        }
        catch (JsonException e)
        {
            return new SourceResult<T>(null, snapshot, new SourceHealth(SourceStatus.Failing, $"{sourceId}: {e.Message}", lastChangeAt));
        }

        var unknown = drift(value);
        var health = unknown.Count > 0
            ? new SourceHealth(SourceStatus.Drifting, $"{sourceId}: unrecognised {string.Join(", ", unknown)}", lastChangeAt)
            : SourceHealth.FromLastChange(lastChangeAt, cadence, TimeProvider.GetUtcNow());
        return new SourceResult<T>(value, snapshot, health);
    }

    private static SourceResult<T> Failed<T>(string detail)
        where T : class => new(null, null, new SourceHealth(SourceStatus.Failing, detail, null));
}
