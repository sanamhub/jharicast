using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Fetch;

namespace Jharicast.Nepal;

/// <summary>
/// DHM's district warnings, read from the three-day warning maps DHM publishes twice a day
/// (ADR-0016). Replaces <see cref="DhmWarningsSource"/>, whose feed stopped updating, under the
/// same source id, <c>dhm.warnings</c>.
/// </summary>
/// <remarks>
/// <para>
/// One request lists the latest bulletins (<see cref="DefaultUrl"/>). When the newest one differs
/// from the stored snapshot, three more fetch its maps, one per day; otherwise the stored levels
/// are used and no image is fetched. Send it through a <see cref="PoliteHttpHandler"/> with
/// <c>HostOverrides["dhm.gov.np"]</c> set to 5 seconds (ADR-0007).
/// </para>
/// <para>
/// Day 1 is the Nepal date of the bulletin's <c>create_at</c>, as the maps' own titles show; days
/// 2 and 3 follow it. The snapshot stored is not the images but what was read from them: the
/// bulletin, each image's URL and SHA-256, and every district's level, so a reading can be checked
/// against the image later.
/// </para>
/// <para>
/// Health is <see cref="SourceStatus.Failing"/> when a request fails,
/// <see cref="SourceStatus.Drifting"/> with no value when the list or a map cannot be read (the
/// official rule then says Unknown rather than reading a wrong level), and
/// <see cref="SourceStatus.Stale"/> when the newest bulletin is more than 24 hours old.
/// </para>
/// </remarks>
public sealed partial class DhmWarningMapSource : ISource<DhmWarningSnapshot>
{
    /// <summary>The bulletin list fetched unless another is given: the newest three warning bulletins.</summary>
    public static readonly Uri DefaultUrl = new("https://dhm.gov.np/mfd/api/page?total=3&pagenumber=1&type=8");

    internal const string Format = "jharicast.dhm-warning-maps/1";

    // The site's own IMAGE_URL, read from its script on 2026-10-06.
    private static readonly Uri ImageBase = new("https://dhm.gov.np/mfd/api/image/");

    // Bulletins come at about 08:00 and 18:00 Nepal time; one missed is not yet stale.
    private static readonly TimeSpan Cadence = TimeSpan.FromHours(12);

    private static readonly string[] LevelNames = ["green", "yellow", "orange", "red"];

    private readonly SourceFetcher _fetcher;
    private readonly ISnapshotStore _store;
    private readonly SourceRequest<DhmWarningSnapshot> _request;

    /// <summary>Creates the source.</summary>
    /// <param name="httpClient">Client built on <see cref="PoliteHttpHandler"/>; not disposed by this class.</param>
    /// <param name="store">Where snapshots go.</param>
    /// <param name="timeProvider">Clock for fetch times and staleness.</param>
    /// <param name="url">The bulletin list URL; null for <see cref="DefaultUrl"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/>, <paramref name="store"/> or <paramref name="timeProvider"/> is null.</exception>
    public DhmWarningMapSource(HttpClient httpClient, ISnapshotStore store, TimeProvider timeProvider, Uri? url = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _fetcher = new SourceFetcher(httpClient, store, timeProvider);
        _store = store;
        _request = new SourceRequest<DhmWarningSnapshot>(DhmWarningsParser.SourceId, url ?? DefaultUrl, Cadence, Parse, _ => [])
        {
            IssuedAt = s => s.Provenance.IssuedAt,
        };
    }

    /// <inheritdoc />
    public async Task<SourceResult<DhmWarningSnapshot>> FetchAsync(CancellationToken cancellationToken)
    {
        var (list, failure) = await _fetcher.GetAsync(_request.Url, cancellationToken).ConfigureAwait(false);
        if (list is null)
        {
            return new SourceResult<DhmWarningSnapshot>(null, null, failure!);
        }

        Bulletin bulletin;
        try
        {
            bulletin = ReadList(list.Body.Span);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            return Drifting($"bulletin list: {e.Message}");
        }

        var previous = await _store.LatestAsync(_request.SourceId, cancellationToken).ConfigureAwait(false);
        if (previous is not null && TryParse(previous) is { } stored && SameBulletin(previous.Body, bulletin))
        {
            return new SourceResult<DhmWarningSnapshot>(stored, previous, _fetcher.Health(_request, stored, previous.FetchedAt));
        }

        var days = new List<(Image Image, string Sha256, IReadOnlyDictionary<string, AlertLevel> Levels)>();
        foreach (var image in bulletin.Images)
        {
            var (fetched, imageFailure) = await _fetcher.GetAsync(image.Url, cancellationToken).ConfigureAwait(false);
            if (fetched is null)
            {
                return new SourceResult<DhmWarningSnapshot>(null, null, imageFailure!);
            }

            var (levels, drift) = DhmWarningMap.Read(fetched.Body.Span);
            if (drift.Count > 0)
            {
                return Drifting($"day {image.Day} map {image.Url.AbsolutePath}: unrecognised {string.Join(", ", drift)}");
            }

            days.Add((image, Convert.ToHexStringLower(SHA256.HashData(fetched.Body.Span)), levels));
        }

        var body = Write(bulletin, days);
        return await _fetcher.CompleteAsync(_request, new SourceFetcher.Fetched(body, list.Status, list.Headers, list.At), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a snapshot this source stored.</summary>
    /// <param name="utf8Json">The stored body.</param>
    /// <param name="fetchedAt">Fetch time, UTC.</param>
    /// <returns>The warnings: one per district above Green on each day, dated, with <see cref="Hazard.Unspecified"/>.</returns>
    /// <exception cref="JsonException">Not a body this source wrote, such as a snapshot of the retired feed.</exception>
    public static DhmWarningSnapshot Parse(ReadOnlyMemory<byte> utf8Json, DateTimeOffset fetchedAt)
    {
        try
        {
            return ParseBody(utf8Json, fetchedAt);
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new JsonException($"DHM warning maps: {e.Message}", e);
        }
    }

    private static DhmWarningSnapshot ParseBody(ReadOnlyMemory<byte> utf8Json, DateTimeOffset fetchedAt)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != Format)
        {
            throw new JsonException($"DHM warning maps: not a {Format} body.");
        }

        var issued = root.GetProperty("bulletin").GetProperty("createdAt").GetDateTimeOffset();
        var warnings = new List<DistrictWarning>();
        var dates = new List<DateOnly>();
        foreach (var day in root.GetProperty("days").EnumerateArray())
        {
            var date = DateOnly.ParseExact(day.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            dates.Add(date);
            foreach (var district in day.GetProperty("levels").EnumerateObject())
            {
                var level = (AlertLevel)Array.IndexOf(LevelNames, district.Value.GetString());
                if (level < AlertLevel.Green)
                {
                    throw new JsonException($"DHM warning maps: unknown level '{district.Value.GetString()}'.");
                }

                if (level > AlertLevel.Green)
                {
                    warnings.Add(new DistrictWarning(Gazetteer.ById(district.Name), Hazard.Unspecified, level) { ValidOn = date });
                }
            }
        }

        return new DhmWarningSnapshot(warnings, [], new Provenance(DhmWarningsParser.SourceId, SourceKind.Official, fetchedAt) { IssuedAt = issued })
        {
            Days = dates,
        };
    }

    // The newest published bulletin with at least one day's map. A day it has no map for stays out
    // of the snapshot's Days, so the assessor says Unknown for that date instead of reading an
    // older bulletin's map as if it were current.
    internal static Bulletin ReadList(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);
        using var document = JsonDocument.ParseValue(ref reader);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("no data array.");
        }

