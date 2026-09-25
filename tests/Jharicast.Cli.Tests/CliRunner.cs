using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Cli;
using Microsoft.Extensions.Time.Testing;

namespace Jharicast.Cli.Tests;

internal sealed record CliRun(int ExitCode, string Out, string Error);

internal static class CliRunner
{
    /// <summary>
    /// The replay directory. Synthetic apart from the linked captures: Open-Meteo values are the
    /// storm report's section 7.2 day maxima for 26 to 28 Sep (ECMWF / GFS / ICON), GEFS 14 of
    /// 31 members over 40 km/h on 28 Sep, and filler for the other days; the OSRM line is the
    /// synthetic Birtamod to Lumbini fixture; DoR has no closure.
    /// </summary>
    public static string Replay => Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay");

    // 07:45 NPT on 24 Sep 2026, when the DHM capture and the report's model run were taken.
    public const string ReplayNow = "2026-09-24T07:45:00+05:45";

    public static async Task<CliRun> Run(HttpMessageHandler? network, Func<string, string?>? environment, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var host = new CliHost
        {
            Out = output,
            Error = error,
            Environment = environment ?? (_ => null),
            // Live-mode tests run at the replay time too, so their leg dates stay inside the forecast.
            Time = new FakeTimeProvider(DateTimeOffset.Parse(ReplayNow, CultureInfo.InvariantCulture)),
            Network = network,
            MinIntervalOverride = TimeSpan.Zero,
        };

        var code = await Cli.RunAsync(args, host, CancellationToken.None);
        return new CliRun(code, output.ToString(), error.ToString());
    }

    public static Task<CliRun> Replayed(params string[] args) => Run(null, null, [.. args, "--fixtures", Replay, "--now", ReplayNow]);
}

/// <summary>
/// Stands in for the network under <c>PoliteHttpHandler</c> in "live" tests. robots.txt comes
/// from a function; everything else from the replay directory. Nothing leaves the process.
/// </summary>
internal sealed class FakeNetwork(Func<string, string?> robots) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _replay = new(new FixtureHandler(CliRunner.Replay));

    public ConcurrentQueue<HttpRequestMessage> Seen { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Seen.Enqueue(request);
        if (request.RequestUri!.AbsolutePath == "/robots.txt")
        {
            return robots(request.RequestUri.Host) is { } content
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return await _replay.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _replay.Dispose();
        }

        base.Dispose(disposing);
    }
}
