using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Fetch;

namespace Jharicast.Nepal;

/// <summary>
/// DHM's district warnings feed, <c>dhm.gov.np/home/getAPIData/1</c> (ADR-0011,
/// <c>dhm.warnings</c>). Retired by default: <see cref="FetchAsync"/> sends no request and
/// reports <see cref="SourceStatus.Disabled"/>, so the official rule says Unknown instead of
/// reading a level DHM no longer issues. <see cref="DhmWarningMapSource"/> replaces it.
/// </summary>
/// <remarks>
/// <para>
/// The feed carries no issue time, and its <c>real_result</c> on 2026-10-06 was identical to the
/// copy saved on 2026-09-24, while DHM issued about 25 warning bulletins in between. DHM's map for
/// 2026-10-06 showed no Orange district; the feed still listed 19. Current warnings are published
/// only as dated maps on <see cref="WarningsPage"/>.
/// </para>
/// <para>
/// With <see cref="Retired"/> false it fetches as before: poll no more than every 30 minutes,
/// through a <see cref="PoliteHttpHandler"/> with <c>HostOverrides["dhm.gov.np"]</c> set to 5
/// seconds (ADR-0007). Health is <see cref="SourceStatus.Stale"/> when the content has not changed
/// for 12 hours, <see cref="SourceStatus.Drifting"/> when the parser met unknown names or values,
/// and <see cref="SourceStatus.Failing"/> when the fetch or the parse failed.
/// </para>
/// </remarks>
public sealed class DhmWarningsSource : ISource<DhmWarningSnapshot>
{
    /// <summary>The URL fetched unless another is given.</summary>
    public static readonly Uri DefaultUrl = new("https://dhm.gov.np/home/getAPIData/1");

    /// <summary>Where DHM publishes its current warnings, as maps for the next three days.</summary>
    public static readonly Uri WarningsPage = new("https://dhm.gov.np/mfd/#/weather/pages/weather-warning");

    internal const string RetiredDetail = "DHM's warnings feed stopped updating (unchanged since 2026-09-24); current warnings are published as maps";

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

    /// <summary>
    /// True (the default) sends no request and reports the feed as disabled. Set it to false only
    /// to replay responses recorded while the feed was current, such as the report's fixtures.
    /// </summary>
    public bool Retired { get; init; } = true;

    /// <inheritdoc />
    public Task<SourceResult<DhmWarningSnapshot>> FetchAsync(CancellationToken cancellationToken) =>
        Retired
            ? Task.FromResult(new SourceResult<DhmWarningSnapshot>(null, null, new SourceHealth(SourceStatus.Disabled, RetiredDetail, null)))
            : _fetcher.FetchAsync(_request, cancellationToken);
}
