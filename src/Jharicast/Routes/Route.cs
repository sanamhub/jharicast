using System;
using System.Collections.Generic;
using System.Linq;

namespace Jharicast;

/// <summary>A watched route: one or more legs, each ridden on one day (ADR-0010).</summary>
/// <param name="Id">Caller's id, stored with every assessment.</param>
/// <param name="Legs">Legs in travel order.</param>
public sealed record Route(string Id, IReadOnlyList<Leg> Legs)
{
    /// <summary>Caller's id.</summary>
    /// <exception cref="ArgumentException">Empty or white space.</exception>
    public string Id { get; } = NotBlank(Id, nameof(Id));

    /// <summary>Legs in travel order, copied at construction.</summary>
    /// <exception cref="ArgumentNullException">Null, or a null leg.</exception>
    /// <exception cref="ArgumentException">No leg, or two legs with the same id. Assessments are keyed by leg id and date, so a repeated id would merge two legs.</exception>
    public IReadOnlyList<Leg> Legs { get; } = Validate(Legs, nameof(Legs));

    internal static string NotBlank(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }

    private static Leg[] Validate(IReadOnlyList<Leg> legs, string paramName)
    {
        ArgumentNullException.ThrowIfNull(legs, paramName);
        Leg[] copy = [.. legs];
        if (copy.Length == 0)
        {
            throw new ArgumentException("A route needs at least one leg.", paramName);
        }

        if (copy.Any(leg => leg is null))
        {
            throw new ArgumentNullException(paramName, "A route cannot contain a null leg.");
        }

        var repeated = copy.GroupBy(leg => leg.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (repeated is not null)
        {
            throw new ArgumentException($"Leg id '{repeated.Key}' appears more than once.", paramName);
        }

        return copy;
    }
}
