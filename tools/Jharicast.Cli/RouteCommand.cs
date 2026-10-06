using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Fetch;
using Jharicast.Nepal;

namespace Jharicast.Cli;

/// <summary>What <c>jharicast route</c> was asked.</summary>
internal sealed record RouteRequest(Place From, Place To, IReadOnlyList<Place> Via, DateOnly Date, int Days, bool Json);

/// <summary>
/// <c>jharicast route</c>: the same road on one or more start dates, one row per leg and day in
/// the storm report's leg table shape, then each rule's reason, the official levels of the
/// districts crossed, data times and health, and the notices.
/// </summary>
internal static class RouteCommand
{
    private static readonly string[] ModelOrder = ["ecmwf_ifs025", "gfs_seamless", "icon_seamless"];

    private static readonly Dictionary<string, string> RuleNames = new(StringComparer.Ordinal)
    {
        ["jharicast.official-orange.v1"] = "official",
        ["jharicast.road-blocked.v1"] = "road",
        ["jharicast.hill-rain.v1"] = "hill rain",
        ["jharicast.gust.v1"] = "wind",
    };

    public static async Task<int> RunAsync(RouteRequest request, Wiring wiring, TextWriter output, CancellationToken cancellationToken)
    {
        var legs = Enumerable.Range(0, request.Days)
            .Select(i => request.Date.AddDays(i))
            .Select(date => new Leg(date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), request.From.Point, request.To.Point, date) { Via = [.. request.Via.Select(v => v.Point)] })
            .ToArray();
        var route = new Route($"{request.From.Name} to {request.To.Name}", legs);
        var assessor = new NepalRouteAssessor(wiring.OpenMeteo(), wiring.Warnings(), wiring.Closures(), wiring.Routing(), wiring.Time);

        NepalRouteAssessment result;
        try
        {
            result = await assessor.AssessAsync(route, RouteRuleSet.V1, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new CliException(e.Message.Split(" (Parameter", 2)[0]);
        }

        if (request.Json)
        {
            WriteJson(output, result, wiring.IsReplay);
            return 0;
        }

        WriteText(output, request, result, assessor.OfficialHorizonDays, NepalTime.DateOf(wiring.Time.GetUtcNow()));
        Output.Health(output, result.Health, result.Provenance);
        Output.Notices(output, wiring.IsReplay);
        return 0;
    }

