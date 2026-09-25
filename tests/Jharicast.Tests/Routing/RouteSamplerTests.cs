using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.OpenMeteo;
using Jharicast.Routing;
using Jharicast.Tests.OpenMeteo;
using Xunit;

namespace Jharicast.Tests.Routing;

public sealed class RouteSamplerTests
{
    // About 49.4 km due east along 27.5 N, as two points: the sampler must fill in the rest.
    private static readonly RouteGeometry Straight = new([new GeoPoint(27.5, 83.0), new GeoPoint(27.5, 83.5)], 49.4, TimeSpan.FromHours(1));

    // Elevation as a function of distance from the start, so tests can place hills by km.
    private static Func<IReadOnlyList<GeoPoint>, CancellationToken, Task<IReadOnlyList<double>>> ByKm(Func<double, double> metres, List<int>? calls = null) =>
        (points, _) =>
        {
            calls?.Add(points.Count);
            return Task.FromResult<IReadOnlyList<double>>([.. points.Select(p => metres(Geo.DistanceKm(Straight.Points[0], p)))]);
        };

    [Fact]
    public async Task Samples_every_5_km_with_cumulative_distance_and_the_end_point()
    {
        var sampled = await new RouteSampler(ByKm(_ => 100)).SampleAsync(Straight, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(11, sampled.Samples.Count); // 0, 5, ..., 45 and the end
        Assert.Equal([0.0, 5, 10, 15, 20, 25, 30, 35, 40, 45], sampled.Samples.SkipLast(1).Select(s => Math.Round(s.Km, 3)));
        Assert.Equal(Geo.DistanceKm(Straight.Points[0], Straight.Points[1]), sampled.DistanceKm, 3); // interpolated points follow a rhumb line, not the great circle
        Assert.Equal(sampled.DistanceKm, sampled.Samples[^1].Km, 9);
        Assert.Equal(Straight.Points[^1], sampled.Samples[^1].Point);
        Assert.Equal(0, sampled.HillKm);
        Assert.All(sampled.Samples, s => Assert.False(s.IsHill));
    }

    // A 200 m pass between km 21 and 23: both sample points of that segment are at 100 m, so only
    // the 1 km profile inside the segment can find it.
    [Fact]
    public async Task A_pass_between_two_valley_samples_makes_its_segment_hill()
    {
        var sampled = await new RouteSampler(ByKm(km => km is > 20.5 and < 23.5 ? 300 : 100)).SampleAsync(Straight, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([20.0], sampled.Samples.Where(s => s.IsHill).Select(s => Math.Round(s.Km, 3)));
        Assert.Equal(5, sampled.HillKm, 3);
        Assert.Equal(100, sampled.Samples[4].ElevationM);
        Assert.Equal(100, sampled.Samples[5].ElevationM);
    }

    [Fact]
    public async Task Flat_ground_above_700_m_is_hill_and_the_thresholds_are_settings()
    {
        var high = await new RouteSampler(ByKm(_ => 800)).SampleAsync(Straight, cancellationToken: TestContext.Current.CancellationToken);
        var tuned = await new RouteSampler(ByKm(_ => 800)) { HillElevationM = 900 }.SampleAsync(Straight, cancellationToken: TestContext.Current.CancellationToken);
        var ramp = await new RouteSampler(ByKm(km => 100 + (km * 20))) { HillRangeM = 101 }.SampleAsync(Straight, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(high.DistanceKm, high.HillKm, 9);
        Assert.All(high.Samples, s => Assert.True(s.IsHill));
        Assert.Equal(0, tuned.HillKm);
        Assert.Equal(0, ramp.Samples.Take(5).Count(s => s.IsHill)); // 100 m per 5 km segment, under 101; km 30 on is above 700
    }

    [Fact]
    public async Task Elevations_are_asked_for_once_for_the_whole_profile()
    {
        var calls = new List<int>();

        await new RouteSampler(ByKm(_ => 100, calls)).SampleAsync(Straight, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([51], calls); // 0 to 49 km every 1 km, plus the end
    }

    [Fact]
    public async Task A_rest_day_route_is_one_point()
    {
        var here = new GeoPoint(28.2096, 83.9856);

        var sampled = await new RouteSampler(ByKm(_ => 800)).SampleAsync(new RouteGeometry([here, here], 0, TimeSpan.Zero), cancellationToken: TestContext.Current.CancellationToken);

        var only = Assert.Single(sampled.Samples);
        Assert.True(only.IsHill);
        Assert.Equal((0.0, 0.0), (sampled.DistanceKm, sampled.HillKm));
    }

    [Fact]
    public async Task A_short_elevation_answer_is_refused()
    {
        var sampler = new RouteSampler((_, _) => Task.FromResult<IReadOnlyList<double>>([1.0]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => sampler.SampleAsync(Straight, cancellationToken: TestContext.Current.CancellationToken));
    }

    // The sampler takes OpenMeteoClient.GetElevationAsync as it is, 100 points per call (O02).
    [Fact]
    public async Task Works_with_the_open_meteo_elevation_client()
    {
        var handler = new OpenMeteoClientTests.RecordingHandler(uri =>
        {
            var count = System.Web.HttpUtility.ParseQueryString(uri.Query)["latitude"]!.Split(',').Length;
            return System.Text.Encoding.UTF8.GetBytes("{\"elevation\":[" + string.Join(',', Enumerable.Repeat("820.0", count)) + "]}");
        });
        using var http = new HttpClient(handler);
        var client = new OpenMeteoClient(http, new OpenMeteoOptions());
        var long150 = new RouteGeometry([new GeoPoint(27.5, 83.0), new GeoPoint(27.5, 84.52)], 150, TimeSpan.FromHours(3));

        var sampled = await new RouteSampler(client.GetElevationAsync).SampleAsync(long150, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count); // about 151 profile points
        Assert.Equal(sampled.DistanceKm, sampled.HillKm, 9);
    }
}
