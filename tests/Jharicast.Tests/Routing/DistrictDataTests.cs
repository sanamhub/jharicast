using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Jharicast.Nepal;
using Xunit;

namespace Jharicast.Tests.Routing;

// Checks the embedded COD-AB district rings (task N02) against the gazetteer.
public sealed class DistrictDataTests
{
    private static readonly Assembly RoutingAssembly = Assembly.Load("Jharicast.Routing");
    private static readonly Dictionary<string, List<GeoPoint[]>> Rings = Load();

    [Fact]
    public void Every_gazetteer_district_has_rings_and_nothing_else_does()
    {
        Assert.Equal(Gazetteer.Districts.Select(d => d.Id).Order(StringComparer.Ordinal), Rings.Keys.Order(StringComparer.Ordinal));
        Assert.All(Rings.Values, rings => Assert.All(rings, ring => Assert.True(ring.Length >= 4)));
    }

    // Simplification must not move a boundary past a headquarters town, all of which sit well
    // inside their district.
    [Fact]
    public void Every_headquarters_town_is_inside_its_own_district_and_no_other()
    {
        Assert.All(Gazetteer.Towns.Where(t => t.IsHeadquarters), town =>
        {
            var containing = Rings.Where(r => r.Value.Any(ring => Geo.Contains(ring, town.Location))).Select(r => r.Key).ToArray();
            Assert.Equal([town.DistrictId], containing);
        });
    }

    [Fact]
    public void Embedded_file_stays_under_one_megabyte()
    {
        using var stream = RoutingAssembly.GetManifestResourceStream("Jharicast.Routing.districts.json");
        Assert.NotNull(stream);
        Assert.InRange(stream.Length, 1, 1024 * 1024);
    }

    private static Dictionary<string, List<GeoPoint[]>> Load()
    {
        using var stream = RoutingAssembly.GetManifestResourceStream("Jharicast.Routing.districts.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateArray().ToDictionary(
            d => d.GetProperty("id").GetString()!,
            d => d.GetProperty("rings").EnumerateArray()
                .Select(ring => ring.EnumerateArray().Select(p => new GeoPoint(p[1].GetDouble(), p[0].GetDouble())).ToArray())
                .ToList(),
            StringComparer.Ordinal);
    }
}
