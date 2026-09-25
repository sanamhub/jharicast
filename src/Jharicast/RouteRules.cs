using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jharicast;

/// <summary>Result of one rule for one leg and day.</summary>
public enum RuleStatus
{
    /// <summary>Clear.</summary>
    Pass = 0,

    /// <summary>Close to the threshold, or a minority of members over it. Recheck.</summary>
    Watch = 1,

    /// <summary>The rule is broken.</summary>
    Breach = 2,
}

/// <summary>
/// Model values for one quantity on one leg and day: the day maximum along the route from each
/// deterministic model, and optionally the ensemble members at the worst point.
/// </summary>
/// <param name="Deterministic">Day maximum per model id, for example <c>ecmwf_ifs025</c> 23.0.</param>
/// <param name="Members">Ensemble members at the worst point, or empty.</param>
public sealed record ModelSample(IReadOnlyDictionary<string, double> Deterministic, IReadOnlyList<double?> Members);

/// <summary>
/// A threshold rule of the report's section 7.1 (hill rain over 64 mm, gusts over 40 km/h).
/// Breach when any deterministic model is over the threshold or the ensemble probability reaches
/// <see cref="BreachProbability"/>; watch when a model is within <see cref="WatchFraction"/> of it or
/// the probability reaches <see cref="WatchProbability"/>.
/// </summary>
/// <param name="Id">Stable id, stored with results.</param>
/// <param name="Threshold">Strict threshold: a value equal to it passes.</param>
public sealed record ThresholdRule(string Id, double Threshold)
{
    /// <summary>Rule 3 of the report: over 64 mm per day on hill sections.</summary>
    public static ThresholdRule HillRain { get; } = new("jharicast.hill-rain.v1", 64);

    /// <summary>Rule 4 of the report: gusts over 40 km/h on the route.</summary>
    public static ThresholdRule Gust { get; } = new("jharicast.gust.v1", 40);

    /// <summary>Ensemble probability that breaches. Default 0.5.</summary>
    public double BreachProbability { get; init; } = 0.5;

    /// <summary>Ensemble probability that makes a watch. Default 0.3.</summary>
    public double WatchProbability { get; init; } = 0.3;

    /// <summary>A deterministic value at or above this fraction of the threshold makes a watch. Default 0.85.</summary>
    public double WatchFraction { get; init; } = 0.85;

    /// <summary>Evaluates a sample.</summary>
    /// <param name="sample">The sample.</param>
    /// <returns>Status and a short reason naming the deciding value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sample"/> is null.</exception>
    public RuleResult Evaluate(ModelSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var worst = sample.Deterministic.Count == 0 ? default : sample.Deterministic.MaxBy(kv => kv.Value);
        var probability = sample.Members.Any(m => m.HasValue) ? EnsembleStats.Exceedance(sample.Members, Threshold) : 0;

        if (worst.Key is not null && worst.Value > Threshold)
        {
            return new(Id, RuleStatus.Breach, Invariant($"{worst.Key} {worst.Value:0.#} over {Threshold:0.#}"));
        }

        if (probability >= BreachProbability)
        {
            return new(Id, RuleStatus.Breach, Invariant($"{probability:P0} of members over {Threshold:0.#}"));
        }

        if (probability >= WatchProbability)
        {
            return new(Id, RuleStatus.Watch, Invariant($"{probability:P0} of members over {Threshold:0.#}"));
        }

        if (worst.Key is not null && worst.Value >= Threshold * WatchFraction)
        {
            return new(Id, RuleStatus.Watch, Invariant($"{worst.Key} {worst.Value:0.#} near {Threshold:0.#}"));
        }

        return new(Id, RuleStatus.Pass, "clear");
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One rule's result.</summary>
/// <param name="RuleId">Rule id.</param>
/// <param name="Status">Status.</param>
/// <param name="Reason">Short, human reason. Invariant culture numbers.</param>
public sealed record RuleResult(string RuleId, RuleStatus Status, string Reason);

/// <summary>Inputs for one leg on one day.</summary>
/// <param name="LegId">Caller's leg id.</param>
/// <param name="Date">Travel date, Nepal time.</param>
/// <param name="OfficialLevels">Highest official warning per district crossed on that day. Missing district means none issued.</param>
/// <param name="RoadBlocked">Any official block on the route.</param>
/// <param name="HillRain">Rain on hill sections.</param>
/// <param name="Gust">Gusts along the route.</param>
public sealed record LegDayInput(
    string LegId,
    DateOnly Date,
    IReadOnlyDictionary<string, AlertLevel> OfficialLevels,
    bool RoadBlocked,
    ModelSample HillRain,
    ModelSample Gust);

/// <summary>What the rules say for one leg on one day.</summary>
/// <param name="LegId">Leg id.</param>
/// <param name="Date">Date.</param>
/// <param name="Status">Worst rule status.</param>
/// <param name="Results">Every rule's result, in rule order.</param>
public sealed record LegDayAssessment(string LegId, DateOnly Date, RuleStatus Status, IReadOnlyList<RuleResult> Results);

/// <summary>The report's four switch rules (section 7.1) as one evaluation.</summary>
public sealed record RouteRuleSet
{
    /// <summary>As used in the 2026-09-24 report.</summary>
    public static RouteRuleSet V1 { get; } = new();

    /// <summary>Rule 1 id.</summary>
    public string OfficialRuleId { get; init; } = "jharicast.official-orange.v1";

    /// <summary>Rule 2 id.</summary>
    public string RoadRuleId { get; init; } = "jharicast.road-blocked.v1";

    /// <summary>Rule 3.</summary>
    public ThresholdRule HillRain { get; init; } = ThresholdRule.HillRain;

    /// <summary>Rule 4.</summary>
    public ThresholdRule Gust { get; init; } = ThresholdRule.Gust;

    /// <summary>Evaluates one leg and day.</summary>
    /// <param name="input">Inputs.</param>
    /// <returns>The assessment.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    public LegDayAssessment Evaluate(LegDayInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var official = AlertLevels.Max(input.OfficialLevels.Values);
        var worstDistrict = input.OfficialLevels.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).FirstOrDefault();

        RuleResult[] results =
        [
            official >= AlertLevel.Orange
                ? new(OfficialRuleId, RuleStatus.Breach, $"official {official} in {worstDistrict.Key}")
                : official == AlertLevel.Yellow
                    ? new(OfficialRuleId, RuleStatus.Watch, $"official Yellow in {worstDistrict.Key}")
                    : new(OfficialRuleId, RuleStatus.Pass, "no official warning"),
            input.RoadBlocked
                ? new(RoadRuleId, RuleStatus.Breach, "road blocked")
                : new(RoadRuleId, RuleStatus.Pass, "open"),
            HillRain.Evaluate(input.HillRain),
            Gust.Evaluate(input.Gust),
        ];

        return new(input.LegId, input.Date, results.Max(r => r.Status), results);
    }
}
