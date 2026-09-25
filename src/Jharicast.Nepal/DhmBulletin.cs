using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Fetch;

namespace Jharicast.Nepal;

/// <summary>DHM's three-day forecast bulletin, English sections as plain text.</summary>
/// <param name="Id">DHM's forecast id.</param>
/// <param name="IssuedAt">Issue time, UTC, from the feed's ISO <c>issue_date</c>. The Nepali title's Bikram Sambat date is never parsed (ADR-0011).</param>
/// <param name="PresentWeather">Present weather, or null when the bulletin has none.</param>
/// <param name="Analysis">Meteorological analysis, or null.</param>
/// <param name="WarningAdvisory">Warning and advisory text, or null.</param>
/// <param name="Forecast">National forecast by day, or null.</param>
/// <param name="ImagePaths">Forecast map paths as the feed gives them, relative to DHM's site.</param>
/// <param name="Drift">Fields the reader expected and did not find. Non-empty means the feed changed.</param>
/// <param name="Provenance">Where and when, with <see cref="Provenance.IssuedAt"/> set.</param>
public sealed record DhmBulletin(string Id, DateTimeOffset IssuedAt, string? PresentWeather, string? Analysis, string? WarningAdvisory, string? Forecast, IReadOnlyList<string> ImagePaths, IReadOnlyList<string> Drift, Provenance Provenance);

/// <summary>
/// DHM's latest three-day forecast bulletin (ADR-0011, <c>dhm.bulletin</c>): the list at
/// <c>dhm.gov.np/mfd/api/three-days-forecast-latest</c>, then the newest bulletin at
/// <c>three-days-forecast/{id}</c>. Check it hourly; the list is fetched with conditional GET
/// when the client is built on <see cref="PoliteHttpHandler"/> (set
/// <c>HostOverrides["dhm.gov.np"]</c> to 5 seconds), and the bulletin itself only when the
/// newest id changes.
/// </summary>
/// <remarks>
/// Bulletins come about 02:15 and 12:15 UTC, so health is <see cref="SourceStatus.Stale"/> when the
/// newest issue time, or the content, is more than 14 hours old. Both bodies are stored as
/// snapshots, <c>dhm.bulletin-list</c> and <c>dhm.bulletin</c>, because the issue time is only in
/// the list. The bulletin's <c>text_english</c> field held only a heading and map images in the
/// 2026-09-25 capture, so the English text is read from its section fields instead.
/// </remarks>
public sealed partial class DhmBulletinSource : ISource<DhmBulletin>
{
    /// <summary>Source id of the bulletin.</summary>
    public const string SourceId = "dhm.bulletin";

    /// <summary>The API root used unless another is given.</summary>
    public static readonly Uri DefaultBaseAddress = new("https://dhm.gov.np/mfd/api/");

    internal const string ListSourceId = "dhm.bulletin-list";

    // ADR-0011: bulletins are stale after 14 h, twice this.
    private static readonly TimeSpan Cadence = TimeSpan.FromHours(7);

    private readonly SourceFetcher _fetcher;
    private readonly SourceRequest<BulletinEntry> _list;
    private readonly Uri _baseAddress;
    private (SourceRequest<DhmBulletin> Request, SourceResult<DhmBulletin> Result)? _last;

    /// <summary>Creates the source.</summary>
    /// <param name="httpClient">Client built on <see cref="PoliteHttpHandler"/>; not disposed by this class.</param>
    /// <param name="store">Where snapshots go.</param>
    /// <param name="timeProvider">Clock for fetch times and staleness.</param>
    /// <param name="baseAddress">API root ending in <c>/</c>; null for <see cref="DefaultBaseAddress"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/>, <paramref name="store"/> or <paramref name="timeProvider"/> is null.</exception>
    public DhmBulletinSource(HttpClient httpClient, ISnapshotStore store, TimeProvider timeProvider, Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _fetcher = new SourceFetcher(httpClient, store, timeProvider);
        _baseAddress = baseAddress ?? DefaultBaseAddress;
        _list = new SourceRequest<BulletinEntry>(ListSourceId, new Uri(_baseAddress, "three-days-forecast-latest"), Cadence, (body, _) => ReadLatest(body), _ => []);
    }

