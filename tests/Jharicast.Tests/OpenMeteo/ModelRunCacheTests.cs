using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Jharicast.OpenMeteo;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static Jharicast.Tests.OpenMeteo.OpenMeteoClientTests;

namespace Jharicast.Tests.OpenMeteo;

public sealed class ModelRunCacheTests
{
    private static readonly GeoPoint Nepalgunj = new(28.05, 81.617);
    private static readonly GeoPoint Kathmandu = new(27.7172, 85.324);
    private static readonly GeoPoint Pokhara = new(28.2096, 83.9856);

    // 03:00 UTC is 08:45 in Kathmandu.
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero));

    private OpenMeteoClient Client(RecordingHandler handler) => new(new HttpClient(handler), new OpenMeteoOptions { TimeProvider = _time });

    private static byte[] Fixture() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "open-meteo-ensemble-ecmwf-2026-09-25.json"));

    [Theory]
    [InlineData("2026-09-25T03:00:00Z", 7, "2026-09-25T07:00:00Z")] // 18Z is the newest run; 00Z is due at 07:00
    [InlineData("2026-09-25T07:00:00Z", 7, "2026-09-25T13:00:00Z")] // 00Z has just appeared; 06Z is due at 13:00
    [InlineData("2026-09-25T12:59:00Z", 7, "2026-09-25T13:00:00Z")]
    [InlineData("2026-09-25T03:00:00Z", 6, "2026-09-25T06:00:00Z")]
    public void The_next_run_is_due_one_interval_after_the_newest_available_run(string fetchedAt, int lagHours, string expected)
    {
        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ModelRunCache.NextRunAvailable(DateTimeOffset.Parse(fetchedAt, System.Globalization.CultureInfo.InvariantCulture), TimeSpan.FromHours(lagHours)));
    }

    [Fact]
    public async Task A_second_request_before_the_next_run_makes_no_http_call()
    {
        var handler = new RecordingHandler(_ => Fixture());
        var client = Client(handler);
        var ct = TestContext.Current.CancellationToken;

        var first = await client.GetEnsembleDailyAsync([Nepalgunj, Kathmandu], "ecmwf_ifs025", ["precipitation_sum"], 3, ct);
        _time.Advance(TimeSpan.FromHours(3.9)); // 06:54 UTC, the 00Z run is not due yet
        var second = await client.GetEnsembleDailyAsync([Nepalgunj, Kathmandu], "ecmwf_ifs025", ["precipitation_sum"], 3, ct);

        Assert.Single(handler.Requests);
        Assert.Equal(first["precipitation_sum"][1].Members, second["precipitation_sum"][1].Members);
        Assert.Equal(first["precipitation_sum"][1].Provenance, second["precipitation_sum"][1].Provenance); // the data time, not the ask time

        _time.Advance(TimeSpan.FromMinutes(6)); // 07:00 UTC
        await client.GetEnsembleDailyAsync([Nepalgunj, Kathmandu], "ecmwf_ifs025", ["precipitation_sum"], 3, ct);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Only_points_not_cached_are_requested()
    {
        var handler = new RecordingHandler(uri => EnsembleJson(uri.Query.Split('&')[0].Count(c => c == ',') + 1));
        var client = Client(handler);
        var ct = TestContext.Current.CancellationToken;

        await client.GetEnsembleDailyAsync([Nepalgunj, Kathmandu], "gfs025", ["precipitation_sum"], 1, ct);
        var result = await client.GetEnsembleDailyAsync([Kathmandu, Pokhara], "gfs025", ["precipitation_sum"], 1, ct);

        Assert.Equal(2, handler.Requests.Count);
        Assert.StartsWith("?latitude=28.2096&longitude=83.9856&", handler.Requests[1].Query, StringComparison.Ordinal);
        Assert.Equal(2, result["precipitation_sum"].Count);
    }

    [Fact]
    public async Task Another_model_a_longer_range_or_a_new_variable_is_a_miss()
    {
        var handler = new RecordingHandler(uri => EnsembleJson(1));
        var client = Client(handler);
        var ct = TestContext.Current.CancellationToken;

        await client.GetEnsembleDailyAsync([Kathmandu], "gfs025", ["precipitation_sum"], 1, ct);
        await client.GetEnsembleDailyAsync([Kathmandu], "ecmwf_ifs025", ["precipitation_sum"], 1, ct);
        await client.GetEnsembleDailyAsync([Kathmandu], "gfs025", ["precipitation_sum", "wind_gusts_10m_max"], 1, ct);

        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task A_shorter_range_is_served_from_the_cache_and_trimmed()
    {
        var handler = new RecordingHandler(_ => Fixture());
        var client = Client(handler);
        var ct = TestContext.Current.CancellationToken;

        await client.GetEnsembleDailyAsync([Nepalgunj, Kathmandu], "ecmwf_ifs025", ["precipitation_sum"], 3, ct);
        var shorter = await client.GetEnsembleDailyAsync([Nepalgunj, Kathmandu], "ecmwf_ifs025", ["precipitation_sum"], 2, ct);

        Assert.Single(handler.Requests);
        Assert.All(shorter["precipitation_sum"], l => Assert.Equal(2, l.Days.Count));
        Assert.All(shorter["precipitation_sum"], l => Assert.Equal(2, l.Members.Count));
    }

    // 18:00 UTC is 23:45 in Kathmandu; at 18:20 UTC the cached first day is yesterday.
    [Fact]
    public async Task An_entry_ends_at_local_midnight()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 9, 25, 18, 0, 0, TimeSpan.Zero));
        var handler = new RecordingHandler(_ => Fixture());
        var client = Client(handler);
        var ct = TestContext.Current.CancellationToken;

        await client.GetEnsembleDailyAsync([Nepalgunj, Kathmandu], "ecmwf_ifs025", ["precipitation_sum"], 3, ct);
        _time.Advance(TimeSpan.FromMinutes(10));
        await client.GetEnsembleDailyAsync([Nepalgunj, Kathmandu], "ecmwf_ifs025", ["precipitation_sum"], 3, ct);
        _time.Advance(TimeSpan.FromMinutes(10));
        await client.GetEnsembleDailyAsync([Nepalgunj, Kathmandu], "ecmwf_ifs025", ["precipitation_sum"], 3, ct);

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Deterministic_results_are_cached_too()
    {
        var json = """{"latitude":27.5,"longitude":83.25,"utc_offset_seconds":20700,"daily":{"time":["2026-09-28"],"precipitation_sum_ecmwf_ifs025":[1.0],"precipitation_sum_gfs_seamless":[10.0]}}"""u8.ToArray();
        var handler = new RecordingHandler(_ => json);
        var client = Client(handler);
        var ct = TestContext.Current.CancellationToken;

        await client.GetDailyAsync([Kathmandu], ["ecmwf_ifs025", "gfs_seamless"], ["precipitation_sum"], 1, ct);
        var again = await client.GetDailyAsync([Kathmandu], ["ecmwf_ifs025", "gfs_seamless"], ["precipitation_sum"], 1, ct);

        Assert.Single(handler.Requests);
        Assert.Equal(10.0, again["gfs_seamless"][0].Variables["precipitation_sum"][0]);
    }

    [Fact]
    public async Task Elevations_come_100_points_per_call_and_are_asked_for_once()
    {
        var points = Enumerable.Range(0, 101).Select(i => new GeoPoint(27 + (i * 0.01), 84)).ToArray();
        var handler = new RecordingHandler(uri =>
        {
            var latitudes = uri.Query.Split('&')[0]["?latitude=".Length..].Split(',');
            return Encoding.UTF8.GetBytes("{\"elevation\":[" + string.Join(',', latitudes.Select(l => l.Replace(".", "", StringComparison.Ordinal))) + "]}");
        });
        var client = Client(handler);
        var ct = TestContext.Current.CancellationToken;

        var elevations = await client.GetElevationAsync(points, ct);
        var again = await client.GetElevationAsync([points[100], points[0]], ct);

        Assert.Equal(2, handler.Requests.Count);
        Assert.StartsWith("https://api.open-meteo.com/v1/elevation?latitude=27,27.01,", handler.Requests[0].AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(101, elevations.Count);
        Assert.Equal(2701, elevations[1]);
        Assert.Equal([elevations[100], elevations[0]], again);
    }
}
