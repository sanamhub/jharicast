using System;
using System.Collections.Generic;
using System.Linq;

namespace Jharicast;

/// <summary>Warning colour, as DHM and most met services use it. Ordered: a larger value is more severe.</summary>
public enum AlertLevel
{
    /// <summary>No alert.</summary>
    Green = 0,

    /// <summary>Be aware.</summary>
    Yellow = 1,

    /// <summary>Be prepared.</summary>
    Orange = 2,

    /// <summary>Take action.</summary>
    Red = 3,
}

/// <summary>Where a value came from. Mirrors the labels of the source report (ADR-0004).</summary>
public enum SourceKind
{
    /// <summary>Published by a mandated agency (DHM, IMD, DoR, DAO, NDRRMA).</summary>
    Official = 0,

    /// <summary>Numbers computed from raw global model output.</summary>
    Model = 1,

    /// <summary>Measured by a gauge, radar or satellite.</summary>
    Observation = 2,

    /// <summary>Our reading of the data. Can be wrong.</summary>
    OwnAnalysis = 3,
}

/// <summary>Provenance carried by every value Jharicast produces (ADR-0004, ADR-0008).</summary>
/// <param name="Source">Stable source id, for example <c>dhm.warnings</c> or <c>open-meteo.ensemble.ecmwf_ifs025</c>.</param>
/// <param name="Kind">Official, model, observation or own analysis.</param>
/// <param name="FetchedAt">When we fetched it, UTC.</param>
public sealed record Provenance(string Source, SourceKind Kind, DateTimeOffset FetchedAt)
{
    /// <summary>When the issuer published it, if known.</summary>
    public DateTimeOffset? IssuedAt { get; init; }

    /// <summary>Model run, for example <c>2026-09-23T06Z</c>, if a model.</summary>
    public string? ModelRun { get; init; }
}

/// <summary>Helpers for <see cref="AlertLevel"/>.</summary>
public static class AlertLevels
{
    /// <summary>
    /// The level a person should act on when an official warning and our own model disagree: the
    /// higher one. An official level is never lowered by a model (ADR-0005).
    /// </summary>
    /// <param name="official">The official level, or null when none was issued for that day.</param>
    /// <param name="model">Our model level.</param>
    /// <returns>The more severe of the two.</returns>
    public static AlertLevel Combine(AlertLevel? official, AlertLevel model) =>
        official is { } o && o > model ? o : model;

    /// <summary>Most severe level in a sequence; Green when empty.</summary>
    /// <param name="levels">Levels.</param>
    /// <returns>The maximum.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="levels"/> is null.</exception>
    public static AlertLevel Max(IEnumerable<AlertLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        return levels.DefaultIfEmpty(AlertLevel.Green).Max();
    }
}