        Bulletin? newest = null;
        foreach (var item in data.EnumerateArray())
        {
            if (Text(item, "status") != "1" || !DateTimeOffset.TryParse(Text(item, "create_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created))
            {
                continue;
            }

            var images = new List<Image>();
            if (item.TryGetProperty("weather_map_images", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var image in list.EnumerateArray())
                {
                    if (image.TryGetProperty("weather_type_day_id", out var day) && day.TryGetInt32(out var n) && n is >= 1 and <= 3
                        && Text(image, "filepath") is { } path)
                    {
                        // The path comes from the response, so it may only name an image under ImageBase.
                        if (!MapPath().IsMatch(path))
                        {
                            throw new InvalidDataException($"unexpected image path '{path}'.");
                        }

                        images.Add(new Image(n, new Uri(ImageBase, path)));
                    }
                }
            }

            images.Sort((a, b) => a.Day.CompareTo(b.Day));
            if (images.Select(i => i.Day).Distinct().Count() != images.Count)
            {
                throw new InvalidDataException($"bulletin {Text(item, "id")} has two maps for one day.");
            }

            if (images.Count > 0 && (newest is null || created > newest.CreatedAt))
            {
                newest = new Bulletin(Text(item, "id") ?? "", created, images);
            }
        }

        return newest ?? throw new InvalidDataException("no published bulletin with a map.");
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DhmWarningSnapshot? TryParse(Snapshot snapshot)
    {
        try
        {
            return Parse(snapshot.Body, snapshot.FetchedAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Same bulletin and the same image files: DHM replaces a map by uploading a new file, which
    // gets a new name.
    private static bool SameBulletin(ReadOnlyMemory<byte> body, Bulletin bulletin)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var images = root.GetProperty("days").EnumerateArray().Select(d => d.GetProperty("image").GetString()).ToArray();
        return root.GetProperty("bulletin").GetProperty("id").GetString() == bulletin.Id
            && images.SequenceEqual(bulletin.Images.Select(i => i.Url.AbsoluteUri));
    }

    private static byte[] Write(Bulletin bulletin, List<(Image Image, string Sha256, IReadOnlyDictionary<string, AlertLevel> Levels)> days)
    {
        var day1 = NepalTime.DateOf(bulletin.CreatedAt);
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("format", Format);
            json.WriteStartObject("bulletin");
            json.WriteString("id", bulletin.Id);
            json.WriteString("createdAt", bulletin.CreatedAt.ToUniversalTime());
            json.WriteEndObject();
            json.WriteStartArray("days");
            foreach (var (image, sha256, levels) in days)
            {
                json.WriteStartObject();
                json.WriteNumber("day", image.Day);
                json.WriteString("date", day1.AddDays(image.Day - 1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                json.WriteString("image", image.Url.AbsoluteUri);
                json.WriteString("sha256", sha256);
                json.WriteStartObject("levels");
                foreach (var (district, level) in levels.OrderBy(l => l.Key, StringComparer.Ordinal))
                {
                    json.WriteString(district, LevelNames[(int)level]);
                }

                json.WriteEndObject();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static SourceResult<DhmWarningSnapshot> Drifting(string detail) =>
        new(null, null, new SourceHealth(SourceStatus.Drifting, $"{DhmWarningsParser.SourceId}: {detail}", null));

    [GeneratedRegex(@"^weather-map/\d{4}/\d{2}/\d{2}/[A-Za-z0-9_-]+\.png$", RegexOptions.CultureInvariant)]
    private static partial Regex MapPath();

    internal sealed record Bulletin(string Id, DateTimeOffset CreatedAt, IReadOnlyList<Image> Images);

    internal sealed record Image(int Day, Uri Url);
}
