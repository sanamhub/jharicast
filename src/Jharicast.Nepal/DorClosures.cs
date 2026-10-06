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

/// <summary>A road closure from the Department of Roads. Carries no contact details (ADR-0011).</summary>
/// <param name="RoadRefNo">Road reference number, for example <c>H01</c>, or null.</param>
/// <param name="LinkCode">Road link code, or null.</param>
/// <param name="ClosureType">Closure type as DoR words it, or null.</param>
/// <param name="Reason">Closure reason as DoR words it, or null.</param>
/// <param name="Location">Point, or null.</param>
/// <param name="StartsAt">Start, UTC, or null.</param>
/// <param name="ExpectedEndAt">Estimated end, UTC, or null.</param>
/// <param name="EndedAt">Actual end, UTC, or null while it lasts.</param>
public sealed record RoadClosure(string? RoadRefNo, string? LinkCode, string? ClosureType, string? Reason, GeoPoint? Location, DateTimeOffset? StartsAt, DateTimeOffset? ExpectedEndAt, DateTimeOffset? EndedAt);

/// <summary>Parsed DoR closures.</summary>
/// <param name="Closures">Closures as fetched.</param>
/// <param name="Drift">Missing or unreadable values. Non-empty means the feed changed.</param>
/// <param name="Provenance">Where and when; kind <see cref="SourceKind.Official"/>.</param>
public sealed record DorClosureSnapshot(IReadOnlyList<RoadClosure> Closures, IReadOnlyList<string> Drift, Provenance Provenance)
{
    /// <summary>Closures in force at an instant: started or with no start, and not ended. A passed estimated end does not reopen a road.</summary>
    /// <param name="now">The instant.</param>
    /// <returns>The closures in force.</returns>
    public IReadOnlyList<RoadClosure> ActiveAt(DateTimeOffset now) =>
        [.. Closures.Where(c => (c.StartsAt is null || c.StartsAt <= now) && (c.EndedAt is null || c.EndedAt > now))];
}

/// <summary>
/// DoR road closures, <c>navigate.dor.gov.np/api/Map_data_api/getRoadClosureMapData</c> (ADR-0011,
/// <c>dor.closures</c>). Poll no more than every 30 minutes, with an <see cref="HttpClient"/>
/// built on <see cref="PoliteHttpHandler"/> and <c>HostOverrides["navigate.dor.gov.np"]</c> set to
/// 5 seconds.
/// </summary>
/// <remarks>
/// The feed also returns the names and phone numbers of the officials to call. Every field not
/// read here is dropped before the snapshot is made, so they are never hashed, stored or parsed
/// (ADR-0008). Times without an offset are Nepal time and are converted to UTC. The time field
/// names were checked against the live feed on 2026-10-06; the research note's guesses
/// (<c>start_time</c>, <c>end_time</c>) were wrong, so every closure read as never ending.
/// </remarks>
public sealed class DorClosureSource : ISource<DorClosureSnapshot>
{
    /// <summary>Source id.</summary>
    public const string SourceId = "dor.closures";

    /// <summary>The URL fetched unless another is given.</summary>
    public static readonly Uri DefaultUrl = new("https://navigate.dor.gov.np/api/Map_data_api/getRoadClosureMapData");

    internal static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "road_refno", "link_code", "closure_type", "closure_reason", "latitude", "longitude", "date_roadblock_start", "date_roadblock_end_estimated", "date_roadblock_end",
    };

    // No cadence is published; a day without change is stale. A guess, as for BIPAD.
    private static readonly TimeSpan Cadence = TimeSpan.FromHours(12);

    private readonly SourceFetcher _fetcher;
    private readonly SourceRequest<DorClosureSnapshot> _request;

    /// <summary>Creates the source.</summary>
    /// <param name="httpClient">Client built on <see cref="PoliteHttpHandler"/>; not disposed by this class.</param>
    /// <param name="store">Where snapshots go.</param>
    /// <param name="timeProvider">Clock for fetch times and staleness.</param>
    /// <param name="url">The feed URL; null for <see cref="DefaultUrl"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/>, <paramref name="store"/> or <paramref name="timeProvider"/> is null.</exception>
    public DorClosureSource(HttpClient httpClient, ISnapshotStore store, TimeProvider timeProvider, Uri? url = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _fetcher = new SourceFetcher(httpClient, store, timeProvider);
        _request = new SourceRequest<DorClosureSnapshot>(SourceId, url ?? DefaultUrl, Cadence, Parse, s => s.Drift) { Scrub = Scrub };
    }

    /// <inheritdoc />
    public Task<SourceResult<DorClosureSnapshot>> FetchAsync(CancellationToken cancellationToken) => _fetcher.FetchAsync(_request, cancellationToken);

    /// <summary>Keeps only <see cref="Fields"/> in each record, whether the records are the root array or a <c>data</c> array.</summary>
    /// <exception cref="JsonException">Not JSON.</exception>
    internal static ReadOnlyMemory<byte> Scrub(ReadOnlyMemory<byte> utf8Json) =>
        JsonScrub.KeepFields(utf8Json, new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { [string.Empty] = Fields, ["data"] = Fields });

    /// <summary>Parses a scrubbed body.</summary>
    /// <exception cref="JsonException">Neither an array nor an object with a <c>data</c> array.</exception>
    internal static DorClosureSnapshot Parse(ReadOnlyMemory<byte> utf8Json, DateTimeOffset fetchedAt)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var root = document.RootElement;
        var records = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array => data,
            _ => throw new JsonException("DoR closures: neither an array nor a data array."),
        };

        var drift = new SortedSet<string>(StringComparer.Ordinal);
        var closures = new List<RoadClosure>();
        foreach (var e in records.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
        {
            GeoPoint? location = null;
            if (FeedReader.Number(e, "latitude") is { } lat && FeedReader.Number(e, "longitude") is { } lon && lat is >= -90 and <= 90 && lon is >= -180 and <= 180)
            {
                location = new GeoPoint(lat, lon);
            }
            else
            {
                drift.Add("missing:location");
            }

            if (!e.TryGetProperty("date_roadblock_start", out _))
            {
                drift.Add("missing:date_roadblock_start");
            }

            closures.Add(new RoadClosure(
                FeedReader.Text(e, "road_refno"),
                FeedReader.Text(e, "link_code"),
                FeedReader.Text(e, "closure_type"),
                FeedReader.Text(e, "closure_reason"),
                location,
                FeedReader.Time(e, "date_roadblock_start", drift),
                FeedReader.Time(e, "date_roadblock_end_estimated", drift),
                FeedReader.Time(e, "date_roadblock_end", drift)));
        }

        return new DorClosureSnapshot(closures, [.. drift], new Provenance(SourceId, SourceKind.Official, fetchedAt));
    }
}
