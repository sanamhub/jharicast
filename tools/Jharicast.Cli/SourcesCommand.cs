using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Fetch;
using Jharicast.Nepal;

namespace Jharicast.Cli;

/// <summary>
/// <c>jharicast sources check</c>: fetches each source once, politely, and prints its health.
/// Exit code 1 when an official source is failing or drifting, the two states a maintainer must
/// act on (ADR-0008). A disabled source is printed but does not fail the check: it is a
/// decision already made, by configuration or by the site's robots.txt.
/// </summary>
internal static class SourcesCommand
{
    // DHM's gauge feed has a parser but no source class; the check reads it directly.
    internal static readonly Uri GaugesUrl = new("https://dhm.gov.np/home/getAPIData/3");

    // One point and one day: the cheapest request that proves the forecast API answers.
    private static readonly GeoPoint Kathmandu = new(27.708317, 85.3205817);

    public static async Task<int> RunAsync(Wiring wiring, TextWriter output, CancellationToken cancellationToken)
    {
        var health = new Dictionary<string, SourceHealth>(StringComparer.Ordinal);
        var provenance = new List<Provenance>();

        async Task Check<T>(string id, ISource<T> source, Func<T, Provenance> origin)
            where T : class
        {
            var result = await source.FetchAsync(cancellationToken).ConfigureAwait(false);
            health[id] = result.Health;
            if (result.Value is { } value)
            {
                provenance.Add(origin(value));
            }
        }

        await Check(DhmWarningsParser.SourceId, wiring.Warnings(), v => v.Provenance).ConfigureAwait(false);
        health[DhmGaugeParser.SourceId] = await Gauges(wiring, provenance, cancellationToken).ConfigureAwait(false);
        await Check(DhmBulletinSource.SourceId, new DhmBulletinSource(wiring.Http, wiring.Store, wiring.Time), v => v.Provenance).ConfigureAwait(false);
        await Check(BipadAlertSource.SourceId, new BipadAlertSource(wiring.Http, wiring.Store, wiring.Time), v => v.Provenance).ConfigureAwait(false);
        await Check(BipadIncidentSource.SourceId, new BipadIncidentSource(wiring.Http, wiring.Store, wiring.Time), v => v.Provenance).ConfigureAwait(false);
        await Check(DorClosureSource.SourceId, wiring.Closures(), v => v.Provenance).ConfigureAwait(false);
        health["open-meteo.forecast"] = await Forecast(wiring, provenance, cancellationToken).ConfigureAwait(false);

        return Write(output, health, provenance, wiring.IsReplay);
    }

    private static int Write(TextWriter output, Dictionary<string, SourceHealth> health, List<Provenance> provenance, bool replay)
    {
        Output.Health(output, health, provenance);
        Output.Notices(output, replay);
        var failed = health.Where(h => Output.Kind(h.Key) == "official" && h.Value.Status is SourceStatus.Failing or SourceStatus.Drifting).Select(h => h.Key).ToArray();
        if (failed.Length > 0)
        {
            output.WriteLine();
            output.WriteLine("Official sources failing or drifting: " + string.Join(", ", failed));
            return Cli.SourceProblem;
        }

        return Cli.Done;
    }

    private static async Task<SourceHealth> Gauges(Wiring wiring, List<Provenance> provenance, CancellationToken cancellationToken)
    {
        return await Guarded(async () =>
        {
            using var response = await wiring.Http.GetAsync(GaugesUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new SourceHealth(SourceStatus.Failing, Output.Invariant($"HTTP {(int)response.StatusCode} from {GaugesUrl.Host}"), null);
            }

            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = DhmGaugeParser.Parse(body, wiring.Time.GetUtcNow());
            provenance.Add(snapshot.Provenance);
            return snapshot.Drift.Count > 0
                ? new SourceHealth(SourceStatus.Drifting, $"{DhmGaugeParser.SourceId}: unrecognised {string.Join(", ", snapshot.Drift)}", null)
                : new SourceHealth(SourceStatus.Fresh, null, snapshot.Provenance.FetchedAt);
        }).ConfigureAwait(false);
    }

    private static async Task<SourceHealth> Forecast(Wiring wiring, List<Provenance> provenance, CancellationToken cancellationToken)
    {
        return await Guarded(async () =>
        {
            var result = await wiring.OpenMeteo().GetDailyAsync([Kathmandu], ["ecmwf_ifs025"], ["precipitation_sum"], 1, cancellationToken).ConfigureAwait(false);
            if (result["ecmwf_ifs025"][0].Provenance is { } p)
            {
                provenance.Add(p);
            }

            return new SourceHealth(SourceStatus.Fresh, null, wiring.Time.GetUtcNow());
        }).ConfigureAwait(false);
    }

    // The same mapping the Nepal sources use: a refusal by configuration or robots.txt is
    // disabled, anything else that stops the fetch is failing.
    private static async Task<SourceHealth> Guarded(Func<Task<SourceHealth>> fetch)
    {
        try
        {
            return await fetch().ConfigureAwait(false);
        }
        catch (SourceUnavailableException e)
        {
            var disabled = e.Message.Contains("disabled", StringComparison.Ordinal) || e.Message.Contains("robots.txt", StringComparison.Ordinal);
            return new SourceHealth(disabled ? SourceStatus.Disabled : SourceStatus.Failing, e.Message, null);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TimeoutException)
        {
            return new SourceHealth(SourceStatus.Failing, e.Message, null);
        }
    }
}
