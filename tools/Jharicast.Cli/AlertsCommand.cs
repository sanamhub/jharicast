using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Fetch;
using Jharicast.Nepal;

namespace Jharicast.Cli;

/// <summary>What <c>jharicast alerts</c> was asked.</summary>
internal sealed record AlertsRequest(DateOnly Date, int Days, bool Json);

/// <summary>
/// <c>jharicast alerts</c>: our rain level against DHM's by province and day, in the shape of the
/// storm report's own-versus-DHM table. Ours is <see cref="RainAlertRule.V1"/> over ECMWF ENS and
/// GEFS members pooled at each district headquarters; a province takes its worst district. DHM's
/// is its highest current rainfall warning in the province, shown for the days its current
/// warnings cover and blank after.
/// </summary>
internal static class AlertsCommand
{
    // The NepalRouteAssessor default: DHM's current warnings stand for today and the next two days.
    internal const int OfficialHorizonDays = 3;

    private const string Variable = "precipitation_sum";

    private static readonly string[] Ensembles = ["ecmwf_ifs025", "gfs025"];

    public static async Task<int> RunAsync(AlertsRequest request, Wiring wiring, TextWriter output, CancellationToken cancellationToken)
    {
        var now = wiring.Time.GetUtcNow();
        var today = NepalTime.DateOf(now);
        var last = request.Date.AddDays(request.Days - 1);
        if (request.Date < today || last.DayNumber - today.DayNumber >= 16)
        {
            throw new CliException(Output.Invariant($"Days must fall between {today:yyyy-MM-dd} and {today.AddDays(15):yyyy-MM-dd}, where there is a forecast."));
        }

        var warnings = await wiring.Warnings().FetchAsync(cancellationToken).ConfigureAwait(false);
        var towns = Gazetteer.Towns.Where(t => t.IsHeadquarters).ToArray();
        var client = wiring.OpenMeteo();
        var health = new Dictionary<string, SourceHealth>(StringComparer.Ordinal) { [DhmWarningsParser.SourceId] = warnings.Health };
        var provenance = new List<Provenance>();
        if (warnings.Value is not null)
        {
            provenance.Add(warnings.Value.Provenance);
        }

        var pooled = towns.Select(_ => new Dictionary<DateOnly, List<double?>>()).ToArray();
        foreach (var model in Ensembles)
        {
            var result = await client.GetEnsembleDailyAsync([.. towns.Select(t => t.Location)], model, [Variable], last.DayNumber - today.DayNumber + 1, cancellationToken).ConfigureAwait(false);
            health["open-meteo.ensemble." + model] = new SourceHealth(SourceStatus.Fresh, null, now);
            for (var i = 0; i < towns.Length; i++)
            {
                var daily = result[Variable][i];
                if (i == 0 && daily.Provenance is { } p)
                {
                    provenance.Add(p);
                }

                for (var d = 0; d < daily.Days.Count; d++)
                {
                    if (!pooled[i].TryGetValue(daily.Days[d], out var members))
                    {
                        pooled[i][daily.Days[d]] = members = [];
                    }

                    members.AddRange(daily.Members[d]);
                }
            }
        }

        var days = Enumerable.Range(0, request.Days).Select(request.Date.AddDays).ToArray();
        var cells = new List<Cell>();
        foreach (var province in Enum.GetValues<Province>())
        {
            foreach (var day in days)
            {
                var candidates = towns.Select((t, i) => (Town: t, Members: pooled[i].GetValueOrDefault(day)))
                    .Where(x => Gazetteer.Districts.Any(d => d.Id == x.Town.DistrictId && d.Province == province) && x.Members is { } m && m.Any(v => v.HasValue))
                    .Select(x => (x.Town, Level: RainAlertRule.V1.Evaluate(x.Members!), Median: EnsembleStats.Quantile(x.Members!, 0.5), High: EnsembleStats.Quantile(x.Members!, 0.9)))
                    .OrderByDescending(x => x.Level).ThenByDescending(x => x.Median)
                    .ToArray();
                AlertLevel? official = warnings.Value is { } w && day.DayNumber - today.DayNumber < OfficialHorizonDays
                    ? AlertLevels.Max(Gazetteer.Districts.Where(d => d.Province == province).Select(d => w.LevelFor(d, Hazard.Rainfall)))
                    : null;
                cells.Add(candidates.Length == 0
                    ? new Cell(province, day, null, official, null, 0, 0)
                    : new Cell(province, day, candidates[0].Level, official, candidates[0].Town.Name, candidates[0].Median, candidates[0].High));
            }
        }

        Write(output, request, cells, health, provenance, days, today, wiring.IsReplay);
        return Cli.Done;
    }

