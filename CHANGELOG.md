# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0-alpha.2] - 2026-10-09

The core packages are unchanged and move to 0.1.0-alpha.2 only because every package shares one
version. The fixes are in `Jharicast.Nepal`, which is not on nuget.org yet.

### Fixed

- `DhmWarningMapSource` reads DHM's newest published bulletin even when it has maps for only some
  of days 1 to 3. It used to fall back to the newest complete bulletin, which could be a day old,
  and show its levels as current. A day without a map is now left out of `Days`, so the official
  rule says Unknown for it. Two maps for one day are drift.
- The warning-map decoder checks every PNG chunk's CRC. A damaged download that still inflated
  to the right size could read as another district's level.

## [0.1.0-alpha.1] - 2026-10-07

### Changed

- **Breaking:** `DhmWarningsSource` is retired by default. It sends no request, reports
  `Disabled`, and the official rule says Unknown. On 2026-10-06 the feed's `real_result` was
  identical to the copy saved on 2026-09-24 and still listed 19 Orange districts, while DHM's
  map for that day had none. Set `Retired = false` only to replay responses recorded while the
  feed was current; `jharicast` does so for `--fixtures`.

### Fixed

- `DorClosureSource` reads DoR's real time fields, `date_roadblock_start`,
  `date_roadblock_end_estimated` and `date_roadblock_end` (checked against the live feed on
  2026-10-06). The guessed names dropped every time, so closures that had ended still counted
  as in force and the source reported `missing:start_time` drift on every fetch.

### Added

- `DhmWarningMapSource`: DHM's district warnings read from the three-day warning maps DHM now
  publishes instead of the feed, under the same source id `dhm.warnings`. Each district gets a
  level per date (`DistrictWarning.ValidOn`, `DhmWarningSnapshot.Days`, `LevelOn`) from the fill
  colour of most of its pixels; checked against 36 maps from 2026-06-29 to 2026-10-06, where each
  district was at least 98 percent one colour. A map that cannot be read is drift and gives no
  levels, so the official rule says Unknown rather than a wrong level. A poll is 1 request, or 4
  when a new bulletin is out. The hazard is not on the map in a readable form, so warnings carry
  the new `Hazard.Unspecified`. ADR-0016.
- `NepalRouteAssessor` gives each leg the official levels for its own date when the warnings are
  dated. A date inside `OfficialHorizonDays` that the newest bulletin does not reach (DHM missed
  issues) is Unknown, not "no official warning"; a date past the horizon has no official level.
  `jharicast` reads the maps on live runs.

- `DhmWarningsSource.WarningsPage`: the page where DHM publishes its current warnings as maps,
  for apps that link to the official source.

- `PoliteHttpOptions.OperatedHosts`: hosts the caller runs itself, such as a self-hosted
  Open-Meteo, whose robots.txt is not read. Every other politeness rule still applies.
- `RuleStatus.Unknown`: a rule whose source did not answer says so instead of passing.
  `RouteRuleSet.Worst` ranks Breach, Watch, Unknown, Pass. `ModelSample.Known`,
  `LegDayInput.OfficialKnown` and `LegDayInput.RoadKnown` carry what is missing.
- `NepalRouteAssessor` keeps going when Open-Meteo's elevation, forecast or ensemble endpoints
  fail or are refused: the failure goes into the health, the model rules say Unknown, and the
  official and road rules still answer. Without elevations every sample counts as hill.
- `NepalRouteAssessor` in `Jharicast.Nepal`: per leg and day, the four trip rules over DHM's
  current warnings, DoR closures within 2 km of the road, and Open-Meteo day maxima and pooled
  ECMWF ENS and GEFS members along the route, with the health and provenance of every input.
  DHM's current warnings apply to today and the next two days (`OfficialHorizonDays`).
  `Jharicast.Nepal` now depends on `Jharicast.OpenMeteo` and `Jharicast.Routing`.
- `Jharicast.Cli`, the `jharicast` command: `route`, `alerts` and `sources check`, with
  `--json`, offline replay from recorded files (`--fixtures`), and a required contact mailbox
  for live requests (`--contact` or `JHARICAST_CONTACT`). Held back from nuget.org with
  `Jharicast.Nepal`, which it contains.
- Every package carries a `PACKAGE.md`; `Jharicast.Routing`, `Jharicast.Nepal` and
  `Jharicast.Cli` carry `THIRD-PARTY-NOTICES.txt` for the data they embed.
- Release workflow: tag, version and changelog checked first; build, test, pack, Native AOT
  consumption and SBOMs; publish after production approval with NuGet trusted publishing.
  `Jharicast.Nepal` and `Jharicast.Cli` stay out of nuget.org and the release page until the
  `PUBLISH_NEPAL` repository variable is `true`.
