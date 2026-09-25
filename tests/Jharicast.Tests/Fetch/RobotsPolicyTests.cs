using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Jharicast.Fetch;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jharicast.Tests.Fetch;

public sealed class RobotsPolicyTests
{
    // RFC 9309 section 5.1, "Simple Example".
    private const string RfcSimpleExample = """
        User-Agent: *
        Disallow: *.gif$
        Disallow: /example/
        Allow: /publications/

        User-Agent: foobot
        Disallow:/
        Allow:/example/page.html
        Allow:/example/allowed.gif

        User-Agent: barbot
        User-Agent: bazbot
        Disallow: /example/page.html

        User-Agent: quxbot

        EOF
        """;

    [Theory]
    [InlineData("foobot", "/example/page.html", true)]
    [InlineData("foobot", "/example/allowed.gif", true)]
    [InlineData("foobot", "/example/other.html", false)]
    [InlineData("foobot", "/", false)]
    [InlineData("barbot", "/example/page.html", false)]
    [InlineData("bazbot", "/example/page.html", false)]
    [InlineData("barbot", "/example/other.html", true)]
    [InlineData("quxbot", "/example/page.html", true)]
    [InlineData("otherbot", "/example/page.html", false)]
    [InlineData("otherbot", "/image.gif", false)]
    [InlineData("otherbot", "/image.gif?size=2", true)]
    [InlineData("otherbot", "/publications/2026.html", true)]
    [InlineData("FooBot/2.1 (+https://example.org/bot)", "/example/page.html", true)]
    public void Rfc_simple_example(string agent, string path, bool allowed)
    {
        Assert.Equal(allowed, RobotsPolicy.Parse(RfcSimpleExample).IsAllowed(agent, path));
    }

    // RFC 9309 section 5.2, "Longest Match".
    [Theory]
    [InlineData("/example/page/", true)]
    [InlineData("/example/page/disallowed.gif", false)]
    public void Rfc_longest_match(string path, bool allowed)
    {
        var policy = RobotsPolicy.Parse("""
            User-Agent: foobot
            Allow: /example/page/
            Disallow: /example/page/disallowed.gif
            """);

        Assert.Equal(allowed, policy.IsAllowed("foobot", path));
    }

    // RFC 9309 section 2.2.1: groups naming the same agent are merged.
    [Fact]
    public void Groups_for_the_same_agent_are_merged()
    {
        var policy = RobotsPolicy.Parse("""
            user-agent: ExampleBot
            disallow: /foo
            disallow: /bar

            user-agent: ExampleBot
            disallow: /baz
            """);

        Assert.False(policy.IsAllowed("examplebot", "/foo"));
        Assert.False(policy.IsAllowed("examplebot", "/baz/1"));
        Assert.True(policy.IsAllowed("examplebot", "/qux"));
    }

    [Fact]
    public void Equal_length_rules_resolve_to_allow()
    {
        var policy = RobotsPolicy.Parse("User-agent: *\nDisallow: /page\nAllow: /page");

        Assert.True(policy.IsAllowed("Jharicast", "/page"));
    }

    [Theory]
    [InlineData("/fish", false)]
    [InlineData("/fish/salmon.html", false)]
    [InlineData("/fishheads/yummy.html", true)]
    [InlineData("/Fish.asp", true)]
    [InlineData("/data/2026/report.json", false)]
    [InlineData("/data/2026/report.json.bak", true)]
    public void Wildcards_and_end_anchors_match(string path, bool allowed)
    {
        var policy = RobotsPolicy.Parse("""
            User-agent: *
            Disallow: /fish$
            Disallow: /fish/
            Disallow: /data/*.json$
            """);

        Assert.Equal(allowed, policy.IsAllowed("Jharicast", path));
    }

    [Fact]
    public void Percent_encoding_and_non_ascii_compare_equal()
    {
        var policy = RobotsPolicy.Parse("User-agent: *\nDisallow: /foo/bar/ツ\nDisallow: /%7Ejoe/");

        Assert.False(policy.IsAllowed("Jharicast", "/foo/bar/%E3%83%84"));
        Assert.False(policy.IsAllowed("Jharicast", "/~joe/index.html"));
    }

    [Fact]
    public void Empty_disallow_comments_and_rules_before_any_group_allow_everything()
    {
        var policy = RobotsPolicy.Parse("Disallow: /\n# a comment\nUser-agent: * # everyone\nDisallow:\nSitemap: https://example.org/sitemap.xml");

        Assert.True(policy.IsAllowed("Jharicast", "/anything"));
        Assert.True(RobotsPolicy.Parse(string.Empty).IsAllowed("Jharicast", "/anything"));
    }

    [Fact]
    public void Robots_txt_itself_is_always_allowed()
    {
        Assert.True(RobotsPolicy.DisallowAll.IsAllowed("Jharicast", "/robots.txt"));
        Assert.False(RobotsPolicy.DisallowAll.IsAllowed("Jharicast", "/"));
        Assert.True(RobotsPolicy.AllowAll.IsAllowed("Jharicast", "/"));
    }

