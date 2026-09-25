using System;

namespace Jharicast.Nepal;

/// <summary>
/// Nepal Standard Time, UTC+05:45 with no daylight saving (ADR-0004). A Nepal day is midnight to
/// midnight in this zone.
/// </summary>
public static class NepalTime
{
    /// <summary>The <c>Asia/Kathmandu</c> zone.</summary>
    public static TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kathmandu");

    /// <summary>A Nepal wall-clock time as a UTC instant.</summary>
    /// <param name="nepalLocal">Wall-clock time in Nepal. Its <see cref="DateTime.Kind"/> is ignored.</param>
    /// <returns>The instant, with offset zero.</returns>
    public static DateTimeOffset ToUtc(DateTime nepalLocal)
    {
        var local = DateTime.SpecifyKind(nepalLocal, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>The Nepal calendar day an instant falls on.</summary>
    /// <param name="instant">Any instant.</param>
    /// <returns>The date in Nepal.</returns>
    public static DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Zone).DateTime);
}
