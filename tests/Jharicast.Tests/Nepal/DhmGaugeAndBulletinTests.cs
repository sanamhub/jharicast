using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jharicast.Fetch;
using Jharicast.Nepal;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jharicast.Tests.Nepal;

public sealed class DhmGaugeParserTests
{
    private const string Capture = "dhm-getapidata-3-2026-09-25.json";
    private static readonly DateTimeOffset Fetched = new(2026, 9, 25, 18, 21, 9, TimeSpan.Zero);

    // The capture of 2026-09-25 18:21 UTC: 516 rain and 338 river stations.
    [Fact]
    public void Capture_parses_without_drift()
    {
        var snapshot = DhmGaugeParser.Parse(Fixtures.Read(Capture), Fetched);

        Assert.Empty(snapshot.Drift);
        Assert.Equal(516, snapshot.Rain.Count);
        Assert.Equal(338, snapshot.Rivers.Count);
        Assert.Equal(19, snapshot.Rain.Count(s => s.Status == GaugeStatus.Warning));
        Assert.Equal((9, 2), (snapshot.Rivers.Count(s => s.Status == GaugeStatus.Warning), snapshot.Rivers.Count(s => s.Status == GaugeStatus.Danger)));
        Assert.Equal(29, snapshot.Rivers.Count(s => s.Trend == RiverTrend.Rising));
        Assert.Equal(SourceKind.Observation, snapshot.Provenance.Kind);
    }

    [Fact]
    public void Rain_station_is_trimmed_resolved_and_totalled()
    {
        var station = DhmGaugeParser.Parse(Fixtures.Read(Capture), Fetched).Rain.Single(s => s.Id == 304);

        Assert.Equal("Arghakhanchi", station.Name); // " Arghakhanchi" in the feed
        Assert.Equal("arghakhanchi", station.District?.Id);
        Assert.Equal(new GeoPoint(27.885, 83.1397), station.Location);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 14, 50, 0, TimeSpan.Zero), station.ObservedAt);
        Assert.Equal([1, 3, 6, 12, 24], station.Totals.Select(t => t.Hours));
        var day = station.Totals[^1];
        Assert.Equal((191.6, true, false), (day.Mm!.Value, day.Warning, day.Danger));
        Assert.Null(station.Totals[0].Mm);
        Assert.Equal(GaugeStatus.Warning, station.Status);
    }

    [Fact]
    public void River_station_levels_are_numbers()
    {
        var station = DhmGaugeParser.Parse(Fixtures.Read(Capture), Fetched).Rivers.Single(s => s.Id == 236);

        Assert.Equal("Bagmati River at Bhorleni", station.Name);
        Assert.Equal("makwanpur", station.District?.Id); // "Makawanpur" in the feed
        Assert.Equal((4.434, 5.2, 6.2), (station.LevelM!.Value, station.WarningLevelM!.Value, station.DangerLevelM!.Value));
        Assert.Equal((GaugeStatus.BelowWarning, RiverTrend.Steady), (station.Status, station.Trend));
    }

    [Fact]
    public void Levels_parse_invariant_under_a_comma_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var json = """{"rainfall_watch":[],"river_watch":[{"id":1,"name":"x","warning_level":"5.2","danger_level":"10.00"}]}"""u8.ToArray();

            var river = Assert.Single(DhmGaugeParser.Parse(json, Fetched).Rivers);

            Assert.Equal((5.2, 10.0), (river.WarningLevelM!.Value, river.DangerLevelM!.Value));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    // Blank and pre-2015 district names give no district and no drift; anything else unknown is drift.
    [Fact]
    public void Unknown_values_become_drift_and_old_district_names_do_not()
    {
        var json = """
            {"rainfall_watch":[
              {"id":1,"name":"a","district":"Rukum","status":"N/A"},
              {"id":2,"name":"b","district":"","status":"FLOODING"},
              {"id":3,"name":"c","district":"Atlantis","averages":[{"interval":"x","value":1}]}],
             "river_watch":[{"id":4,"name":"d","district":"Nawalparasi  ","warning_level":"high","steady":"SIDEWAYS"}]}
            """u8.ToArray();

        var snapshot = DhmGaugeParser.Parse(json, Fetched);

        Assert.Equal(["district:Atlantis", "interval:x", "status:FLOODING", "trend:SIDEWAYS", "warning_level:high"], snapshot.Drift);
        Assert.All(snapshot.Rain, s => Assert.Null(s.District));
        Assert.Null(snapshot.Rivers[0].WarningLevelM);
    }

    [Fact]
    public void Missing_river_watch_throws()
    {
        Assert.Throws<JsonException>(() => DhmGaugeParser.Parse("""{"rainfall_watch":[]}"""u8.ToArray(), Fetched));
    }

    // The live feed's free-text fields have carried names and phone numbers; Scrub keeps only what
    // the parser reads. The committed fixture was made with the same field list.
    [Fact]
    public void Scrub_drops_free_text_fields_and_leaves_the_fixture_unchanged()
    {
        var json = """{"type":3,"rainfall_watch":[{"id":1,"name":"a","description":"caretaker 9800000000","stationIndex":"x","onm":"y","images":[],"tags":[]}],"river_watch":[{"id":2,"name":"b","onm":"z","description":"d"}]}"""u8.ToArray();

        var scrubbed = Encoding.UTF8.GetString(DhmGaugeParser.Scrub(json).Span);

        Assert.Equal("""{"type":3,"rainfall_watch":[{"id":1,"name":"a"}],"river_watch":[{"id":2,"name":"b"}]}""", scrubbed);
        var fixture = Fixtures.Read(Capture);
        Assert.Equal(new Snapshot("t", new Uri("https://example.org/"), Fetched, System.Net.HttpStatusCode.OK, new System.Collections.Generic.Dictionary<string, string>(), fixture).Sha256,
            new Snapshot("t", new Uri("https://example.org/"), Fetched, System.Net.HttpStatusCode.OK, new System.Collections.Generic.Dictionary<string, string>(), DhmGaugeParser.Scrub(fixture)).Sha256);
    }
}

