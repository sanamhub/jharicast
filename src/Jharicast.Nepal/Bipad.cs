using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Fetch;

namespace Jharicast.Nepal;

/// <summary>An alert from the BIPAD portal (NDRRMA).</summary>
/// <param name="Id">BIPAD id.</param>
/// <param name="Title">English title, for example "Heavy Rainfall at Hetauda-10, Makwanpur".</param>
/// <param name="District">District named after the title's last comma, or null when the title names none.</param>
/// <param name="HazardId">BIPAD hazard id (11 flood and 14 heavy rainfall in the 2026-09-25 capture), or null.</param>
/// <param name="ReferenceType">What raised it, for example <c>rain</c> or <c>river</c>, or null.</param>
/// <param name="Location">Point, or null.</param>
/// <param name="StartedAt">Start, UTC, or null.</param>
/// <param name="ExpiresAt">Expiry, UTC, or null when open-ended.</param>
/// <param name="Verified">BIPAD's verified flag.</param>
public sealed record BipadAlert(int Id, string Title, District? District, int? HazardId, string? ReferenceType, GeoPoint? Location, DateTimeOffset? StartedAt, DateTimeOffset? ExpiresAt, bool Verified);

/// <summary>Parsed BIPAD alerts.</summary>
/// <param name="Alerts">Alerts as fetched, current and past.</param>
/// <param name="Drift">Values the parser did not recognise. Non-empty means the feed changed.</param>
/// <param name="Provenance">Where and when; kind <see cref="SourceKind.Official"/>.</param>
public sealed record BipadAlertSnapshot(IReadOnlyList<BipadAlert> Alerts, IReadOnlyList<string> Drift, Provenance Provenance)
{
    /// <summary>
    /// Alerts in force at an instant: started, and not expired. Many alerts have no expiry, so an
    /// open-ended alert counts for 24 hours after its start. The 24 hours is our guess, not BIPAD's.
    /// </summary>
    /// <param name="now">The instant.</param>
    /// <returns>The alerts in force.</returns>
    public IReadOnlyList<BipadAlert> ActiveAt(DateTimeOffset now) =>
        [.. Alerts.Where(a => a.StartedAt is { } start && start <= now && (a.ExpiresAt is { } end ? end > now : now - start < TimeSpan.FromHours(24)))];
}

/// <summary>An incident from the BIPAD portal: a landslide, flood, fire and so on that happened.</summary>
/// <param name="Id">BIPAD id.</param>
/// <param name="Title">English title.</param>
/// <param name="HazardId">BIPAD hazard id, or null.</param>
/// <param name="Location">Point, or null.</param>
/// <param name="OccurredAt">When it happened, UTC, or null.</param>
/// <param name="ReportedAt">When it was reported, UTC, or null.</param>
/// <param name="Verified">BIPAD's verified flag.</param>
public sealed record BipadIncident(int Id, string Title, int? HazardId, GeoPoint? Location, DateTimeOffset? OccurredAt, DateTimeOffset? ReportedAt, bool Verified);

/// <summary>Parsed BIPAD incidents.</summary>
/// <param name="Incidents">Incidents as fetched.</param>
/// <param name="Drift">Values the parser did not recognise. Non-empty means the feed changed.</param>
/// <param name="Provenance">Where and when; kind <see cref="SourceKind.Official"/>.</param>
public sealed record BipadIncidentSnapshot(IReadOnlyList<BipadIncident> Incidents, IReadOnlyList<string> Drift, Provenance Provenance);

