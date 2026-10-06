using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Jharicast.Cli;
using Jharicast.Nepal;
using Xunit;

namespace Jharicast.Cli.Tests;

public sealed class AlertsAndSourcesTests
{
    // AC-6.2: province by day, "ours / DHM", then the score table, as in the report's section 4.2.
    [Fact]
    public async Task Alerts_on_recorded_fixtures_print_the_own_versus_dhm_table()
    {
        var run = await CliRunner.Replayed("alerts", "--date", "2026-09-24");

        Assert.Equal(0, run.ExitCode);
        var lines = run.Out.Split('\n').Select(l => l.TrimEnd()).ToArray();
        var header = Array.FindIndex(lines, l => l.StartsWith("Province", StringComparison.Ordinal));
        Assert.Equal(["Province", "24 Sep", "25 Sep", "26 Sep", "27 Sep", "28 Sep"], lines[header].Split("  ", StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()));
        var rows = lines.Skip(header + 2).Take(7).ToArray();
        Assert.Equal(Enum.GetNames<Province>(), rows.Select(r => r.Split(' ')[0]));
        Assert.StartsWith("Koshi          Yellow / Orange", rows[0], StringComparison.Ordinal);
        Assert.All(rows, r => Assert.EndsWith("/ -", r, StringComparison.Ordinal)); // 28 Sep is past DHM's current warnings

        Assert.Contains(lines, l => l.StartsWith("Same level", StringComparison.Ordinal) && l.EndsWith("of 21", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Within one level", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Ours lower than DHM", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Ours higher than DHM", StringComparison.Ordinal));
        Assert.Contains(Output.Disclaimer, run.Out, StringComparison.Ordinal);
        Assert.Contains("Data times and health", run.Out, StringComparison.Ordinal);
    }

    // The report's own table: 12 of 28 the same, 27 within one, ours lower 14 times, higher twice.
    [Fact]
    public void Score_reproduces_the_reports_counts_from_its_table()
    {
        string[][] table =
        [
            ["OO", "OR", "GY", "GG"], // Koshi
            ["OY", "YY", "GG", "GG"], // Madhesh
            ["OO", "OR", "GY", "GY"], // Bagmati
            ["YO", "OR", "OR", "YY"], // Gandaki
            ["YO", "RO", "OO", "YY"], // Lumbini
            ["GY", "OO", "OR", "GY"], // Karnali
            ["GG", "GO", "OR", "YY"], // Sudurpashchim
        ];
        static AlertLevel Level(char c) => c switch { 'G' => AlertLevel.Green, 'Y' => AlertLevel.Yellow, 'O' => AlertLevel.Orange, _ => AlertLevel.Red };
        var cells = table.SelectMany((row, p) => row.Select((cell, d) =>
            new AlertsCommand.Cell((Province)(p + 1), new DateOnly(2026, 9, 24 + d), Level(cell[0]), Level(cell[1]), null, 0, 0))).ToList();

        Assert.Equal(new AlertsCommand.AlertScore(28, 12, 27, 14, 2), AlertsCommand.Score(cells));
    }

    [Fact]
    public async Task Alerts_json_carries_cells_score_and_notices()
    {
        var run = await CliRunner.Replayed("alerts", "--date", "2026-09-24", "--days", "2", "--json");

        Assert.Equal(0, run.ExitCode);
        using var document = JsonDocument.Parse(run.Out);
        Assert.Equal(14, document.RootElement.GetProperty("cells").GetArrayLength());
        Assert.Equal(14, document.RootElement.GetProperty("score").GetProperty("compared").GetInt32());
        Assert.Equal(Output.Disclaimer, document.RootElement.GetProperty("disclaimer").GetString());
    }

    [Fact]
    public async Task Sources_check_on_recorded_fixtures_prints_every_source_and_passes()
    {
        var run = await CliRunner.Run(null, null, "sources", "check", "--fixtures", CliRunner.Replay, "--now", "2026-09-25T20:00:00+05:45");

        Assert.Equal(0, run.ExitCode);
        foreach (var id in new[] { "dhm.warnings", "dhm.gauges", "dhm.bulletin", "bipad.alerts", "bipad.incidents", "dor.closures", "open-meteo.forecast" })
        {
            Assert.Contains(run.Out.Split('\n'), l => l.StartsWith(id + " ", StringComparison.Ordinal) && l.Contains("Fresh", StringComparison.Ordinal));
        }

        Assert.Contains(Output.Disclaimer, run.Out, StringComparison.Ordinal);
    }

    // A 403 is the site saying no: reported once, not retried, and the check fails (ADR-0007).
    [Fact]
    public async Task Sources_check_exits_1_when_an_official_source_refuses()
    {
        var run = await Live(uri => uri.AbsolutePath == ClosuresPath ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null, out var network);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains(run.Out.Split('\n'), l => l.StartsWith("dor.closures ", StringComparison.Ordinal) && l.Contains("Failing", StringComparison.Ordinal) && l.Contains("HTTP 403", StringComparison.Ordinal));
        Assert.Contains("Official sources failing or drifting: dor.closures", run.Out, StringComparison.Ordinal);
        Assert.Single(network.Seen, r => r.RequestUri!.AbsolutePath == ClosuresPath);
    }

    [Fact]
    public async Task Sources_check_exits_1_when_an_official_source_drifts()
    {
        var drifted = """[{"road_refno":"H01","latitude":27.1,"longitude":85.0,"startDate":"2026-09-26 06:30:00"}]""";
        var run = await Live(uri => uri.AbsolutePath == ClosuresPath ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(drifted) } : null, out _);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("unrecognised missing:date_roadblock_start", run.Out, StringComparison.Ordinal);
    }

    // DHM's feed stopped updating (2026-10-06): a live run reports it disabled and never asks for it.
    [Fact]
    public async Task A_live_run_reports_the_retired_dhm_feed_and_sends_it_no_request()
    {
        var run = await Live(_ => null, out var network);

        Assert.Contains(run.Out.Split('\n'), l => l.StartsWith("dhm.warnings ", StringComparison.Ordinal) && l.Contains("Disabled", StringComparison.Ordinal));
        Assert.DoesNotContain(network.Seen, r => r.RequestUri!.AbsolutePath == "/home/getAPIData/1");
    }

    private const string ClosuresPath = "/api/Map_data_api/getRoadClosureMapData";

    // Open-Meteo's robots.txt is the open question for this round. A disallow shows as disabled,
    // with the reason, and is not a failure of the official sources.
    [Fact]
    public async Task Sources_check_reports_a_robots_txt_refusal_as_disabled()
    {
        var run = await Live(null, out var network, robots: host => host == "api.open-meteo.com" ? "User-agent: *\nDisallow: /\n" : null);

        Assert.Equal(0, run.ExitCode);
        var line = run.Out.Split('\n').Single(l => l.StartsWith("open-meteo.forecast ", StringComparison.Ordinal));
        Assert.Contains("Disabled", line, StringComparison.Ordinal);
        Assert.Contains("robots.txt on api.open-meteo.com disallows /v1/forecast", line, StringComparison.Ordinal);
        Assert.DoesNotContain(network.Seen, r => r.RequestUri!.AbsolutePath == "/v1/forecast");
    }

    private static Task<CliRun> Live(Func<Uri, HttpResponseMessage?>? answer, out FakeNetwork network, Func<string, string?>? robots = null)
    {
        network = new FakeNetwork(robots ?? (_ => null), answer);
        return RunAndClean(network);
    }

    private static async Task<CliRun> RunAndClean(FakeNetwork network)
    {
        var snapshots = Path.Combine(Path.GetTempPath(), "jharicast-cli-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            return await CliRunner.Run(network, null, "sources", "check", "--contact", "ops@example.org", "--snapshots", snapshots);
        }
        finally
        {
            if (Directory.Exists(snapshots))
            {
                Directory.Delete(snapshots, recursive: true);
            }
        }
    }
}
