using System;
using System.Collections.Generic;

namespace Jharicast;

/// <summary>
/// A run of Nepal calendar days, both ends included, at most 21 days long. The ensembles reach
/// 15 days; past 21 a forecast is noise.
/// </summary>
/// <param name="Start">First day.</param>
/// <param name="End">Last day, on or after <paramref name="Start"/>.</param>
public sealed record TimeWindow(DateOnly Start, DateOnly End)
{
    internal const int MaxDays = 21;

    /// <summary>First day.</summary>
    public DateOnly Start { get; } = Start;

    /// <summary>Last day.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Before <see cref="Start"/>, or making the window longer than 21 days counting both ends.</exception>
    public DateOnly End { get; } = Validate(Start, End, nameof(End));

    /// <summary>Every day from <see cref="Start"/> to <see cref="End"/>, in order.</summary>
    /// <returns>Between 1 and 21 days.</returns>
    public IReadOnlyList<DateOnly> Days()
    {
        var days = new DateOnly[End.DayNumber - Start.DayNumber + 1];
        for (var i = 0; i < days.Length; i++)
        {
            days[i] = Start.AddDays(i);
        }

        return days;
    }

    private static DateOnly Validate(DateOnly start, DateOnly end, string paramName)
    {
        if (end < start)
        {
            throw new ArgumentOutOfRangeException(paramName, end, "End is before Start.");
        }

        if (end.DayNumber - start.DayNumber + 1 > MaxDays)
        {
            throw new ArgumentOutOfRangeException(paramName, end, $"A window is at most {MaxDays} days, both ends included.");
        }

        return end;
    }
}
