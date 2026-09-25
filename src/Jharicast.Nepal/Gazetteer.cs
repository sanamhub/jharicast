using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace Jharicast.Nepal;

/// <summary>Nepal's seven provinces, numbered as in the constitution.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1008", Justification = "Values are the constitutional province numbers; there is no province 0.")]
public enum Province
{
    /// <summary>Koshi (1).</summary>
    Koshi = 1,

    /// <summary>Madhesh (2).</summary>
    Madhesh = 2,

    /// <summary>Bagmati (3).</summary>
    Bagmati = 3,

    /// <summary>Gandaki (4).</summary>
    Gandaki = 4,

    /// <summary>Lumbini (5).</summary>
    Lumbini = 5,

    /// <summary>Karnali (6).</summary>
    Karnali = 6,

    /// <summary>Sudurpashchim (7).</summary>
    Sudurpashchim = 7,
}

/// <summary>Ecological belt. Used for display and summaries, never for rules: hill sections come from elevation (ADR-0010).</summary>
public enum Belt
{
    /// <summary>Mountain (Himalayan) district.</summary>
    Mountain = 0,

    /// <summary>Hill district.</summary>
    Hill = 1,

    /// <summary>Terai or Inner Terai district.</summary>
    Terai = 2,
}

/// <summary>A district.</summary>
/// <param name="Id">Stable lower-case id, letters only, for example <c>nawalparasieast</c>.</param>
/// <param name="Name">English name as the Survey Department spells it.</param>
/// <param name="NameNe">Nepali name.</param>
/// <param name="Province">Province.</param>
/// <param name="Belt">Ecological belt.</param>
/// <param name="Aliases">Other spellings seen in official sources.</param>
public sealed record District(string Id, string Name, string NameNe, Province Province, Belt Belt, IReadOnlyList<string> Aliases);

