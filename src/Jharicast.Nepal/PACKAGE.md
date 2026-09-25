# Jharicast.Nepal

The Nepal pack of [Jharicast](https://github.com/sanamhub/jharicast): the 77 districts with every spelling seen in official
feeds, district headquarters and other towns, sources for DHM warnings and bulletins, BIPAD
alerts and incidents, and DoR road closures, and `NepalRouteAssessor`, which joins them with
routing and Open-Meteo into per-leg, per-day assessments.

> **Not an official forecast.** Follow the Department of Hydrology and Meteorology (DHM) and the
> District Administration Office.

**Data ownership.** The feeds belong to DHM, the Department of Roads and NDRRMA (BIPAD). This
package is not published to nuget.org until those agencies grant permission to poll and use
their data. DoR contact names and phone numbers are dropped before anything is stored.

**Embedded data.** Town positions are OpenStreetMap data, "© OpenStreetMap contributors",
under the [ODbL 1.0](https://opendatacommons.org/licenses/odbl/1-0/); see
[THIRD-PARTY-NOTICES.txt](https://github.com/sanamhub/jharicast/blob/main/THIRD-PARTY-NOTICES.txt), which is also in this package.

Give every source an `HttpClient` built on `Jharicast.Fetch.PoliteHttpHandler`, with
5 seconds between requests to each government host, and poll no more often than every
30 minutes.
