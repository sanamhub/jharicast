using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jharicast.Fetch;
using Jharicast.Nepal;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jharicast.Tests.Nepal;

public sealed class DhmWarningsParserTests
{
    // Copied from the reference tests unchanged.
    [Fact]
    public void Dhm_warning_fixture_parses_without_drift()
    {
        var fetched = new DateTimeOffset(2026, 9, 24, 17, 0, 0, TimeSpan.Zero);

        var snapshot = DhmWarningsParser.Parse(Fixtures.Read("dhm-getapidata-1-2026-09-24.json"), fetched);

        Assert.Empty(snapshot.Drift);
        Assert.Equal(39, snapshot.Warnings.Count(w => w.Hazard == Hazard.Rainfall));
        Assert.Equal(19, snapshot.Warnings.Count(w => w.Hazard == Hazard.Rainfall && w.Level == AlertLevel.Orange));
        Assert.True(Gazetteer.TryResolve("Jhapa", out var jhapa));
        Assert.Equal(AlertLevel.Orange, snapshot.LevelFor(jhapa, Hazard.Rainfall));
        Assert.Equal(SourceKind.Official, snapshot.Provenance.Kind);
    }

    // Copied from the reference tests unchanged.
    [Fact]
    public void Unknown_district_and_level_become_drift_not_exceptions()
    {
        var json = """{"real_result":{"rain_fall":[{"level_id":"3","area_name":"Atlantis"},{"level_id":"9","area_name":"Jhapa"}],"meteor":[]}}"""u8.ToArray();

        var snapshot = DhmWarningsParser.Parse(json, DateTimeOffset.UnixEpoch);

        Assert.Empty(snapshot.Warnings);
        Assert.Equal(["district:Atlantis", "hazard:meteor", "level:9"], snapshot.Drift);
    }

    // The capture sends level_id as a string; a number must mean the same.
    [Fact]
    public void Numeric_level_id_is_read_like_a_string_one()
    {
        var json = """{"real_result":{"rain_fall":[{"level_id":4,"area_name":"Kaski"}]}}"""u8.ToArray();

        var snapshot = DhmWarningsParser.Parse(json, DateTimeOffset.UnixEpoch);

        Assert.Empty(snapshot.Drift);
        var warning = Assert.Single(snapshot.Warnings);
        Assert.Equal(("kaski", Hazard.Rainfall, AlertLevel.Red), (warning.District.Id, warning.Hazard, warning.Level));
    }

    // A body without real_result is a different feed, not "no warnings" (ADR-0008).
    [Fact]
    public void Missing_real_result_throws()
    {
        Assert.Throws<JsonException>(() => DhmWarningsParser.Parse("""{"type":1,"data":[]}"""u8.ToArray(), DateTimeOffset.UnixEpoch));
    }
}