    private static void WriteText(TextWriter output, RouteRequest request, NepalRouteAssessment result, int horizon, DateOnly today)
    {
        var name = request.From.Name == request.To.Name ? "Stay " + request.From.Name : $"{request.From.Name} to {request.To.Name}";
        output.WriteLine(result.RouteId);
        output.WriteLine();
        Output.Table(
            output,
            ["Date", "Route (km, hill km)", "Rain day max E / G / I (mm)", "Gust max E / G / I (km/h)", "Flag"],
            [.. result.Legs.Select(l => new[]
            {
                l.Assessment.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                Output.Invariant($"{name} ({l.DistanceKm:0}, {l.HillKm:0})"),
                l.Input.HillRain.Deterministic.Count > 0 ? ByModel(l.Input.HillRain.Deterministic) : l.HillKm == 0 ? "no hill section" : "- / - / -",
                ByModel(l.Input.Gust.Deterministic),
                Flag(l.Assessment),
            })]);

        var official = result.Health.GetValueOrDefault(DhmWarningsParser.SourceId);
        var road = result.Health.GetValueOrDefault(DorClosureSource.SourceId);
        foreach (var leg in result.Legs)
        {
            output.WriteLine();
            output.WriteLine(Output.Invariant($"{leg.Assessment.Date:yyyy-MM-dd}: {leg.Assessment.Status}"));
            foreach (var rule in leg.Assessment.Results)
            {
                output.WriteLine($"  {RuleNames.GetValueOrDefault(rule.RuleId, rule.RuleId),-10} {rule.Status,-6} {Reason(rule, leg, official, road, horizon, today)}");
            }

            output.WriteLine("  Districts crossed: " + (leg.Districts.Count == 0 ? "none in Nepal" : string.Join(", ", leg.Districts.Select(d => d.Name))));
            if (leg.Input.OfficialLevels.Count > 0)
            {
                output.WriteLine("  Official DHM levels: " + string.Join(", ", leg.Input.OfficialLevels.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{leg.Districts.First(d => d.Id == kv.Key).Name} {kv.Value}")));
            }

            foreach (var closure in leg.Closures)
            {
                output.WriteLine($"  DoR closure: {closure.RoadRefNo ?? "road"} {closure.ClosureType ?? string.Empty} {closure.Reason ?? string.Empty}".TrimEnd());
            }

            if (leg.Input.Gust.Members.Count > 0)
            {
                output.WriteLine(Output.Invariant($"  Ensemble at the worst point (ECMWF ENS and GEFS pooled, {leg.Input.Gust.Members.Count} members): {100 * EnsembleStats.Exceedance(leg.Input.Gust.Members, RouteRuleSet.V1.Gust.Threshold):0}% of gusts over {RouteRuleSet.V1.Gust.Threshold:0} km/h"));
            }
        }
    }

    // A rule can only say what its source told it. When the source is not fresh, or DHM has not
    // issued for that day yet, say that instead of "no official warning" (ADR-0005 rule 3).
    private static string Reason(RuleResult rule, NepalLegDay leg, SourceHealth? official, SourceHealth? road, int horizon, DateOnly today)
    {
        if (rule.RuleId == RouteRuleSet.V1.OfficialRuleId && rule.Status == RuleStatus.Pass)
        {
            if (official is { Status: SourceStatus.Failing or SourceStatus.Disabled })
            {
                return $"official level unknown: dhm.warnings is {Output.Word(official.Status)}";
            }

            if (leg.Assessment.Date.DayNumber - today.DayNumber >= horizon)
            {
                return "no DHM warning covers this day yet; recheck nearer the date";
            }
        }

        if (rule.RuleId == RouteRuleSet.V1.RoadRuleId && rule.Status == RuleStatus.Pass && road is { Status: SourceStatus.Failing or SourceStatus.Disabled })
        {
            return $"road status unknown: dor.closures is {Output.Word(road.Status)}";
        }

        if (rule.RuleId == RouteRuleSet.V1.HillRain.Id && leg.HillKm == 0)
        {
            return "no hill section on this leg";
        }

        return rule.Reason;
    }

    private static string Flag(LegDayAssessment assessment)
    {
        var hit = assessment.Results.Where(r => r.Status == assessment.Status && r.Status != RuleStatus.Pass).Select(r => RuleNames.GetValueOrDefault(r.RuleId, r.RuleId));
        return assessment.Status == RuleStatus.Pass ? "Pass: no rule broken" : $"{assessment.Status}: {string.Join(", ", hit)}";
    }

    private static string ByModel(IReadOnlyDictionary<string, double> values) =>
        string.Join(" / ", ModelOrder.Select(m => values.TryGetValue(m, out var v) ? Output.Invariant($"{v:0.#}") : "-"));

    private static void WriteJson(TextWriter output, NepalRouteAssessment result, bool replay)
    {
        var document = new
        {
            routeId = result.RouteId,
            replay,
            legs = result.Legs.Select(l => new
            {
                legId = l.Assessment.LegId,
                date = l.Assessment.Date,
                status = l.Assessment.Status,
                results = l.Assessment.Results,
                distanceKm = Math.Round(l.DistanceKm, 1),
                hillKm = Math.Round(l.HillKm, 1),
                districts = l.Districts.Select(d => d.Id),
                officialLevels = l.Input.OfficialLevels,
                roadBlocked = l.Input.RoadBlocked,
                closures = l.Closures,
                hillRain = l.Input.HillRain,
                gust = l.Input.Gust,
            }),
            health = result.Health,
            provenance = result.Provenance,
            disclaimer = Output.Disclaimer,
            attributions = Output.Attributions,
        };
        output.WriteLine(JsonSerializer.Serialize(document, Json.Options));
    }
}

/// <summary>JSON settings for <c>--json</c>: camel case, enum names, indented.</summary>
internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