    // GDACS publishes "Request-rate: 1/60": one request a minute.
    [Theory]
    [InlineData("Request-rate: 1/60", 60)]
    [InlineData("Request-rate: 2/1m", 30)]
    [InlineData("Request-rate: 1/1h", 3600)]
    [InlineData("Crawl-delay: 5", 5)]
    [InlineData("Crawl-delay: 2.5", 2.5)]
    [InlineData("Crawl-delay: 5\nRequest-rate: 1/60", 60)]
    public void Crawl_delay_and_request_rate_set_the_interval(string line, double seconds)
    {
        var policy = RobotsPolicy.Parse("User-agent: *\n" + line);

        Assert.Equal(TimeSpan.FromSeconds(seconds), policy.MinInterval("Jharicast"));
    }

    [Theory]
    [InlineData("Crawl-delay: soon")]
    [InlineData("Request-rate: 0/60")]
    [InlineData("Request-rate: fast")]
    public void Unreadable_intervals_are_ignored(string line)
    {
        Assert.Null(RobotsPolicy.Parse("User-agent: *\n" + line).MinInterval("Jharicast"));
    }
}

public sealed class PoliteHttpHandlerRobotsTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));

    private PoliteHttpOptions Options() => new() { UserAgent = PoliteHttpHandlerTests.UserAgent, TimeProvider = _time };

    [Fact]
    public async Task A_disallowed_path_is_never_requested()
    {
        var server = FakeServer.Always(_time, HttpStatusCode.OK, () => FakeServer.RobotsTxt("User-agent: jharicast\nDisallow: /private/"));
        using var client = new HttpClient(new PoliteHttpHandler(Options(), server));
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<SourceUnavailableException>(() => _time.Drive(client.GetAsync(new Uri("https://a.example/private/data"), ct)));
        using var open = await _time.Drive(client.GetAsync(new Uri("https://a.example/public/data"), ct));

        Assert.Equal(["/public/data"], server.Seen.Select(s => s.Uri.AbsolutePath));
    }

    [Fact]
    public async Task Robots_txt_is_fetched_once_per_host_per_day()
    {
        var server = FakeServer.Always(_time, HttpStatusCode.OK);
        using var client = new HttpClient(new PoliteHttpHandler(Options(), server));
        var ct = TestContext.Current.CancellationToken;

        await _time.Drive(Task.WhenAll(client.GetAsync(new Uri("https://a.example/1"), ct), client.GetAsync(new Uri("https://a.example/2"), ct)));
        await _time.Drive(client.GetAsync(new Uri("https://a.example/3"), ct));
        Assert.Single(server.RobotsSeen);

        _time.Advance(TimeSpan.FromHours(24));
        await _time.Drive(client.GetAsync(new Uri("https://a.example/4"), ct));
        await _time.Drive(client.GetAsync(new Uri("https://b.example/1"), ct));

        Assert.Equal(["a.example", "a.example", "b.example"], server.RobotsSeen.Select(s => s.Uri.Host));
        Assert.Equal(5, server.Count);
    }

    // RFC 9309 section 2.3.1.4: an unreachable robots.txt means nothing may be fetched.
    [Fact]
    public async Task A_server_error_on_robots_txt_disallows_everything_until_the_next_try()
    {
        var robotsStatus = HttpStatusCode.ServiceUnavailable;
        var server = FakeServer.Always(_time, HttpStatusCode.OK, () => new HttpResponseMessage(robotsStatus));
        using var client = new HttpClient(new PoliteHttpHandler(Options(), server));
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<SourceUnavailableException>(() => _time.Drive(client.GetAsync(new Uri("https://a.example/data"), ct)));
        Assert.Equal(0, server.Count);
        Assert.Equal(4, server.RobotsSeen.Count); // the first try and three retries

        robotsStatus = HttpStatusCode.NotFound;
        _time.Advance(TimeSpan.FromMinutes(10));
        using var response = await _time.Drive(client.GetAsync(new Uri("https://a.example/data"), ct));

        Assert.Equal(1, server.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_refused_robots_txt_disallows_everything(HttpStatusCode status)
    {
        var server = FakeServer.Always(_time, HttpStatusCode.OK, () => new HttpResponseMessage(status));
        using var client = new HttpClient(new PoliteHttpHandler(Options(), server));

        await Assert.ThrowsAsync<SourceUnavailableException>(() => _time.Drive(client.GetAsync(new Uri("https://a.example/data"), TestContext.Current.CancellationToken)));
        Assert.Equal(0, server.Count);
    }

    // AC-3.5: the handler honours Crawl-delay.
    [Fact]
    public async Task Crawl_delay_raises_the_interval_for_the_host()
    {
        var server = FakeServer.Always(_time, HttpStatusCode.OK, () => FakeServer.RobotsTxt("User-agent: *\nCrawl-delay: 10"));
        using var client = new HttpClient(new PoliteHttpHandler(Options(), server));
        var ct = TestContext.Current.CancellationToken;

        await _time.Drive(Task.WhenAll(client.GetAsync(new Uri("https://a.example/1"), ct), client.GetAsync(new Uri("https://a.example/2"), ct)));

        var seen = server.Seen.List();
        Assert.InRange(seen[1].At - seen[0].At, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10.2));
        Assert.InRange(seen[0].At - server.RobotsSeen.Single().At, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10.2));
    }
}
