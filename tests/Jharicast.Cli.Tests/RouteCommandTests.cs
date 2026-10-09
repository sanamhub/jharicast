using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jharicast.Cli;
using Xunit;

namespace Jharicast.Cli.Tests;

public sealed class RouteCommandTests
{
    // AC-6.1: the report's D1 verdict for a 28 Sep start is a wind watch and nothing else.
    [Fact]
    public async Task Route_on_recorded_fixtures_prints_the_D1_verdict_for_28_Sep()
    {
        var run = await CliRunner.Replayed("route", "--from", "Birtamod", "--to", "Lumbini", "--date", "2026-09-28");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(string.Empty, run.Error);
        var row = run.Out.Split('\n').Single(l => l.StartsWith("2026-09-28  Birtamod to Lumbini", StringComparison.Ordinal));
        Assert.Contains("1 / 10 / 1", row, StringComparison.Ordinal);
        Assert.Contains("35 / 31 / 24", row, StringComparison.Ordinal);
        Assert.EndsWith("Watch: wind", row.TrimEnd(), StringComparison.Ordinal);
        Assert.Contains("wind       Watch", run.Out, StringComparison.Ordinal);
        Assert.Contains("hill rain  Pass", run.Out, StringComparison.Ordinal);
        Assert.Contains("road       Pass   open", run.Out, StringComparison.Ordinal);
        Assert.Contains("official   Pass   no DHM warning covers this day yet", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_route_output_ends_with_data_times_health_disclaimer_and_attributions()
    {
        var run = await CliRunner.Replayed("route", "--from", "Birtamod", "--to", "Lumbini", "--date", "2026-09-28");

        var health = run.Out.IndexOf("Data times and health", StringComparison.Ordinal);
        var disclaimer = run.Out.IndexOf(Output.Disclaimer, StringComparison.Ordinal);
        Assert.True(health > 0 && disclaimer > health);
        Assert.Contains("dhm.warnings", run.Out[health..], StringComparison.Ordinal);
        Assert.Contains("fetched 2026-09-24 07:45 NPT", run.Out, StringComparison.Ordinal);
        Assert.Contains("Replayed from recorded files", run.Out, StringComparison.Ordinal);
        Assert.All(Output.Attributions, a => Assert.Contains(a, run.Out, StringComparison.Ordinal));
        Assert.Contains("Weather data by Open-Meteo.com (CC BY 4.0)", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("safe", run.Out, StringComparison.OrdinalIgnoreCase);
    }

    // Section 7.2 of the report: a 26 Sep start breaks wind, hill rain and the DHM rule; 27 Sep
    // wind and hill rain; 28 Sep is the wind watch.
    [Fact]
    public async Task Three_start_dates_reproduce_the_reports_start_day_table()
    {
        var run = await CliRunner.Replayed("route", "--from", "Birtamod", "--to", "Lumbini", "--date", "2026-09-26", "--days", "3");

        Assert.Equal(0, run.ExitCode);
        string Flag(string date) => run.Out.Split('\n').Single(l => l.StartsWith(date + "  Birtamod", StringComparison.Ordinal)).Split("  ").Last().Trim();
        Assert.Equal("Breach: official, hill rain, wind", Flag("2026-09-26"));
        Assert.Equal("Breach: hill rain, wind", Flag("2026-09-27"));
        Assert.Equal("Watch: wind", Flag("2026-09-28"));
        Assert.Contains("Official DHM levels: ", run.Out, StringComparison.Ordinal);
        Assert.Contains("Jhapa Orange", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_carries_the_assessments_health_and_notices()
    {
        var run = await CliRunner.Replayed("route", "--from", "Birtamod", "--to", "Lumbini", "--date", "2026-09-28", "--json");

        Assert.Equal(0, run.ExitCode);
        using var document = JsonDocument.Parse(run.Out);
        var root = document.RootElement;
        var leg = root.GetProperty("legs")[0];
        Assert.Equal("Watch", leg.GetProperty("status").GetString());
        Assert.Equal("2026-09-28", leg.GetProperty("date").GetString());
        Assert.Equal(35, leg.GetProperty("gust").GetProperty("deterministic").GetProperty("ecmwf_ifs025").GetDouble());
        Assert.Equal("jhapa", leg.GetProperty("districts")[0].GetString());
        Assert.Equal("Fresh", root.GetProperty("health").GetProperty("dhm.warnings").GetProperty("status").GetString());
        Assert.True(root.GetProperty("replay").GetBoolean());
        Assert.Equal(Output.Disclaimer, root.GetProperty("disclaimer").GetString());
        Assert.Equal(4, root.GetProperty("attributions").GetArrayLength());
    }

    [Fact]
    public async Task Coordinates_work_in_place_of_town_names()
    {
        var run = await CliRunner.Replayed("route", "--from", "26.6434,87.9915", "--to", "27.4702,83.2852", "--date", "2026-09-28");

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("26.6434,87.9915 to 27.4702,83.2852", run.Out, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Atlantis", "is not a town Jharicast knows")]
    [InlineData("Khalanga", "names 2 towns")]
    [InlineData("95,10", "not a valid latitude,longitude")]
    public async Task A_place_that_does_not_resolve_is_a_one_line_error(string place, string message)
    {
        var run = await CliRunner.Replayed("route", "--from", place, "--to", "Lumbini", "--date", "2026-09-28");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains(message, run.Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, run.Out);
    }

    [Fact]
    public async Task A_date_without_a_forecast_is_a_one_line_error()
    {
        var run = await CliRunner.Replayed("route", "--from", "Birtamod", "--to", "Lumbini", "--date", "2026-09-20");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("forecasts cover 2026-09-24 to 2026-10-09", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Now_without_fixtures_is_refused()
    {
        var run = await CliRunner.Run(null, null, "route", "--from", "Birtamod", "--to", "Lumbini", "--date", "2026-09-28", "--now", CliRunner.ReplayNow);

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("--now replays recorded data and needs --fixtures", run.Error, StringComparison.Ordinal);
    }

    // The contact rule: required for any live request, never defaulted, read from an option or
    // the environment, and nothing is sent without it.
    [Fact]
    public async Task A_live_run_without_a_contact_mailbox_fails_before_any_request()
    {
        using var network = new FakeNetwork(_ => null);

        var run = await CliRunner.Run(network, null, "route", "--from", "Birtamod", "--to", "Lumbini", "--date", "2026-09-28");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("A contact mailbox is required", run.Error, StringComparison.Ordinal);
        Assert.Contains(Wiring.ContactVariable, run.Error, StringComparison.Ordinal);
        Assert.Empty(network.Seen);
    }

    [Theory]
    [InlineData("not-a-mailbox")]
    [InlineData("ops@example.org) evil")]
    [InlineData("ops@example.org;x")]
    public void A_contact_that_is_not_a_plain_mailbox_is_refused(string contact)
    {
        var error = Assert.Throws<CliException>(() => Wiring.UserAgent(contact));

        Assert.Contains("is not a mailbox", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_user_agent_names_the_repository_and_the_contact()
    {
        var agent = Wiring.UserAgent("ops@example.org");

        // Any version: the test must not fail on every release.
        Assert.Matches(@"^Jharicast/\d+\.\d+\.\d+(-[0-9A-Za-z.]+)? \(\+https://github\.com/sanamhub/jharicast; ops@example\.org\)", agent);
    }

    // Open-Meteo's robots.txt is the maintainer's open question. When a host says no, the CLI
    // says so, names the host, and makes no assessment rather than one without model data.
    [Fact]
    public async Task A_model_host_whose_robots_txt_disallows_us_is_reported_not_worked_around()
    {
        using var network = new FakeNetwork(host => host == "api.open-meteo.com" ? "User-agent: *\nDisallow: /\n" : null);
        var snapshots = Path.Combine(Path.GetTempPath(), "jharicast-cli-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var run = await CliRunner.Run(network, v => v == Wiring.ContactVariable ? "ops@example.org" : null, "route", "--from", "Birtamod", "--to", "Lumbini", "--date", "2026-09-28", "--snapshots", snapshots);

            // The official and road rules still answer; the model rules say Unknown, and the
            // refusal is shown in the health table instead of being worked around.
            Assert.Equal(0, run.ExitCode);
            Assert.Contains("robots.txt on api.open-meteo.com disallows", run.Out, StringComparison.Ordinal);
            Assert.Contains("open-meteo.forecast               model     Disabled", run.Out, StringComparison.Ordinal);
            Assert.DoesNotContain(network.Seen, r => r.RequestUri!.Host == "api.open-meteo.com" && r.RequestUri.AbsolutePath != "/robots.txt");
            Assert.All(network.Seen, r => Assert.Contains("ops@example.org", r.Headers.UserAgent.ToString(), StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(snapshots))
            {
                Directory.Delete(snapshots, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_disabled_host_is_reported_as_disabled()
    {
        using var network = new FakeNetwork(_ => null);
        var snapshots = Path.Combine(Path.GetTempPath(), "jharicast-cli-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var run = await CliRunner.Run(network, null, "route", "--from", "Birtamod", "--to", "Lumbini", "--date", "2026-09-28", "--contact", "ops@example.org", "--snapshots", snapshots, "--disable-host", "router.project-osrm.org");

            Assert.Equal(2, run.ExitCode);
            Assert.Contains("router.project-osrm.org is disabled by configuration", run.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(network.Seen, r => r.RequestUri!.Host == "router.project-osrm.org");
        }
        finally
        {
            if (Directory.Exists(snapshots))
            {
                Directory.Delete(snapshots, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_missing_option_is_exit_code_2_with_the_parsers_message()
    {
        var run = await CliRunner.Replayed("route", "--from", "Birtamod", "--date", "2026-09-28");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("Option '--to' is required.", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_date_in_another_format_is_refused_by_the_parser()
    {
        var run = await CliRunner.Replayed("route", "--from", "Birtamod", "--to", "Lumbini", "--date", "28/09/2026");

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("'28/09/2026' is not a date. Use yyyy-MM-dd.", run.Error, StringComparison.Ordinal);
    }
}