public sealed class DhmBulletinSourceTests
{
    private const string List = "dhm-mfd-three-days-forecast-latest-2026-09-25.json";
    private const string Bulletin = "dhm-mfd-three-days-forecast-1578-2026-09-25.json";
    private static readonly DateTimeOffset Fetched = new(2026, 9, 25, 18, 22, 19, TimeSpan.Zero);

    private static StubHandler Server() => new(request =>
        new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith("/three-days-forecast-latest", StringComparison.Ordinal) ? Fixtures.Read(List)
                : request.RequestUri.AbsolutePath.EndsWith("/three-days-forecast/1578", StringComparison.Ordinal) ? Fixtures.Read(Bulletin)
                : throw new InvalidOperationException(request.RequestUri.ToString())),
        });

    [Fact]
    public void Latest_list_gives_the_newest_issue()
    {
        var entry = DhmBulletinSource.ReadLatest(Fixtures.Read(List));

        Assert.Equal("1578", entry.Id);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 12, 15, 0, TimeSpan.Zero), entry.IssuedAt);
        Assert.Equal(5, entry.Images.Count);
    }

    [Theory]
    [InlineData("""{"id":"1"}""")]
    [InlineData("""[{"id":"../admin","issue_date":"2026-09-25T12:15:00.000Z"}]""")]
    [InlineData("""[{"id":"1","issue_date":"२०८३ आश्विन ०९"}]""")]
    [InlineData("[]")]
    public void A_list_of_another_shape_throws(string json)
    {
        Assert.Throws<JsonException>(() => DhmBulletinSource.ReadLatest(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public async Task Fetch_reads_the_english_sections_of_the_newest_bulletin()
    {
        var time = new FakeTimeProvider(Fetched);
        var store = new MemorySnapshotStore();
        using var http = new HttpClient(Server());

        var result = await new DhmBulletinSource(http, store, time).FetchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SourceStatus.Fresh, result.Health.Status);
        var bulletin = result.Value!;
        Assert.Equal("1578", bulletin.Id);
        Assert.Empty(bulletin.Drift);
        Assert.StartsWith("Mostly cloudy conditions prevail throughout the country.", bulletin.PresentWeather, StringComparison.Ordinal);
        Assert.StartsWith("The country is currently under the influence of monsoon winds", bulletin.Analysis, StringComparison.Ordinal);
        Assert.Contains("heavy to very heavy rainfall at a few places in Gandaki", bulletin.WarningAdvisory, StringComparison.Ordinal);
        Assert.StartsWith("2083 Asoj 09 (Friday)\nNight:", bulletin.Forecast, StringComparison.Ordinal);
        Assert.DoesNotContain("<", bulletin.Forecast, StringComparison.Ordinal);
        Assert.DoesNotContain("&nbsp;", bulletin.WarningAdvisory, StringComparison.Ordinal);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 12, 15, 0, TimeSpan.Zero), bulletin.Provenance.IssuedAt);
        Assert.Equal((SourceKind.Official, DhmBulletinSource.SourceId), (bulletin.Provenance.Kind, bulletin.Provenance.Source));
        Assert.Equal([DhmBulletinSource.ListSourceId, DhmBulletinSource.SourceId], store.Saved.Select(s => s.SourceId));
    }

    // Checked hourly: the bulletin is fetched again only when the newest id changes, and it goes
    // stale 14 h after its issue time (ADR-0011) even while the list is being fetched.
    [Fact]
    public async Task Same_id_is_not_fetched_again_and_goes_stale_14_hours_after_issue()
    {
        var time = new FakeTimeProvider(Fetched);
        var server = Server();
        using var http = new HttpClient(server);
        var source = new DhmBulletinSource(http, new MemorySnapshotStore(), time);
        var token = TestContext.Current.CancellationToken;

        await source.FetchAsync(token);
        time.Advance(TimeSpan.FromHours(1));
        var second = await source.FetchAsync(token);
        time.Advance(TimeSpan.FromHours(12)); // 15 h after the 12:15 issue
        var third = await source.FetchAsync(token);

        Assert.Equal(1, server.Seen.Count(u => u.AbsolutePath.EndsWith("/1578", StringComparison.Ordinal)));
        Assert.Equal(3, server.Seen.Count(u => u.AbsolutePath.EndsWith("-latest", StringComparison.Ordinal)));
        Assert.Equal(SourceStatus.Fresh, second.Health.Status);
        Assert.Equal(SourceStatus.Stale, third.Health.Status);
        Assert.Contains("issued", third.Health.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_section_is_drift()
    {
        using var http = new HttpClient(new StubHandler(request => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith("-latest", StringComparison.Ordinal)
                ? Fixtures.Read(List)
                : """{"present_weather_en":"Clear.","met_analysis_en":null,"national_forecast_en":"<p>Fine</p>"}"""u8.ToArray()),
        }));

        var result = await new DhmBulletinSource(http, new MemorySnapshotStore(), new FakeTimeProvider(Fetched)).FetchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SourceStatus.Drifting, result.Health.Status);
        Assert.Equal(["field:warning_advisory_en"], result.Value!.Drift);
        Assert.Equal(("Clear.", null, "Fine"), (result.Value.PresentWeather, result.Value.Analysis, result.Value.Forecast));
    }
}
