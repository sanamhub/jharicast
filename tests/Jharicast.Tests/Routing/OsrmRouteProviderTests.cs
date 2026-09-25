using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jharicast.Routing;
using Jharicast.Tests.Nepal;
using Xunit;

namespace Jharicast.Tests.Routing;

public sealed class OsrmRouteProviderTests
{
    private static readonly Uri Local = new("http://localhost:5000/");

    // The Route object example from OSRM's HTTP docs (90 m, 300 s, four points along 10 N), with its
    // GeoJSON line encoded as polyline6, the shape this provider asks for.
    [Fact]
    public async Task Reads_the_docs_example()
    {
        var stub = StubHandler.Json(_ => Fixtures.Read("osrm-route-docs-example.json"));
        using var http = new HttpClient(stub);

        var route = await new OsrmRouteProvider(http, Local).GetRouteAsync([new GeoPoint(10, 120), new GeoPoint(10, 120.3)], TestContext.Current.CancellationToken);

        Assert.Equal([new GeoPoint(10, 120), new GeoPoint(10, 120.1), new GeoPoint(10, 120.2), new GeoPoint(10, 120.3)], route.Points);
        Assert.Equal(0.09, route.DistanceKm, 9);
        Assert.Equal(TimeSpan.FromMinutes(5), route.Duration);
    }

    // Longitude first, invariant formatting, full overview in polyline6.
    [Fact]
    public async Task Request_puts_longitude_first_and_asks_for_polyline6()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var stub = StubHandler.Json(_ => Fixtures.Read("osrm-route-docs-example.json"));
            using var http = new HttpClient(stub);

            await new OsrmRouteProvider(http, new Uri("http://localhost:5000/osrm")).GetRouteAsync(
                [new GeoPoint(26.643393, 87.9914702), new GeoPoint(27.7172, 85.324), new GeoPoint(27.48, 83.28)], TestContext.Current.CancellationToken);

            Assert.Equal(
                "http://localhost:5000/osrm/route/v1/driving/87.99147,26.643393;85.324,27.7172;83.28,27.48?overview=full&geometries=polyline6",
                Assert.Single(stub.Seen).AbsoluteUri);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public async Task An_osrm_error_code_is_reported_with_its_message()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"code":"NoSegment","message":"Could not find a matching segment for coordinate 0"}"""),
        }));

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => new OsrmRouteProvider(http, Local).GetRouteAsync([new GeoPoint(0, 0), new GeoPoint(1, 1)], TestContext.Current.CancellationToken));

        Assert.Equal("OSRM: NoSegment: Could not find a matching segment for coordinate 0", e.Message);
    }

    [Fact]
    public async Task A_non_json_error_is_an_http_error()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>bad gateway</html>") }));

        await Assert.ThrowsAsync<HttpRequestException>(() => new OsrmRouteProvider(http, Local).GetRouteAsync([new GeoPoint(0, 0), new GeoPoint(1, 1)], TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""{"code":"Ok","routes":[]}""")]
    [InlineData("""{"code":"Ok","routes":[{"geometry":{"type":"LineString"},"distance":1,"duration":1}]}""")]
    [InlineData("""{"code":"Ok","routes":[{"geometry":"_","distance":1,"duration":1}]}""")]
    public void A_response_of_another_shape_throws(string json)
    {
        Assert.Throws<JsonException>(() => OsrmRouteProvider.Read(System.Text.Encoding.UTF8.GetBytes(json), null));
    }

    [Fact]
    public async Task Needs_two_to_a_hundred_waypoints()
    {
        using var http = new HttpClient(StubHandler.Json(_ => []));
        var provider = new OsrmRouteProvider(http, Local);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetRouteAsync([new GeoPoint(0, 0)], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.GetRouteAsync([.. Enumerable.Repeat(new GeoPoint(0, 0), 101)], TestContext.Current.CancellationToken));
    }
}
