using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.Fetch;
using Jharicast.OpenMeteo;
using Jharicast.Routing;

namespace Jharicast.Nepal;

/// <summary>One leg on its day: the rules' verdict, the inputs they were given, and the route facts behind them.</summary>
/// <param name="Assessment">What the rules say.</param>
/// <param name="Input">What the rules were given, so a caller can show the numbers.</param>
/// <param name="DistanceKm">Road length, km.</param>
/// <param name="HillKm">Length of the hill sections, km (ADR-0010).</param>
/// <param name="Districts">Districts crossed, in route order.</param>
/// <param name="Closures">DoR closures in force within 2 km of the route on the day.</param>
public sealed record NepalLegDay(LegDayAssessment Assessment, LegDayInput Input, double DistanceKm, double HillKm, IReadOnlyList<District> Districts, IReadOnlyList<RoadClosure> Closures);

/// <summary>A route's assessment: one entry per leg, plus the health and provenance of every input.</summary>
/// <param name="RouteId">The route's id.</param>
/// <param name="Legs">One entry per leg, in route order.</param>
/// <param name="Health">Health of every input, keyed by source id. An official source that is not fresh means its rule saw less than it should (ADR-0005 rule 3).</param>
/// <param name="Provenance">Where and when every input came from.</param>
public sealed record NepalRouteAssessment(string RouteId, IReadOnlyList<NepalLegDay> Legs, IReadOnlyDictionary<string, SourceHealth> Health, IReadOnlyList<Provenance> Provenance);

/// <summary>
/// Joins the Nepal sources, the router and Open-Meteo into the inputs of <see cref="RouteRuleSet"/>,
/// one per leg and day. Official levels come from DHM's current district warnings, road blocks
/// from DoR closures within 2 km of the route, and rain and gusts from the deterministic models
/// and the ensembles at the route's sample points (ADR-0005, ADR-0010, ADR-0011).
/// </summary>
/// <remarks>
/// <para>
/// DHM's warning maps give levels for each of three dates (<see cref="DhmWarningSnapshot.Days"/>),
/// and a leg gets the levels for its own date. The retired feed carried current levels with no
/// dates; those apply to today and the next days up to <see cref="OfficialHorizonDays"/>. A day
/// outside either has no official level, which the rule reports as no warning issued, not as clear,
/// except a date inside the horizon that the bulletin does not reach: that is Unknown.
/// </para>
/// <para>
/// Rain is the day maximum over hill sample points only (rule 3); gusts over every sample point
/// (rule 4). Ensemble members are ECMWF ENS and GEFS pooled, as in the report's own rain rule,
/// taken at the point whose pooled median is highest.
/// </para>
/// </remarks>
public sealed class NepalRouteAssessor
{
    internal const string RoutingSourceId = "routing";
    internal const string ElevationSourceId = "open-meteo.elevation";
    internal const string ForecastSourceId = "open-meteo.forecast";
    internal const string RainVariable = "precipitation_sum";
    internal const string GustVariable = "wind_gusts_10m_max";

    // ADR-0012 defaults. ICON-EPS stays out: it was rate-limited in the report.
    internal static readonly string[] DeterministicModels = ["ecmwf_ifs025", "gfs_seamless", "icon_seamless"];
    internal static readonly string[] EnsembleModels = ["ecmwf_ifs025", "gfs025"];

    private const double SampleStepKm = 5;
    private const double ClosureRadiusKm = 2;
    private const int MaxForecastDays = 16;

    private readonly OpenMeteoClient _openMeteo;
    private readonly ISource<DhmWarningSnapshot> _warnings;
    private readonly ISource<DorClosureSnapshot> _closures;
    private readonly IRouteProvider _routing;
    private readonly TimeProvider _time;
    private readonly RouteSampler _sampler;
    private readonly DistrictResolver _districts = new();

