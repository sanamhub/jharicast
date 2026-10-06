# Changelog

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

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
