using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Jharicast.Fetch;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jharicast.Tests.Fetch;

public sealed class SnapshotTests
{
    private static readonly Uri Url = new("https://a.example/feed");
    private static readonly DateTimeOffset At = new(2026, 9, 25, 17, 0, 5, TimeSpan.Zero);

    private static Snapshot Of(string body, string sourceId = "test.feed", DateTimeOffset? at = null) =>
        new(sourceId, Url, at ?? At, HttpStatusCode.OK, new Dictionary<string, string> { ["Content-Type"] = "application/json" }, Encoding.UTF8.GetBytes(body));

    [Fact]
    public void Json_bodies_that_differ_only_in_key_order_and_spacing_hash_equal()
    {
        var a = Of("""{"real_result":{"rain_fall":[{"level_id":"3","area_name":"Jhapa"}]},"data":[]}""");
        var b = Of("""
            {
              "data": [ ],
              "real_result": { "rain_fall": [ { "area_name": "Jhapa", "level_id": "3" } ] }
            }
            """);

        Assert.Equal(a.Sha256, b.Sha256);
        Assert.Equal(64, a.Sha256.Length);
    }

    [Theory]
    [InlineData("""{"level_id":"4","area_name":"Jhapa"}""")]
    [InlineData("""{"level_id":3,"area_name":"Jhapa"}""")]
    [InlineData("""[{"area_name":"Jhapa","level_id":"3"}]""")]
    [InlineData("""{"area_name":"Jhapa","level_id":"3.0"}""")]
    public void A_real_change_changes_the_hash(string changed)
    {
        Assert.NotEqual(Of("""{"area_name":"Jhapa","level_id":"3"}""").Sha256, Of(changed).Sha256);
    }

    [Fact]
    public void Numbers_keep_their_text_so_a_reformatted_number_is_a_change()
    {
        Assert.NotEqual(Of("""{"mm":1.10}""").Sha256, Of("""{"mm":1.1}""").Sha256);
    }

    [Fact]
    public void A_body_that_is_not_json_is_hashed_as_it_is()
    {
        Assert.NotEqual(Of("<html> a </html>").Sha256, Of("<html>a</html>").Sha256);
        Assert.Equal(Of("<html>a</html>").Sha256, Of("<html>a</html>").Sha256);
    }

    [Theory]
    [InlineData("")]
    [InlineData("DHM.Warnings")]
    [InlineData("../escape")]
    [InlineData("..")]
    [InlineData("dhm/warnings")]
    public void Source_ids_must_be_safe_directory_names(string sourceId)
    {
        Assert.Throws<ArgumentException>(() => Of("{}", sourceId));
    }

    [Fact]
    public void Staleness_comes_from_the_time_of_the_last_change()
    {
        var now = At;
        var cadence = TimeSpan.FromHours(6);

        Assert.Equal(SourceStatus.Fresh, SourceHealth.FromLastChange(now.AddHours(-12), cadence, now).Status);
        var stale = SourceHealth.FromLastChange(now.AddHours(-12.5), cadence, now);
        Assert.Equal(SourceStatus.Stale, stale.Status);
        Assert.Equal("unchanged for 12.5 h, expected every 6 h", stale.Detail);
        Assert.Equal(now.AddHours(-12.5), stale.LastChangeAt);
        Assert.Equal(SourceStatus.Fresh, SourceHealth.FromLastChange(null, cadence, now).Status);
    }
}

