using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jharicast.Routing;

/// <summary>Road geometry for one leg.</summary>
/// <param name="Points">The road, in order, as decoded from the router.</param>
/// <param name="DistanceKm">Road distance, km, as the router measured it.</param>
/// <param name="Duration">Driving time as the router estimated it.</param>
public sealed record RouteGeometry(IReadOnlyList<GeoPoint> Points, double DistanceKm, TimeSpan Duration);

/// <summary>Finds a road route through waypoints (ADR-0010).</summary>
public interface IRouteProvider
{
    /// <summary>The recommended route through the waypoints, in order.</summary>
    /// <param name="waypoints">Start, any via points, and end: at least two.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The route.</returns>
    Task<RouteGeometry> GetRouteAsync(IReadOnlyList<GeoPoint> waypoints, CancellationToken cancellationToken);
}

/// <summary>
/// OSRM's route service, <c>/route/v1/driving/{lon},{lat};...?overview=full&amp;geometries=polyline6</c>.
/// Point it at a self-hosted OSRM on the Geofabrik Nepal extract. The public demo server is for
/// personal CLI use only, at no more than one request per second (ADR-0010): build the
/// <see cref="HttpClient"/> on <c>Jharicast.Fetch.PoliteHttpHandler</c>.
/// </summary>
public sealed class OsrmRouteProvider : IRouteProvider
{
    // OSRM caps a route request at 100 coordinates by default.
    private const int MaxWaypoints = 100;

    private readonly HttpClient _http;
    private readonly Uri _baseAddress;

    /// <summary>Creates the provider.</summary>
    /// <param name="httpClient">The client that sends requests; not disposed by this class.</param>
    /// <param name="baseAddress">OSRM root, for example <c>http://localhost:5000/</c>. A path is kept; a missing trailing slash is added.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="baseAddress"/> is not absolute.</exception>
    public OsrmRouteProvider(HttpClient httpClient, Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("The OSRM address must be absolute.", nameof(baseAddress));
        }

        _http = httpClient;
        _baseAddress = baseAddress.AbsoluteUri.EndsWith('/') ? baseAddress : new Uri(baseAddress.AbsoluteUri + "/");
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">Fewer than two or more than 100 waypoints.</exception>
    /// <exception cref="HttpRequestException">OSRM answered with an error status and no OSRM error body.</exception>
    /// <exception cref="InvalidOperationException">OSRM answered with a code other than <c>Ok</c>, such as <c>NoRoute</c> or <c>NoSegment</c>.</exception>
    /// <exception cref="JsonException">The response is not the documented shape.</exception>
    public async Task<RouteGeometry> GetRouteAsync(IReadOnlyList<GeoPoint> waypoints, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        if (waypoints.Count is < 2 or > MaxWaypoints)
        {
            throw new ArgumentException($"A route needs 2 to {MaxWaypoints} waypoints; got {waypoints.Count}.", nameof(waypoints));
        }

        using var response = await _http.GetAsync(BuildUri(waypoints), cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return Read(body, response.IsSuccessStatusCode ? null : (int)response.StatusCode);
    }

    // OSRM takes longitude first.
    internal Uri BuildUri(IReadOnlyList<GeoPoint> waypoints) =>
        new(_baseAddress, "route/v1/driving/"
            + string.Join(';', waypoints.Select(p => Coordinate(p.Longitude) + "," + Coordinate(p.Latitude)))
            + "?overview=full&geometries=polyline6");

    /// <summary>Reads a route response.</summary>
    /// <param name="utf8Json">The body.</param>
    /// <param name="errorStatus">The HTTP status when it was not a success, else null.</param>
    internal static RouteGeometry Read(ReadOnlyMemory<byte> utf8Json, int? errorStatus)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json);
        }
        catch (JsonException) when (errorStatus is { } status)
        {
            throw new HttpRequestException(string.Create(CultureInfo.InvariantCulture, $"OSRM answered HTTP {status}."));
        }

        using (document)
        {
            var root = document.RootElement;
            var code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (!string.Equals(code, "Ok", StringComparison.Ordinal))
            {
                var message = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var m) ? m.GetString() : null;
                throw code is null && errorStatus is { } status
                    ? new HttpRequestException(string.Create(CultureInfo.InvariantCulture, $"OSRM answered HTTP {status}."))
                    : new InvalidOperationException($"OSRM: {code ?? "no code"}{(message is null ? string.Empty : ": " + message)}");
            }

            if (!root.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array || routes.GetArrayLength() == 0)
            {
                throw new JsonException("OSRM: no routes.");
            }

            var route = routes[0];
            if (!route.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.String
                || !route.TryGetProperty("distance", out var distance) || !distance.TryGetDouble(out var metres)
                || !route.TryGetProperty("duration", out var duration) || !duration.TryGetDouble(out var seconds))
            {
                throw new JsonException("OSRM: the route lacks a polyline geometry, a distance or a duration.");
            }

            IReadOnlyList<GeoPoint> points;
            try
            {
                points = Geo.DecodePolyline(geometry.GetString()!, 6);
            }
            catch (FormatException e)
            {
                throw new JsonException("OSRM: " + e.Message, e);
            }

            return new RouteGeometry(points, metres / 1000, TimeSpan.FromSeconds(seconds));
        }
    }

    private static string Coordinate(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
