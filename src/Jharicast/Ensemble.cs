using System;
using System.Collections.Generic;
using System.Linq;

namespace Jharicast;

/// <summary>Statistics over ensemble members. Missing members (null) are ignored.</summary>
public static class EnsembleStats
{
    /// <summary>Linear-interpolated quantile (the "R-7" method, same as numpy's default).</summary>
    /// <param name="members">Member values.</param>
    /// <param name="q">Quantile in [0, 1]. 0.5 is the median, 0.9 the "1-in-10 high" of the report.</param>
    /// <returns>The quantile.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="members"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="q"/> is outside [0, 1].</exception>
    /// <exception cref="ArgumentException">No non-null member.</exception>
    public static double Quantile(IEnumerable<double?> members, double q)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentOutOfRangeException.ThrowIfLessThan(q, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(q, 1);
        var sorted = members.Where(v => v.HasValue).Select(v => v!.Value).Order().ToArray();
        if (sorted.Length == 0)
        {
            throw new ArgumentException("No members with a value.", nameof(members));
        }

        var position = q * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + ((position - lower) * (sorted[upper] - sorted[lower]));
    }

    /// <summary>Fraction of members strictly above <paramref name="threshold"/>. "Over 100 mm" means &gt; 100.</summary>
    /// <param name="members">Member values.</param>
    /// <param name="threshold">Threshold.</param>
    /// <returns>A fraction in [0, 1].</returns>
    /// <exception cref="ArgumentNullException"><paramref name="members"/> is null.</exception>
    /// <exception cref="ArgumentException">No non-null member.</exception>
    public static double Exceedance(IEnumerable<double?> members, double threshold)
    {
        ArgumentNullException.ThrowIfNull(members);
        var values = members.Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        if (values.Length == 0)
        {
            throw new ArgumentException("No members with a value.", nameof(members));
        }

        return (double)values.Count(v => v > threshold) / values.Length;
    }
}

/// <summary>
/// The report's own-model rain rule (section 4.2), pooled ECMWF ENS and GEFS members, DHM rain
/// classes. Every threshold is a property so a later version can change it under a new
/// <see cref="Id"/> without breaking stored results (ADR-0006).
/// </summary>
public sealed record RainAlertRule
{
    /// <summary>The rule as used in the 2026-09-24 report.</summary>
    public static RainAlertRule V1 { get; } = new();

    /// <summary>Stored with every result so old assessments stay explainable.</summary>
    public string Id { get; init; } = "jharicast.rain-alert.v1";

    /// <summary>Red if P(&gt; 100 mm) is at least this. Default 0.5.</summary>
    public double RedP100 { get; init; } = 0.5;

    /// <summary>Red if P(&gt; 200 mm) is at least this. Default 0.2.</summary>
    public double RedP200 { get; init; } = 0.2;

    /// <summary>Orange if P(&gt; 50 mm) is at least this. Default 0.5.</summary>
    public double OrangeP50 { get; init; } = 0.5;

    /// <summary>Orange if P(&gt; 100 mm) is at least this. Default 0.3.</summary>
    public double OrangeP100 { get; init; } = 0.3;

    /// <summary>Yellow if P(&gt; 50 mm) is at least this. Default 0.2.</summary>
    public double YellowP50 { get; init; } = 0.2;

    /// <summary>Yellow if the median is at least this many mm. Default 20.</summary>
    public double YellowMedianMm { get; init; } = 20;

    /// <summary>Evaluates one point and day.</summary>
    /// <param name="members">24-hour rain per member, mm, all ensembles pooled.</param>
    /// <returns>The level.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="members"/> is null.</exception>
    /// <exception cref="ArgumentException">No non-null member.</exception>
    public AlertLevel Evaluate(IReadOnlyCollection<double?> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        double P(double mm) => EnsembleStats.Exceedance(members, mm);

        if (P(100) >= RedP100 || P(200) >= RedP200)
        {
            return AlertLevel.Red;
        }

        if (P(50) >= OrangeP50 || P(100) >= OrangeP100)
        {
            return AlertLevel.Orange;
        }

        if (P(50) >= YellowP50 || EnsembleStats.Quantile(members, 0.5) >= YellowMedianMm)
        {
            return AlertLevel.Yellow;
        }

        return AlertLevel.Green;
    }
}
