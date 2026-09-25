# Jharicast.Routing

Road geometry and districts for [Jharicast](https://github.com/sanamhub/jharicast): `OsrmRouteProvider` for OSRM's route
service, `RouteSampler` for points every 5 km with elevation-based hill sections, and
`DistrictResolver` for the Nepal district a point lies in.

Point `OsrmRouteProvider` at your own OSRM server built on an OpenStreetMap extract. The public
demo server allows light personal use only. Routes are OpenStreetMap data:
"© OpenStreetMap contributors" (ODbL).

**Embedded data.** The district polygons are from the Survey Department of Nepal and UN RCO
Nepal, via OCHA / HDX (cod-ab-npl), licensed
[CC BY-IGO 3.0](https://creativecommons.org/licenses/by/3.0/igo/legalcode), and simplified to
about 200 m. They keep that licence, not MIT. Credit and the list of changes are in
[THIRD-PARTY-NOTICES.txt](https://github.com/sanamhub/jharicast/blob/main/THIRD-PARTY-NOTICES.txt), which is also in this package.
