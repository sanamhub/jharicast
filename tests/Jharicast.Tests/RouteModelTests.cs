using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Jharicast.Tests;

public sealed class RouteModelTests
{
    private static readonly GeoPoint Birtamod = new(26.66, 87.99);
    private static readonly GeoPoint Lumbini = new(27.48, 83.28);
    private static readonly DateOnly Sep28 = new(2026, 9, 28);

    [Fact]
    public void Route_keeps_legs_in_order_with_their_via_points()
    {
        var route = new Route("jhapa-lumbini",
        [
            new Leg("D1", Birtamod, Lumbini, Sep28) { Via = [new GeoPoint(27.0, 84.9)] },
            new Leg("D2", Lumbini, Lumbini, Sep28.AddDays(1)),
        ]);

        Assert.Equal(["D1", "D2"], route.Legs.Select(l => l.Id));
        Assert.Equal([new GeoPoint(27.0, 84.9)], route.Legs[0].Via);
        Assert.Empty(route.Legs[1].Via);
    }

    [Fact]
    public void Route_needs_at_least_one_leg()
    {
        var error = Assert.Throws<ArgumentException>(() => new Route("empty", []));

        Assert.Equal("Legs", error.ParamName);
    }

    [Fact]
    public void Route_rejects_a_repeated_leg_id()
    {
        Assert.Throws<ArgumentException>(() => new Route("twice", [new Leg("D1", Birtamod, Lumbini, Sep28), new Leg("D1", Lumbini, Birtamod, Sep28.AddDays(1))]));
    }

    [Fact]
    public void Route_rejects_null_legs_and_blank_ids()
    {
        Assert.Throws<ArgumentNullException>(() => new Route("null", null!));
        Assert.Throws<ArgumentNullException>(() => new Route("null leg", [null!]));
        Assert.Throws<ArgumentException>(() => new Route(" ", [new Leg("D1", Birtamod, Lumbini, Sep28)]));
        Assert.Throws<ArgumentException>(() => new Leg("", Birtamod, Lumbini, Sep28));
        Assert.Throws<ArgumentNullException>(() => new Leg("D1", Birtamod, Lumbini, Sep28) { Via = null! });
    }

    [Fact]
    public void Route_copies_the_legs_so_the_caller_cannot_change_them_later()
    {
        var legs = new List<Leg> { new("D1", Birtamod, Lumbini, Sep28) };
        var route = new Route("copy", legs);

        legs.Add(new Leg("D2", Lumbini, Birtamod, Sep28.AddDays(1)));

        Assert.Single(route.Legs);
    }

    [Fact]
    public void TimeWindow_days_include_both_ends()
    {
        var window = new TimeWindow(Sep28, new DateOnly(2026, 10, 1));

        Assert.Equal([Sep28, new(2026, 9, 29), new(2026, 9, 30), new(2026, 10, 1)], window.Days());
        Assert.Equal([Sep28], new TimeWindow(Sep28, Sep28).Days());
    }

    [Fact]
    public void TimeWindow_end_must_not_be_before_start()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new TimeWindow(Sep28, Sep28.AddDays(-1)));

        Assert.Equal("End", error.ParamName);
    }

    [Fact]
    public void TimeWindow_is_at_most_21_days()
    {
        Assert.Equal(21, new TimeWindow(Sep28, Sep28.AddDays(20)).Days().Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeWindow(Sep28, Sep28.AddDays(21)));
    }
}
