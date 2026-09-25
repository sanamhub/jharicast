using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;

namespace Jharicast.Tests;

public sealed class CoreRuleTests
{
    [Theory]
    [InlineData(0.5, 2.5)]
    [InlineData(0.9, 3.7)]
    public void Quantile_interpolates_linearly_like_numpy(double q, double expected)
    {
        // R-7: position q * (n - 1), so 0.9 of [1, 2, 3, 4] sits at 2.7, between 3 and 4.
        Assert.Equal(expected, EnsembleStats.Quantile([1, 2, 3, 4], q), 10);
    }

    [Fact]
    public void Quantile_and_exceedance_ignore_missing_members()
    {
        double?[] members = [null, 1, 2, null, 3, 4, null];

        Assert.Equal(2.5, EnsembleStats.Quantile(members, 0.5), 10);
        Assert.Equal(0.5, EnsembleStats.Exceedance(members, 2), 10);
    }

    [Fact]
    public void Quantile_and_exceedance_throw_when_every_member_is_missing()
    {
        double?[] members = [null, null, null];

        Assert.Throws<ArgumentException>(() => EnsembleStats.Quantile(members, 0.5));
        Assert.Throws<ArgumentException>(() => EnsembleStats.Exceedance(members, 10));
    }

    public static TheoryData<AlertLevel?, AlertLevel> AllLevelPairs()
    {
        var data = new TheoryData<AlertLevel?, AlertLevel>();
        AlertLevel?[] officials = [null, .. Enum.GetValues<AlertLevel>().Cast<AlertLevel?>()];
        foreach (var official in officials)
        {
            foreach (var model in Enum.GetValues<AlertLevel>())
            {
                data.Add(official, model);
            }
        }

        return data;
    }

    // AC-3.2, ADR-0005: the official level is a floor, never lowered by the model.
    [Theory]
    [MemberData(nameof(AllLevelPairs))]
    public void Combine_is_at_least_the_official_level_and_at_least_the_model_level(AlertLevel? official, AlertLevel model)
    {
        var combined = AlertLevels.Combine(official, model);

        Assert.True(combined >= model);
        if (official is { } o)
        {
            Assert.True(combined >= o);
        }
    }

    [Fact]
    public void Combine_covers_all_twenty_pairs()
    {
        Assert.Equal(20, AllLevelPairs().Count);
    }

    // ADR-0006: "over N" is strictly greater than N. A model value equal to the threshold does
    // not breach; it lands in the watch band because it is above 85 percent of the threshold.
    [Fact]
    public void Threshold_value_equal_to_the_threshold_does_not_breach()
    {
        var sample = new ModelSample(new Dictionary<string, double> { ["ecmwf"] = 40 }, []);

        var result = ThresholdRule.Gust.Evaluate(sample);

        Assert.Equal(RuleStatus.Watch, result.Status);
        Assert.Equal("ecmwf 40 near 40", result.Reason);
    }

    [Fact]
    public void Threshold_members_equal_to_the_threshold_count_as_not_over()
    {
        double?[] members = [.. Enumerable.Repeat<double?>(64, 51)];
        var sample = new ModelSample(new Dictionary<string, double>(), members);

        Assert.Equal(RuleStatus.Pass, ThresholdRule.HillRain.Evaluate(sample).Status);
    }

    [Fact]
    public void Reasons_use_a_dot_as_decimal_separator_under_a_comma_culture()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            var breach = ThresholdRule.Gust.Evaluate(new ModelSample(new Dictionary<string, double> { ["gfs"] = 52.5 }, []));
            var near = ThresholdRule.HillRain.Evaluate(new ModelSample(new Dictionary<string, double> { ["icon"] = 60.3 }, []));
            var custom = new ThresholdRule("test.half", 40.5).Evaluate(new ModelSample(new Dictionary<string, double> { ["gfs"] = 41.25 }, []));

            Assert.Equal("gfs 52.5 over 40", breach.Reason);
            Assert.Equal("icon 60.3 near 64", near.Reason);
            Assert.Equal("gfs 41.3 over 40.5", custom.Reason);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
