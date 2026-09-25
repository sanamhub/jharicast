using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Jharicast.Routing;

/// <summary>One sample point along a route.</summary>
/// <param name="Point">Position.</param>
/// <param name="Km">Distance from the start along the route, km.</param>
/// <param name="ElevationM">Ground elevation, m.</param>
/// <param name="IsHill">
/// True when the segment from this point to the next is a hill section (ADR-0010). The last point
/// carries the flag of the segment before it; a route of one point is hill when above the
/// elevation threshold.
/// </param>
public sealed record RouteSample(GeoPoint Point, double Km, double ElevationM, bool IsHill);

/// <summary>A route sampled every few km, with elevations and hill sections.</summary>
/// <param name="Samples">Sample points from start to end.</param>
/// <param name="DistanceKm">Length along the road geometry, km.</param>
/// <param name="HillKm">Length of the hill segments, km.</param>
public sealed record SampledRoute(IReadOnlyList<RouteSample> Samples, double DistanceKm, double HillKm);

/// <summary>
/// Samples road geometry every <c>stepKm</c> and marks hill sections from elevation, not from
/// district belts (ADR-0010): a segment is hill when its elevation range is at least
/// <see cref="HillRangeM"/> or it rises above <see cref="HillElevationM"/>. Each segment's range
/// is taken over an elevation profile every <see cref="ProfileStepKm"/>, so a pass between two
/// valley sample points still counts.
/// </summary>
/// <remarks>
/// Elevations come from the function given, normally <c>OpenMeteoClient.GetElevationAsync</c>
/// (Copernicus DEM 90 m, 100 points per call). Taking a function keeps this package free of the
/// Open-Meteo one.
/// </remarks>
/// <param name="elevations">Elevations in metres for points, one per point in the order given.</param>
public sealed class RouteSampler(Func<IReadOnlyList<GeoPoint>, CancellationToken, Task<IReadOnlyList<double>>> elevations)
{
    private readonly Func<IReadOnlyList<GeoPoint>, CancellationToken, Task<IReadOnlyList<double>>> _elevations = elevations ?? throw new ArgumentNullException(nameof(elevations));

    /// <summary>Elevation range within a segment that makes it hill, m. Default 150, to be tuned (task R04).</summary>
    public double HillRangeM { get; init; } = 150;

    /// <summary>Elevation that a segment reaching above it makes hill, m. Default 700, to be tuned (task R04).</summary>
    public double HillElevationM { get; init; } = 700;

    /// <summary>Spacing of the elevation profile inside each segment, km. Default 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not a positive, finite number.</exception>
    public double ProfileStepKm
    {
        get;
        init
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The profile step must be a positive, finite distance.");
            }

            field = value;
        }
    } = 1;

    /// <summary>Samples a route and marks its hill segments.</summary>
    /// <param name="route">Road geometry.</param>
    /// <param name="stepKm">
    /// Sample spacing, km, default 5. Rounded to a whole number of <see cref="ProfileStepKm"/>, or
    /// taken as the profile step when smaller.
    /// </param>
    /// <param name="cancellationToken">Cancels the elevation lookups.</param>
    /// <returns>The samples, length and hill km.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="route"/> is null.</exception>
    /// <exception cref="ArgumentException">The route has no points.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepKm"/> is not a positive, finite number.</exception>
    /// <exception cref="InvalidOperationException">The elevation function returned a different number of values.</exception>
    public async Task<SampledRoute> SampleAsync(RouteGeometry route, double stepKm = 5, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.Points.Count == 0)
        {
            throw new ArgumentException("The route has no points.", nameof(route));
        }

        if (!double.IsFinite(stepKm) || stepKm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stepKm), stepKm, "Step must be a positive, finite distance.");
        }

        var profileStep = Math.Min(ProfileStepKm, stepKm);
        var perSample = Math.Max(1, (int)Math.Round(stepKm / profileStep));
        var profile = Geo.Resample(route.Points, profileStep);
        var heights = await _elevations(profile, cancellationToken).ConfigureAwait(false);
        if (heights.Count != profile.Count)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Asked for {profile.Count} elevations, got {heights.Count}."));
        }

        var km = new double[profile.Count];
        for (var i = 1; i < profile.Count; i++)
        {
            km[i] = km[i - 1] + Geo.DistanceKm(profile[i - 1], profile[i]);
        }

        if (profile.Count == 1)
        {
            return new SampledRoute([new RouteSample(profile[0], 0, heights[0], heights[0] > HillElevationM)], 0, 0);
        }

        var samples = new List<RouteSample>();
        var hillKm = 0.0;
        var last = false;
        for (var start = 0; start < profile.Count - 1; start += perSample)
        {
            var end = Math.Min(start + perSample, profile.Count - 1);
            var segment = heights.Skip(start).Take(end - start + 1).ToArray();
            last = segment.Max() - segment.Min() >= HillRangeM || segment.Max() > HillElevationM;
            if (last)
            {
                hillKm += km[end] - km[start];
            }

            samples.Add(new RouteSample(profile[start], km[start], heights[start], last));
        }

        samples.Add(new RouteSample(profile[^1], km[^1], heights[^1], last));
        return new SampledRoute(samples, km[^1], hillKm);
    }
}