/// <summary>
/// The 77 districts and every spelling we have seen for them. Sources spell names differently:
/// DHM's warning API says "Chitawan", "Kabhrepalanchok", "Makawanpur", "Tanahu"; its GeoJSON says
/// "Chitwan", "Dhanusa", "Nawalparasi_E", "Rukum_E". <see cref="TryResolve"/> maps all of them.
/// </summary>
/// <remarks>
/// Nepali names come from DHM's district GeoJSON. Belts follow the CBS 16 mountain and 21 Terai
/// district lists and are UNVERIFIED against the current CBS publication (IMPLEMENTATION task N02).
/// </remarks>
public static partial class Gazetteer
{
    /// <summary>All 77 districts, grouped by province.</summary>
    public static IReadOnlyList<District> Districts { get; } =
    [
        new("taplejung", "Taplejung", "ताप्लेजुङ", Province.Koshi, Belt.Mountain, []),
        new("panchthar", "Panchthar", "पाँचथर", Province.Koshi, Belt.Hill, []),
        new("ilam", "Ilam", "ईलाम", Province.Koshi, Belt.Hill, []),
        new("jhapa", "Jhapa", "झापा", Province.Koshi, Belt.Terai, []),
        new("morang", "Morang", "मोरङ्ग", Province.Koshi, Belt.Terai, []),
        new("sunsari", "Sunsari", "सुनसरी", Province.Koshi, Belt.Terai, []),
        new("dhankuta", "Dhankuta", "धनकुटा", Province.Koshi, Belt.Hill, []),
        new("terhathum", "Terhathum", "तेह्रथुम", Province.Koshi, Belt.Hill, ["Tehrathum"]),
        new("sankhuwasabha", "Sankhuwasabha", "सङ्खुवासभा", Province.Koshi, Belt.Mountain, []),
        new("bhojpur", "Bhojpur", "भोजपुर", Province.Koshi, Belt.Hill, []),
        new("solukhumbu", "Solukhumbu", "सोलुखुम्बु", Province.Koshi, Belt.Mountain, []),
        new("okhaldhunga", "Okhaldhunga", "ओखलढुङ्गा", Province.Koshi, Belt.Hill, []),
        new("khotang", "Khotang", "खोटाङ्ग", Province.Koshi, Belt.Hill, []),
        new("udayapur", "Udayapur", "उदयपुर", Province.Koshi, Belt.Hill, []),
        new("saptari", "Saptari", "सप्तरी", Province.Madhesh, Belt.Terai, []),
        new("siraha", "Siraha", "सिराहा", Province.Madhesh, Belt.Terai, []),
        new("dhanusha", "Dhanusha", "धनुषा", Province.Madhesh, Belt.Terai, ["Dhanusa"]),
        new("mahottari", "Mahottari", "महोत्तरी", Province.Madhesh, Belt.Terai, []),
        new("sarlahi", "Sarlahi", "सर्लाही", Province.Madhesh, Belt.Terai, []),
        new("rautahat", "Rautahat", "रौतहट", Province.Madhesh, Belt.Terai, []),
        new("bara", "Bara", "बारा", Province.Madhesh, Belt.Terai, []),
        new("parsa", "Parsa", "पर्सा", Province.Madhesh, Belt.Terai, []),
        new("dolakha", "Dolakha", "दोलखा", Province.Bagmati, Belt.Mountain, []),
        new("sindhupalchok", "Sindhupalchok", "सिन्धुपाल्चोक", Province.Bagmati, Belt.Mountain, ["Sindhupalchowk"]),
        new("rasuwa", "Rasuwa", "रसुवा", Province.Bagmati, Belt.Mountain, []),
        new("dhading", "Dhading", "धादिङ्ग", Province.Bagmati, Belt.Hill, []),
        new("nuwakot", "Nuwakot", "नुवाकोट", Province.Bagmati, Belt.Hill, []),
        new("kathmandu", "Kathmandu", "काठमाडौं", Province.Bagmati, Belt.Hill, []),
        new("bhaktapur", "Bhaktapur", "भक्तपुर", Province.Bagmati, Belt.Hill, []),
        new("lalitpur", "Lalitpur", "ललितपुर", Province.Bagmati, Belt.Hill, []),
        new("kavrepalanchok", "Kavrepalanchok", "काभ्रेपलाञ्चोक", Province.Bagmati, Belt.Hill, ["Kabhrepalanchok", "Kavre", "Kabhre"]),
        new("ramechhap", "Ramechhap", "रामेछाप", Province.Bagmati, Belt.Hill, []),
        new("sindhuli", "Sindhuli", "सिन्धुली", Province.Bagmati, Belt.Hill, []),
        new("makwanpur", "Makwanpur", "मकवानपुर", Province.Bagmati, Belt.Hill, ["Makawanpur"]),
        new("chitwan", "Chitwan", "चितवन", Province.Bagmati, Belt.Terai, ["Chitawan"]),
        new("gorkha", "Gorkha", "गोरखा", Province.Gandaki, Belt.Hill, []),
        new("manang", "Manang", "मनाङ", Province.Gandaki, Belt.Mountain, []),
        new("mustang", "Mustang", "मुस्ताङ", Province.Gandaki, Belt.Mountain, []),
        new("myagdi", "Myagdi", "म्याग्दी", Province.Gandaki, Belt.Hill, []),
        new("kaski", "Kaski", "कास्की", Province.Gandaki, Belt.Hill, []),
        new("lamjung", "Lamjung", "लम्जुङ्ग", Province.Gandaki, Belt.Hill, []),
        new("tanahun", "Tanahun", "तनहुँ", Province.Gandaki, Belt.Hill, ["Tanahu"]),
        new("nawalparasieast", "Nawalparasi East", "नवलपरासी -पर्व", Province.Gandaki, Belt.Terai, ["Nawalparasi_E", "Nawalpur", "Nawalparasi (Bardaghat Susta East)"]),
        new("syangja", "Syangja", "स्याङ्जा", Province.Gandaki, Belt.Hill, []),
        new("parbat", "Parbat", "पर्वत", Province.Gandaki, Belt.Hill, []),
        new("baglung", "Baglung", "बागलुङ", Province.Gandaki, Belt.Hill, []),
        new("rukumeast", "Rukum East", "रुकुम-ई", Province.Lumbini, Belt.Hill, ["Rukum_E", "Eastern Rukum"]),
        new("rolpa", "Rolpa", "रोल्पा", Province.Lumbini, Belt.Hill, []),
        new("pyuthan", "Pyuthan", "प्युठान", Province.Lumbini, Belt.Hill, []),
        new("gulmi", "Gulmi", "गुल्मी", Province.Lumbini, Belt.Hill, []),
        new("arghakhanchi", "Arghakhanchi", "अर्घाखाँची", Province.Lumbini, Belt.Hill, []),
        new("palpa", "Palpa", "पाल्पा", Province.Lumbini, Belt.Hill, []),
        new("nawalparasiwest", "Nawalparasi West", "नवलपरासी - पश्चिम", Province.Lumbini, Belt.Terai, ["Nawalparasi_W", "Parasi", "Nawalparasi (Bardaghat Susta West)"]),
        new("rupandehi", "Rupandehi", "रुपन्देही", Province.Lumbini, Belt.Terai, []),
        new("kapilvastu", "Kapilvastu", "कपिलवस्तु", Province.Lumbini, Belt.Terai, ["Kapilbastu"]),
        new("dang", "Dang", "दाङ्ग", Province.Lumbini, Belt.Terai, []),
        new("banke", "Banke", "बाँके", Province.Lumbini, Belt.Terai, []),
        new("bardiya", "Bardiya", "बर्दिया", Province.Lumbini, Belt.Terai, []),
        new("dolpa", "Dolpa", "डोल्पा", Province.Karnali, Belt.Mountain, []),
        new("mugu", "Mugu", "मुगु", Province.Karnali, Belt.Mountain, []),
        new("humla", "Humla", "हुम्ला", Province.Karnali, Belt.Mountain, []),
        new("jumla", "Jumla", "जुम्ला", Province.Karnali, Belt.Mountain, []),
        new("kalikot", "Kalikot", "कालीकोट", Province.Karnali, Belt.Mountain, []),
        new("dailekh", "Dailekh", "दैलेख", Province.Karnali, Belt.Hill, []),
        new("jajarkot", "Jajarkot", "जाजरकोट", Province.Karnali, Belt.Hill, []),
        new("rukumwest", "Rukum West", "पश्चिम रुकुम", Province.Karnali, Belt.Hill, ["Rukum_W", "Western Rukum"]),
        new("salyan", "Salyan", "सल्यान", Province.Karnali, Belt.Hill, []),
        new("surkhet", "Surkhet", "सुर्खेत", Province.Karnali, Belt.Hill, []),
        new("bajura", "Bajura", "बाजुरा", Province.Sudurpashchim, Belt.Mountain, []),
        new("bajhang", "Bajhang", "बझाङ", Province.Sudurpashchim, Belt.Mountain, []),
        new("achham", "Achham", "आछाम", Province.Sudurpashchim, Belt.Hill, []),
        new("doti", "Doti", "डोटी", Province.Sudurpashchim, Belt.Hill, []),
        new("kailali", "Kailali", "कैलाली", Province.Sudurpashchim, Belt.Terai, []),
        new("kanchanpur", "Kanchanpur", "कञ्चनपुर", Province.Sudurpashchim, Belt.Terai, []),
        new("dadeldhura", "Dadeldhura", "डडेल्धुरा", Province.Sudurpashchim, Belt.Hill, []),
        new("baitadi", "Baitadi", "बैतड़ी", Province.Sudurpashchim, Belt.Hill, []),
        new("darchula", "Darchula", "दार्चुला", Province.Sudurpashchim, Belt.Mountain, []),
    ];

    // Declared after Districts on purpose: static initializers run in source order.
    private static readonly FrozenDictionary<string, District> ByKey = BuildIndex();

    /// <summary>Resolves any known spelling, ignoring case, spaces, underscores and punctuation.</summary>
    /// <param name="name">A district name from any source.</param>
    /// <param name="district">The district, when found.</param>
    /// <returns>True when found. A false result from a feed is schema drift: log it, do not guess.</returns>
    public static bool TryResolve(string? name, out District district)
    {
        district = null!;
        return name is not null && ByKey.TryGetValue(Key(name), out district!);
    }

    internal static string Key(string name) => new(name.Where(char.IsAsciiLetter).Select(char.ToLowerInvariant).ToArray());

    private static FrozenDictionary<string, District> BuildIndex()
    {
        var index = new Dictionary<string, District>(StringComparer.Ordinal);
        foreach (var d in Districts)
        {
            foreach (var spelling in d.Aliases.Prepend(d.Name))
            {
                index.Add(Key(spelling), d); // throws on a duplicate alias: a data bug caught at type load
            }
        }

        return index.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
