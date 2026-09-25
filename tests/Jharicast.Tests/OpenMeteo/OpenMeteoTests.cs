using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jharicast.OpenMeteo;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Jharicast.Tests.OpenMeteo;

public sealed class OpenMeteoReaderTests
{
    private static ReadOnlyMemory<byte> Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // Copied from the reference tests unchanged.
    [Fact]
    public void Open_meteo_ensemble_fixture_has_control_plus_50_members_per_day()
    {
        var locations = EnsembleResponseReader.ReadDaily(Fixture("open-meteo-ensemble-ecmwf-2026-09-25.json"), "precipitation_sum");

        Assert.Equal(2, locations.Count);
        Assert.Equal(3, locations[0].Days.Count);
        Assert.All(locations[0].Members, day => Assert.Equal(51, day.Count));
        Assert.Equal(28.0, locations[0].Latitude); // requested 28.05: the grid snaps
    }

    // Trap O1: two ensemble models in one request suffix every member key with the model.
    [Fact]
    public void A_multi_model_ensemble_response_is_rejected_with_a_clear_message()
    {
        var json = """
            {"latitude":28.0,"longitude":81.5,"daily":{"time":["2026-09-25"],
             "precipitation_sum_ecmwf_ifs025":[1.0],"precipitation_sum_member01_ecmwf_ifs025":[2.0],
             "precipitation_sum_gfs025":[3.0],"precipitation_sum_member01_gfs025":[4.0]}}
            """u8.ToArray();

        var error = Assert.Throws<JsonException>(() => EnsembleResponseReader.ReadDaily(json, "precipitation_sum"));

        Assert.Contains("Request one model at a time", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Forecast_reader_splits_suffixed_keys_by_model()
    {
        var json = """
            [{"latitude":28.0,"longitude":81.5,"daily":{"time":["2026-09-28","2026-09-29"],
              "precipitation_sum_ecmwf_ifs025":[1.0,0.2],"precipitation_sum_gfs_seamless":[10.0,null],
              "wind_gusts_10m_max_ecmwf_ifs025":[35.0,27.0],"wind_gusts_10m_max_gfs_seamless":[31.0,17.0]}},
             {"latitude":27.5,"longitude":83.25,"daily":{"time":["2026-09-28","2026-09-29"],
              "precipitation_sum_ecmwf_ifs025":[1.5,0.0],"precipitation_sum_gfs_seamless":[9.0,9.0],
              "wind_gusts_10m_max_ecmwf_ifs025":[33.0,20.0],"wind_gusts_10m_max_gfs_seamless":[30.0,12.0]}}]
            """u8.ToArray();

        var byModel = ForecastResponseReader.ReadDaily(json, ["ecmwf_ifs025", "gfs_seamless"], ["precipitation_sum", "wind_gusts_10m_max"]);

        Assert.Equal(2, byModel["gfs_seamless"].Count);
        Assert.Equal([10.0, null], byModel["gfs_seamless"][0].Variables["precipitation_sum"]);
        Assert.Equal([33.0, 20.0], byModel["ecmwf_ifs025"][1].Variables["wind_gusts_10m_max"]);
        Assert.Equal(new DateOnly(2026, 9, 29), byModel["ecmwf_ifs025"][1].Days[1]);
        Assert.Equal("ecmwf_ifs025", byModel["ecmwf_ifs025"][0].Model);
    }

    [Fact]
    public void Forecast_reader_accepts_bare_keys_for_a_single_model_and_names_a_missing_column()
    {
        var json = """{"latitude":28.0,"longitude":81.5,"daily":{"time":["2026-09-28"],"precipitation_sum":[4.2]}}"""u8.ToArray();

        Assert.Equal([4.2], ForecastResponseReader.ReadDaily(json, ["icon_seamless"], ["precipitation_sum"])["icon_seamless"][0].Variables["precipitation_sum"]);
        var error = Assert.Throws<JsonException>(() => ForecastResponseReader.ReadDaily(json, ["icon_seamless", "gfs_seamless"], ["precipitation_sum"]));
        Assert.Contains("precipitation_sum_icon_seamless", error.Message, StringComparison.Ordinal);
    }
}

public sealed class OpenMeteoClientTests
{
    private static readonly GeoPoint Nepalgunj = new(28.05, 81.617);
    private static readonly GeoPoint Kathmandu = new(27.7172, 85.324);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 25, 3, 0, 0, TimeSpan.Zero));

    private OpenMeteoClient Client(RecordingHandler handler) => new(new HttpClient(handler), new OpenMeteoOptions { TimeProvider = _time });

    [Fact]
    public async Task Ensemble_request_url_for_two_points_and_one_model()
    {
        var handler = new RecordingHandler(_ => Fixture("open-meteo-ensemble-ecmwf-2026-09-25.json"));
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            await Client(handler).GetEnsembleDailyAsync([Nepalgunj, Kathmandu], "ecmwf_ifs025", ["precipitation_sum"], 3, TestContext.Current.CancellationToken);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.Equal(
            "https://ensemble-api.open-meteo.com/v1/ensemble?latitude=28.05,27.7172&longitude=81.617,85.324&daily=precipitation_sum&models=ecmwf_ifs025&forecast_days=3&timezone=Asia%2FKathmandu&wind_speed_unit=kmh",
            handler.Requests.Single().AbsoluteUri);
    }

