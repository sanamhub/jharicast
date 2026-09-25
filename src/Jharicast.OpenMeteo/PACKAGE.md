# Jharicast.OpenMeteo

Forecast, ensemble and elevation clients for [Open-Meteo](https://open-meteo.com/), from
[Jharicast](https://github.com/sanamhub/jharicast). Daily values per model and per ensemble member, one ensemble model per
request, up to 50 points per call, cached until the model's next run.

**Terms.** Open-Meteo's data is CC BY 4.0. Its free API is for non-commercial use only; a
commercial user needs a paid plan and sets `OpenMeteoOptions.BaseAddress` and `ApiKey`. This
package does not decide that for you. Anything that shows the numbers must credit
"Weather data by Open-Meteo.com (CC BY 4.0)" and, for ECMWF models, "Contains ECMWF data
(CC BY 4.0)".

Build the `HttpClient` on `Jharicast.Fetch.PoliteHttpHandler` so requests are identified
and spaced. Results come back in the order of the points asked for: Open-Meteo snaps
coordinates to its grid, so match by position.
