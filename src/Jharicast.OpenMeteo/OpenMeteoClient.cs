using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jharicast.OpenMeteo;

/// <summary>
/// Calls Open-Meteo's forecast and ensemble APIs (ADR-0012). Give it an <see cref="HttpClient"/>
/// built on <c>Jharicast.Fetch.PoliteHttpHandler</c>, so the calls are identified, spaced and
/// cached. Results come back in the order of the points asked for: Open-Meteo snaps coordinates
/// to its grid, so match by position, not by latitude and longitude.
/// </summary>
public sealed class OpenMeteoClient
{
    // ADR-0012: up to 50 locations per request. Each still counts against the daily limit.
    internal const int MaxPointsPerRequest = 50;

    private readonly HttpClient _http;
    private readonly OpenMeteoOptions _options;

    /// <summary>Creates the client.</summary>
    /// <param name="httpClient">The client that sends requests; not disposed by this class.</param>
    /// <param name="options">Hosts, key, time zone and clock.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public OpenMeteoClient(HttpClient httpClient, OpenMeteoOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _http = httpClient;
        _options = options;
    }

    /// <summary>
    /// Daily ensemble members for one model, for example <c>ecmwf_ifs025</c> or <c>gfs025</c>. One
    /// model per request, because several models rename every member key (ADR-0012).
    /// </summary>
    /// <param name="points">Locations, at least one. Batched 50 per request.</param>
    /// <param name="model">One ensemble model id.</param>
    /// <param name="variables">Daily variables, for example <c>precipitation_sum</c> and <c>wind_gusts_10m_max</c>.</param>
    /// <param name="days">Forecast days, 1 to 16.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>Per variable, one entry per point in the order given, each with provenance <c>open-meteo.ensemble.&lt;model&gt;</c>.</returns>
    /// <exception cref="ArgumentException">No point, no variable, or an id that is not lower-case letters, digits and underscores (a comma, for several models, is refused).</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="days"/> is outside 1 to 16.</exception>
    /// <exception cref="HttpRequestException">Open-Meteo answered with an error status; the message carries its reason.</exception>
    /// <exception cref="JsonException">The response is not the expected shape.</exception>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<EnsembleDaily>>> GetEnsembleDailyAsync(
        IReadOnlyList<GeoPoint> points, string model, IReadOnlyList<string> variables, int days, CancellationToken cancellationToken)
    {
        Validate(points, [model], variables, days);
        var result = variables.ToDictionary(v => v, _ => new List<EnsembleDaily>(points.Count), StringComparer.Ordinal);
        foreach (var batch in points.Chunk(MaxPointsPerRequest))
        {
            var uri = BuildUri(_options.EnsembleBaseAddress, "v1/ensemble", batch, [model], variables, days);
            var (body, fetchedAt) = await GetAsync(uri, cancellationToken).ConfigureAwait(false);
            var provenance = new Provenance($"open-meteo.ensemble.{model}", SourceKind.Model, fetchedAt);
            foreach (var variable in variables)
            {
                var locations = EnsembleResponseReader.ReadDaily(body, variable);
                CheckCount(locations.Count, batch.Length);
                result[variable].AddRange(locations.Select(l => l with { Provenance = provenance }));
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<EnsembleDaily>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// Daily values from deterministic models, for example <c>ecmwf_ifs025</c>, <c>gfs_seamless</c>
    /// and <c>icon_seamless</c>, in one request per batch of points.
    /// </summary>
    /// <param name="points">Locations, at least one. Batched 50 per request.</param>
    /// <param name="models">Deterministic model ids, at least one.</param>
    /// <param name="variables">Daily variables, for example <c>precipitation_sum</c> and <c>wind_gusts_10m_max</c>.</param>
    /// <param name="days">Forecast days, 1 to 16.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    /// <returns>Per model, one entry per point in the order given, each with provenance <c>open-meteo.forecast.&lt;model&gt;</c>.</returns>
    /// <exception cref="ArgumentException">No point, model or variable, or an id that is not lower-case letters, digits and underscores.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="days"/> is outside 1 to 16.</exception>
    /// <exception cref="HttpRequestException">Open-Meteo answered with an error status; the message carries its reason.</exception>
    /// <exception cref="JsonException">The response is not the expected shape, or lacks a requested model and variable.</exception>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ForecastDaily>>> GetDailyAsync(
        IReadOnlyList<GeoPoint> points, IReadOnlyList<string> models, IReadOnlyList<string> variables, int days, CancellationToken cancellationToken)
    {
        Validate(points, models, variables, days);
        var result = models.ToDictionary(m => m, _ => new List<ForecastDaily>(points.Count), StringComparer.Ordinal);
        foreach (var batch in points.Chunk(MaxPointsPerRequest))
        {
            var uri = BuildUri(_options.BaseAddress, "v1/forecast", batch, models, variables, days);
            var (body, fetchedAt) = await GetAsync(uri, cancellationToken).ConfigureAwait(false);
            foreach (var (model, locations) in ForecastResponseReader.ReadDaily(body, models, variables))
            {
                CheckCount(locations.Count, batch.Length);
                var provenance = new Provenance($"open-meteo.forecast.{model}", SourceKind.Model, fetchedAt);
                result[model].AddRange(locations.Select(l => l with { Provenance = provenance }));
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ForecastDaily>)kv.Value, StringComparer.Ordinal);
    }

    internal Uri BuildUri(Uri baseAddress, string path, IReadOnlyCollection<GeoPoint> points, IReadOnlyList<string> models, IReadOnlyList<string> variables, int days)
    {
        var query = new StringBuilder()
            .Append("latitude=").AppendJoin(',', points.Select(p => Coordinate(p.Latitude)))
            .Append("&longitude=").AppendJoin(',', points.Select(p => Coordinate(p.Longitude)))
            .Append("&daily=").AppendJoin(',', variables)
            .Append("&models=").AppendJoin(',', models)
            .Append("&forecast_days=").Append(days.ToString(CultureInfo.InvariantCulture))
            .Append("&timezone=").Append(Uri.EscapeDataString(_options.TimeZone))
            .Append("&wind_speed_unit=kmh");
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            query.Append("&apikey=").Append(Uri.EscapeDataString(_options.ApiKey));
        }

        return new Uri(baseAddress, $"{path}?{query}");
    }

    // Invariant, always: under ne-NP or de-DE, 27.5 can format as "27,5" and move the point.
    private static string Coordinate(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private async Task<(byte[] Body, DateTimeOffset FetchedAt)> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Open-Meteo answered {(int)response.StatusCode}: {Reason(body) ?? "no reason given"}.", null, response.StatusCode);
        }

        return (body, _options.TimeProvider.GetUtcNow());
    }

    private static string? Reason(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void CheckCount(int received, int requested)
    {
        if (received != requested)
        {
            throw new JsonException($"Open-Meteo returned {received} locations for {requested} requested.");
        }
    }

    private static void Validate(IReadOnlyList<GeoPoint> points, IReadOnlyList<string> models, IReadOnlyList<string> variables, int days)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(variables);
        if (points.Count == 0)
        {
            throw new ArgumentException("At least one point is needed.", nameof(points));
        }

        if (models.Count == 0 || variables.Count == 0)
        {
            throw new ArgumentException("At least one model and one variable are needed.", models.Count == 0 ? nameof(models) : nameof(variables));
        }

        foreach (var id in models.Concat(variables))
        {
            if (string.IsNullOrEmpty(id) || !id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
            {
                throw new ArgumentException($"'{id}' is not an Open-Meteo id: use lower-case letters, digits and underscores, one model per entry.", models.Contains(id) ? nameof(models) : nameof(variables));
            }
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(days, 16);
    }
}
