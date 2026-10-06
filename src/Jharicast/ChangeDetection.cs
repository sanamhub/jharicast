using System;
using System.Collections.Generic;
using System.Linq;

namespace Jharicast;

/// <summary>Whether a change makes things worse or better.</summary>
public enum ChangeDirection
{
    /// <summary>More severe.</summary>
    Escalation = 0,

    /// <summary>Less severe.</summary>
    Deescalation = 1,
}

/// <summary>A material change between two assessments of the same leg and day (ADR-0009).</summary>
/// <param name="LegId">Leg.</param>
/// <param name="Date">Day.</param>
/// <param name="Subject">The overall status (<c>overall</c>) or a rule id.</param>
/// <param name="From">Previous status.</param>
/// <param name="To">New status.</param>
/// <param name="Reason">The new reason text.</param>
public sealed record MaterialChange(string LegId, DateOnly Date, string Subject, RuleStatus From, RuleStatus To, string Reason)
{
    /// <summary>Escalation when <see cref="To"/> is more severe.</summary>
    public ChangeDirection Direction => To > From ? ChangeDirection.Escalation : ChangeDirection.Deescalation;
}

/// <summary>
/// Compares assessments. Only status changes are material: a forecast moving from 70 mm to 90 mm
/// on a rule already in breach is not news, and a reason text change alone is ignored.
/// </summary>
public static class AssessmentDiff
{
    /// <summary>Returns material changes from <paramref name="previous"/> to <paramref name="current"/>, overall first.</summary>
    /// <param name="previous">Earlier assessment, or null when this leg and day are new.</param>
    /// <param name="current">New assessment.</param>
    /// <returns>Changes; empty when nothing material changed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> is null.</exception>
    /// <exception cref="ArgumentException">The two assessments are for different legs or days.</exception>
    public static IReadOnlyList<MaterialChange> Compare(LegDayAssessment? previous, LegDayAssessment current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null)
        {
            return current.Status == RuleStatus.Pass
                ? []
                : [new(current.LegId, current.Date, "overall", RuleStatus.Pass, current.Status, Worst(current).Reason)];
        }

        if (previous.LegId != current.LegId || previous.Date != current.Date)
        {
            throw new ArgumentException("Assessments are for different legs or days.", nameof(previous));
        }

        var changes = new List<MaterialChange>();
        if (previous.Status != current.Status)
        {
            changes.Add(new(current.LegId, current.Date, "overall", previous.Status, current.Status, Worst(current).Reason));
        }

        var before = previous.Results.ToDictionary(r => r.RuleId, StringComparer.Ordinal);
        foreach (var now in current.Results)
        {
            var was = before.TryGetValue(now.RuleId, out var b) ? b.Status : RuleStatus.Pass;
            if (was != now.Status)
            {
                changes.Add(new(current.LegId, current.Date, now.RuleId, was, now.Status, now.Reason));
            }
        }

        return changes;
    }

    // The rule that decided the overall status; enum order is not severity (Unknown is 3).
    private static RuleResult Worst(LegDayAssessment a) => a.Results.FirstOrDefault(r => r.Status == a.Status) ?? a.Results[0];
}
