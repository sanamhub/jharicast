using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Fetch;
using Jharicast.Nepal;
using Jharicast.OpenMeteo;
using Jharicast.Routing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jharicast.Tests.Nepal;

/// <summary>
/// The assessor over every source faked: DHM from its 2026-09-24 capture, DoR from hand-written
/// records, OSRM from a synthetic Birtamod to Lumbini line along the East-West Highway, and
/// Open-Meteo from the report's section 7.2 numbers for a 28 Sep start.
/// </summary>
public sealed class NepalRouteAssessorTests
{
    private static readonly GeoPoint Birtamod = new(26.643393, 87.9914702);
    private static readonly GeoPoint Lumbini = new(27.4702049, 83.2852349);

    // 07:45 NPT on 24 Sep, when the DHM capture and the report's model run were taken.
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 2, 0, 0, TimeSpan.Zero);

    private static Route D1(DateOnly date) => new("jhapa-lumbini", [new Leg("D1", Birtamod, Lumbini, date)]);

    // Section 7.2, 28 Sep: day max on the road ECMWF 1 / GFS 10 / ICON 1 mm, gusts 35 / 31 / 24
    // km/h, GEFS 45% of members over 40 km/h at Lumbini (14 of 31). ECMWF ENS is not given for
    // that day; 12 of 51 members over 40 at Lumbini is our choice, so the pooled share is 26 of 82.
    private static double? Deterministic(string model, string variable, GeoPoint point, DateOnly day) => (model, variable) switch
    {
        ("ecmwf_ifs025", NepalRouteAssessor.RainVariable) => 1,
        ("gfs_seamless", NepalRouteAssessor.RainVariable) => 10,
        ("icon_seamless", NepalRouteAssessor.RainVariable) => 1,
        ("ecmwf_ifs025", _) => 35,
        ("gfs_seamless", _) => 31,
        _ => 24,
    };

    private static double? Member(string model, string variable, GeoPoint point, DateOnly day, int member)
    {
        if (variable == NepalRouteAssessor.RainVariable)
        {
            return member % 5;
        }

        var atLumbini = Geo.DistanceKm(point, Lumbini) < 1;
        var over = model == "gfs025" ? 14 : 12;
        return atLumbini ? (member < over ? 45 : 30) : 25;
    }

    // Hill between Narayanghat and Butwal (Daunne), plain elsewhere.
    private static double Elevation(GeoPoint point) => point.Longitude is > 83.5 and < 84.4 ? 800 : 100;

    private static byte[] NoClosures => "[]"u8.ToArray();

    private static NepalRouteAssessor Assessor(FakeTimeProvider time, byte[] closures, HttpMessageHandler? dhm = null, Func<int, byte[]>? route = null, List<Uri>? routeCalls = null, int horizon = 3) =>
        Assessor(time, closures, dhm, route, routeCalls, horizon, out _);

    private static NepalRouteAssessor Assessor(FakeTimeProvider time, byte[] closures, HttpMessageHandler? dhm, Func<int, byte[]>? route, List<Uri>? routeCalls, int horizon, out FakeOpenMeteo openMeteo)
    {
        openMeteo = new FakeOpenMeteo(new DateOnly(2026, 9, 24), Deterministic, Member, Elevation);
        var client = new OpenMeteoClient(new HttpClient(openMeteo), new OpenMeteoOptions { TimeProvider = time });
        var warnings = new DhmWarningsSource(new HttpClient(dhm ?? StubHandler.Json(_ => Fixtures.Read("dhm-getapidata-1-2026-09-24.json"))), new MemorySnapshotStore(), time) { Retired = false };
        var dor = new DorClosureSource(new HttpClient(StubHandler.Json(_ => closures)), new MemorySnapshotStore(), time);
        var osrmHandler = new StubHandler(request =>
        {
            routeCalls?.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent((route ?? (_ => Fixtures.Read("osrm-route-birtamod-lumbini-synthetic.json")))(0)) };
        });
        var osrm = new OsrmRouteProvider(new HttpClient(osrmHandler), new Uri("http://osrm.test/"));
        return new NepalRouteAssessor(client, warnings, dor, osrm, time) { OfficialHorizonDays = horizon };
    }

    private static FakeTimeProvider Clock() => new(Now);

    [Fact]
    public async Task Start_on_28_Sep_reproduces_the_reports_D1_verdict_wind_watch_only()
    {
        var result = await Assessor(Clock(), NoClosures).AssessAsync(D1(new DateOnly(2026, 9, 28)), RouteRuleSet.V1, CancellationToken.None);

        var leg = Assert.Single(result.Legs);
        var byRule = leg.Assessment.Results.ToDictionary(r => r.RuleId, r => r);
        Assert.Equal(RuleStatus.Watch, leg.Assessment.Status);
        Assert.Equal(RuleStatus.Watch, byRule["jharicast.gust.v1"].Status);
        Assert.Equal(RuleStatus.Pass, byRule["jharicast.hill-rain.v1"].Status);
        Assert.Equal(RuleStatus.Pass, byRule["jharicast.official-orange.v1"].Status);
        Assert.Equal(RuleStatus.Pass, byRule["jharicast.road-blocked.v1"].Status);

        Assert.Equal(new Dictionary<string, double> { ["ecmwf_ifs025"] = 1, ["gfs_seamless"] = 10, ["icon_seamless"] = 1 }, leg.Input.HillRain.Deterministic);
        Assert.Equal(new Dictionary<string, double> { ["ecmwf_ifs025"] = 35, ["gfs_seamless"] = 31, ["icon_seamless"] = 24 }, leg.Input.Gust.Deterministic);
        Assert.Equal(82, leg.Input.Gust.Members.Count);
        Assert.Equal(26.0 / 82, EnsembleStats.Exceedance(leg.Input.Gust.Members, 40), 10);
        Assert.True(leg.HillKm > 0 && leg.HillKm < leg.DistanceKm);
        Assert.Equal("jhapa", leg.Districts[0].Id);
        Assert.Equal("rupandehi", leg.Districts[^1].Id);
    }

    [Fact]
    public async Task Health_and_provenance_cover_every_input()
    {
        var result = await Assessor(Clock(), NoClosures).AssessAsync(D1(new DateOnly(2026, 9, 28)), RouteRuleSet.V1, CancellationToken.None);

        Assert.Equal(
            ["dhm.warnings", "dor.closures", "open-meteo.elevation", "open-meteo.ensemble.ecmwf_ifs025", "open-meteo.ensemble.gfs025", "open-meteo.forecast", "routing"],
            result.Health.Keys.Order(StringComparer.Ordinal));
        Assert.All(result.Health.Values, h => Assert.Equal(SourceStatus.Fresh, h.Status));
        Assert.Contains(result.Provenance, p => p.Source == "dhm.warnings" && p.Kind == SourceKind.Official);
        Assert.Contains(result.Provenance, p => p.Source == "open-meteo.forecast.icon_seamless" && p.Kind == SourceKind.Model);
        Assert.Contains(result.Provenance, p => p.Source == "open-meteo.ensemble.gfs025");
    }

    // The capture has Jhapa at Orange for rain. Current warnings apply to today and the next two
    // days (OfficialHorizonDays 3); 28 Sep is day five and has no official level.
    [Fact]
    public async Task Current_warnings_apply_within_the_horizon_only()
    {
        var assessor = Assessor(Clock(), NoClosures);

        var today = Assert.Single((await assessor.AssessAsync(D1(new DateOnly(2026, 9, 26)), RouteRuleSet.V1, CancellationToken.None)).Legs);
        var later = Assert.Single((await assessor.AssessAsync(D1(new DateOnly(2026, 9, 27)), RouteRuleSet.V1, CancellationToken.None)).Legs);

        Assert.Equal(AlertLevel.Orange, today.Input.OfficialLevels["jhapa"]);
        Assert.Equal(RuleStatus.Breach, today.Assessment.Results[0].Status);
        Assert.Empty(later.Input.OfficialLevels);
        Assert.Equal("no official warning", later.Assessment.Results[0].Reason);
    }

    [Fact]
    public async Task A_longer_horizon_carries_the_warnings_further()
    {
        var assessor = Assessor(Clock(), NoClosures, horizon: 5);

        var result = await assessor.AssessAsync(D1(new DateOnly(2026, 9, 28)), RouteRuleSet.V1, CancellationToken.None);

        Assert.Equal(RuleStatus.Breach, Assert.Single(result.Legs).Assessment.Status);
    }

    // On the road halfway between Itahari and Lahan. Distance is measured to the road between
    // samples, so this holds wherever the 5 km samples happen to fall.
    [Fact]
    public async Task A_closure_on_the_road_blocks_the_leg_until_it_ends()
    {
        var onRoad = """
            [{"road_refno":"H01","link_code":"H0100","closure_type":"Full","closure_reason":"Landslide","latitude":26.6915,"longitude":86.877,"date_roadblock_start":"2026-09-24 06:00:00","date_roadblock_end_estimated":"2026-09-25 18:00:00","date_roadblock_end":null}]
            """u8.ToArray();
        var ended = """
            [{"road_refno":"H01","link_code":"H0100","closure_type":"Full","closure_reason":"Landslide","latitude":26.6915,"longitude":86.877,"date_roadblock_start":"2026-09-23 06:00:00","date_roadblock_end_estimated":null,"date_roadblock_end":"2026-09-24 07:00:00"}]
            """u8.ToArray();
        var farAway = """
            [{"road_refno":"H13","link_code":"H1300","closure_type":"Full","closure_reason":"Landslide","latitude":29.27,"longitude":82.18,"date_roadblock_start":"2026-09-24 06:00:00","date_roadblock_end_estimated":null,"date_roadblock_end":null}]
            """u8.ToArray();
        var date = new DateOnly(2026, 9, 28);

        var blocked = Assert.Single((await Assessor(Clock(), onRoad).AssessAsync(D1(date), RouteRuleSet.V1, CancellationToken.None)).Legs);
        var open = Assert.Single((await Assessor(Clock(), ended).AssessAsync(D1(date), RouteRuleSet.V1, CancellationToken.None)).Legs);
        var elsewhere = Assert.Single((await Assessor(Clock(), farAway).AssessAsync(D1(date), RouteRuleSet.V1, CancellationToken.None)).Legs);

        Assert.True(blocked.Input.RoadBlocked);
        Assert.Equal("Landslide", Assert.Single(blocked.Closures).Reason);
        Assert.Equal(RuleStatus.Breach, blocked.Assessment.Status);
        Assert.False(open.Input.RoadBlocked);
        Assert.False(elsewhere.Input.RoadBlocked);
    }

    [Fact]
    public async Task A_failing_official_source_is_reported_in_health_not_thrown()
    {
        var result = await Assessor(Clock(), NoClosures, StubHandler.Status(HttpStatusCode.Forbidden)).AssessAsync(D1(new DateOnly(2026, 9, 24)), RouteRuleSet.V1, CancellationToken.None);

        Assert.Equal(SourceStatus.Failing, result.Health["dhm.warnings"].Status);
        Assert.Contains("403", result.Health["dhm.warnings"].Detail, StringComparison.Ordinal);
        Assert.Empty(Assert.Single(result.Legs).Input.OfficialLevels);
    }

    // The retired DHM feed (DhmWarningsSource) must never read as "no official warning".
    // The maps date each level: Jhapa Orange on 25 Sep only. 24 Sep and 26 Sep get no level, and
    // 27 Sep, past the bulletin's last day, has no official level at all.
    [Fact]
    public async Task Dated_warnings_apply_to_their_own_date_only()
    {
        var time = Clock();
        var jhapa = Gazetteer.Districts.Single(d => d.Id == "jhapa");
        DateOnly[] days = [new(2026, 9, 24), new(2026, 9, 25), new(2026, 9, 26)];
        var snapshot = new DhmWarningSnapshot(
            [new DistrictWarning(jhapa, Hazard.Unspecified, AlertLevel.Orange) { ValidOn = days[1] }],
            [],
            new Provenance("dhm.warnings", SourceKind.Official, Now))
        {
            Days = days,
        };
        var client = new OpenMeteoClient(new HttpClient(new FakeOpenMeteo(new DateOnly(2026, 9, 24), Deterministic, Member, Elevation)), new OpenMeteoOptions { TimeProvider = time });
        var dor = new DorClosureSource(new HttpClient(StubHandler.Json(_ => NoClosures)), new MemorySnapshotStore(), time);
        var osrm = new OsrmRouteProvider(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Fixtures.Read("osrm-route-birtamod-lumbini-synthetic.json")) })), new Uri("http://osrm.test/"));
        var assessor = new NepalRouteAssessor(client, new FixedWarnings(snapshot), dor, osrm, time);

        async Task<NepalLegDay> On(DateOnly date) => Assert.Single((await assessor.AssessAsync(D1(date), RouteRuleSet.V1, CancellationToken.None)).Legs);

        Assert.Empty((await On(days[0])).Input.OfficialLevels);
        Assert.Equal(AlertLevel.Orange, (await On(days[1])).Input.OfficialLevels["jhapa"]);
        Assert.Equal(RuleStatus.Breach, (await On(days[1])).Assessment.Results[0].Status);
        Assert.Empty((await On(days[2])).Input.OfficialLevels);
        var later = await On(new DateOnly(2026, 9, 27));
        Assert.Empty(later.Input.OfficialLevels);
        Assert.Equal("no official warning", later.Assessment.Results[0].Reason);
    }

    private sealed class FixedWarnings(DhmWarningSnapshot snapshot) : ISource<DhmWarningSnapshot>
    {
        public Task<SourceResult<DhmWarningSnapshot>> FetchAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new SourceResult<DhmWarningSnapshot>(snapshot, null, new SourceHealth(SourceStatus.Fresh, null, Now)));
    }

    [Fact]
    public async Task The_retired_dhm_feed_makes_the_official_rule_unknown()
    {
        var time = Clock();
        var stub = StubHandler.Json(_ => Fixtures.Read("dhm-getapidata-1-2026-09-24.json"));
        var client = new OpenMeteoClient(new HttpClient(new FakeOpenMeteo(new DateOnly(2026, 9, 24), Deterministic, Member, Elevation)), new OpenMeteoOptions { TimeProvider = time });
        var warnings = new DhmWarningsSource(new HttpClient(stub), new MemorySnapshotStore(), time);
        var dor = new DorClosureSource(new HttpClient(StubHandler.Json(_ => NoClosures)), new MemorySnapshotStore(), time);
        var osrm = new OsrmRouteProvider(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Fixtures.Read("osrm-route-birtamod-lumbini-synthetic.json")) })), new Uri("http://osrm.test/"));

        var result = await new NepalRouteAssessor(client, warnings, dor, osrm, time).AssessAsync(D1(new DateOnly(2026, 9, 24)), RouteRuleSet.V1, CancellationToken.None);

        var official = Assert.Single(result.Legs).Assessment.Results.Single(r => r.RuleId == RouteRuleSet.V1.OfficialRuleId);
        Assert.Equal(RuleStatus.Unknown, official.Status);
        Assert.Equal(SourceStatus.Disabled, result.Health["dhm.warnings"].Status);
        Assert.Empty(stub.Seen);
    }

    [Fact]
    public async Task Without_open_meteo_the_official_and_road_rules_still_answer_and_the_model_rules_say_unknown()
    {
        var time = Clock();
        var client = new OpenMeteoClient(new HttpClient(StubHandler.Status(HttpStatusCode.ServiceUnavailable)), new OpenMeteoOptions { TimeProvider = time });
        var warnings = new DhmWarningsSource(new HttpClient(StubHandler.Json(_ => Fixtures.Read("dhm-getapidata-1-2026-09-24.json"))), new MemorySnapshotStore(), time) { Retired = false };
        var dor = new DorClosureSource(new HttpClient(StubHandler.Json(_ => NoClosures)), new MemorySnapshotStore(), time);
        var osrm = new OsrmRouteProvider(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Fixtures.Read("osrm-route-birtamod-lumbini-synthetic.json")) })), new Uri("http://osrm.test/"));

        var result = await new NepalRouteAssessor(client, warnings, dor, osrm, time).AssessAsync(D1(new DateOnly(2026, 9, 24)), RouteRuleSet.V1, CancellationToken.None);

        var leg = Assert.Single(result.Legs);
        Assert.All(new[] { "open-meteo.elevation", "open-meteo.forecast", "open-meteo.ensemble.ecmwf_ifs025", "open-meteo.ensemble.gfs025" }, id => Assert.Equal(SourceStatus.Failing, result.Health[id].Status));
        Assert.Equal(leg.DistanceKm, leg.HillKm);
        Assert.Equal(RuleStatus.Unknown, leg.Assessment.Results.Single(r => r.RuleId == RouteRuleSet.V1.HillRain.Id).Status);
        Assert.Equal(RuleStatus.Unknown, leg.Assessment.Results.Single(r => r.RuleId == RouteRuleSet.V1.Gust.Id).Status);
        Assert.NotEqual(RuleStatus.Unknown, leg.Assessment.Results.Single(r => r.RuleId == RouteRuleSet.V1.OfficialRuleId).Status);
        Assert.Equal(RuleStatus.Pass, leg.Assessment.Results.Single(r => r.RuleId == RouteRuleSet.V1.RoadRuleId).Status);
    }

    [Fact]
    public async Task A_rest_day_asks_no_route_and_legs_sharing_a_road_ask_once()
    {
        var calls = new List<Uri>();
        var route = new Route("trip", [
            new Leg("D1", Birtamod, Lumbini, new DateOnly(2026, 9, 25)),
            new Leg("D2", Lumbini, Lumbini, new DateOnly(2026, 9, 26)),
            new Leg("D3", Birtamod, Lumbini, new DateOnly(2026, 9, 27)),
        ]);

        var result = await Assessor(Clock(), NoClosures, routeCalls: calls).AssessAsync(route, RouteRuleSet.V1, CancellationToken.None);

        Assert.Single(calls);
        Assert.Equal(["D1", "D2", "D3"], result.Legs.Select(l => l.Assessment.LegId));
        var rest = result.Legs[1];
        Assert.Equal(0, rest.DistanceKm);
        Assert.Equal("rupandehi", Assert.Single(rest.Districts).Id);
        Assert.Empty(rest.Input.HillRain.Deterministic); // Lumbini is on the plain: no hill point
    }

    [Theory]
    [InlineData(2026, 9, 23)]
    [InlineData(2026, 10, 10)]
    public async Task A_leg_outside_the_forecast_range_is_refused(int year, int month, int day)
    {
        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Assessor(Clock(), NoClosures).AssessAsync(D1(new DateOnly(year, month, day)), RouteRuleSet.V1, CancellationToken.None));

        Assert.Contains("2026-09-24 to 2026-10-09", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_meteo_is_asked_in_batches_with_the_nepal_time_zone()
    {
        var assessor = Assessor(Clock(), NoClosures, null, null, null, 3, out var openMeteo);

        await assessor.AssessAsync(D1(new DateOnly(2026, 9, 28)), RouteRuleSet.V1, CancellationToken.None);

        var models = openMeteo.Seen.Where(u => u.AbsolutePath != "/v1/elevation").ToArray();
        Assert.All(models, u => Assert.Contains("timezone=Asia%2FKathmandu", u.Query, StringComparison.Ordinal));
        Assert.All(models, u => Assert.Contains("forecast_days=5", u.Query, StringComparison.Ordinal));
        Assert.Contains(models, u => u.AbsolutePath == "/v1/ensemble" && u.Query.Contains("models=gfs025", StringComparison.Ordinal));
    }
}
