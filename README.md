# Jharicast

Weather and road rules for a route, per day, from global models and official Nepali warnings,
in .NET. It tells you which of your rules a leg breaks, why, where each number came from, and
when that answer changes. It is for riders and drivers planning a trip in Nepal, and for apps
that watch a trip for them.

> **Not an official forecast.** Always follow the Department of Hydrology and Meteorology (DHM)
> and the District Administration Office. Jharicast never says a route is safe: its best answer,
> `Pass`, means only that none of your rules is broken.

**Status: in development.** Nothing is published to nuget.org yet, and the API can change
until 1.0.

## Data terms, first

The code is MIT. The data is not Jharicast's, and each owner's terms apply to you as soon as you
fetch or show it. A commercial product has to clear these terms as well as the code licence.

| Data | Owner | Terms | Credit to show |
| --- | --- | --- | --- |
| Model forecasts and ensembles | [Open-Meteo](https://open-meteo.com/) (ECMWF, NOAA and DWD models) | CC BY 4.0. The free API is for non-commercial use only; commercial use needs a paid plan. | "Weather data by Open-Meteo.com (CC BY 4.0)", "Contains ECMWF data (CC BY 4.0)" |
| District warnings, gauges, forecast bulletins | Department of Hydrology and Meteorology (DHM), Nepal | The agency's own. No redistribution licence has been granted. | DHM by name |
| Road closures | Department of Roads (DoR), Nepal | The agency's own. | DoR by name |
| Alerts and incidents | BIPAD portal, National Disaster Risk Reduction and Management Authority (NDRRMA) | The agency's own. | BIPAD / NDRRMA by name |
| Road geometry (OSRM) and town positions | OpenStreetMap contributors | [ODbL 1.0](https://opendatacommons.org/licenses/odbl/1-0/) | "© OpenStreetMap contributors" |
| District boundaries | Survey Department of Nepal and UN RCO Nepal, via OCHA / HDX ([cod-ab-npl](https://data.humdata.org/dataset/cod-ab-npl)) | [CC BY-IGO 3.0](https://creativecommons.org/licenses/by/3.0/igo/legalcode), simplified by us | the Survey Department and OCHA / HDX, with the licence |

The town list and the district polygons are embedded in the packages; they keep their licences,
not MIT. The details and the changes made are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
The `jharicast` command prints the credits under every answer.

**Permission.** `Jharicast.Nepal`, which reads the DHM, DoR and BIPAD feeds, is not published to
nuget.org until those agencies grant permission to poll and use their data. The `Jharicast.Cli`
package contains it, so it waits too.

**No evasion.** Jharicast identifies itself, obeys robots.txt, keeps one request at a time per
host with at least 5 seconds between requests to a government host, caches, and stops when a site
refuses. It never works around a block, a CAPTCHA or an access control. If a site says no, the
fix is to ask the site's owner.

## Packages

| Package | What it does | Depends on |
| --- | --- | --- |
| `Jharicast` | The rules: alert levels, ensemble statistics, the four trip rules, material change detection, geometry. No I/O, no dependencies. | nothing |
| `Jharicast.Fetch` | Polite HTTP: User-Agent with a contact, robots.txt, per-host intervals, bounded retries, circuit breaker, kill switch, snapshots and source health. | nothing |
| `Jharicast.OpenMeteo` | Forecast, ensemble and elevation clients, cached per model run. | `Jharicast` |
| `Jharicast.Routing` | OSRM routes, sampling every 5 km with elevation-based hill sections, district lookup. Embeds the district polygons. | `Jharicast` |
| `Jharicast.Nepal` | The 77 districts and their spellings, towns, DHM, BIPAD and DoR sources, and `NepalRouteAssessor`. Not published yet. | all of the above |
| `Jharicast.Cli` | The `jharicast` command. Not published yet. | all of the above |

## The jharicast command

Until it is published, run it from a clone:

```bash
dotnet run --project tools/Jharicast.Cli -c Release -- route --from Birtamod --to Lumbini --date 2026-09-28 --contact ops@example.org
```

| Command | What it prints |
| --- | --- |
| `route --from <place> --to <place> [--via <place>] --date <yyyy-MM-dd> [--days N] [--json]` | One row per leg and day: km and hill km, rain and gust day maxima per model, and the flag; then each rule's reason, the districts crossed and their DHM levels, data times and source health. `--days` repeats the leg on later start dates. |
| `alerts --date <yyyy-MM-dd> [--days N] [--json]` | Our ensemble rain level against DHM's, by province and day, and how often they agree. |
| `sources check` | Fetches each source once and prints its health. Exit code 1 when an official source is failing or drifting. |

A place is a town in the gazetteer (every district headquarters, plus towns such as Birtamod,
Butwal and Lumbini) or `latitude,longitude`. No geocoding service is called.

**Contact mailbox.** Live requests carry `Jharicast/<version> (+https://github.com/sanamhub/jharicast; <contact>)`,
so a site owner can reach whoever runs the command. Pass `--contact <mailbox>` or set
`JHARICAST_CONTACT`. There is no default: without one the command stops before sending anything.
Use a project or team mailbox, not a personal address.

**Offline replay.** `--fixtures <dir>` answers every request from files laid out as
`<host>/<path>.json` and never opens a socket; `--now <time>` sets the replay clock. The tests
run every command offline, this way or against a fake network.

Other options: `--disable-host <host>` never requests that host; `--osrm <url>` points at your own
OSRM server (the default is the public demo server, which allows light personal use only);
`--snapshots <dir>` sets where fetched snapshots are kept.

When a source is refused (robots.txt, a disabled host, a 403, an open circuit breaker), the output
names the source and the reason. A route cannot be assessed without the router and Open-Meteo, so
if either is refused the command exits 2 and prints no verdict. A missing or stale official
source is shown in the health table and in the rule it feeds, never hidden.

Exit codes: 0 done, 1 `sources check` found an official source failing or drifting, 2 the
command could not run.

## Using the library

```csharp
using Jharicast;
using Jharicast.Fetch;
using Jharicast.Nepal;
using Jharicast.OpenMeteo;
using Jharicast.Routing;

var polite = new PoliteHttpOptions { UserAgent = "Jharicast/0.1 (+https://github.com/sanamhub/jharicast; ops@example.org)" };
polite.HostOverrides["dhm.gov.np"] = TimeSpan.FromSeconds(5);
polite.HostOverrides["navigate.dor.gov.np"] = TimeSpan.FromSeconds(5);
using var http = new HttpClient(new PoliteHttpHandler(polite, new SocketsHttpHandler()));
var store = new FileSnapshotStore("snapshots");
var time = TimeProvider.System;

var assessor = new NepalRouteAssessor(
    new OpenMeteoClient(http, new OpenMeteoOptions()),
    new DhmWarningMapSource(http, store, time),
    new DorClosureSource(http, store, time),
    new OsrmRouteProvider(http, new Uri("http://localhost:5000/")),
    time);

var route = new Route("jhapa-lumbini", [
    new Leg("D1", new GeoPoint(26.66, 87.99), new GeoPoint(27.48, 83.28), new DateOnly(2026, 9, 28)),
]);

var result = await assessor.AssessAsync(route, RouteRuleSet.V1, CancellationToken.None);
foreach (var leg in result.Legs)
{
    Console.WriteLine($"{leg.Assessment.LegId} {leg.Assessment.Date}: {leg.Assessment.Status}");
}
```

`result.Health` has the health of every input; show it next to the verdict. To report only what
changed since the last run, compare each assessment with the previous one using
`AssessmentDiff.Compare(previous, current)`: it returns status changes only, so a forecast moving
from 70 mm to 90 mm on a rule already broken is not news.

## How it decides

Four rules per leg and day, each `Pass`, `Watch` or `Breach`:

| Rule | Breach when |
| --- | --- |
| Official | DHM has an Orange or Red warning for a district the leg crosses (Yellow is a watch) |
| Road | a DoR closure in force lies within 2 km of the road |
| Hill rain | a model's day maximum on the leg's hill sections is over 64 mm, or at least half the ensemble members are |
| Wind | a model's day maximum gust on the leg is over 40 km/h, or at least half the ensemble members are |

An official warning is never lowered by a model. DHM's current warnings are applied to today and
the next two days; a later day has no official level yet, and the output says so rather than
showing it as clear. Global models smooth out rain on steep ground, and in the storm this was
built from they were lower than DHM in 14 of 28 province-days, all in the mountains, so trust DHM
there.

Hill sections come from ground elevation along the road, not from district belts. The two
thresholds (150 m of range within 5 km, or above 700 m) are defaults that have not yet been tuned
against measured routes.

## Where it comes from

A rider's storm report of 2026-09-24 did this by hand: pull ECMWF, GFS and ICON, compare with
DHM's district warnings, apply four trip rules to every leg, recheck each evening. Jharicast's
rules reproduce that report's own alert levels and trip verdicts in its tests.

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).

## License

MIT for the code ([LICENSE](LICENSE)). Data remains under its owners' terms, above and in
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).
