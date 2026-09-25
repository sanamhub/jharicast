using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Jharicast.Fetch;
using Jharicast.Nepal;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jharicast.Tests.Nepal;

public sealed class BipadSourceTests
{
    private static readonly DateTimeOffset Fetched = new(2026, 9, 25, 18, 28, 0, TimeSpan.Zero);

    private static byte[] Page(int from, int count) => Encoding.UTF8.GetBytes(
        $$"""{"count":9223372036854775807,"next":null,"results":[{{string.Join(',', Enumerable.Range(from, count).Select(i => $$"""{"id":{{i}},"title":"Heavy Rainfall at Place-{{i}}, Kaski","hazard":14,"point":{"type":"Point","coordinates":[83.9,28.2]},"startedOn":"2026-09-25T20:00:00+05:45","expireOn":null,"verified":true,"description":"station keeper 9800000000","referenceData":"{}"}"""))}}]}""");

    // Capture of 2026-09-25 18:27 UTC, first page of 20, scrubbed to the parsed fields.
    [Fact]
    public void Alert_capture_parses_with_districts_from_titles()
    {
        var snapshot = BipadAlertSource.Parse(Fixtures.Read("bipad-alert-2026-09-25.json"), Fetched);

        Assert.Empty(snapshot.Drift);
        Assert.Equal(20, snapshot.Alerts.Count);
        Assert.All(snapshot.Alerts, a => Assert.NotNull(a.District));
        var phalewas = snapshot.Alerts.Single(a => a.Id == 46036);
        Assert.Equal(("parbat", 14, "rain"), (phalewas.District!.Id, phalewas.HazardId!.Value, phalewas.ReferenceType));
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T14:15:04.036016Z", CultureInfo.InvariantCulture), phalewas.StartedAt); // 20:00:04.036016+05:45
        Assert.Null(phalewas.ExpiresAt);
        Assert.Equal(SourceKind.Official, snapshot.Provenance.Kind);
    }

    [Fact]
    public void Active_alerts_are_started_and_unexpired_and_open_ended_ones_last_a_day()
    {
        var snapshot = BipadAlertSource.Parse(Fixtures.Read("bipad-alert-2026-09-25.json"), Fetched);

        var active = snapshot.ActiveAt(Fetched);

        Assert.Contains(active, a => a.Id == 46036); // started 14:15 UTC, no expiry
        Assert.DoesNotContain(active, a => a.Id == 45961); // expired 2026-09-23
        Assert.DoesNotContain(snapshot.ActiveAt(Fetched.AddHours(-1)), a => a.Id == 46051); // starts 18:15 UTC
        Assert.DoesNotContain(snapshot.ActiveAt(Fetched.AddDays(2)), a => a.Id == 46036);
    }

    [Fact]
    public void Incident_capture_parses()
    {
        var snapshot = BipadIncidentSource.Parse(Fixtures.Read("bipad-incident-2026-09-25.json"), Fetched);

        Assert.Empty(snapshot.Drift);
        Assert.Equal(20, snapshot.Incidents.Count);
        var fire = snapshot.Incidents.Single(i => i.Id == 64504);
        Assert.Equal(new GeoPoint(28.09534, 81.84765), fire.Location);
        Assert.Equal(new DateTimeOffset(2023, 6, 1, 18, 15, 0, TimeSpan.Zero), fire.OccurredAt);
    }

    // count is the 64-bit maximum, so paging stops on the first short page.
    [Fact]
    public async Task Pages_until_a_short_page_and_ignores_count()
    {
        var stub = new StubHandler(request =>
        {
            var offset = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query)["offset"]!, CultureInfo.InvariantCulture);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Page(offset, offset < 200 ? 100 : 37)) };
        });
        using var http = new HttpClient(stub);
        var store = new MemorySnapshotStore();

        var result = await new BipadAlertSource(http, store, new FakeTimeProvider(Fetched)).FetchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["0", "100", "200"], stub.Seen.Select(u => System.Web.HttpUtility.ParseQueryString(u.Query)["offset"]));
        Assert.All(stub.Seen, u => Assert.Contains("limit=100", u.Query, StringComparison.Ordinal));
        Assert.Equal(237, result.Value!.Alerts.Count);
        Assert.Equal(SourceStatus.Fresh, result.Health.Status);
        Assert.Null(result.Health.Detail);
        Assert.Single(store.Saved);
    }

    [Fact]
    public async Task Stops_at_the_page_limit_and_says_so()
    {
        var stub = StubHandler.Json(n => Page(n * 100, 100));
        using var http = new HttpClient(stub);

        var result = await new BipadAlertSource(http, new MemorySnapshotStore(), new FakeTimeProvider(Fetched)) { MaxPages = 2 }.FetchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, stub.Seen.Count);
        Assert.Equal(200, result.Value!.Alerts.Count);
        Assert.Contains("stopped after 2 pages", result.Health.Detail, StringComparison.Ordinal);
    }

    // Only parsed fields reach the store; free text such as the station description never does.
    [Fact]
    public async Task Stored_snapshot_keeps_only_parsed_fields()
    {
        var store = new MemorySnapshotStore();
        using var http = new HttpClient(StubHandler.Json(_ => Page(0, 3)));

        await new BipadAlertSource(http, store, new FakeTimeProvider(Fetched)).FetchAsync(TestContext.Current.CancellationToken);

        var body = Encoding.UTF8.GetString(Assert.Single(store.Saved).Body.Span);
        Assert.DoesNotContain("description", body, StringComparison.Ordinal);
        Assert.DoesNotContain("9800000000", body, StringComparison.Ordinal);
        Assert.DoesNotContain("referenceData", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_page_error_fails_the_fetch_without_a_partial_snapshot()
    {
        var stub = new StubHandler(request => request.RequestUri!.Query.Contains("offset=100", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Page(0, 100)) });
        using var http = new HttpClient(stub);
        var store = new MemorySnapshotStore();

        var result = await new BipadIncidentSource(http, store, new FakeTimeProvider(Fetched)).FetchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SourceStatus.Failing, result.Health.Status);
        Assert.Null(result.Value);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void Unknown_district_in_a_title_and_a_bad_point_are_drift()
    {
        var json = """{"results":[{"id":1,"title":"Flood at X-1, Atlantis","point":{"coordinates":[500,1]}}]}"""u8.ToArray();

        Assert.Equal(["district:Atlantis", "point:1"], BipadAlertSource.Parse(json, Fetched).Drift);
    }
}