/// <summary>
/// BIPAD alerts, <c>bipadportal.gov.np/api/v1/alert/</c> (ADR-0011, <c>bipad.alerts</c>). Poll no
/// more than every 30 minutes, with an <see cref="HttpClient"/> built on
/// <see cref="PoliteHttpHandler"/> and <c>HostOverrides["bipadportal.gov.np"]</c> set to 5 seconds.
/// </summary>
/// <remarks>
/// Pages with <c>limit</c> and <c>offset</c> until a short page. The <c>count</c> field is ignored:
/// it is the 64-bit maximum. Only the fields parsed are kept in the snapshot, which holds every
/// page's results as one <c>results</c> array.
/// </remarks>
/// <param name="httpClient">Client built on <see cref="PoliteHttpHandler"/>; not disposed by this class.</param>
/// <param name="store">Where snapshots go.</param>
/// <param name="timeProvider">Clock for fetch times and staleness.</param>
/// <param name="baseAddress">API root ending in <c>/</c>; null for <c>https://bipadportal.gov.np/api/v1/</c>.</param>
public sealed class BipadAlertSource(HttpClient httpClient, ISnapshotStore store, TimeProvider timeProvider, Uri? baseAddress = null) : ISource<BipadAlertSnapshot>
{
    /// <summary>Source id.</summary>
    public const string SourceId = "bipad.alerts";

    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal) { "id", "title", "hazard", "referenceType", "point", "startedOn", "expireOn", "createdOn", "verified", "public" };

    private readonly BipadPager _pager = new(httpClient, store, timeProvider, baseAddress, "alert/", "-startedOn", Fields);

    /// <summary>Most pages read in one fetch. Default 10.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Below 1.</exception>
    public int MaxPages { get => _pager.MaxPages; init => _pager.MaxPages = value; }

    /// <inheritdoc />
    public Task<SourceResult<BipadAlertSnapshot>> FetchAsync(CancellationToken cancellationToken) =>
        _pager.FetchAsync(SourceId, Parse, s => s.Drift, cancellationToken);

    /// <summary>Parses a body of <c>results</c>.</summary>
    /// <exception cref="JsonException">No <c>results</c> array.</exception>
    internal static BipadAlertSnapshot Parse(ReadOnlyMemory<byte> utf8Json, DateTimeOffset fetchedAt)
    {
        var drift = new SortedSet<string>(StringComparer.Ordinal);
        var alerts = BipadPager.Results(utf8Json, e =>
        {
            var title = FeedReader.Text(e, "title") ?? string.Empty;
            return new BipadAlert(FeedReader.Id(e), title, DistrictInTitle(title, drift), FeedReader.Int(e, "hazard"), FeedReader.Text(e, "referenceType"),
                FeedReader.Point(e, drift), FeedReader.Time(e, "startedOn", drift), FeedReader.Time(e, "expireOn", drift), FeedReader.Flag(e, "verified"));
        });
        return new BipadAlertSnapshot(alerts, [.. drift], new Provenance(SourceId, SourceKind.Official, fetchedAt));
    }

    private static District? DistrictInTitle(string title, SortedSet<string> drift)
    {
        var comma = title.LastIndexOf(',');
        if (comma < 0)
        {
            return null;
        }

        var name = title[(comma + 1)..].Trim();
        if (Gazetteer.TryResolve(name, out var district))
        {
            return district;
        }

        drift.Add("district:" + name);
        return null;
    }
}

/// <summary>
/// BIPAD incidents, <c>bipadportal.gov.np/api/v1/incident/</c> (ADR-0011, <c>bipad.incidents</c>),
/// paged like <see cref="BipadAlertSource"/>. Free-text fields (address, detail, description,
/// verification message) are not kept.
/// </summary>
/// <param name="httpClient">Client built on <see cref="PoliteHttpHandler"/>; not disposed by this class.</param>
/// <param name="store">Where snapshots go.</param>
/// <param name="timeProvider">Clock for fetch times and staleness.</param>
/// <param name="baseAddress">API root ending in <c>/</c>; null for <c>https://bipadportal.gov.np/api/v1/</c>.</param>
public sealed class BipadIncidentSource(HttpClient httpClient, ISnapshotStore store, TimeProvider timeProvider, Uri? baseAddress = null) : ISource<BipadIncidentSnapshot>
{
    /// <summary>Source id.</summary>
    public const string SourceId = "bipad.incidents";

    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal) { "id", "title", "hazard", "point", "incidentOn", "reportedOn", "verified", "approved" };

    private readonly BipadPager _pager = new(httpClient, store, timeProvider, baseAddress, "incident/", "-incidentOn", Fields);

    /// <summary>Most pages read in one fetch. Default 10.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Below 1.</exception>
    public int MaxPages { get => _pager.MaxPages; init => _pager.MaxPages = value; }

    /// <inheritdoc />
    public Task<SourceResult<BipadIncidentSnapshot>> FetchAsync(CancellationToken cancellationToken) =>
        _pager.FetchAsync(SourceId, Parse, s => s.Drift, cancellationToken);

    /// <summary>Parses a body of <c>results</c>.</summary>
    /// <exception cref="JsonException">No <c>results</c> array.</exception>
    internal static BipadIncidentSnapshot Parse(ReadOnlyMemory<byte> utf8Json, DateTimeOffset fetchedAt)
    {
        var drift = new SortedSet<string>(StringComparer.Ordinal);
        var incidents = BipadPager.Results(utf8Json, e => new BipadIncident(FeedReader.Id(e), FeedReader.Text(e, "title") ?? string.Empty, FeedReader.Int(e, "hazard"),
            FeedReader.Point(e, drift), FeedReader.Time(e, "incidentOn", drift), FeedReader.Time(e, "reportedOn", drift), FeedReader.Flag(e, "verified")));
        return new BipadIncidentSnapshot(incidents, [.. drift], new Provenance(SourceId, SourceKind.Official, fetchedAt));
    }
}