    /// <inheritdoc />
    public async Task<SourceResult<DhmBulletin>> FetchAsync(CancellationToken cancellationToken)
    {
        var list = await _fetcher.FetchAsync(_list, cancellationToken).ConfigureAwait(false);
        if (list.Value is not { } entry)
        {
            return new SourceResult<DhmBulletin>(null, list.Snapshot, list.Health);
        }

        if (_last is var (lastRequest, lastResult) && lastResult.Value!.Id == entry.Id)
        {
            // Same bulletin as last time: no second request, only a fresh look at its age.
            return lastResult with { Health = _fetcher.Health(lastRequest, lastResult.Value, lastResult.Health.LastChangeAt) };
        }

        var request = new SourceRequest<DhmBulletin>(SourceId, new Uri(_baseAddress, "three-days-forecast/" + entry.Id), Cadence, (body, at) => ReadBulletin(body, entry, at), b => b.Drift)
        {
            IssuedAt = b => b.IssuedAt,
        };
        var result = await _fetcher.FetchAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Value is not null)
        {
            _last = (request, result);
        }

        return result;
    }

    /// <summary>The newest entry of the latest-bulletins list.</summary>
    /// <exception cref="JsonException">Not an array of entries with an id of digits and an ISO issue date.</exception>
    internal static BulletinEntry ReadLatest(ReadOnlyMemory<byte> utf8Json)
    {
        using var document = JsonDocument.Parse(utf8Json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("DHM bulletin list: not an array.");
        }

        var entries = new List<BulletinEntry>();
        foreach (var e in document.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
        {
            var id = e.TryGetProperty("id", out var i) ? (i.ValueKind == JsonValueKind.Number ? i.GetRawText() : i.GetString()) : null;
            var issued = e.TryGetProperty("issue_date", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            // The id goes into a URL path, so digits only.
            if (id is not { Length: > 0 } || !id.All(char.IsAsciiDigit)
                || !DateTimeOffset.TryParse(issued, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var issuedAt))
            {
                throw new JsonException("DHM bulletin list: an entry lacks a numeric id or an ISO issue_date.");
            }

            var images = e.TryGetProperty("images", out var im) && im.ValueKind == JsonValueKind.Array
                ? im.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                : [];
            entries.Add(new BulletinEntry(id, issuedAt.ToUniversalTime(), images));
        }

        return entries.MaxBy(e => e.IssuedAt) ?? throw new JsonException("DHM bulletin list: empty.");
    }

    /// <summary>Reads one bulletin's English sections.</summary>
    /// <exception cref="JsonException">Not a JSON object.</exception>
    internal static DhmBulletin ReadBulletin(ReadOnlyMemory<byte> utf8Json, BulletinEntry entry, DateTimeOffset fetchedAt)
    {
        using var document = JsonDocument.Parse(utf8Json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("DHM bulletin: not an object.");
        }

        var drift = new List<string>();
        return new DhmBulletin(
            entry.Id,
            entry.IssuedAt,
            Section("present_weather_en"),
            Section("met_analysis_en"),
            Section("warning_advisory_en"),
            Section("national_forecast_en"),
            entry.Images,
            drift,
            new Provenance(SourceId, SourceKind.Official, fetchedAt) { IssuedAt = entry.IssuedAt });

        string? Section(string name)
        {
            if (!root.TryGetProperty(name, out var v) || v.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                drift.Add("field:" + name);
                return null;
            }

            return v.ValueKind == JsonValueKind.Null ? null : PlainText(v.GetString()!);
        }
    }

    /// <summary>HTML fragment to plain text: paragraph and line breaks become new lines, other tags go, entities are decoded.</summary>
    internal static string? PlainText(string html)
    {
        var text = BlockBreak().Replace(html, "\n");
        text = WebUtility.HtmlDecode(Tag().Replace(text, string.Empty)).Replace(' ', ' ');
        var lines = text.Split('\n').Select(l => Spaces().Replace(l, " ").Trim()).Where(l => l.Length > 0);
        var joined = string.Join('\n', lines);
        return joined.Length > 0 ? joined : null;
    }

    [GeneratedRegex(@"<\s*(br|/p|/div|/li)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockBreak();

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\r\f\v]+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    /// <summary>One entry of the latest-bulletins list.</summary>
    internal sealed record BulletinEntry(string Id, DateTimeOffset IssuedAt, IReadOnlyList<string> Images);
}