public sealed class DhmWarningsSourceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 17, 0, 0, TimeSpan.Zero);

    // Retired on 2026-10-06: the feed stopped updating, and a frozen Orange read as current.
    [Fact]
    public async Task By_default_the_retired_feed_sends_no_request_and_reports_disabled()
    {
        var stub = StubHandler.Json(_ => Fixtures.Read("dhm-getapidata-1-2026-09-24.json"));
        var store = new MemorySnapshotStore();
        using var http = new HttpClient(stub);

        var result = await new DhmWarningsSource(http, store, new FakeTimeProvider(Start)).FetchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SourceStatus.Disabled, result.Health.Status);
        Assert.Equal(DhmWarningsSource.RetiredDetail, result.Health.Detail);
        Assert.Null(result.Value);
        Assert.Empty(stub.Seen);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public async Task First_fetch_is_fresh_stored_and_official()
    {
        var time = new FakeTimeProvider(Start);
        var store = new MemorySnapshotStore();
        using var http = new HttpClient(StubHandler.Json(_ => Fixtures.Read("dhm-getapidata-1-2026-09-24.json")));

        var result = await new DhmWarningsSource(http, store, time) { Retired = false }.FetchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SourceStatus.Fresh, result.Health.Status);
        Assert.Equal(Start, result.Health.LastChangeAt);
        Assert.NotNull(result.Value);
        Assert.Equal(SourceKind.Official, result.Value.Provenance.Kind);
        Assert.Equal(DhmWarningsParser.SourceId, result.Value.Provenance.Source);
        Assert.Same(result.Snapshot, Assert.Single(store.Saved));
        Assert.Equal(DhmWarningsSource.DefaultUrl, result.Snapshot!.Url);
    }

    // ADR-0011: warnings are stale when unchanged for twice their 6 h cadence.
    [Fact]
    public async Task Unchanged_content_is_stored_once_and_goes_stale_after_12_hours()
    {
        var time = new FakeTimeProvider(Start);
        var store = new MemorySnapshotStore();
        using var http = new HttpClient(StubHandler.Json(_ => Fixtures.Read("dhm-getapidata-1-2026-09-24.json")));
        var source = new DhmWarningsSource(http, store, time) { Retired = false };
        var token = TestContext.Current.CancellationToken;

        await source.FetchAsync(token);
        time.Advance(TimeSpan.FromHours(11));
        var within = await source.FetchAsync(token);
        time.Advance(TimeSpan.FromHours(2));
        var after = await source.FetchAsync(token);

        Assert.Single(store.Saved);
        Assert.Equal(SourceStatus.Fresh, within.Health.Status);
        Assert.Equal(SourceStatus.Stale, after.Health.Status);
        Assert.Equal(Start, after.Health.LastChangeAt);
        Assert.NotNull(after.Value); // stale data is still returned, labelled
    }

    [Fact]
    public async Task Changed_content_is_stored_again_and_resets_the_age()
    {
        var time = new FakeTimeProvider(Start);
        var store = new MemorySnapshotStore();
        using var http = new HttpClient(StubHandler.Json(n => n == 0
            ? """{"real_result":{"rain_fall":[]}}"""u8.ToArray()
            : """{"real_result":{"rain_fall":[{"level_id":"3","area_name":"Jhapa"}]}}"""u8.ToArray()));
        var source = new DhmWarningsSource(http, store, time) { Retired = false };
        var token = TestContext.Current.CancellationToken;

        await source.FetchAsync(token);
        time.Advance(TimeSpan.FromHours(20));
        var result = await source.FetchAsync(token);

        Assert.Equal(2, store.Saved.Count);
        Assert.Equal(SourceStatus.Fresh, result.Health.Status);
        Assert.Equal(time.GetUtcNow(), result.Health.LastChangeAt);
    }

    [Fact]
    public async Task Drift_is_reported_with_the_value()
    {
        using var http = new HttpClient(StubHandler.Json(_ => """{"real_result":{"rain_fall":[{"level_id":"3","area_name":"Atlantis"}]}}"""u8.ToArray()));

        var result = await new DhmWarningsSource(http, new MemorySnapshotStore(), new FakeTimeProvider(Start)) { Retired = false }.FetchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SourceStatus.Drifting, result.Health.Status);
        Assert.Contains("district:Atlantis", result.Health.Detail, StringComparison.Ordinal);
        Assert.NotNull(result.Value);
    }

    [Fact]
    public async Task A_body_that_does_not_parse_is_failing_but_kept_for_inspection()
    {
        var store = new MemorySnapshotStore();
        using var http = new HttpClient(StubHandler.Json(_ => "<html>maintenance</html>"u8.ToArray()));

        var result = await new DhmWarningsSource(http, store, new FakeTimeProvider(Start)) { Retired = false }.FetchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SourceStatus.Failing, result.Health.Status);
        Assert.Null(result.Value);
        Assert.Single(store.Saved);
    }

    [Fact]
    public async Task An_error_status_is_failing_and_stores_nothing()
    {
        var store = new MemorySnapshotStore();
        using var http = new HttpClient(StubHandler.Status(HttpStatusCode.ServiceUnavailable));

        var result = await new DhmWarningsSource(http, store, new FakeTimeProvider(Start)) { Retired = false }.FetchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SourceStatus.Failing, result.Health.Status);
        Assert.Contains("503", result.Health.Detail, StringComparison.Ordinal);
        Assert.Null(result.Snapshot);
        Assert.Empty(store.Saved);
    }

    // The kill switch (ADR-0007) shows up as Disabled, and no request is sent.
    [Fact]
    public async Task A_disabled_host_is_reported_as_disabled()
    {
        var stub = StubHandler.Json(_ => Fixtures.Read("dhm-getapidata-1-2026-09-24.json"));
        var options = new PoliteHttpOptions { UserAgent = "Jharicast/test (+https://github.com/sanamhub/jharicast; test@example.org)" };
        options.DisabledHosts.Add(DhmWarningsSource.DefaultUrl.Host);
        using var http = new HttpClient(new PoliteHttpHandler(options, stub));

        var result = await new DhmWarningsSource(http, new MemorySnapshotStore(), new FakeTimeProvider(Start)) { Retired = false }.FetchAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SourceStatus.Disabled, result.Health.Status);
        Assert.Empty(stub.Seen);
    }
}
