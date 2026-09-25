using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Jharicast.Tests;

/// <summary>
/// Cases taken from the 2026-09-24 storm report. The report gives medians and 1-in-10
/// highs, not raw members, so members are synthesised through those two quantiles.
/// </summary>
public sealed class ReportGoldenTests
{
    // 51 members on a piecewise-linear quantile curve through (0, 0), (0.5, median), (0.9, p90), (1, 1.3 * p90).
    private static double?[] Members(double median, double p90) =>
        [.. Enumerable.Range(0, 51).Select(i =>
        {
            var q = i / 50.0;
            return (double?)(q <= 0.5 ? q / 0.5 * median
                : q <= 0.9 ? median + ((q - 0.5) / 0.4 * (p90 - median))
                : p90 + ((q - 0.9) / 0.1 * 0.3 * p90));
        })];

    [Theory]
    [InlineData("Tansen 25 Sep", 101, 141, AlertLevel.Red)]
    [InlineData("Janakpur 24 Sep", 59, 81, AlertLevel.Orange)]
    [InlineData("Biratnagar 24 Sep", 58, 87, AlertLevel.Orange)]
    [InlineData("Nepalgunj 26 Sep", 66, 126, AlertLevel.Orange)]
    [InlineData("Dhangadhi 26 Sep", 80, 151, AlertLevel.Orange)]
    [InlineData("Dhangadhi 27 Sep", 40, 102, AlertLevel.Yellow)]
    public void Rain_rule_reproduces_the_reports_own_levels(string point, double median, double p90, AlertLevel expected)
    {
        var members = Members(median, p90);

        Assert.Equal(median, EnsembleStats.Quantile(members, 0.5), 1);
        Assert.True(expected == RainAlertRule.V1.Evaluate(members), point);
    }

    [Fact]
    public void Official_level_is_never_lowered_by_the_model()
    {
        Assert.Equal(AlertLevel.Red, AlertLevels.Combine(AlertLevel.Red, AlertLevel.Orange));
        Assert.Equal(AlertLevel.Red, AlertLevels.Combine(AlertLevel.Orange, AlertLevel.Red)); // Lumbini 25 Sep: ours higher, ours shown
        Assert.Equal(AlertLevel.Yellow, AlertLevels.Combine(null, AlertLevel.Yellow));
    }

    private static ModelSample Sample(double e, double g, double? i, double?[]? members = null)
    {
        var d = new Dictionary<string, double> { ["ecmwf"] = e, ["gfs"] = g };
        if (i is { } icon)
        {
            d["icon"] = icon;
        }

        return new ModelSample(d, members ?? []);
    }

    [Fact]
    public void Start_on_26_Sep_breaks_wind_and_hill_rain_rules()
    {
        var day = new LegDayInput(
            "D1",
            new DateOnly(2026, 9, 26),
            new Dictionary<string, AlertLevel> { ["nawalparasiwest"] = AlertLevel.Orange, ["rupandehi"] = AlertLevel.Orange },
            RoadBlocked: false,
            HillRain: Sample(23, 19, 127),
            Gust: Sample(60, 53, 82));

        var assessment = RouteRuleSet.V1.Evaluate(day);

        Assert.Equal(RuleStatus.Breach, assessment.Status);
        Assert.All(assessment.Results.Where(r => r.RuleId != "jharicast.road-blocked.v1"), r => Assert.Equal(RuleStatus.Breach, r.Status));
    }

    [Fact]
    public void Start_on_28_Sep_is_a_wind_watch_only()
    {
        // GEFS 45% of members over 40 km/h at Lumbini.
        double?[] gefs = [.. Enumerable.Range(0, 31).Select(i => (double?)(i < 14 ? 45 : 30))];
        var day = new LegDayInput("D1", new DateOnly(2026, 9, 28), new Dictionary<string, AlertLevel>(), false, Sample(1, 10, 1), Sample(35, 31, 24, gefs));

        var assessment = RouteRuleSet.V1.Evaluate(day);

        Assert.Equal(RuleStatus.Watch, assessment.Status);
        Assert.Equal(RuleStatus.Watch, assessment.Results.Single(r => r.RuleId == "jharicast.gust.v1").Status);
        Assert.Equal(RuleStatus.Pass, assessment.Results.Single(r => r.RuleId == "jharicast.hill-rain.v1").Status);
    }

    [Fact]
    public void Nepalgunj_to_Birendranagar_29_Sep_passes()
    {
        var day = new LegDayInput("P1-D4", new DateOnly(2026, 9, 29), new Dictionary<string, AlertLevel>(), false, Sample(0, 9, 0), Sample(27, 17, 20));

        Assert.Equal(RuleStatus.Pass, RouteRuleSet.V1.Evaluate(day).Status);
    }

    [Fact]
    public void Diff_reports_escalation_once_per_subject_and_ignores_value_jitter()
    {
        var date = new DateOnly(2026, 9, 28);
        var before = RouteRuleSet.V1.Evaluate(new LegDayInput("D1", date, new Dictionary<string, AlertLevel>(), false, Sample(1, 10, 1), Sample(35, 31, 24)));
        var jitter = RouteRuleSet.V1.Evaluate(new LegDayInput("D1", date, new Dictionary<string, AlertLevel>(), false, Sample(2, 12, 3), Sample(36, 30, 25)));
        var worse = RouteRuleSet.V1.Evaluate(new LegDayInput("D1", date, new Dictionary<string, AlertLevel>(), false, Sample(2, 12, 3), Sample(52, 44, 27)));

        Assert.Empty(AssessmentDiff.Compare(before, jitter));
        var changes = AssessmentDiff.Compare(jitter, worse);
        Assert.Equal(["overall", "jharicast.gust.v1"], changes.Select(c => c.Subject));
        Assert.All(changes, c => Assert.Equal(ChangeDirection.Escalation, c.Direction));
    }
}
