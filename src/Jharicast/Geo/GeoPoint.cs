using System;

namespace Jharicast;

/// <summary>A WGS 84 position in decimal degrees.</summary>
/// <param name="Latitude">Latitude, -90 to 90.</param>
/// <param name="Longitude">Longitude, -180 to 180.</param>
public readonly record struct GeoPoint(double Latitude, double Longitude)
{
    /// <summary>Latitude in degrees, north positive.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside -90 to 90, or NaN.</exception>
    public double Latitude { get; } = InRange(Latitude, 90, nameof(Latitude));

    /// <summary>Longitude in degrees, east positive.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Outside -180 to 180, or NaN.</exception>
    public double Longitude { get; } = InRange(Longitude, 180, nameof(Longitude));

    // NaN fails both comparisons, so it is rejected along with out-of-range values.
    private static double InRange(double value, double limit, string name) =>
        value >= -limit && value <= limit
            ? value
            : throw new ArgumentOutOfRangeException(name, value, $"Must be between -{limit} and {limit} degrees.");
}
