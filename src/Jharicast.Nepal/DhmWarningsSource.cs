using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Fetch;

namespace Jharicast.Nepal;

/// <summary>
/// DHM's current district warnings, <c>dhm.gov.np/home/getAPIData/1</c> (ADR-0011,
/// <c>dhm.warnings</c>). Poll it no more than every 30 minutes. Give it an <see cref="HttpClient"/>
/// built on <see cref="PoliteHttpHandler"/> with <c>HostOverrides["dhm.gov.np"]</c> set to 5
/// seconds (ADR-0007).
/// </summary>
/// <remarks>
/// Health is <see cref="SourceStatus.Stale"/> when the content has not changed for 12 hours (DHM
/// updates warnings about every 6), <see cref="SourceStatus.Drifting"/> when the parser met names
/// or values it does not know, and <see cref="SourceStatus.Failing"/> when the fetch or the parse
/// failed. DHM sends <c>Cache-Control: no-store</c>, so change is found by the snapshot hash.
/// </remarks>
public sealed class DhmWarningsSource : ISource<DhmWarningSnapshot>
{
    /// <summary>The URL fetched unless another is given.</summary>
    public static readonly Uri DefaultUrl = new("https://dhm.gov.np/home/getAPIData/1");

    // ADR-0011: warnings change about every 6 h, so 12 h without a change is stale.
    private static readonly TimeSpan Cadence = TimeSpan.FromHours(6);

    private readonly SourceFetcher _fetcher;
    private readonly SourceRequest<DhmWarningSnapshot> _request;

    /// <summary>Creates the source.</summary>
    /// <param name="httpClient">Client built on <see cref="PoliteHttpHandler"/>; not disposed by this class.</param>
    /// <param name="store">Where snapshots go.</param>
    /// <param name="timeProvider">Clock for fetch times and staleness.</param>
    /// <param name="url">The feed URL; null for <see cref="DefaultUrl"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/>, <paramref name="store"/> or <paramref name="timeProvider"/> is null.</exception>
    public DhmWarningsSource(HttpClient httpClient, ISnapshotStore store, TimeProvider timeProvider, Uri? url = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _fetcher = new SourceFetcher(httpClient, store, timeProvider);
        _request = new SourceRequest<DhmWarningSnapshot>(DhmWarningsParser.SourceId, url ?? DefaultUrl, Cadence, DhmWarningsParser.Parse, s => s.Drift);
    }

    /// <inheritdoc />
    public Task<SourceResult<DhmWarningSnapshot>> FetchAsync(CancellationToken cancellationToken) =>
        _fetcher.FetchAsync(_request, cancellationToken);
}