    [Fact]
    public async Task Ensemble_results_keep_request_order_and_carry_model_provenance()
    {
        var handler = new RecordingHandler(_ => Fixture("open-meteo-ensemble-ecmwf-2026-09-25.json"));

        var result = await Client(handler).GetEnsembleDailyAsync([Nepalgunj, Kathmandu], "ecmwf_ifs025", ["precipitation_sum"], 3, TestContext.Current.CancellationToken);

        var locations = result["precipitation_sum"];
        Assert.Equal([28.0, 27.75], locations.Select(l => l.Latitude));
        Assert.All(locations, l =>
        {
            Assert.Equal("open-meteo.ensemble.ecmwf_ifs025", l.Provenance!.Source);
            Assert.Equal(SourceKind.Model, l.Provenance.Kind);
            Assert.Equal(_time.GetUtcNow(), l.Provenance.FetchedAt);
        });
    }

    [Fact]
    public async Task Points_are_batched_50_per_request()
    {
        var points = Enumerable.Range(0, 51).Select(i => new GeoPoint(26 + (i * 0.05), 80 + (i * 0.1))).ToArray();
        var handler = new RecordingHandler(uri => EnsembleJson(uri.Query.Split('&')[0].Count(c => c == ',') + 1));

        var result = await Client(handler).GetEnsembleDailyAsync(points, "gfs025", ["wind_gusts_10m_max"], 5, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(51, result["wind_gusts_10m_max"].Count);
        Assert.StartsWith("?latitude=28.5&", handler.Requests[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Several_ensemble_models_in_one_call_are_refused()
    {
        var handler = new RecordingHandler(_ => []);

        await Assert.ThrowsAsync<ArgumentException>(() => Client(handler).GetEnsembleDailyAsync([Kathmandu], "ecmwf_ifs025,gfs025", ["precipitation_sum"], 3, TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Deterministic_request_names_every_model_and_results_carry_forecast_provenance()
    {
        var json = """
            {"latitude":27.5,"longitude":83.25,"daily":{"time":["2026-09-28"],
             "precipitation_sum_ecmwf_ifs025":[1.0],"precipitation_sum_gfs_seamless":[10.0],"precipitation_sum_icon_seamless":[1.0],
             "wind_gusts_10m_max_ecmwf_ifs025":[35.0],"wind_gusts_10m_max_gfs_seamless":[31.0],"wind_gusts_10m_max_icon_seamless":[24.0]}}
            """u8.ToArray();
        var handler = new RecordingHandler(_ => json);

        var result = await Client(handler).GetDailyAsync([new GeoPoint(27.48, 83.28)], ["ecmwf_ifs025", "gfs_seamless", "icon_seamless"], ["precipitation_sum", "wind_gusts_10m_max"], 7, TestContext.Current.CancellationToken);

        Assert.Equal(
            "https://api.open-meteo.com/v1/forecast?latitude=27.48&longitude=83.28&daily=precipitation_sum,wind_gusts_10m_max&models=ecmwf_ifs025,gfs_seamless,icon_seamless&forecast_days=7&timezone=Asia%2FKathmandu&wind_speed_unit=kmh",
            handler.Requests.Single().AbsoluteUri);
        Assert.Equal(35.0, result["ecmwf_ifs025"][0].Variables["wind_gusts_10m_max"][0]);
        Assert.Equal("open-meteo.forecast.icon_seamless", result["icon_seamless"][0].Provenance!.Source);
    }

    [Fact]
    public async Task An_error_status_carries_open_meteos_reason()
    {
        var handler = new RecordingHandler(_ => """{"error":true,"reason":"Cannot initialize WeatherVariable from invalid String value rain_sum_x"}"""u8.ToArray(), HttpStatusCode.BadRequest);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).GetDailyAsync([Kathmandu], ["gfs_seamless"], ["rain_sum_x"], 3, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("rain_sum_x", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_api_key_is_sent_for_a_paid_plan()
    {
        var handler = new RecordingHandler(_ => Fixture("open-meteo-ensemble-ecmwf-2026-09-25.json"));
        var client = new OpenMeteoClient(new HttpClient(handler), new OpenMeteoOptions { ApiKey = "test-key", TimeProvider = _time });

        await client.GetEnsembleDailyAsync([Nepalgunj, Kathmandu], "ecmwf_ifs025", ["precipitation_sum"], 3, TestContext.Current.CancellationToken);

        Assert.EndsWith("&apikey=test-key", handler.Requests.Single().Query, StringComparison.Ordinal);
    }

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    internal static byte[] EnsembleJson(int locations)
    {
        var one = """{"latitude":27.5,"longitude":83.25,"daily":{"time":["2026-09-25"],"precipitation_sum":[3.0],"precipitation_sum_member01":[4.0],"wind_gusts_10m_max":[30.0],"wind_gusts_10m_max_member01":[40.0]}}""";
        return Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat(one, locations)) + "]");
    }

    /// <summary>Answers every request from a function and records the URLs. Nothing leaves the process.</summary>
    internal sealed class RecordingHandler(Func<Uri, byte[]> respond, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(respond(request.RequestUri!)) });
        }
    }
}
