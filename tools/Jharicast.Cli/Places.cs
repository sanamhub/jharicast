using System;
using System.Globalization;
using System.Linq;
using Jharicast.Nepal;

namespace Jharicast.Cli;

/// <summary>A place given on the command line, resolved.</summary>
internal sealed record Place(string Name, GeoPoint Point);

/// <summary>
/// Resolves <c>--from</c>, <c>--to</c> and <c>--via</c>: explicit <c>lat,lon</c>, or a town in the
/// gazetteer by name or alias, ignoring case. No geocoding service is asked.
/// </summary>
internal static class Places
{
    /// <exception cref="CliException">Not coordinates, not a known town, or a town name used twice.</exception>
    public static Place Resolve(string text)
    {
        var parts = text.Split(',');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
        {
            if (lat is < -90 or > 90 || lon is < -180 or > 180)
            {
                throw new CliException($"'{text}' is not a valid latitude,longitude.");
            }

            return new Place(text, new GeoPoint(lat, lon));
        }

        var name = text.Trim();
        var matches = Gazetteer.Towns
            .Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) || t.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return matches.Length switch
        {
            1 => new Place(matches[0].Name, matches[0].Location),
            0 => throw new CliException($"'{text}' is not a town Jharicast knows. Give it as latitude,longitude, for example 27.4702,83.2852."),
            _ => throw new CliException($"'{text}' names {matches.Length} towns ({string.Join(", ", matches.Select(m => $"{m.Name} in {m.DistrictId}"))}). Give it as latitude,longitude."),
        };
    }
}

/// <summary>A user error: printed as one line, exit code 2, no stack trace.</summary>
internal sealed class CliException(string message) : Exception(message)
{
    public CliException()
        : this("The command failed.")
    {
    }

    public CliException(string message, Exception innerException)
        : this(message + " " + innerException?.Message)
    {
    }
}
