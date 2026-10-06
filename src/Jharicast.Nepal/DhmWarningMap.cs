using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jharicast.Routing;

namespace Jharicast.Nepal;

/// <summary>
/// Reads the district levels off one of DHM's warning map images (ADR-0016). The map is a
/// matplotlib plot with longitude and latitude axes, so every pixel has a position; each district
/// gets the colour most of its pixels have.
/// </summary>
/// <remarks>
/// <para>
/// The layout was measured on 2026-10-06 and checked against 36 maps from 2026-07-03 to
/// 2026-10-06: 1300 by 800 pixels, 80°E at x = 64 and 88°E at x = 1226, 30°N at y = 191 and 27°N
/// at y = 635. On every one, each district's pixels were at least 98 percent one level colour.
/// </para>
/// <para>
/// The fill colours are not the legend's colours: the legend's green is (0, 176, 80) and its red
/// (255, 0, 0), while districts are filled (77, 229, 7) and (239, 1, 2). District borders are
/// sometimes drawn in the legend's red, so reading the legend colours would count every border as
/// a Red warning.
/// </para>
/// </remarks>
internal static class DhmWarningMap
{
    public const int Width = 1300;
    public const int Height = 800;

    // Axis ticks: pixel, degrees.
    private const int WestX = 64, EastX = 1226, NorthY = 191, SouthY = 635;
    private const double WestLon = 80, EastLon = 88, NorthLat = 30, SouthLat = 27;

    // One sample every 4 pixels gives Bhaktapur, the smallest district, 13 samples.
    private const int Step = 4;
    private const int MinSamples = 8;
    private const double MinShare = 0.8;

    // Squared RGB distance within which a pixel counts as a fill colour. Flat fills match exactly;
    // the margin allows for a re-encode, and the nearest other fill is over 100 away.
    private const int MaxDistanceSquared = 24 * 24;

    private static readonly (byte R, byte G, byte B, AlertLevel Level)[] Fills =
    [
        (77, 229, 7, AlertLevel.Green),
        (255, 248, 9, AlertLevel.Yellow),
        (252, 144, 12, AlertLevel.Orange),
        (239, 1, 2, AlertLevel.Red),
    ];

    private static readonly Lazy<Dictionary<string, (int X, int Y)[]>> Samples = new(BuildSamples);

    /// <summary>Reads every district's level.</summary>
    /// <param name="png">The map image.</param>
    /// <returns>A level for every district read, and what could not be read. Drift is never empty when a district is missing.</returns>
    public static (IReadOnlyDictionary<string, AlertLevel> Levels, IReadOnlyList<string> Drift) Read(ReadOnlySpan<byte> png)
    {
        RgbImage image;
        try
        {
            image = Png.Decode(png);
        }
        catch (InvalidDataException e)
        {
            return (new Dictionary<string, AlertLevel>(), ["image:" + e.Message]);
        }

        if (image.Width != Width || image.Height != Height)
        {
            return (new Dictionary<string, AlertLevel>(), [$"layout:{image.Width}x{image.Height}"]);
        }

        if (!HasTicks(image))
        {
            return (new Dictionary<string, AlertLevel>(), ["layout:axis ticks moved"]);
        }

        var levels = new Dictionary<string, AlertLevel>(StringComparer.Ordinal);
        var drift = new List<string>();
        Span<int> votes = stackalloc int[Fills.Length];
        foreach (var (district, points) in Samples.Value)
        {
            votes.Clear();
            foreach (var (x, y) in points)
            {
                if (Classify(image[x, y]) is { } fill)
                {
                    votes[fill]++;
                }
            }

            var total = 0;
            var best = 0;
            for (var i = 0; i < votes.Length; i++)
            {
                total += votes[i];
                best = votes[i] > votes[best] ? i : best;
            }

            if (total < MinSamples || votes[best] < MinShare * total)
            {
                drift.Add($"district:{district}");
            }
            else
            {
                levels[district] = Fills[best].Level;
            }
        }

        foreach (var id in DistrictResolver.DistrictIds.Where(id => !Samples.Value.ContainsKey(id)))
        {
            drift.Add($"district:{id}");
        }

        return (levels, drift);
    }

    private static int? Classify((byte R, byte G, byte B) pixel)
    {
        for (var i = 0; i < Fills.Length; i++)
        {
            var (dr, dg, db) = (pixel.R - Fills[i].R, pixel.G - Fills[i].G, pixel.B - Fills[i].B);
            if (dr * dr + dg * dg + db * db <= MaxDistanceSquared)
            {
                return i;
            }
        }

        return null;
    }

    // The four tick marks just outside the frame are dark on every map seen. If DHM changes the
    // plot's extent or size, they move, and reading positions from the old layout would put
    // districts in the wrong place.
    private static bool HasTicks(RgbImage image)
    {
        static bool Dark((byte R, byte G, byte B) p) => p.R + p.G + p.B < 200;
        return Dark(image[WestX, 763]) && Dark(image[EastX, 763]) && Dark(image[41, NorthY]) && Dark(image[41, SouthY]);
    }

    private static Dictionary<string, (int X, int Y)[]> BuildSamples()
    {
        // Tolerance 0: a pixel on a border belongs to no district rather than the nearest one.
        var resolver = new DistrictResolver { ToleranceKm = 0 };
        var samples = new Dictionary<string, List<(int, int)>>(StringComparer.Ordinal);
        for (var x = 0; x < Width; x += Step)
        {
            for (var y = 0; y < Height; y += Step)
            {
                var lon = WestLon + (x - WestX) * (EastLon - WestLon) / (EastX - WestX);
                var lat = NorthLat + (y - NorthY) * (SouthLat - NorthLat) / (SouthY - NorthY);
                if (lon is < 79.9 or > 88.3 || lat is < 26.3 or > 30.5)
                {
                    continue;
                }

                if (resolver.Resolve(new GeoPoint(lat, lon)) is { } id)
                {
                    (samples.TryGetValue(id, out var list) ? list : samples[id] = []).Add((x, y));
                }
            }
        }

        return samples.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
    }
}
