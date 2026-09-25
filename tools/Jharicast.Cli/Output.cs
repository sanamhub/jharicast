using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jharicast.Fetch;
using Jharicast.Nepal;

namespace Jharicast.Cli;

/// <summary>Text every output ends with, and the helpers that lay out tables.</summary>
internal static class Output
{
    // ADR-0005 rule 4 and ADR-0014: every output carries this.
    public const string Disclaimer = "Not an official forecast. Follow the Department of Hydrology and Meteorology (DHM) and the District Administration Office. \"Pass\" means only that no rule was broken.";

    // ADR-0012 and ADR-0014.
    public static readonly string[] Attributions =
    [
        "Weather data by Open-Meteo.com (CC BY 4.0). Contains ECMWF data (CC BY 4.0), and NOAA GFS and DWD ICON data through Open-Meteo.",
        "Warnings: Department of Hydrology and Meteorology (DHM), Nepal. Road closures: Department of Roads (DoR), Nepal. Alerts and incidents: BIPAD portal, NDRRMA. Their data belongs to them.",
        "Routes and towns: (c) OpenStreetMap contributors (ODbL).",
        "District boundaries: Survey Department of Nepal and UN RCO Nepal, via OCHA / HDX (cod-ab-npl), CC BY-IGO 3.0, simplified.",
    ];

    public static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    public static string Npt(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, NepalTime.Zone).ToString("yyyy-MM-dd HH:mm 'NPT'", CultureInfo.InvariantCulture);

    public static void Notices(TextWriter writer, bool replay)
    {
        writer.WriteLine();
        if (replay)
        {
            writer.WriteLine("Replayed from recorded files (--fixtures): not current data.");
        }

        writer.WriteLine(Disclaimer);
        foreach (var line in Attributions)
        {
            writer.WriteLine(line);
        }
    }

    /// <summary>Source, status, data time and detail, one row per source, official first (ADR-0005 rule 2).</summary>
    public static void Health(TextWriter writer, IReadOnlyDictionary<string, SourceHealth> health, IReadOnlyList<Provenance> provenance)
    {
        writer.WriteLine();
        writer.WriteLine("Data times and health");
        var rows = health
            .OrderBy(h => IsOfficial(h.Key) ? 0 : 1)
            .ThenBy(h => h.Key, StringComparer.Ordinal)
            .Select(h =>
            {
                var fetched = provenance.Where(p => p.Source == h.Key || p.Source.StartsWith(h.Key + ".", StringComparison.Ordinal)).Select(p => (DateTimeOffset?)p.FetchedAt).Max() ?? (h.Value.Status == SourceStatus.Fresh ? h.Value.LastChangeAt : null);
                var issued = provenance.Where(p => p.Source == h.Key).Select(p => p.IssuedAt).Max();
                var times = fetched is { } f ? "fetched " + Npt(f) : "not fetched";
                if (issued is { } i)
                {
                    times += ", issued " + Npt(i);
                }

                return new[] { h.Key, Kind(h.Key), h.Value.Status.ToString(), times, h.Value.Detail ?? string.Empty };
            })
            .ToList();
        Table(writer, ["Source", "Kind", "Status", "Data time", "Detail"], rows);

        foreach (var (id, h) in health.Where(h => Kind(h.Key) == "official" && h.Value.Status != SourceStatus.Fresh))
        {
            writer.WriteLine(Invariant($"Warning: {id} is {Word(h.Status)}. What it feeds is missing or old in this output; check the agency's own site."));
        }
    }

    public static string Word(SourceStatus status) => status switch
    {
        SourceStatus.Fresh => "fresh",
        SourceStatus.Stale => "stale",
        SourceStatus.Drifting => "drifting",
        SourceStatus.Failing => "failing",
        _ => "disabled",
    };

    public static string Kind(string sourceId) => sourceId switch
    {
        "routing" => "routing",
        "dhm.gauges" => "observation",
        _ when IsOfficial(sourceId) => "official",
        _ => "model",
    };

    public static bool IsOfficial(string sourceId) =>
        sourceId.StartsWith("dhm.", StringComparison.Ordinal) || sourceId.StartsWith("dor.", StringComparison.Ordinal) || sourceId.StartsWith("bipad.", StringComparison.Ordinal);

    public static void Table(TextWriter writer, IReadOnlyList<string> header, IReadOnlyList<string[]> rows)
    {
        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        string Line(IReadOnlyList<string> cells) => string.Join("  ", cells.Select((c, i) => i == cells.Count - 1 ? c : c.PadRight(widths[i]))).TrimEnd();
        writer.WriteLine(Line(header));
        writer.WriteLine(Line([.. widths.Select(w => new string('-', w))]));
        foreach (var row in rows)
        {
            writer.WriteLine(Line(row));
        }
    }
}
