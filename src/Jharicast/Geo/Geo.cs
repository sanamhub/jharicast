using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jharicast;

/// <summary>
/// Spherical geometry for routes: distances, Google polyline decoding, resampling and point in
/// polygon (ADR-0010). Distances are in km on a sphere of the mean Earth radius, which differs
/// from the ellipsoid by at most about 0.5 percent, less than the error of a 5 km sample spacing.
/// </summary>
public static class Geo
{
    // Mean Earth radius (IUGG), km.
    private const double EarthRadiusKm = 6371.0088;

    /// <summary>Great-circle distance by the haversine formula.</summary>
    /// <param name="from">Start.</param>
    /// <param name="to">End.</param>
    /// <returns>Distance in km.</returns>
    public static double DistanceKm(GeoPoint from, GeoPoint to)
    {
        var lat1 = Radians(from.Latitude);
        var lat2 = Radians(to.Latitude);
        var sinLat = Math.Sin((lat2 - lat1) / 2);
        var sinLon = Math.Sin(Radians(to.Longitude - from.Longitude) / 2);
        var h = (sinLat * sinLat) + (Math.Cos(lat1) * Math.Cos(lat2) * sinLon * sinLon);
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    // Sum of segment distances, km. Internal until a package outside the core needs it.
    internal static double LengthKm(IReadOnlyList<GeoPoint> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var total = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            total += DistanceKm(path[i - 1], path[i]);
        }

        return total;
    }

    /// <summary>
    /// Decodes a Google encoded polyline. OSRM's <c>polyline6</c> uses precision 6; Google's
    /// default is 5. The wrong precision scales every coordinate by 10 and puts Nepal in the
    /// Indian Ocean, so pass the precision the server was asked for.
    /// </summary>
    /// <param name="encoded">The encoded string.</param>
    /// <param name="precision">5 or 6 decimal digits.</param>
    /// <returns>The points, in order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="encoded"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="precision"/> is not 5 or 6.</exception>
    /// <exception cref="FormatException">The string is truncated, has a character outside the encoding, or decodes outside valid coordinates.</exception>
    public static IReadOnlyList<GeoPoint> DecodePolyline(string encoded, int precision)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (precision is not (5 or 6))
        {
            throw new ArgumentOutOfRangeException(nameof(precision), precision, "Polyline precision is 5 (Google) or 6 (OSRM polyline6).");
        }

