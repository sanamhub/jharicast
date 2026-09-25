using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jharicast.Fetch;

/// <summary>How far a source's latest result can be trusted (ADR-0008, ADR-0011).</summary>
public enum SourceStatus
{
    /// <summary>Fetched, parsed, and changed within its expected cadence.</summary>
    Fresh = 0,

    /// <summary>Content unchanged for longer than twice its expected cadence, or its own issue time is that old.</summary>
    Stale = 1,

    /// <summary>Parsed, but with values the parser did not recognise. The feed changed; alert the maintainer.</summary>
    Drifting = 2,

    /// <summary>Unreachable, refused, or not parseable.</summary>
    Failing = 3,

    /// <summary>Turned off by configuration, or its host's robots.txt disallows it.</summary>
    Disabled = 4,
}

/// <summary>A source's health at one fetch.</summary>
/// <param name="Status">Status.</param>
/// <param name="Detail">What went wrong, or null when fresh.</param>
/// <param name="LastChangeAt">When the content last changed, as far as the caller knows.</param>
public sealed record SourceHealth(SourceStatus Status, string? Detail, DateTimeOffset? LastChangeAt)
{
    /// <summary>
    /// Fresh or stale from content age: stale when the content has not changed for more than
    /// twice <paramref name="expectedCadence"/> (ADR-0011: warnings every 6 h are stale after 12 h).
    /// </summary>
    /// <param name="lastChangeAt">When the content hash last changed; null when unknown, which counts as fresh.</param>
    /// <param name="expectedCadence">How often the issuer normally updates it.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The health, with a detail naming the age when stale.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedCadence"/> is not positive.</exception>
    public static SourceHealth FromLastChange(DateTimeOffset? lastChangeAt, TimeSpan expectedCadence, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expectedCadence, TimeSpan.Zero);
        if (lastChangeAt is { } changed && now - changed > 2 * expectedCadence)
        {
            var hours = (now - changed).TotalHours;
            return new SourceHealth(SourceStatus.Stale, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"unchanged for {hours:0.#} h, expected every {expectedCadence.TotalHours:0.#} h"), changed);
        }

        return new SourceHealth(SourceStatus.Fresh, null, lastChangeAt);
    }
}

/// <summary>One fetch of a source: the parsed value, the bytes it came from, and the source's health.</summary>
/// <typeparam name="T">The parsed type.</typeparam>
/// <param name="Value">Parsed value, or null when the fetch or parse failed.</param>
/// <param name="Snapshot">What was fetched, or null when nothing was.</param>
/// <param name="Health">Health at this fetch.</param>
public sealed record SourceResult<T>(T? Value, Snapshot? Snapshot, SourceHealth Health);

/// <summary>A data source: one fetch, politely, returning value, snapshot and health.</summary>
/// <typeparam name="T">The parsed type.</typeparam>
public interface ISource<T>
{
    /// <summary>Fetches and parses once. Failures are reported in <see cref="SourceResult{T}.Health"/>, not thrown.</summary>
    /// <param name="cancellationToken">Cancels the fetch.</param>
    /// <returns>The result.</returns>
    Task<SourceResult<T>> FetchAsync(CancellationToken cancellationToken);
}
