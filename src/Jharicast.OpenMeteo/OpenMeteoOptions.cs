using System;

namespace Jharicast.OpenMeteo;

/// <summary>
/// Where and how <see cref="OpenMeteoClient"/> calls Open-Meteo (ADR-0012). The library never
/// decides the licence: the free hosts are for non-commercial use only, and a commercial user sets
/// the customer hosts and an <see cref="ApiKey"/>.
/// </summary>
public sealed class OpenMeteoOptions
{
    /// <summary>Forecast and elevation host. Default <c>https://api.open-meteo.com/</c>; paid plans use <c>https://customer-api.open-meteo.com/</c>.</summary>
    public Uri BaseAddress { get; init; } = new("https://api.open-meteo.com/");

    /// <summary>Ensemble host. Default <c>https://ensemble-api.open-meteo.com/</c>; a paid plan sets the host its contract names.</summary>
    public Uri EnsembleBaseAddress { get; init; } = new("https://ensemble-api.open-meteo.com/");

    /// <summary>API key for a paid plan, sent as <c>apikey</c>; null for the free API. Read it from the environment, never from source.</summary>
    public string? ApiKey { get; init; }

    /// <summary>IANA time zone for daily values. Default <c>Asia/Kathmandu</c>, so a day is a Nepal day (ADR-0004).</summary>
    public string TimeZone { get; init; } = "Asia/Kathmandu";

    /// <summary>Clock for <see cref="Provenance.FetchedAt"/>. Default <see cref="System.TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
