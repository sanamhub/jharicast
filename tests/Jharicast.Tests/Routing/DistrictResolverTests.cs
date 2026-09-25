using System;
using System.Linq;
using Jharicast.Nepal;
using Jharicast.Routing;
using Xunit;

namespace Jharicast.Tests.Routing;

public sealed class DistrictResolverTests
{
    private static readonly DistrictResolver Resolver = new();

    [Theory]
    [InlineData(28.2096, 83.9856, "kaski")] // Pokhara
    [InlineData(28.05, 81.6167, "banke")] // Nepalgunj
    [InlineData(27.7172, 85.3240, "kathmandu")]
    public void Resolves_towns_from_the_task(double latitude, double longitude, string districtId)
    {
        Assert.Equal(districtId, Resolver.Resolve(new GeoPoint(latitude, longitude)));
    }

    [Theory]
    [InlineData(26.8467, 80.9462)] // Lucknow
    [InlineData(29.6500, 91.1000)] // Lhasa
    [InlineData(26.4000, 88.2000)] // Siliguri side of the Mechi, about 10 km out
    public void Points_outside_nepal_are_null(double latitude, double longitude)
    {
        Assert.Null(Resolver.Resolve(new GeoPoint(latitude, longitude)));
    }

    // Every town in the gazetteer, headquarters or not, lies in the district it is listed under.
    [Fact]
    public void Every_gazetteer_town_resolves_to_its_own_district()
    {
        Assert.All(Gazetteer.Towns, town => Assert.Equal(town.DistrictId, Resolver.Resolve(town.Location)));
    }

    [Fact]
    public void Ids_are_the_gazetteer_ids()
    {
        Assert.Equal(Gazetteer.Districts.Select(d => d.Id).Order(StringComparer.Ordinal), DistrictResolver.DistrictIds);
    }

    // Between Kaski and Myagdi the separately simplified rings leave a sliver: this point is in
    // neither, 55 m from Kaski's edge and 70 m from Myagdi's (found by a one-off search of the data).
    [Fact]
    public void A_point_in_a_sliver_gets_the_nearest_district_within_tolerance()
    {
        var sliver = new GeoPoint(28.611731, 83.865963);

        Assert.Null(new DistrictResolver { ToleranceKm = 0 }.Resolve(sliver));
        Assert.Equal("kaski", Resolver.Resolve(sliver));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DistrictResolver { ToleranceKm = -1 });
    }

    [Fact]
    public void Districts_crossed_are_distinct_and_in_route_order()
    {
        GeoPoint[] points = [new(28.2096, 83.9856), new(28.21, 83.99), new(27.87, 83.55), new(28.21, 83.98), new(26.8467, 80.9462)]; // Pokhara, Pokhara, Tansen, Pokhara, Lucknow
        var route = new SampledRoute([.. points.Select((p, i) => new RouteSample(p, i, 0, false))], points.Length, 0);

        Assert.Equal(["kaski", "palpa"], Resolver.DistrictsCrossed(route));
    }
}