public sealed class FileSnapshotStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jharicast-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Snapshot Of(string body, DateTimeOffset at) =>
        new("dhm.warnings", new Uri("https://a.example/feed"), at, HttpStatusCode.OK, new Dictionary<string, string> { ["ETag"] = "\"v1\"" }, Encoding.UTF8.GetBytes(body));

    [Fact]
    public async Task Saves_under_date_and_source_and_reads_back_the_latest()
    {
        var store = new FileSnapshotStore(_root);
        var ct = TestContext.Current.CancellationToken;
        var older = Of("""{"n":1}""", new DateTimeOffset(2026, 9, 24, 23, 59, 0, TimeSpan.Zero));
        var newer = Of("""{"n":2}""", new DateTimeOffset(2026, 9, 25, 5, 30, 0, new TimeSpan(5, 45, 0))); // 23:45 UTC on the 24th

        await store.SaveAsync(newer, ct);
        await store.SaveAsync(older, ct);

        var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal([$"2026/09/24/dhm.warnings/234500-{newer.Sha256[..8]}.json.gz", $"2026/09/24/dhm.warnings/235900-{older.Sha256[..8]}.json.gz"], files);

        var latest = await store.LatestAsync("dhm.warnings", ct);
        Assert.NotNull(latest);
        Assert.Equal(older.Sha256, latest.Sha256);
        Assert.Equal(older.FetchedAt, latest.FetchedAt);
        Assert.Equal(older.Url, latest.Url);
        Assert.Equal("\"v1\"", latest.Headers["etag"]);
        Assert.Equal(older.Body.ToArray(), latest.Body.ToArray());
    }

    [Fact]
    public async Task Is_append_only_and_empty_for_an_unknown_source()
    {
        var store = new FileSnapshotStore(_root);
        var ct = TestContext.Current.CancellationToken;
        var at = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);

        await store.SaveAsync(Of("""{"n":1}""", at), ct);
        await store.SaveAsync(Of("""{"n":1}""", at), ct); // identical: stored once
        await store.SaveAsync(Of("""{"n":2}""", at), ct);

        Assert.Equal(2, Directory.EnumerateFiles(_root, "*.json.gz", SearchOption.AllDirectories).Count());
        Assert.Null(await store.LatestAsync("bipad.alerts", ct));
    }
}

public sealed class ConditionalGetTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));

    private PoliteHttpOptions Options() => new() { UserAgent = PoliteHttpHandlerTests.UserAgent, TimeProvider = _time };

    [Fact]
    public async Task A_304_returns_the_cached_body()
    {
        var conditions = new List<string?>();
        var server = new FakeServer(_time, (request, index) =>
        {
            conditions.Add(request.Headers.IfNoneMatch.SingleOrDefault()?.Tag);
            return Task.FromResult(index == 0 ? Ok("""{"v":1}""", "\"abc\"") : new HttpResponseMessage(HttpStatusCode.NotModified));
        });
        using var client = new HttpClient(new PoliteHttpHandler(Options(), server));
        var ct = TestContext.Current.CancellationToken;
        var uri = new Uri("https://a.example/feed");

        var first = await _time.Drive(client.GetStringAsync(uri, ct));
        using var second = await _time.Drive(client.GetAsync(uri, ct));

        Assert.Equal([null, "\"abc\""], conditions);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("""{"v":1}""", first);
        Assert.Equal(first, await second.Content.ReadAsStringAsync(ct));
        Assert.Equal("\"abc\"", second.Headers.ETag?.Tag);
    }

    [Fact]
    public async Task Last_modified_is_sent_back_as_if_modified_since()
    {
        var lastModified = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var sent = new List<DateTimeOffset?>();
        var server = new FakeServer(_time, (request, index) =>
        {
            sent.Add(request.Headers.IfModifiedSince);
            return Task.FromResult(Ok("bulletin", etag: null, lastModified));
        });
        using var client = new HttpClient(new PoliteHttpHandler(Options(), server));
        var ct = TestContext.Current.CancellationToken;

        await _time.Drive(client.GetStringAsync(new Uri("https://a.example/bulletin"), ct));
        await _time.Drive(client.GetStringAsync(new Uri("https://a.example/bulletin"), ct));

        Assert.Equal([null, lastModified], sent);
    }

    // DHM answers with Cache-Control: no-store. Nothing is kept, so nothing is sent back.
    [Fact]
    public async Task A_no_store_response_is_not_kept()
    {
        var conditional = 0;
        var server = new FakeServer(_time, (request, _) =>
        {
            conditional += request.Headers.IfNoneMatch.Count;
            return Task.FromResult(Ok("{}", "\"abc\"", noStore: true));
        });
        using var client = new HttpClient(new PoliteHttpHandler(Options(), server));
        var ct = TestContext.Current.CancellationToken;

        await _time.Drive(client.GetStringAsync(new Uri("https://a.example/feed"), ct));
        await _time.Drive(client.GetStringAsync(new Uri("https://a.example/feed"), ct));

        Assert.Equal(0, conditional);
    }

    private static HttpResponseMessage Ok(string body, string? etag, DateTimeOffset? lastModified = null, bool noStore = false)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        response.Content.Headers.LastModified = lastModified;
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        if (noStore)
        {
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        }

        return response;
    }
}
