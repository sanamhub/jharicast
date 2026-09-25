using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Jharicast.Nepal;
using Xunit;

namespace Jharicast.Tests.Nepal;

public sealed class NepalSourceTests
{
    private static ReadOnlyMemory<byte> Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // Copied from the reference tests unchanged.
    [Fact]
    public void Gazetteer_has_77_districts_and_resolves_every_spelling_seen_in_dhm_sources()
    {
        Assert.Equal(77, Gazetteer.Districts.Count);
        Assert.Equal(77, Gazetteer.Districts.Select(d => d.Id).Distinct().Count());
        string[] seen = ["Chitawan", "Kabhrepalanchok", "Makawanpur", "Tanahu", "Dhanusa", "Nawalparasi_E", "Rukum_W", "Kapilbastu", "RUKUM_E", "Nawalparasi West"];
        Assert.All(seen, name => Assert.True(Gazetteer.TryResolve(name, out _), name));
        Assert.Equal(16, Gazetteer.Districts.Count(d => d.Belt == Belt.Mountain));
    }

    // The 2026-09-24 capture spells districts two ways: data[].areaName and real_result.*[].area_name.
    [Fact]
    public void Gazetteer_resolves_every_area_name_in_the_dhm_fixture()
    {
        using var document = JsonDocument.Parse(Fixture("dhm-getapidata-1-2026-09-24.json"));
        var root = document.RootElement;
        var names = root.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("areaName").GetString())
            .Concat(root.GetProperty("real_result").EnumerateObject().SelectMany(h => h.Value.EnumerateArray()).Select(e => e.GetProperty("area_name").GetString()))
            .Distinct()
            .ToArray();

        Assert.Equal(77, root.GetProperty("data").GetArrayLength());
        Assert.All(names, name => Assert.True(Gazetteer.TryResolve(name, out _), name));
        Assert.Equal(77, names.Select(n => Gazetteer.TryResolve(n, out var d) ? d.Id : null).Distinct().Count());
    }

    // The index is built by a static initializer that throws on a duplicate key. Running the
    // type's constructor here turns a data bug into a failing test, not a crash at first use.
    [Fact]
    public void No_two_districts_share_an_alias_key()
    {
        RuntimeHelpers.RunClassConstructor(typeof(Gazetteer).TypeHandle);

        var keys = Gazetteer.Districts.SelectMany(d => d.Aliases.Prepend(d.Name)).Select(Gazetteer.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
    }

    // Trap from the task: suffix forms are not redundant aliases.
    [Fact]
    public void Suffix_forms_key_differently_from_the_full_name()
    {
        Assert.Equal("rukume", Gazetteer.Key("Rukum_E"));
        Assert.Equal("sankhuwasabha", Gazetteer.Key("Sankhuwa Sabha"));
        Assert.True(Gazetteer.TryResolve("Rukum_E", out var east));
        Assert.Equal("rukumeast", east.Id);
        Assert.False(Gazetteer.TryResolve("Atlantis", out _));
        Assert.False(Gazetteer.TryResolve(null, out _));
    }

    [Fact]
    public void Every_district_has_exactly_one_headquarters_town()
    {
        var headquarters = Gazetteer.Towns.Where(t => t.IsHeadquarters).GroupBy(t => t.DistrictId).ToDictionary(g => g.Key, g => g.Count());

        Assert.All(Gazetteer.Districts, d => Assert.Equal(1, headquarters.GetValueOrDefault(d.Id)));
        Assert.Equal(77, headquarters.Count);
    }

    [Fact]
    public void Every_town_is_in_a_known_district_and_inside_nepal()
    {
        var ids = Gazetteer.Districts.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);

        Assert.All(Gazetteer.Towns, t =>
        {
            Assert.Contains(t.DistrictId, ids);
            Assert.InRange(t.Location.Latitude, 26.3, 30.5);
            Assert.InRange(t.Location.Longitude, 80.0, 88.3);
        });
    }

    // The towns the storm report names, by the name the report uses.
    [Theory]
    [InlineData("Birtamod", "jhapa")]
    [InlineData("Biratnagar", "morang")]
    [InlineData("Janakpur", "dhanusha")]
    [InlineData("Hetauda", "makwanpur")]
    [InlineData("Pokhara", "kaski")]
    [InlineData("Beni", "myagdi")]
    [InlineData("Tansen", "palpa")]
    [InlineData("Butwal", "rupandehi")]
    [InlineData("Bhairahawa", "rupandehi")]
    [InlineData("Lumbini", "rupandehi")]
    [InlineData("Nepalgunj", "banke")]
    [InlineData("Birendranagar", "surkhet")]
    [InlineData("Dhangadhi", "kailali")]
    [InlineData("Mahendranagar", "kanchanpur")]
    [InlineData("Burtibang", "baglung")]
    [InlineData("Dhorpatan", "baglung")]
    [InlineData("Thakurdwara", "bardiya")]
    [InlineData("Kathmandu", "kathmandu")]
    public void The_report_towns_are_listed(string name, string districtId)
    {
        var town = Assert.Single(Gazetteer.Towns, t => t.Name == name || t.Aliases.Contains(name));

        Assert.Equal(districtId, town.DistrictId);
    }

    [Fact]
    public void Nepali_names_have_no_stray_spaces()
    {
        Assert.All(Gazetteer.Districts, d => Assert.Equal(d.NameNe.Trim(), d.NameNe));
    }
}
