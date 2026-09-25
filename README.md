# Jharicast

Weather and road rules for a route, per day, from global models and official Nepali warnings,
in .NET. It tells you which of your rules a leg breaks, why, where each number came from, and
when that answer changes.

> **Not an official forecast.** Always follow the Department of Hydrology and Meteorology (DHM)
> and the District Administration Office. Jharicast never says a route is safe.

**Status: in development.** Nothing is published to nuget.org yet, and the API can change
until 1.0.

## Where it comes from

A rider's storm report of 2026-09-24 did this by hand: pull ECMWF, GFS and ICON, compare with DHM's district warnings, apply four trip
rules to every leg, recheck each evening. Jharicast's reference rules reproduce that report's own
alert levels and trip verdicts.

## Data terms, first

- Open-Meteo's free API is for non-commercial use only. Commercial users need a paid plan.
  Attribution: "Weather data by Open-Meteo.com (CC BY 4.0)".
- DHM, DoR and BIPAD data belongs to those agencies. The Nepal pack asks them for permission and
  is not published until they answer.
- Jharicast does not work around blocks, CAPTCHAs or access controls. It identifies itself,
  obeys robots.txt, caches, and asks agencies for permission.

## License

MIT for the code. Data remains under its owners' terms.