/// <summary>Limit and offset paging over one BIPAD resource, and the readers both resources share.</summary>
internal sealed class BipadPager
{
    internal static readonly Uri DefaultBaseAddress = new("https://bipadportal.gov.np/api/v1/");

    // Results per page. BIPAD accepted 100 in the research requests.
    internal const int PageSize = 100;

    // No cadence is published; alerts change hourly in the monsoon and not for days outside it, so
    // a day without change is stale. A guess until a season of snapshots says otherwise.
    private static readonly TimeSpan Cadence = TimeSpan.FromHours(12);

    private readonly SourceFetcher _fetcher;
    private readonly Uri _resource;
    private readonly string _ordering;
    private readonly Dictionary<string, HashSet<string>> _keep;

    internal BipadPager(HttpClient httpClient, ISnapshotStore store, TimeProvider timeProvider, Uri? baseAddress, string resource, string ordering, HashSet<string> fields)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _fetcher = new SourceFetcher(httpClient, store, timeProvider);
        _resource = new Uri(baseAddress ?? DefaultBaseAddress, resource);
        _ordering = ordering;
        _keep = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { ["results"] = fields };
    }

    internal int MaxPages
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 10;

    internal Uri PageUri(int offset) => new(_resource, string.Create(CultureInfo.InvariantCulture, $"?limit={PageSize}&offset={offset}&ordering={_ordering}"));

    internal async Task<SourceResult<T>> FetchAsync<T>(string sourceId, Func<ReadOnlyMemory<byte>, DateTimeOffset, T> parse, Func<T, IReadOnlyList<string>> drift, CancellationToken cancellationToken)
        where T : class
    {
        using var buffer = new MemoryStream();
        SourceFetcher.Fetched? first = null;
        var truncated = true;
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("results");
            for (var page = 0; page < MaxPages; page++)
            {
                var (fetched, failure) = await _fetcher.GetAsync(PageUri(page * PageSize), cancellationToken).ConfigureAwait(false);
                if (fetched is null)
                {
                    return new SourceResult<T>(default, null, failure!);
                }

                first ??= fetched;
                int count;
                try
                {
                    // Scrubbed page by page, so no dropped field is ever held in the combined body.
                    using var document = JsonDocument.Parse(JsonScrub.KeepFields(fetched.Body, _keep));
                    if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                    {
                        throw new JsonException("BIPAD: no results array.");
                    }

                    count = results.GetArrayLength();
                    foreach (var item in results.EnumerateArray())
                    {
                        item.WriteTo(writer);
                    }
                }
                catch (JsonException e)
                {
                    return new SourceResult<T>(default, null, new SourceHealth(SourceStatus.Failing, $"{sourceId}: {e.Message}", null));
                }

                if (count < PageSize)
                {
                    truncated = false;
                    break;
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var combined = first! with { Body = buffer.ToArray() };
        var request = new SourceRequest<T>(sourceId, PageUri(0), Cadence, parse, drift);
        var result = await _fetcher.CompleteAsync(request, combined, cancellationToken).ConfigureAwait(false);
        return truncated && result.Health.Status == SourceStatus.Fresh
            ? result with { Health = result.Health with { Detail = string.Create(CultureInfo.InvariantCulture, $"stopped after {MaxPages} pages of {PageSize}") } }
            : result;
    }

    internal static T[] Results<T>(ReadOnlyMemory<byte> utf8Json, Func<JsonElement, T> read)
    {
        using var document = JsonDocument.Parse(utf8Json);
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? [.. results.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(read)]
            : throw new JsonException("BIPAD: no results array.");
    }
}