    /// <summary>Creates the assessor.</summary>
    /// <param name="openMeteo">Model values and elevations.</param>
    /// <param name="warnings">DHM's district warnings, normally <see cref="DhmWarningMapSource"/>.</param>
    /// <param name="closures">DoR closures, normally <see cref="DorClosureSource"/>.</param>
    /// <param name="routing">Road geometry for each leg.</param>
    /// <param name="timeProvider">Clock that decides today, Nepal time.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public NepalRouteAssessor(OpenMeteoClient openMeteo, ISource<DhmWarningSnapshot> warnings, ISource<DorClosureSnapshot> closures, IRouteProvider routing, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(openMeteo);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(closures);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _openMeteo = openMeteo;
        _warnings = warnings;
        _closures = closures;
        _routing = routing;
        _time = timeProvider;
        _sampler = new RouteSampler(openMeteo.GetElevationAsync);
    }

    /// <summary>
    /// Days, counting today, that undated warnings (the retired feed's) apply to. Default 3, the
    /// span of DHM's three-day forecast. Dated warnings ignore it. Raising it is more cautious; a
    /// leg past it has no official level.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Below 1.</exception>
    public int OfficialHorizonDays
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 3;

    /// <summary>Assesses every leg of a route on its date.</summary>
    /// <param name="route">The route. Every leg's date must be between today and 15 days from today, Nepal time.</param>
    /// <param name="rules">The rules, normally <see cref="RouteRuleSet.V1"/>.</param>
    /// <param name="cancellationToken">Cancels the fetches.</param>
    /// <returns>
    /// One entry per leg, with the health of every input. A failing DHM, DoR, elevation, forecast
    /// or ensemble source is reported in the health, not thrown, and the rules that read it say
    /// <see cref="RuleStatus.Unknown"/>. Without elevations every sample counts as hill, the
    /// cautious reading of rule 3.
    /// </returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A leg is dated before today or more than 15 days ahead, where there is no forecast.</exception>
    /// <exception cref="SourceUnavailableException">The router was refused before sending. Without a route there is nothing to assess.</exception>
    /// <exception cref="System.Net.Http.HttpRequestException">The router failed.</exception>
    /// <exception cref="System.Text.Json.JsonException">The router answered in an unexpected shape.</exception>
    /// <exception cref="InvalidOperationException">The router found no route.</exception>
    public async Task<NepalRouteAssessment> AssessAsync(Route route, RouteRuleSet rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(rules);
        var now = _time.GetUtcNow();
        var today = NepalTime.DateOf(now);
        foreach (var leg in route.Legs)
        {
            if (leg.Date < today || leg.Date.DayNumber - today.DayNumber >= MaxForecastDays)
            {
                throw new ArgumentOutOfRangeException(nameof(route), leg.Date, string.Create(CultureInfo.InvariantCulture, $"Leg {leg.Id} is dated {leg.Date:yyyy-MM-dd}; forecasts cover {today:yyyy-MM-dd} to {today.AddDays(MaxForecastDays - 1):yyyy-MM-dd}."));
            }
        }

        var health = new Dictionary<string, SourceHealth>(StringComparer.Ordinal);
        var provenance = new List<Provenance>();
        var warningsTask = _warnings.FetchAsync(cancellationToken);
        var closuresTask = _closures.FetchAsync(cancellationToken);
        var warnings = await warningsTask.ConfigureAwait(false);
        var closures = await closuresTask.ConfigureAwait(false);
        health[DhmWarningsParser.SourceId] = warnings.Health;
        health[DorClosureSource.SourceId] = closures.Health;
        AddIf(provenance, warnings.Value?.Provenance);
        AddIf(provenance, closures.Value?.Provenance);

        var sampled = new List<(Leg Leg, SampledRoute Route, IReadOnlyList<string> Districts)>();
        var geometries = new Dictionary<string, SampledRoute>(StringComparer.Ordinal);
        foreach (var leg in route.Legs)
        {
            GeoPoint[] waypoints = [leg.From, .. leg.Via, leg.To];
            var key = string.Join(';', waypoints.Select(p => string.Create(CultureInfo.InvariantCulture, $"{p.Latitude},{p.Longitude}")));
            if (!geometries.TryGetValue(key, out var samples))
            {
                var geometry = waypoints.Length == 2 && waypoints[0] == waypoints[1]
                    ? new RouteGeometry([leg.From], 0, TimeSpan.Zero)
                    : await _routing.GetRouteAsync(waypoints, cancellationToken).ConfigureAwait(false);
                samples = geometries[key] = await SampleAsync(geometry, health, cancellationToken).ConfigureAwait(false);
            }

            sampled.Add((leg, samples, _districts.DistrictsCrossed(samples)));
        }

        health[RoutingSourceId] = Fresh(now);
        health.TryAdd(ElevationSourceId, Fresh(now));

        GeoPoint[] points = [.. sampled.SelectMany(s => s.Route.Samples.Select(p => p.Point)).Distinct()];
        var days = route.Legs.Max(l => l.Date).DayNumber - today.DayNumber + 1;
        IReadOnlyDictionary<string, IReadOnlyList<ForecastDaily>> forecast = new Dictionary<string, IReadOnlyList<ForecastDaily>>(StringComparer.Ordinal);
        try
        {
            forecast = await _openMeteo.GetDailyAsync(points, DeterministicModels, [RainVariable, GustVariable], days, cancellationToken).ConfigureAwait(false);
            health[ForecastSourceId] = Fresh(now);
            provenance.AddRange(forecast.Values.Select(v => v[0].Provenance).OfType<Provenance>());
        }
        catch (Exception ex) when (IsSourceFailure(ex))
        {
            health[ForecastSourceId] = Unavailable(ex);
        }

        var ensembles = new List<IReadOnlyDictionary<string, IReadOnlyList<EnsembleDaily>>>();
        foreach (var model in EnsembleModels)
        {
            try
            {
                var ensemble = await _openMeteo.GetEnsembleDailyAsync(points, model, [RainVariable, GustVariable], days, cancellationToken).ConfigureAwait(false);
                ensembles.Add(ensemble);
                health["open-meteo.ensemble." + model] = Fresh(now);
                AddIf(provenance, ensemble[RainVariable][0].Provenance);
            }
            catch (Exception ex) when (IsSourceFailure(ex))
            {
                health["open-meteo.ensemble." + model] = Unavailable(ex);
            }
        }

        var index = points.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i);
        var legs = new List<NepalLegDay>();
        foreach (var (leg, samples, districtIds) in sampled)
        {
            var legPoints = samples.Samples.Select(s => index[s.Point]).ToArray();
            var hillPoints = samples.Samples.Where(s => s.IsHill).Select(s => index[s.Point]).ToArray();
            var inForce = ClosuresOnRoute(closures.Value, samples, leg.Date, now);
            var input = new LegDayInput(
                leg.Id,
                leg.Date,
                OfficialLevels(warnings.Value, districtIds, leg.Date, today),
                inForce.Count > 0,
                Sample(forecast, ensembles, RainVariable, hillPoints, leg.Date),
                Sample(forecast, ensembles, GustVariable, legPoints, leg.Date))
            {
                OfficialKnown = OfficialKnownOn(warnings.Value, leg.Date, today),
                RoadKnown = closures.Value is not null,
            };
            legs.Add(new NepalLegDay(rules.Evaluate(input), input, samples.DistanceKm, samples.HillKm, [.. districtIds.Select(Gazetteer.ById)], inForce));
        }

        return new NepalRouteAssessment(route.Id, legs, health, provenance);
    }

    private static SourceHealth Fresh(DateTimeOffset now) => new(SourceStatus.Fresh, null, now);

    // What a model or elevation source can fail with: refused before sending, a failed request or
    // timeout, or an answer in an unexpected shape. Cancellation by the caller is not one of them.
    private static bool IsSourceFailure(Exception ex) =>
        ex is SourceUnavailableException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException
        || ex is TaskCanceledException { InnerException: TimeoutException };

    private static SourceHealth Unavailable(Exception ex) =>
        new(ex is SourceUnavailableException ? SourceStatus.Disabled : SourceStatus.Failing, ex.Message, null);

    // Elevations are asked for until the first failure in this assessment. Without them the
    // samples come from the geometry alone and every one counts as hill, so rule 3 reads rain
    // along the whole route rather than missing a hill section.
    private async Task<SampledRoute> SampleAsync(RouteGeometry geometry, Dictionary<string, SourceHealth> health, CancellationToken cancellationToken)
    {
        if (!health.ContainsKey(ElevationSourceId))
        {
            try
            {
                return await _sampler.SampleAsync(geometry, SampleStepKm, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSourceFailure(ex))
            {
                health[ElevationSourceId] = Unavailable(ex);
            }
        }

        var points = Geo.Resample(geometry.Points, SampleStepKm);
        var samples = new List<RouteSample>(points.Count);
        var km = 0.0;
        for (var i = 0; i < points.Count; i++)
        {
            km += i == 0 ? 0 : Geo.DistanceKm(points[i - 1], points[i]);
            samples.Add(new RouteSample(points[i], km, double.NaN, IsHill: true));
        }

        return new SampledRoute(samples, km, km);
    }

    private static void AddIf(List<Provenance> list, Provenance? provenance)
    {
        if (provenance is not null)
        {
            list.Add(provenance);
        }
    }

    // Past the horizon there is no warning to know of. Inside it, a dated snapshot must cover the
    // date: a bulletin too old to reach it (DHM missed issues) is not "no warning issued".
    private bool OfficialKnownOn(DhmWarningSnapshot? warnings, DateOnly date, DateOnly today) =>
        date.DayNumber - today.DayNumber >= OfficialHorizonDays
        || warnings is { Days.Count: 0 }
        || (warnings is not null && warnings.Days.Contains(date));

    private Dictionary<string, AlertLevel> OfficialLevels(DhmWarningSnapshot? warnings, IReadOnlyList<string> districtIds, DateOnly date, DateOnly today)
    {
        var levels = new Dictionary<string, AlertLevel>(StringComparer.Ordinal);
        var dated = warnings is { Days.Count: > 0 };
        if (warnings is null || (dated ? !warnings.Days.Contains(date) : date.DayNumber - today.DayNumber >= OfficialHorizonDays))
        {
            return levels;
        }

        foreach (var id in districtIds)
        {
            var district = Gazetteer.ById(id);
            var level = dated ? warnings.LevelOn(district, date) : warnings.LevelFor(district);
            if (level > AlertLevel.Green)
            {
                levels[id] = level;
            }
        }

        return levels;
    }

    // A closure counts for a day when it has not ended now and starts before the day ends. DoR's
    // estimated end is not trusted to reopen a road on a later day (DorClosureSnapshot.ActiveAt).
    // Distance is to the road between samples, not to the samples alone: with samples 5 km apart,
    // a closure on the road midway would be 2.5 km from both.
    private static List<RoadClosure> ClosuresOnRoute(DorClosureSnapshot? closures, SampledRoute route, DateOnly date, DateTimeOffset now)
    {
        if (closures is null)
        {
            return [];
        }

        var dayEnd = NepalTime.ToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var samples = route.Samples;
        return
        [
            .. closures.Closures.Where(c =>
                c.Location is { } at
                && (c.EndedAt is null || c.EndedAt > now)
                && (c.StartsAt is null || c.StartsAt < dayEnd)
                && (samples.Count == 1
                    ? Geo.DistanceKm(at, samples[0].Point) <= ClosureRadiusKm
                    : samples.Zip(samples.Skip(1)).Any(s => Geo.DistanceToSegmentKm(at, s.First.Point, s.Second.Point) <= ClosureRadiusKm))),
        ];
    }

    private static ModelSample Sample(
        IReadOnlyDictionary<string, IReadOnlyList<ForecastDaily>> forecast,
        List<IReadOnlyDictionary<string, IReadOnlyList<EnsembleDaily>>> ensembles,
        string variable,
        int[] points,
        DateOnly date)
    {
        var deterministic = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var model in DeterministicModels)
        {
            if (!forecast.TryGetValue(model, out var series))
            {
                continue;
            }

            var values = points
                .Select(i => series[i])
                .Select(f => (Day: IndexOf(f.Days, date), f))
                .Where(x => x.Day >= 0 && x.f.Variables.TryGetValue(variable, out var v) && v[x.Day].HasValue)
                .Select(x => x.f.Variables[variable][x.Day]!.Value)
                .ToArray();
            if (values.Length > 0)
            {
                deterministic[model] = values.Max();
            }
        }

        IReadOnlyList<double?> worst = [];
        var worstMedian = double.NegativeInfinity;
        foreach (var i in points)
        {
            List<double?> pooled = [];
            foreach (var ensemble in ensembles)
            {
                var daily = ensemble[variable][i];
                var day = IndexOf(daily.Days, date);
                if (day >= 0)
                {
                    pooled.AddRange(daily.Members[day]);
                }
            }

            if (pooled.Any(m => m.HasValue) && EnsembleStats.Quantile(pooled, 0.5) is var median && median > worstMedian)
            {
                worstMedian = median;
                worst = pooled;
            }
        }

        return new ModelSample(deterministic, worst) { Known = forecast.Count > 0 || ensembles.Count > 0 };
    }

    private static int IndexOf(IReadOnlyList<DateOnly> days, DateOnly date)
    {
        for (var i = 0; i < days.Count; i++)
        {
            if (days[i] == date)
            {
                return i;
            }
        }

        return -1;
    }
}
