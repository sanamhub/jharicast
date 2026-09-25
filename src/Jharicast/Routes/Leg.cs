using System;
using System.Collections.Generic;

namespace Jharicast;

/// <summary>One leg of a route, ridden on one Nepal calendar day.</summary>
/// <param name="Id">Caller's id, unique within the route, for example <c>D1</c>.</param>
/// <param name="From">Start.</param>
/// <param name="To">End. Equal to <paramref name="From"/> for a rest day.</param>
/// <param name="Date">Travel date, Nepal time.</param>
public sealed record Leg(string Id, GeoPoint From, GeoPoint To, DateOnly Date)
{
    /// <summary>Caller's id.</summary>
    /// <exception cref="ArgumentException">Empty or white space.</exception>
    public string Id { get; } = Route.NotBlank(Id, nameof(Id));

    /// <summary>Points the router must pass through, in order. Empty by default.</summary>
    /// <exception cref="ArgumentNullException">Set to null.</exception>
    public IReadOnlyList<GeoPoint> Via
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = [.. value];
        }
    } = [];
}