    private static void Write(TextWriter output, AlertsRequest request, List<Cell> cells, Dictionary<string, SourceHealth> health, List<Provenance> provenance, DateOnly[] days, DateOnly today, bool replay)
    {
        if (request.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(new { cells, score = Score(cells), health, provenance, replay, disclaimer = Output.Disclaimer, attributions = Output.Attributions }, Json.Options));
            return;
        }

        output.WriteLine("Our rain level / DHM's, by province. Ours: RainAlertRule V1 over ECMWF ENS and GEFS pooled at district headquarters. DHM: current rainfall warnings, which cover " + Output.Invariant($"{today:yyyy-MM-dd} to {today.AddDays(OfficialHorizonDays - 1):yyyy-MM-dd}; '-' where none is issued."));
        output.WriteLine();
        Output.Table(
            output,
            ["Province", .. days.Select(d => Output.Invariant($"{d:dd MMM}"))],
            [.. Enum.GetValues<Province>().Select(p => new[] { p.ToString() }.Concat(days.Select(d => cells.Single(c => c.Province == p && c.Date == d).Text)).ToArray())]);

        var score = Score(cells);
        output.WriteLine();
        Output.Table(
            output,
            ["Score", "Count"],
            [
                ["Same level", Output.Invariant($"{score.Same} of {score.Compared}")],
                ["Within one level", Output.Invariant($"{score.WithinOne} of {score.Compared}")],
                ["Ours lower than DHM", Output.Invariant($"{score.OursLower}")],
                ["Ours higher than DHM", Output.Invariant($"{score.OursHigher}")],
            ]);

        var setters = cells.Where(c => c.Ours >= AlertLevel.Orange).ToArray();
        if (setters.Length > 0)
        {
            output.WriteLine();
            output.WriteLine("Point that set each of our Orange and Red levels (pooled median / 1-in-10 high, mm)");
            Output.Table(
                output,
                ["Province, day", "Point", "Median", "1-in-10 high"],
                [.. setters.Select(c => new[] { Output.Invariant($"{c.Province} {c.Date:dd MMM}"), c.Point!, Output.Invariant($"{c.Median:0}"), Output.Invariant($"{c.High:0}") })]);
        }

        Output.Health(output, health, provenance);
        Output.Notices(output, replay);
    }

    internal static AlertScore Score(IReadOnlyList<Cell> cells)
    {
        var compared = cells.Where(c => c.Ours is not null && c.Official is not null).Select(c => (Ours: (int)c.Ours!.Value, Dhm: (int)c.Official!.Value)).ToArray();
        return new AlertScore(
            compared.Length,
            compared.Count(c => c.Ours == c.Dhm),
            compared.Count(c => Math.Abs(c.Ours - c.Dhm) <= 1),
            compared.Count(c => c.Ours < c.Dhm),
            compared.Count(c => c.Ours > c.Dhm));
    }

    /// <summary>One province and day.</summary>
    internal sealed record Cell(Province Province, DateOnly Date, AlertLevel? Ours, AlertLevel? Official, string? Point, double Median, double High)
    {
        public string Text => $"{(Ours?.ToString() ?? "-")} / {(Official?.ToString() ?? "-")}";
    }

    internal sealed record AlertScore(int Compared, int Same, int WithinOne, int OursLower, int OursHigher);
}
