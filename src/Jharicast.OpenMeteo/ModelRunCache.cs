using System;
using System.Collections.Concurrent;

namespace Jharicast.OpenMeteo;

/// <summary>
/// Holds per-point model results until the model's next run is expected to be published
/// (ADR-0012). Asking again sooner costs a call against the daily limit and returns the same
/// numbers. An entry also ends at local midnight, because its first day is then yesterday.
/// </summary>
/// <param name="time">Clock.</param>
/// <param name="lag">How long after run time a run appears on Open-Meteo.</param>
internal sealed class ModelRunCache(TimeProvider time, TimeSpan lag)
{
    // ECMWF IFS, GFS and ICON all run at 00, 06, 12 and 18 UTC, and so do their ensembles.
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<Key, Entry> _entries = new();

    /// <summary>A cached value with at least <paramref name="days"/> days, still current.</summary>
    public bool TryGet<T>(Key key, int days, out T value)
        where T : class
    {
        value = null!;
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        var now = time.GetUtcNow();
        if (now >= entry.ExpiresAt || LocalDate(now, entry.UtcOffset) != entry.LocalDate || entry.Days < days || entry.Value is not T typed)
        {
            return false;
        }

        value = typed;
        return true;
    }

    /// <summary>Stores a value fetched at <paramref name="fetchedAt"/> for a location <paramref name="utcOffset"/> from UTC.</summary>
    public void Set(Key key, object value, int days, DateTimeOffset fetchedAt, TimeSpan utcOffset) =>
        _entries[key] = new Entry(value, days, NextRunAvailable(fetchedAt, lag), utcOffset, LocalDate(fetchedAt, utcOffset));

    /// <summary>When the run after the newest one available at <paramref name="fetchedAt"/> is expected.</summary>
    internal static DateTimeOffset NextRunAvailable(DateTimeOffset fetchedAt, TimeSpan lag)
    {
        var utc = fetchedAt.ToUniversalTime();
        var newestRunTicks = (utc - lag).UtcTicks / RunInterval.Ticks * RunInterval.Ticks;
        return new DateTimeOffset(newestRunTicks, TimeSpan.Zero) + RunInterval + lag;
    }

    private static DateOnly LocalDate(DateTimeOffset instant, TimeSpan utcOffset) => DateOnly.FromDateTime(instant.UtcDateTime + utcOffset);

    /// <summary>
    /// Endpoint, model, point rounded to 0.001 degrees (about 100 m, far below any model grid),
    /// and variable. A forecast entry holds several variables from one fetch, so its key carries
    /// the comma-joined list. The day is not in the key: an entry stops matching at local midnight.
    /// </summary>
    internal readonly record struct Key(string Endpoint, string Model, long LatitudeE3, long LongitudeE3, string Variable)
    {
        public static Key For(string endpoint, string model, GeoPoint point, string variable) =>
            new(endpoint, model, (long)Math.Round(point.Latitude * 1000), (long)Math.Round(point.Longitude * 1000), variable);
    }

    private sealed record Entry(object Value, int Days, DateTimeOffset ExpiresAt, TimeSpan UtcOffset, DateOnly LocalDate);
}
