using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace Jharicast.Tests;

public sealed class GeoTests
{
    private static readonly GeoPoint Kathmandu = new(27.7172, 85.3240);
    private static readonly GeoPoint Pokhara = new(28.2096, 83.9856);

    // The task asked for 140 km plus or minus 2. Haversine with R = 6371.0088 km gives 142.39 km,
    // which the spherical law of cosines confirms, so the test pins the computed value.
    [Fact]
    public void Kathmandu_to_Pokhara_is_142_km_as_the_crow_flies()
    {
        Assert.InRange(Geo.DistanceKm(Kathmandu, Pokhara), 142.3, 142.5);
        Assert.Equal(Geo.DistanceKm(Kathmandu, Pokhara), Geo.DistanceKm(Pokhara, Kathmandu), 10);
        Assert.Equal(0, Geo.DistanceKm(Kathmandu, Kathmandu));
    }

    [Theory]
    [InlineData(91, 0)]
    [InlineData(-90.5, 0)]
    [InlineData(0, 180.1)]
    [InlineData(0, -181)]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.NaN)]
    public void GeoPoint_rejects_coordinates_outside_their_range(double latitude, double longitude)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeoPoint(latitude, longitude));
    }

    // The example string from Google's "Encoded Polyline Algorithm Format" page.
    [Fact]
    public void DecodePolyline_reads_the_google_documentation_example()
    {
        var points = Geo.DecodePolyline("_p~iF~ps|U_ulLnnqC_mqNvxq`@", 5);

        Assert.Equal(3, points.Count);
        AssertPoint(38.5, -120.2, points[0]);
        AssertPoint(40.7, -120.95, points[1]);
        AssertPoint(43.252, -126.453, points[2]);
    }

    [Fact]
    public void DecodePolyline_with_precision_6_scales_by_ten()
    {
        var points = Geo.DecodePolyline("_p~iF~ps|U_ulLnnqC_mqNvxq`@", 6);

        AssertPoint(3.85, -12.02, points[0]);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void DecodePolyline_round_trips_a_nepal_route(int precision)
    {
        GeoPoint[] route = [new(26.66, 87.99), Kathmandu, Pokhara, new(27.48, 83.28)];

        var decoded = Geo.DecodePolyline(Encode(route, precision), precision);

        Assert.Equal(route.Length, decoded.Count);
        for (var i = 0; i < route.Length; i++)
        {
            AssertPoint(route[i].Latitude, route[i].Longitude, decoded[i]);
        }
    }

    // OSRM polyline6 read as precision 5 lands 10 times too far from the equator.
    [Fact]
    public void DecodePolyline_rejects_polyline6_read_as_precision_5()
    {
        var polyline6 = Encode([Kathmandu, Pokhara], 6);

        Assert.Throws<FormatException>(() => Geo.DecodePolyline(polyline6, 5));
    }

    [Theory]
    [InlineData("_p~iF~ps|")]
    [InlineData("_p~iF~ps|U_ulLnnqC_mqNvxq")]
    [InlineData("_p~iF ~ps|U")]
    public void DecodePolyline_rejects_a_truncated_or_invalid_string(string encoded)
    {
        Assert.Throws<FormatException>(() => Geo.DecodePolyline(encoded, 5));
    }

    [Fact]
    public void DecodePolyline_rejects_a_precision_other_than_5_or_6()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Geo.DecodePolyline("_p~iF~ps|U", 7));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Resampled_path_length_is_within_1_percent_of_the_original(int seed)
    {
        var path = WindingRoad(seed);

        var resampled = Geo.Resample(path, 5);

        var original = Geo.LengthKm(path);
        Assert.InRange(Math.Abs(Geo.LengthKm(resampled) - original) / original, 0, 0.01);
        Assert.Equal(path[0], resampled[0]);
        Assert.Equal(path[^1], resampled[^1]);
        Assert.Equal((int)Math.Floor(original / 5) + 2, resampled.Count);
        for (var i = 1; i < resampled.Count; i++)
        {
            Assert.True(Geo.DistanceKm(resampled[i - 1], resampled[i]) <= 5.0001);
        }
    }

    [Fact]
    public void Resample_keeps_a_single_point_and_rejects_a_bad_step()
    {
        Assert.Equal([Kathmandu], Geo.Resample([Kathmandu], 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => Geo.Resample([Kathmandu, Pokhara], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Geo.Resample([Kathmandu, Pokhara], double.NaN));
    }

    // A U shape: two arms and a base. The notch between the arms is outside, which a convex
    // test would get wrong. The 1,000 points are a Halton sequence: spread evenly like random
    // points, and the same on every run.
    [Fact]
    public void Contains_agrees_with_a_brute_force_check_on_a_concave_polygon()
    {
        GeoPoint[] ring = [new(26, 80), new(26, 90), new(30, 90), new(30, 87), new(27.5, 87), new(27.5, 83), new(30, 83), new(30, 80)];
        static bool BruteForce(GeoPoint p) =>
            (p.Longitude is >= 80 and <= 83 && p.Latitude is >= 26 and <= 30)
            || (p.Longitude is >= 87 and <= 90 && p.Latitude is >= 26 and <= 30)
            || (p.Longitude is >= 80 and <= 90 && p.Latitude is >= 26 and <= 27.5);

        var inside = 0;
        for (var i = 1; i <= 1000; i++)
        {
            // The odd offsets keep points off the polygon edges, where either answer is correct.
            var point = new GeoPoint(25.0137 + (Halton(i, 2) * 6), 79.0071 + (Halton(i, 3) * 12));
            var expected = BruteForce(point);
            Assert.True(expected == Geo.Contains(ring, point), $"({point.Latitude}, {point.Longitude})");
            inside += expected ? 1 : 0;
        }

        Assert.InRange(inside, 200, 600); // the sample covers both sides of the boundary
    }

    [Fact]
    public void Contains_accepts_a_closed_ring()
    {
        GeoPoint[] closed = [new(0, 0), new(0, 1), new(1, 1), new(1, 0), new(0, 0)];

        Assert.True(Geo.Contains(closed, new GeoPoint(0.5, 0.5)));
        Assert.False(Geo.Contains(closed, new GeoPoint(1.5, 0.5)));
        Assert.Throws<ArgumentException>(() => Geo.Contains([new(0, 0), new(1, 1)], new GeoPoint(0.5, 0.5)));
    }

    [Fact]
    public void DistanceToSegmentKm_measures_to_the_nearest_point_of_the_segment()
    {
        GeoPoint a = new(0, 0);
        GeoPoint b = new(0, 1);

        Assert.Equal(11.1195, Geo.DistanceToSegmentKm(new GeoPoint(0.1, 0.5), a, b), 3); // beside the middle
        Assert.Equal(Geo.DistanceKm(new GeoPoint(0, 2), b), Geo.DistanceToSegmentKm(new GeoPoint(0, 2), a, b), 6); // past the end
        Assert.Equal(Geo.DistanceKm(Kathmandu, Pokhara), Geo.DistanceToSegmentKm(Kathmandu, Pokhara, Pokhara), 6); // a point segment
    }

    // Van der Corput radical inverse of i in the given prime base: a value in (0, 1).
    private static double Halton(int i, int prime)
    {
        var result = 0.0;
        var fraction = 1.0 / prime;
        for (var n = i; n > 0; n /= prime)
        {
            result += fraction * (n % prime);
            fraction /= prime;
        }

        return result;
    }

    private static void AssertPoint(double latitude, double longitude, GeoPoint actual)
    {
        Assert.Equal(latitude, actual.Latitude, 6);
        Assert.Equal(longitude, actual.Longitude, 6);
    }

    // A road-like path from Birtamod west: a vertex about every 1 km, bending gently, with a
    // different bend pattern per seed.
    private static GeoPoint[] WindingRoad(int seed)
    {
        var points = new List<GeoPoint> { new(26.66, 87.99) };
        var heading = Math.PI;
        for (var i = 1; i <= 400; i++)
        {
            heading += (Halton(i + (seed * 1000), seed + 4) - 0.5) * 0.2;
            var last = points[^1];
            points.Add(new GeoPoint(last.Latitude + (0.009 * Math.Sin(heading)), last.Longitude + (0.01 * Math.Cos(heading))));
        }

        return [.. points];
    }

    // Google's encoding, used only to build test input for the decoder.
    private static string Encode(IEnumerable<GeoPoint> points, int precision)
    {
        var factor = Math.Pow(10, precision);
        var text = new StringBuilder();
        long lastLat = 0;
        long lastLon = 0;
        foreach (var p in points)
        {
            var lat = (long)Math.Round(p.Latitude * factor);
            var lon = (long)Math.Round(p.Longitude * factor);
            Append(text, lat - lastLat);
            Append(text, lon - lastLon);
            lastLat = lat;
            lastLon = lon;
        }

        return text.ToString();

        static void Append(StringBuilder text, long delta)
        {
            var value = delta < 0 ? ~(delta << 1) : delta << 1;
            while (value >= 0x20)
            {
                text.Append((char)((0x20 | (value & 0x1f)) + 63));
                value >>= 5;
            }

            text.Append((char)(value + 63));
        }
    }
}