        var factor = precision == 5 ? 1e5 : 1e6;
        var points = new List<GeoPoint>();
        var index = 0;
        long lat = 0;
        long lon = 0;
        while (index < encoded.Length)
        {
            lat += NextDelta(encoded, ref index);
            lon += NextDelta(encoded, ref index);
            var latitude = lat / factor;
            var longitude = lon / factor;
            if (Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180)
            {
                throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"Polyline decodes to ({latitude}, {longitude}), outside valid coordinates. Check the precision."));
            }

            points.Add(new GeoPoint(latitude, longitude));
        }

        return points;
    }

    /// <summary>
    /// Points every <paramref name="stepKm"/> along a path, measured along the path, plus the last
    /// point. New points are interpolated linearly in latitude and longitude inside a segment,
    /// which is accurate for road segments a few km long.
    /// </summary>
    /// <param name="path">Points in order.</param>
    /// <param name="stepKm">Spacing, km, greater than 0.</param>
    /// <returns>The first point, one point per step, and the last point; a copy of the path when it has fewer than two points.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepKm"/> is not a positive, finite number.</exception>
    public static IReadOnlyList<GeoPoint> Resample(IReadOnlyList<GeoPoint> path, double stepKm)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!double.IsFinite(stepKm) || stepKm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stepKm), stepKm, "Step must be a positive, finite distance.");
        }

        if (path.Count < 2)
        {
            return [.. path];
        }

        var result = new List<GeoPoint> { path[0] };
        var nextAt = stepKm;
        var travelled = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            var a = path[i - 1];
            var b = path[i];
            var segment = DistanceKm(a, b);
            while (segment > 0 && travelled + segment >= nextAt)
            {
                var t = (nextAt - travelled) / segment;
                result.Add(new GeoPoint(a.Latitude + (t * (b.Latitude - a.Latitude)), a.Longitude + (t * (b.Longitude - a.Longitude))));
                nextAt += stepKm;
            }

            travelled += segment;
        }

        if (result[^1] != path[^1])
        {
            result.Add(path[^1]);
        }

        return result;
    }

    /// <summary>
    /// Whether a point is inside a polygon ring, by ray casting in longitude and latitude, after a
    /// bounding-box check. The ring may repeat its first point at the end or not. A point exactly
    /// on an edge can fall either way.
    /// </summary>
    /// <param name="ring">The ring's vertices, at least three.</param>
    /// <param name="point">The point.</param>
    /// <returns>True when inside.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ring"/> is null.</exception>
    /// <exception cref="ArgumentException">The ring has fewer than three vertices.</exception>
    public static bool Contains(IReadOnlyList<GeoPoint> ring, GeoPoint point)
    {
        ArgumentNullException.ThrowIfNull(ring);
        if (ring.Count < 3)
        {
            throw new ArgumentException("A ring needs at least three vertices.", nameof(ring));
        }

        if (!InBoundingBox(ring, point))
        {
            return false;
        }

        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var a = ring[i];
            var b = ring[j];
            if ((a.Latitude > point.Latitude) != (b.Latitude > point.Latitude)
                && point.Longitude < ((b.Longitude - a.Longitude) * (point.Latitude - a.Latitude) / (b.Latitude - a.Latitude)) + a.Longitude)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// Shortest distance from a point to a segment. Finds the nearest point on a local plane
    /// centred on the point, then measures to it by haversine; accurate to well under 1 percent
    /// for segments up to a few tens of km.
    /// </summary>
    /// <param name="point">The point.</param>
    /// <param name="segmentStart">Segment start.</param>
    /// <param name="segmentEnd">Segment end.</param>
    /// <returns>Distance in km.</returns>
    public static double DistanceToSegmentKm(GeoPoint point, GeoPoint segmentStart, GeoPoint segmentEnd)
    {
        var cosLat = Math.Cos(Radians(point.Latitude));
        var ax = (segmentStart.Longitude - point.Longitude) * cosLat;
        var ay = segmentStart.Latitude - point.Latitude;
        var dx = (segmentEnd.Longitude - segmentStart.Longitude) * cosLat;
        var dy = segmentEnd.Latitude - segmentStart.Latitude;
        var lengthSquared = (dx * dx) + (dy * dy);
        var t = lengthSquared == 0 ? 0 : Math.Clamp(-((ax * dx) + (ay * dy)) / lengthSquared, 0, 1);
        var nearest = new GeoPoint(
            segmentStart.Latitude + (t * (segmentEnd.Latitude - segmentStart.Latitude)),
            segmentStart.Longitude + (t * (segmentEnd.Longitude - segmentStart.Longitude)));
        return DistanceKm(point, nearest);
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    private static bool InBoundingBox(IReadOnlyList<GeoPoint> ring, GeoPoint point)
    {
        double minLat = double.MaxValue, maxLat = double.MinValue, minLon = double.MaxValue, maxLon = double.MinValue;
        foreach (var p in ring)
        {
            minLat = Math.Min(minLat, p.Latitude);
            maxLat = Math.Max(maxLat, p.Latitude);
            minLon = Math.Min(minLon, p.Longitude);
            maxLon = Math.Max(maxLon, p.Longitude);
        }

        return point.Latitude >= minLat && point.Latitude <= maxLat && point.Longitude >= minLon && point.Longitude <= maxLon;
    }

    // One zigzag-encoded value: 5-bit chunks, low first, each offset by 63; 0x20 marks "more follows".
    private static long NextDelta(string encoded, ref int index)
    {
        long result = 0;
        var shift = 0;
        int chunk;
        do
        {
            if (index >= encoded.Length)
            {
                throw new FormatException("Polyline ends in the middle of a value.");
            }

            chunk = encoded[index++] - 63;
            if (chunk is < 0 or > 63 || shift > 55)
            {
                throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"Polyline has an invalid character at position {index - 1}."));
            }

            result |= (long)(chunk & 0x1f) << shift;
            shift += 5;
        }
        while (chunk >= 0x20);

        return (result & 1) != 0 ? ~(result >> 1) : result >> 1;
    }
}
