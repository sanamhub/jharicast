using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Jharicast.Fetch;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jharicast.Tests.Fetch;

public sealed class PoliteHttpHandlerTests
{
    internal const string UserAgent = "Jharicast/0.1 (+https://github.com/sanamhub/jharicast; ops@example.org)";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));

    private PoliteHttpOptions Options(int maxRetries = 3) => new() { UserAgent = UserAgent, TimeProvider = _time, MaxRetries = maxRetries };

    [Theory]
    [InlineData("Jharicast/0.1 (ops@example.org)")]
    [InlineData("Jharicast/0.1 (+https://github.com/sanamhub/jharicast)")]
    [InlineData(" ")]
    public void User_agent_must_name_a_project_url_and_a_mailbox(string userAgent)
    {
        Assert.Throws<ArgumentException>(() => new PoliteHttpOptions { UserAgent = userAgent });
    }

    [Fact]
    public async Task Every_request_carries_the_configured_user_agent()
    {
        var server = FakeServer.Always(_time, HttpStatusCode.OK);
        using var client = new HttpClient(new PoliteHttpHandler(Options(), server));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");

        using var response = await _time.Drive(client.GetAsync(new Uri("https://a.example/data"), TestContext.Current.CancellationToken));

        Assert.Equal(UserAgent, server.Seen.Single().UserAgent);
        Assert.Equal(UserAgent, server.RobotsSeen.Single().UserAgent);
    }

    // AC-3.5: never two requests in flight to one host.
    [Fact]
    public async Task Two_concurrent_requests_to_one_host_are_serialised_and_spaced()
    {
        var release = new TaskCompletionSource();
        var server = new FakeServer(_time, async (_, index) =>
        {
            if (index == 0)
            {
                await release.Task;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(new PoliteHttpHandler(Options(), server));
        var ct = TestContext.Current.CancellationToken;

        var first = client.GetAsync(new Uri("https://a.example/1"), ct);
        var second = client.GetAsync(new Uri("https://a.example/2"), ct);
        await _time.DriveUntil(() => server.Count == 1);
        _time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50, ct);
        Assert.Equal(1, server.Count); // the second waits while the first is in flight

        var releasedAt = _time.GetUtcNow();
        release.SetResult();
        await _time.Drive(Task.WhenAll(first, second));

        var seen = server.Seen.List();
        Assert.Equal(1, server.MaxInFlight);
        Assert.InRange(seen[1].At - releasedAt, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.2));
    }

    [Fact]
    public async Task Host_override_sets_a_longer_interval()
    {
        var options = Options();
        options.HostOverrides["slow.example"] = TimeSpan.FromSeconds(5);
        var server = FakeServer.Always(_time, HttpStatusCode.OK);
        using var client = new HttpClient(new PoliteHttpHandler(options, server));
        var ct = TestContext.Current.CancellationToken;

        await _time.Drive(Task.WhenAll(client.GetAsync(new Uri("https://SLOW.example/1"), ct), client.GetAsync(new Uri("https://slow.example/2"), ct)));

        var seen = server.Seen.List();
        Assert.InRange(seen[1].At - seen[0].At, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5.2));
    }

    [Fact]
    public async Task Different_hosts_run_in_parallel()
    {
        var bothArrived = new TaskCompletionSource();
        var arrived = 0;
        var server = new FakeServer(_time, async (_, _) =>
        {
            if (System.Threading.Interlocked.Increment(ref arrived) == 2)
            {
                bothArrived.SetResult();
            }

            // Serialised requests would wait here forever; the real-time limit turns that into a failure.
            await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(new PoliteHttpHandler(Options(), server));
        var ct = TestContext.Current.CancellationToken;

        var responses = await _time.Drive(Task.WhenAll(client.GetAsync(new Uri("https://a.example/"), ct), client.GetAsync(new Uri("https://b.example/"), ct)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(2, server.MaxInFlight);
    }

    [Fact]
    public async Task Too_many_requests_with_retry_after_7_waits_7_seconds()
    {
        var server = new FakeServer(_time, (_, index) => Task.FromResult(index == 0 ? TooManyRequests(TimeSpan.FromSeconds(7)) : new HttpResponseMessage(HttpStatusCode.OK)));
        using var client = new HttpClient(new PoliteHttpHandler(Options(), server));

        using var response = await _time.Drive(client.GetAsync(new Uri("https://a.example/"), TestContext.Current.CancellationToken));

        var seen = server.Seen.List();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, seen.Count);
        Assert.InRange(seen[1].At - seen[0].At, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7.2));
    }

    private static HttpResponseMessage TooManyRequests(TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    [Fact]
    public async Task Server_errors_are_retried_three_times_with_growing_backoff()
    {
        var server = FakeServer.Always(_time, HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient(new PoliteHttpHandler(Options(), server));

        using var response = await _time.Drive(client.GetAsync(new Uri("https://a.example/"), TestContext.Current.CancellationToken));

        var seen = server.Seen.List();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(4, seen.Count);
        for (var retry = 0; retry < 3; retry++)
        {
            var gap = seen[retry + 1].At - seen[retry].At;
            var backoff = Math.Pow(2, retry);
            Assert.InRange(gap.TotalSeconds, backoff, (backoff * 1.5) + 0.2);
        }
    }

    [Fact]
    public async Task Connection_errors_are_retried_then_thrown()
    {
        var server = new FakeServer(_time, (_, _) => throw new HttpRequestException("connection refused"));
        using var client = new HttpClient(new PoliteHttpHandler(Options(maxRetries: 1), server));

        await Assert.ThrowsAsync<HttpRequestException>(() => _time.Drive(client.GetAsync(new Uri("https://a.example/"), TestContext.Current.CancellationToken)));
        Assert.Equal(2, server.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Refusals_and_missing_pages_are_not_retried(HttpStatusCode status)
    {
        var server = FakeServer.Always(_time, status);
        using var client = new HttpClient(new PoliteHttpHandler(Options(), server));

        using var response = await _time.Drive(client.GetAsync(new Uri("https://a.example/"), TestContext.Current.CancellationToken));

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, server.Count);
    }

    [Fact]
    public async Task Five_failures_open_the_breaker_until_it_expires()
    {
        var server = FakeServer.Always(_time, HttpStatusCode.BadGateway);
        using var client = new HttpClient(new PoliteHttpHandler(Options(maxRetries: 0), server));
        var ct = TestContext.Current.CancellationToken;
        var uri = new Uri("https://a.example/");

        for (var i = 0; i < 5; i++)
        {
            using var failed = await _time.Drive(client.GetAsync(uri, ct));
        }

        await Assert.ThrowsAsync<SourceUnavailableException>(() => _time.Drive(client.GetAsync(uri, ct)));
        Assert.Equal(5, server.Count);

        _time.Advance(TimeSpan.FromMinutes(10));
        using var probe = await _time.Drive(client.GetAsync(uri, ct));
        Assert.Equal(6, server.Count); // one request after the break; it failed, so the breaker is open again
        await Assert.ThrowsAsync<SourceUnavailableException>(() => _time.Drive(client.GetAsync(uri, ct)));
        Assert.Equal(6, server.Count);
    }

    [Fact]
    public async Task A_success_resets_the_failure_count()
    {
        var server = new FakeServer(_time, (_, index) => Task.FromResult(new HttpResponseMessage(index == 4 ? HttpStatusCode.OK : HttpStatusCode.BadGateway)));
        using var client = new HttpClient(new PoliteHttpHandler(Options(maxRetries: 0), server));
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 9; i++)
        {
            using var response = await _time.Drive(client.GetAsync(new Uri("https://a.example/"), ct));
        }

        Assert.Equal(9, server.Count); // 4 failures, a success, 4 failures: never 5 in a row
    }

    [Fact]
    public async Task An_operated_host_is_fetched_without_reading_its_robots_txt_and_others_still_obey_theirs()
    {
        var options = Options();
        options.OperatedHosts.Add("meteo.internal");
        var server = FakeServer.Always(_time, HttpStatusCode.OK, () => FakeServer.RobotsTxt("User-agent: *\nDisallow: /\n"));
        using var client = new HttpClient(new PoliteHttpHandler(options, server));
        var ct = TestContext.Current.CancellationToken;

        using var own = await _time.Drive(client.GetAsync(new Uri("http://meteo.internal:8080/v1/forecast"), ct));
        await Assert.ThrowsAsync<SourceUnavailableException>(() => _time.Drive(client.GetAsync(new Uri("https://api.example/v1/forecast"), ct)));

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(1, server.Count);
        Assert.Equal(["api.example"], server.RobotsSeen.Select(r => r.Uri.Host));
        Assert.Equal(UserAgent, Assert.Single(server.Seen).UserAgent);
    }

    [Fact]
    public async Task Disabled_hosts_are_never_requested()
    {
        var options = Options();
        options.DisabledHosts.Add("off.example");
        var server = FakeServer.Always(_time, HttpStatusCode.OK);
        using var client = new HttpClient(new PoliteHttpHandler(options, server));

        await Assert.ThrowsAsync<SourceUnavailableException>(() => client.GetAsync(new Uri("https://off.example/"), TestContext.Current.CancellationToken));
        Assert.Equal(0, server.Count);
        Assert.Empty(server.RobotsSeen);
    }

    [Fact]
    public async Task Only_get_and_head_are_sent()
    {
        var server = FakeServer.Always(_time, HttpStatusCode.OK);
        using var client = new HttpClient(new PoliteHttpHandler(Options(), server));
        var ct = TestContext.Current.CancellationToken;

        using var head = await _time.Drive(client.SendAsync(new HttpRequestMessage(HttpMethod.Head, new Uri("https://a.example/")), ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync(new Uri("https://b.example/"), new StringContent("x"), ct));

        Assert.Equal(1, server.Count);
    }
}
