using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Fetch;
using Jharicast.Nepal;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jharicast.Tests.Nepal;

/// <summary>
/// DHM's warning maps, from real responses saved on 2026-10-06: the list of the three newest
/// bulletins, the three maps of bulletin 12340 (issued 18:00 NPT on 6 Oct), and day 1 of bulletin
/// 12309 (25 Sep, the only map in the 36 checked with Red). Expected levels were read from the
/// same images by a separate Python script and checked by eye against the maps.
/// </summary>
public sealed class DhmWarningMapTests
{
    private const string Map12340Day1 = "dhm-warning-map-12340-day-1.png";
    private const string Map12309Day1 = "dhm-warning-map-12309-day-1.png";

    // 18:30 NPT on 6 Oct, after bulletin 12340.
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 45, 0, TimeSpan.Zero);

    [Fact]
    public void The_decoder_reads_dhm_s_png()
    {
        var image = Png.Decode(Fixtures.Read(Map12340Day1));

        Assert.Equal((1300, 800), (image.Width, image.Height));
        Assert.Equal(((byte)255, (byte)0, (byte)0), image[85, 638]); // the legend's red box
        Assert.Equal(((byte)255, (byte)255, (byte)255), image[5, 5]);
    }

    [Fact]
    public void The_decoder_reads_palette_images_and_every_filter()
    {
        // 2 by 5, one row per filter type; every pixel is palette entry 1.
        byte[] rows = [0, 1, 1, 1, 1, 0, 2, 0, 0, 3, 1, 0, 4, 0, 0];
        var png = Build(2, 5, colourType: 3, rows, palette: [0, 0, 0, 77, 229, 7]);

        var image = Png.Decode(png);

        for (var y = 0; y < 5; y++)
        {
            Assert.Equal(((byte)77, (byte)229, (byte)7), image[0, y]);
            Assert.Equal(((byte)77, (byte)229, (byte)7), image[1, y]);
        }
    }

    public static TheoryData<string, byte[]> Damaged() => new()
    {
        { "not a png", "GIF89a and more bytes"u8.ToArray() },
        { "too large", Build(5000, 1, 2, new byte[16]) },
        { "16-bit", Build(1, 1, 2, new byte[7], depth: 16) },
        { "interlaced", Build(1, 1, 2, new byte[4], interlace: 1) },
        { "short data", Build(4, 4, 2, new byte[10]) },
        { "bad filter", Build(1, 1, 2, [9, 0, 0, 0]) },
        { "palette index past the end", Build(1, 1, 3, [0, 5], palette: [1, 2, 3]) },
        { "truncated", Fixtures.Read(Map12340Day1)[..5000] },
        { "bad crc", Flip(Fixtures.Read(Map12340Day1), 2000) },
    };

    [Theory]
    [MemberData(nameof(Damaged))]
    public void The_decoder_refuses_what_it_cannot_read(string what, byte[] png)
    {
        Assert.NotNull(what);
        Assert.Throws<InvalidDataException>(() => Png.Decode(png));
    }

    [Fact]
    public void Every_district_is_read_off_the_6_oct_map()
    {
        var (levels, drift) = DhmWarningMap.Read(Fixtures.Read(Map12340Day1));

        Assert.Empty(drift);
        Assert.Equal(77, levels.Count);
        Assert.Equal(AlertLevel.Green, levels["jhapa"]);
        Assert.Equal(AlertLevel.Green, levels["sunsari"]);
        Assert.Equal(AlertLevel.Yellow, levels["kathmandu"]);
        Assert.Equal(AlertLevel.Yellow, levels["bhaktapur"]); // the smallest district
        Assert.Equal(31, levels.Values.Count(l => l == AlertLevel.Yellow));
        Assert.Equal(46, levels.Values.Count(l => l == AlertLevel.Green));
    }

    [Fact]
    public void Red_and_orange_fills_are_told_apart_from_the_red_borders()
    {
        var (levels, drift) = DhmWarningMap.Read(Fixtures.Read(Map12309Day1));

        Assert.Empty(drift);
        Assert.Equal(
            ["dhading", "dolakha", "dolpa", "gorkha", "manang", "mustang", "rasuwa", "sankhuwasabha", "sindhupalchok", "solukhumbu", "taplejung"],
            levels.Where(l => l.Value == AlertLevel.Red).Select(l => l.Key).Order(StringComparer.Ordinal));
        Assert.Equal(55, levels.Values.Count(l => l == AlertLevel.Orange));
        Assert.Equal(AlertLevel.Yellow, levels["jhapa"]);
        Assert.DoesNotContain(AlertLevel.Green, levels.Values);
    }

    [Fact]
    public void A_map_of_another_size_is_drift_not_a_reading()
    {
        var (levels, drift) = DhmWarningMap.Read(Build(2, 2, 2, new byte[2 * 7]));

        Assert.Empty(levels);
        Assert.Equal(["layout:2x2"], drift);
    }

    [Fact]
    public void A_map_with_its_districts_painted_over_names_them_as_drift()
    {
        // The same layout with the plot area blanked: the ticks are there, the fills are not.
        var image = Png.Decode(Fixtures.Read(Map12340Day1));
        for (var y = 95; y < 755; y++)
        {
            for (var x = 47; x < 1282; x++)
            {
                image.Rgb.AsSpan((y * image.Width + x) * 3, 3).Fill(255);
            }
        }

        var (levels, drift) = DhmWarningMap.Read(Build(image.Width, image.Height, 2, Rows(image)));

        Assert.Empty(levels);
        Assert.Equal(77, drift.Count);
        Assert.Contains("district:jhapa", drift);
    }

    [Fact]
    public async Task The_source_reads_three_dated_days_from_the_newest_bulletin()
    {
        var (source, stub, store) = Source(Serve());

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Fresh, result.Health.Status);
        var value = result.Value!;
        Assert.Equal([new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 8)], value.Days);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 10, 27, 59, 483, TimeSpan.Zero), value.Provenance.IssuedAt);
        Assert.Equal("dhm.warnings", value.Provenance.Source);
        Assert.Equal(SourceKind.Official, value.Provenance.Kind);
        Assert.Equal(AlertLevel.Green, value.LevelOn(District("jhapa"), new DateOnly(2026, 10, 6)));
        Assert.Equal(AlertLevel.Yellow, value.LevelOn(District("kathmandu"), new DateOnly(2026, 10, 6)));
        Assert.Equal(AlertLevel.Green, value.LevelOn(District("dang"), new DateOnly(2026, 10, 6)));
        Assert.Equal(AlertLevel.Yellow, value.LevelOn(District("dang"), new DateOnly(2026, 10, 7)));
        Assert.Equal(AlertLevel.Green, value.LevelOn(District("kathmandu"), new DateOnly(2026, 10, 9)));
        Assert.All(value.Warnings, w => Assert.Equal(Hazard.Unspecified, w.Hazard));
        Assert.Equal(4, stub.Seen.Count);
        Assert.Equal("/mfd/api/image/weather-map/2026/10/06/warning-map-day-2-1791288008163.png", stub.Seen.ElementAt(2).AbsolutePath);

        var saved = Assert.Single(store.Saved);
        Assert.Equal(DhmWarningMapSource.DefaultUrl, saved.Url);
        using var body = JsonDocument.Parse(saved.Body);
        Assert.Equal("jharicast.dhm-warning-maps/1", body.RootElement.GetProperty("format").GetString());
        Assert.Equal(64, body.RootElement.GetProperty("days")[0].GetProperty("sha256").GetString()!.Length);
        Assert.Equal(value, DhmWarningMapSource.Parse(saved.Body, saved.FetchedAt), new SnapshotComparer());
    }

    [Fact]
    public async Task An_unchanged_bulletin_fetches_no_images_again()
    {
        var (source, stub, store) = Source(Serve());
        var first = await source.FetchAsync(CancellationToken.None);

        var second = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(5, stub.Seen.Count);
        Assert.Single(store.Saved);
        Assert.Equal(SourceStatus.Fresh, second.Health.Status);
        Assert.Equal(first.Value, second.Value, new SnapshotComparer());
    }

    [Fact]
    public async Task A_bulletin_over_a_day_old_is_stale()
    {
        var (source, _, _) = Source(Serve(), new FakeTimeProvider(Now.AddHours(26)));

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Stale, result.Health.Status);
        Assert.NotNull(result.Value);
    }

    [Fact]
    public async Task A_missing_map_fails_the_source_and_gives_no_levels()
    {
        var (source, _, store) = Source(Serve(image: _ => null));

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Failing, result.Health.Status);
        Assert.Null(result.Value);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task An_unreadable_map_is_drift_with_no_levels_and_nothing_stored()
    {
        var (source, _, store) = Source(Serve(image: path => path.Contains("day-3", StringComparison.Ordinal) ? "<html>maintenance</html>"u8.ToArray() : Fixtures.Read(Map12340Day1)));

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Drifting, result.Health.Status);
        Assert.Contains("day 3 map", result.Health.Detail, StringComparison.Ordinal);
        Assert.Null(result.Value);
        Assert.Empty(store.Saved);
    }

    [Theory]
    [InlineData("../../../admin/x.png")]
    [InlineData("https://example.com/weather-map/2026/10/06/x.png")]
    [InlineData("weather-map/2026/10/06/x.png?y=1")]
    public async Task An_image_path_outside_dhm_s_map_folder_is_never_fetched(string path)
    {
        var list = Encoding.UTF8.GetString(Fixtures.Read("dhm-warning-maps-list-2026-10-06.json"))
            .Replace("weather-map/2026/10/06/warning-map-day-1-1791288004396.png", path, StringComparison.Ordinal);
        var (source, stub, _) = Source(Serve(list: Encoding.UTF8.GetBytes(list)));

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Drifting, result.Health.Status);
        Assert.Single(stub.Seen);
    }

    [Fact]
    public async Task A_newest_bulletin_with_one_map_gives_that_day_only_not_an_older_bulletin_s_days()
    {
        var list = JsonNode.Parse(Fixtures.Read("dhm-warning-maps-list-2026-10-06.json"))!;
        var images = list["data"]![0]!["weather_map_images"]!.AsArray();
        foreach (var image in images.Where(i => (int)i!["weather_type_day_id"]! != 1).ToList())
        {
            images.Remove(image);
        }

        var (source, stub, _) = Source(Serve(list: Encoding.UTF8.GetBytes(list.ToJsonString())));

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Fresh, result.Health.Status);
        Assert.Equal([new DateOnly(2026, 10, 6)], result.Value!.Days);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 10, 27, 59, 483, TimeSpan.Zero), result.Value.Provenance.IssuedAt);
        Assert.Equal(2, stub.Seen.Count);
    }

    [Fact]
    public async Task Two_maps_for_one_day_are_drift()
    {
        var list = Encoding.UTF8.GetString(Fixtures.Read("dhm-warning-maps-list-2026-10-06.json"))
            .Replace("\"weather_type_day_id\":2", "\"weather_type_day_id\":1", StringComparison.Ordinal);
        var (source, _, _) = Source(Serve(list: Encoding.UTF8.GetBytes(list)));

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Drifting, result.Health.Status);
        Assert.Contains("two maps for one day", result.Health.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_with_no_map_is_drift()
    {
        var (source, _, _) = Source(Serve(list: """{"data":[{"id":"1","status":"1","create_at":"2026-10-06T10:27:59Z","weather_map_images":[]}]}"""u8.ToArray()));

        var result = await source.FetchAsync(CancellationToken.None);

        Assert.Equal(SourceStatus.Drifting, result.Health.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public void A_snapshot_of_the_retired_feed_is_not_read_as_maps()
    {
        Assert.Throws<JsonException>(() => DhmWarningMapSource.Parse(Fixtures.Read("dhm-getapidata-1-2026-09-24.json"), Now));
        Assert.Throws<JsonException>(() => DhmWarningMapSource.Parse("""{"format":"jharicast.dhm-warning-maps/1","bulletin":{"id":"1","createdAt":"2026-10-06T10:27:59Z"},"days":[{"date":"2026-10-06","levels":{"nowhere":"red"}}]}"""u8.ToArray(), Now));
    }

    private static District District(string id) => Gazetteer.Districts.Single(d => d.Id == id);

    private static (DhmWarningMapSource Source, StubHandler Stub, MemorySnapshotStore Store) Source(StubHandler stub, FakeTimeProvider? time = null)
    {
        var store = new MemorySnapshotStore();
        return (new DhmWarningMapSource(new HttpClient(stub), store, time ?? new FakeTimeProvider(Now)), stub, store);
    }

    // The list from its fixture; each image by the day in its file name.
    private static StubHandler Serve(byte[]? list = null, Func<string, byte[]?>? image = null) => new(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = path == "/mfd/api/page"
            ? list ?? Fixtures.Read("dhm-warning-maps-list-2026-10-06.json")
            : (image ?? DefaultImage)(path);
        return body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    });

    private static byte[]? DefaultImage(string path) =>
        path.Contains("day-1", StringComparison.Ordinal) ? Fixtures.Read(Map12340Day1)
        : path.Contains("day-2", StringComparison.Ordinal) ? Fixtures.Read("dhm-warning-map-12340-day-2.png")
        : path.Contains("day-3", StringComparison.Ordinal) ? Fixtures.Read("dhm-warning-map-12340-day-3.png")
        : null;

    private static byte[] Rows(RgbImage image)
    {
        var stride = image.Width * 3;
        var rows = new byte[image.Height * (stride + 1)];
        for (var y = 0; y < image.Height; y++)
        {
            image.Rgb.AsSpan(y * stride, stride).CopyTo(rows.AsSpan(y * (stride + 1) + 1));
        }

        return rows;
    }

    // A PNG around the given filtered rows, with any header values the test needs to break.
    private static byte[] Build(int width, int height, byte colourType, byte[] rows, byte[]? palette = null, byte depth = 8, byte interlace = 0)
    {
        using var file = new MemoryStream();
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        (header[8], header[9], header[12]) = (depth, colourType, interlace);
        Chunk(file, "IHDR", header);
        if (palette is not null)
        {
            Chunk(file, "PLTE", palette);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(rows);
        }

        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", []);
        return file.ToArray();
    }

    // One bit flipped inside the image data, which still inflates: only the CRC catches it.
    private static byte[] Flip(byte[] png, int at)
    {
        png[at] ^= 1;
        return png;
    }

    private static void Chunk(Stream file, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        file.Write(word);
        byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
        file.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(word, Png.Crc32(typed));
        file.Write(word);
    }

    private sealed class SnapshotComparer : IEqualityComparer<DhmWarningSnapshot?>
    {
        public bool Equals(DhmWarningSnapshot? x, DhmWarningSnapshot? y) =>
            x is not null && y is not null
            && x.Warnings.SequenceEqual(y.Warnings)
            && x.Days.SequenceEqual(y.Days)
            && x.Provenance.IssuedAt == y.Provenance.IssuedAt;

        public int GetHashCode(DhmWarningSnapshot? obj) => obj?.Warnings.Count ?? 0;
    }
}