public sealed class DorClosureSourceTests
{
    private const string Fixture = "dor-closures-synthetic.json";
    private static readonly DateTimeOffset Fetched = new(2026, 9, 26, 6, 0, 0, TimeSpan.Zero);
    private static readonly string[] Banned = ["contact", "phone", "mobile"];

    private static async Task<(SourceResult<DorClosureSnapshot> Result, MemorySnapshotStore Store)> FetchFixture(byte[]? body = null)
    {
        var store = new MemorySnapshotStore();
        using var http = new HttpClient(StubHandler.Json(_ => body ?? Fixtures.Read(Fixture)));
        var result = await new DorClosureSource(http, store, new FakeTimeProvider(Fetched)).FetchAsync(TestContext.Current.CancellationToken);
        return (result, store);
    }

    // AC-3.4: no DoR contact field survives parsing or snapshot storage.
    [Fact]
    public async Task No_contact_field_reaches_the_store_or_the_parsed_types()
    {
        var (result, store) = await FetchFixture();

        var stored = Encoding.UTF8.GetString(Assert.Single(store.Saved).Body.Span);
        Assert.All(Banned, word => Assert.DoesNotContain(word, stored, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Example", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("0000000000", stored, StringComparison.Ordinal);
        var properties = typeof(RoadClosure).GetProperties().Concat(typeof(DorClosureSnapshot).GetProperties()).Select(p => p.Name);
        Assert.All(properties, name => Assert.All(Banned, word => Assert.DoesNotContain(word, name, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(2, result.Value!.Closures.Count);
    }

    // Also for the record shape wrapped in a data array.
    [Fact]
    public async Task Contact_fields_are_dropped_from_a_data_wrapper_too()
    {
        var wrapped = Encoding.UTF8.GetBytes("""{"status":true,"data":""" + Encoding.UTF8.GetString(Fixtures.Read(Fixture)) + "}");

        var (result, store) = await FetchFixture(wrapped);

        var stored = Encoding.UTF8.GetString(Assert.Single(store.Saved).Body.Span);
        Assert.All(Banned, word => Assert.DoesNotContain(word, stored, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, result.Value!.Closures.Count);
    }

    // Times come without an offset and are Nepal time, UTC+05:45.
    [Fact]
    public async Task Times_are_converted_from_nepal_time_to_utc()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var (result, _) = await FetchFixture();

            var landslide = result.Value!.Closures[0];
            Assert.Equal(new DateTimeOffset(2026, 9, 26, 0, 45, 0, TimeSpan.Zero), landslide.StartsAt);
            Assert.Equal(TimeSpan.Zero, landslide.StartsAt!.Value.Offset);
            Assert.Equal(new DateTimeOffset(2026, 9, 27, 12, 15, 0, TimeSpan.Zero), landslide.ExpectedEndAt);
            Assert.Equal(new GeoPoint(27.19, 84.98), landslide.Location);
            Assert.Equal(("H01", "Landslide"), (landslide.RoadRefNo, landslide.Reason));
            Assert.Empty(result.Value.Drift);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public async Task Active_closures_are_those_not_yet_ended()
    {
        var (result, _) = await FetchFixture();

        var active = Assert.Single(result.Value!.ActiveAt(Fetched));
        Assert.Equal("H01", active.RoadRefNo); // H10 ended 03:30 UTC
        Assert.Equal(SourceKind.Official, result.Value.Provenance.Kind);
    }

    [Fact]
    public async Task Renamed_time_fields_show_as_drift()
    {
        var (result, _) = await FetchFixture("""[{"road_refno":"H01","latitude":27.1,"longitude":85.0,"startDate":"2026-09-26 06:30:00"}]"""u8.ToArray());

        Assert.Equal(SourceStatus.Drifting, result.Health.Status);
        Assert.Equal(["missing:start_time"], result.Value!.Drift);
    }

    [Fact]
    public void A_body_of_another_shape_throws()
    {
        Assert.Throws<JsonException>(() => DorClosureSource.Parse("""{"rows":[]}"""u8.ToArray(), Fetched));
    }
}

public sealed class NepalTimeTests
{
    [Fact]
    public void Nepal_time_is_utc_plus_5_45_and_days_follow_it()
    {
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 18, 15, 0, TimeSpan.Zero), NepalTime.ToUtc(new DateTime(2026, 9, 28, 0, 0, 0)));
        Assert.Equal(new DateOnly(2026, 9, 28), NepalTime.DateOf(new DateTimeOffset(2026, 9, 27, 18, 15, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 9, 27), NepalTime.DateOf(new DateTimeOffset(2026, 9, 27, 18, 14, 59, TimeSpan.Zero)));
    }
}
