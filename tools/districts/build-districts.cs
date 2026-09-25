// One-off: turns the COD-AB admin level 2 GeoJSON into src/Jharicast.Routing/Data/districts.json.
//
//   dotnet run tools/districts/build-districts.cs -- <npl_admin2.geojson> <districts.json>
//
// Input: npl_admin2.geojson from npl_admin_boundaries.geojson.zip on HDX (cod-ab-npl, v02),
// downloaded by hand. It is CC BY-IGO 3.0; see THIRD-PARTY-NOTICES.txt. Each ring is simplified
// with Douglas-Peucker at 0.002 degrees (about 200 m), coordinates rounded to 5 decimals, and
// each feature mapped to a district id through Gazetteer.TryResolve. The output keeps ids and
// rings only, rings as [longitude, latitude] pairs like GeoJSON.
#:project ../../src/Jharicast.Nepal/Jharicast.Nepal.csproj
#:property PublishAot=false

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jharicast.Nepal;

const double Tolerance = 0.002;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: build-districts.cs <npl_admin2.geojson> <districts.json>");
    return 2;
}

using var input = JsonDocument.Parse(File.ReadAllBytes(args[0]));
var districts = new SortedDictionary<string, List<double[][]>>(StringComparer.Ordinal);
var before = 0;
var after = 0;
foreach (var feature in input.RootElement.GetProperty("features").EnumerateArray())
{
    var name = feature.GetProperty("properties").GetProperty("adm2_name").GetString();
    if (!Gazetteer.TryResolve(name, out var district))
    {
        Console.Error.WriteLine($"unknown district '{name}'");
        return 1;
    }

    var geometry = feature.GetProperty("geometry");
    var polygons = geometry.GetProperty("type").GetString() switch
    {
        "Polygon" => [geometry.GetProperty("coordinates")],
        "MultiPolygon" => geometry.GetProperty("coordinates").EnumerateArray().ToArray(),
        var other => throw new InvalidDataException($"{name}: geometry {other}"),
    };

    var rings = districts.TryGetValue(district.Id, out var existing) ? existing : districts[district.Id] = [];
    foreach (var polygon in polygons)
    {
        var outer = polygon.EnumerateArray().First(); // no COD-AB v02 district has a hole
        var points = outer.EnumerateArray().Select(p => new[] { p[0].GetDouble(), p[1].GetDouble() }).ToArray();
        var simplified = Simplify(points, Tolerance);
        before += points.Length;
        after += simplified.Length;
        if (simplified.Length >= 4)
        {
            rings.Add(simplified);
        }
    }
}

if (districts.Count != Gazetteer.Districts.Count)
{
    Console.Error.WriteLine($"expected {Gazetteer.Districts.Count} districts, got {districts.Count}");
    return 1;
}

using (var output = File.Create(args[1]))
using (var writer = new Utf8JsonWriter(output))
{
    writer.WriteStartArray();
    foreach (var (id, rings) in districts)
    {
        writer.WriteStartObject();
        writer.WriteString("id", id);
        writer.WriteStartArray("rings");
        foreach (var ring in rings)
        {
            writer.WriteStartArray();
            foreach (var p in ring)
            {
                writer.WriteStartArray();
                writer.WriteRawValue(Math.Round(p[0], 5).ToString("0.#####", CultureInfo.InvariantCulture));
                writer.WriteRawValue(Math.Round(p[1], 5).ToString("0.#####", CultureInfo.InvariantCulture));
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
}

Console.WriteLine($"{districts.Count} districts, {before} vertices in, {after} out, {new FileInfo(args[1]).Length} bytes");
return 0;

// Douglas-Peucker on a closed ring, planar in degrees. The ring's first point is kept as an
// anchor together with the point farthest from it, so the two halves simplify independently.
static double[][] Simplify(double[][] ring, double tolerance)
{
    var n = ring.Length - 1; // last point repeats the first
    var far = 0;
    for (var i = 1; i < n; i++)
    {
        if (Distance(ring[i], ring[0]) > Distance(ring[far], ring[0]))
        {
            far = i;
        }
    }

    var keep = new bool[ring.Length];
    keep[0] = keep[far] = keep[n] = true;
    Mark(ring, 0, far, tolerance, keep);
    Mark(ring, far, n, tolerance, keep);
    return ring.Where((_, i) => keep[i]).ToArray();
}

static void Mark(double[][] points, int first, int last, double tolerance, bool[] keep)
{
    var index = -1;
    var max = tolerance;
    for (var i = first + 1; i < last; i++)
    {
        var d = SegmentDistance(points[i], points[first], points[last]);
        if (d > max)
        {
            max = d;
            index = i;
        }
    }

    if (index >= 0)
    {
        keep[index] = true;
        Mark(points, first, index, tolerance, keep);
        Mark(points, index, last, tolerance, keep);
    }
}

static double Distance(double[] a, double[] b) => Math.Sqrt(((a[0] - b[0]) * (a[0] - b[0])) + ((a[1] - b[1]) * (a[1] - b[1])));

static double SegmentDistance(double[] p, double[] a, double[] b)
{
    var dx = b[0] - a[0];
    var dy = b[1] - a[1];
    var lengthSquared = (dx * dx) + (dy * dy);
    var t = lengthSquared == 0 ? 0 : Math.Clamp((((p[0] - a[0]) * dx) + ((p[1] - a[1]) * dy)) / lengthSquared, 0, 1);
    return Distance(p, [a[0] + (t * dx), a[1] + (t * dy)]);
}
