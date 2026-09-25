using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Jharicast.Routing;

/// <summary>
/// Finds the Nepal district a point lies in, from the COD-AB district polygons embedded in this
/// package (Survey Department of Nepal via OCHA / HDX, CC BY-IGO 3.0, simplified to about 200 m;
/// THIRD-PARTY-NOTICES.txt). Ids are <c>Jharicast.Nepal</c> gazetteer ids such as <c>kaski</c>.
/// </summary>
/// <remarks>
/// Each district was simplified on its own, so neighbouring boundaries can leave slivers up to
/// about 200 m wide between them. A point in no polygon but within <see cref="ToleranceKm"/> of
/// one gets the nearest district, so a road on a boundary is never "outside Nepal". The same
/// tolerance applies at the national border, which errs towards applying Nepal's warnings.
/// </remarks>
public sealed class DistrictResolver
{
    private static readonly Lazy<Polygon[]> Polygons = new(Load);

    /// <summary>How far outside every polygon a point may be and still get the nearest district, km. Default 0.5.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Negative or not finite.</exception>
    public double ToleranceKm
    {
        get;
        init
        {
            if (!double.IsFinite(value) || value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The tolerance must be a finite distance, 0 or more.");
            }

            field = value;
        }
    } = 0.5;

    /// <summary>The ids of all districts in the embedded data, sorted.</summary>
    public static IReadOnlyList<string> DistrictIds { get; } = [.. Polygons.Value.Select(p => p.DistrictId).Distinct().Order(StringComparer.Ordinal)];

    /// <summary>The district a point lies in.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The district id, or null outside Nepal.</returns>
    public string? Resolve(GeoPoint point)
    {
        var polygons = Polygons.Value;
        foreach (var polygon in polygons)
        {
            if (polygon.Box.Contains(point, 0) && Geo.Contains(polygon.Ring, point))
            {
                return polygon.DistrictId;
            }
        }

        if (ToleranceKm == 0)
        {
            return null;
        }

        // About 111 km per degree of latitude; longitude degrees are shorter, so this box is wide enough.
        var margin = ToleranceKm / 111.0 / Math.Cos(point.Latitude * Math.PI / 180);
        string? nearest = null;
        var best = ToleranceKm;
        foreach (var polygon in polygons.Where(p => p.Box.Contains(point, margin)))
        {
            var ring = polygon.Ring;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var d = Geo.DistanceToSegmentKm(point, ring[j], ring[i]);
                if (d <= best)
                {
                    best = d;
                    nearest = polygon.DistrictId;
                }
            }
        }

        return nearest;
    }

    /// <summary>The districts a sampled route passes through, each once, in the order first reached.</summary>
    /// <param name="route">The route.</param>
    /// <returns>District ids; sample points outside Nepal are skipped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="route"/> is null.</exception>
    public IReadOnlyList<string> DistrictsCrossed(SampledRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var sample in route.Samples)
        {
            if (Resolve(sample.Point) is { } id && seen.Add(id))
            {
                ordered.Add(id);
            }
        }

        return ordered;
    }

    // districts.json: [{"id": "...", "rings": [[[lon, lat], ...], ...]}, ...]
    private static Polygon[] Load()
    {
        using var stream = typeof(DistrictResolver).Assembly.GetManifestResourceStream("Jharicast.Routing.districts.json")
            ?? throw new InvalidOperationException("The embedded district polygons are missing from Jharicast.Routing.");
        using var document = JsonDocument.Parse(stream);
        var polygons = new List<Polygon>();
        foreach (var district in document.RootElement.EnumerateArray())
        {
            var id = district.GetProperty("id").GetString()!;
            foreach (var ring in district.GetProperty("rings").EnumerateArray())
            {
                GeoPoint[] points = [.. ring.EnumerateArray().Select(p => new GeoPoint(p[1].GetDouble(), p[0].GetDouble()))];
                polygons.Add(new Polygon(id, points, Box.Of(points)));
            }
        }

        return [.. polygons];
    }

    private sealed record Polygon(string DistrictId, IReadOnlyList<GeoPoint> Ring, Box Box);

    private readonly record struct Box(double MinLat, double MaxLat, double MinLon, double MaxLon)
    {
        public static Box Of(IReadOnlyList<GeoPoint> ring) =>
            new(ring.Min(p => p.Latitude), ring.Max(p => p.Latitude), ring.Min(p => p.Longitude), ring.Max(p => p.Longitude));

        public bool Contains(GeoPoint p, double margin) =>
            p.Latitude >= MinLat - margin && p.Latitude <= MaxLat + margin && p.Longitude >= MinLon - margin && p.Longitude <= MaxLon + margin;
    }
}
