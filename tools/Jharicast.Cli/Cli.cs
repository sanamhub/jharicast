using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Fetch;

namespace Jharicast.Cli;

/// <summary>The command tree. Exit codes: 0 done, 1 a source check found an official source failing or drifting, 2 the command could not run.</summary>
internal static class Cli
{
    public const int Done = 0;
    public const int SourceProblem = 1;
    public const int CouldNotRun = 2;

    public static async Task<int> RunAsync(string[] args, CliHost host, CancellationToken cancellationToken = default)
    {
        var root = Build(host);
        var configuration = new CommandLineConfiguration(root) { Output = host.Out, Error = host.Error };
        var parse = configuration.Parse(args);
        if (parse.Errors.Count > 0)
        {
            foreach (var error in parse.Errors)
            {
                await host.Error.WriteLineAsync("jharicast: " + error.Message).ConfigureAwait(false);
            }

            await host.Error.WriteLineAsync("Run jharicast --help for the commands and their options.").ConfigureAwait(false);
            return CouldNotRun;
        }

        return await parse.InvokeAsync(cancellationToken).ConfigureAwait(false);
    }

    // A plain command named jharicast, not RootCommand: RootCommand takes its name from the
    // executable, Jharicast.Cli, which is not what a user types.
    internal static Command Build(CliHost host)
    {
        var root = new Command("jharicast", "Jharicast: per-leg, per-day trip rules for Nepal from weather models, DHM warnings and DoR closures. Not an official forecast.");
        root.Options.Add(new System.CommandLine.Help.HelpOption());
        root.Options.Add(new VersionOption());
        root.Subcommands.Add(Route(host));
        return root;
    }

    private static Command Route(CliHost host)
    {
        var common = new CommonOptions();
        var from = new Option<string>("--from") { Description = "Start: a town (Birtamod, Pokhara) or latitude,longitude.", Required = true };
        var to = new Option<string>("--to") { Description = "End: a town or latitude,longitude. The same as --from for a rest day.", Required = true };
        var via = new Option<string[]>("--via") { Description = "A place the road must pass, in order. Repeat for more.", AllowMultipleArgumentsPerToken = false };
        var date = new Option<DateOnly>("--date") { Description = "Travel date, Nepal time, yyyy-MM-dd.", Required = true, CustomParser = DateArgument };
        var days = new Option<int>("--days") { Description = "Assess the same road on this many start dates from --date.", DefaultValueFactory = _ => 1 };
        var json = new Option<bool>("--json") { Description = "Print the assessments as JSON." };
        var command = new Command("route", "Which trip rules a leg breaks on a day, and why.") { from, to, via, date, days, json };
        common.AddTo(command);
        command.SetAction((parse, token) => Guard(host, async () =>
        {
            var count = parse.GetValue(days);
            if (count is < 1 or > 16)
            {
                throw new CliException("--days must be 1 to 16.");
            }

            var request = new RouteRequest(
                Places.Resolve(parse.GetValue(from)!),
                Places.Resolve(parse.GetValue(to)!),
                [.. (parse.GetValue(via) ?? []).Select(Places.Resolve)],
                parse.GetValue(date),
                count,
                parse.GetValue(json));
            using var wiring = Wiring.Create(common.Read(parse), host);
            return await RouteCommand.RunAsync(request, wiring, host.Out, token).ConfigureAwait(false);
        }));
        return command;
    }

    private static DateOnly DateArgument(System.CommandLine.Parsing.ArgumentResult result)
    {
        var text = result.Tokens.Count == 1 ? result.Tokens[0].Value : string.Empty;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }

        result.AddError($"'{text}' is not a date. Use yyyy-MM-dd.");
        return default;
    }

    internal static DateOnly ParseDate(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new CliException($"'{text}' is not a date. Use yyyy-MM-dd.");

    // One place turns every expected failure into a line on stderr and exit code 2. A refusal is
    // reported as the site's answer, and nothing retries around it (ADR-0007).
    private static async Task<int> Guard(CliHost host, Func<Task<int>> run)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (CliException e)
        {
            await host.Error.WriteLineAsync("jharicast: " + e.Message).ConfigureAwait(false);
        }
        catch (SourceUnavailableException e)
        {
            await host.Error.WriteLineAsync($"jharicast: refused before sending: {e.Message} Jharicast does not work around this. No assessment was made.").ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or TimeoutException)
        {
            await host.Error.WriteLineAsync($"jharicast: could not assess: {e.Message} No assessment was made.").ConfigureAwait(false);
        }

        return CouldNotRun;
    }

    /// <summary>Options every command takes.</summary>
    private sealed class CommonOptions
    {
        private readonly Option<string?> _fixtures = new("--fixtures") { Description = "Answer every request from recorded files in this directory. Nothing is fetched." };
        private readonly Option<string?> _now = new("--now") { Description = "With --fixtures: the time to replay at, ISO 8601 with offset." };
        private readonly Option<string?> _contact = new("--contact") { Description = $"Project contact mailbox for the User-Agent of live requests. Default: the {Wiring.ContactVariable} environment variable. Never a personal address." };
        private readonly Option<string?> _snapshots = new("--snapshots") { Description = "Where live snapshots are kept. Default: the local application data folder." };
        private readonly Option<string[]> _disable = new("--disable-host") { Description = "Never request this host (kill switch). Repeat for more." };
        private readonly Option<Uri> _osrm = new("--osrm") { Description = "OSRM server. Default: the public demo server, for personal use at one request per second.", DefaultValueFactory = _ => new Uri("https://router.project-osrm.org/") };

        public void AddTo(Command command)
        {
            command.Options.Add(_fixtures);
            command.Options.Add(_now);
            command.Options.Add(_contact);
            command.Options.Add(_snapshots);
            command.Options.Add(_disable);
            command.Options.Add(_osrm);
        }

        public CommonSettings Read(ParseResult parse)
        {
            DateTimeOffset? now = null;
            if (parse.GetValue(_now) is { } text)
            {
                now = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                    ? at
                    : throw new CliException($"--now: '{text}' is not an ISO 8601 time.");
            }

            return new CommonSettings(parse.GetValue(_fixtures), now, parse.GetValue(_contact), parse.GetValue(_snapshots), parse.GetValue(_disable) ?? [], parse.GetValue(_osrm)!);
        }
    }
}
