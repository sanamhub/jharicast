using System.Collections.Generic;

namespace Jharicast.Nepal;

/// <summary>A town: every district headquarters, plus towns the storm report names.</summary>
/// <param name="Name">English name.</param>
/// <param name="DistrictId">The <see cref="District.Id"/> it lies in.</param>
/// <param name="Location">Position of its OpenStreetMap place node.</param>
/// <param name="IsHeadquarters">True for the district headquarters.</param>
/// <param name="Aliases">Other names in common use, for example "Bhairahawa" for Siddharthanagar.</param>
public sealed record Town(string Name, string DistrictId, GeoPoint Location, bool IsHeadquarters, IReadOnlyList<string> Aliases);

public static partial class Gazetteer
{
    /// <summary>
    /// District headquarters (one per district) and the storm report's other towns, in district order.
    /// Names are not unique: "Khalanga" is Darchula's headquarters and an old name for Baitadi's.
    /// </summary>
    /// <remarks>
    /// Positions are OpenStreetMap place nodes (© OpenStreetMap contributors, ODbL), each cited by id.
    /// Headquarters come from the <c>admin_centre</c> member of each district relation, read on
    /// 2026-09-25; Jumla's relation has none, so Chandannath is taken from its own node.
    /// </remarks>
    public static IReadOnlyList<Town> Towns { get; } =
    [
        new("Phungling", "taplejung", new GeoPoint(27.3586978, 87.6727257), IsHeadquarters: true, []), // OSM node 607154975
        new("Phidim", "panchthar", new GeoPoint(27.1441345, 87.7660956), IsHeadquarters: true, []), // OSM node 3001756120
        new("Ilam", "ilam", new GeoPoint(26.9103896, 87.9281937), IsHeadquarters: true, []), // OSM node 2424953267
        new("Bhadrapur", "jhapa", new GeoPoint(26.5455566, 88.0903762), IsHeadquarters: true, []), // OSM node 3316604577
        new("Birtamod", "jhapa", new GeoPoint(26.643393, 87.9914702), IsHeadquarters: false, []), // OSM node 575580459
        new("Biratnagar", "morang", new GeoPoint(26.4623007, 87.281617), IsHeadquarters: true, []), // OSM node 329222054
        new("Inaruwa", "sunsari", new GeoPoint(26.6018952, 87.149907), IsHeadquarters: true, []), // OSM node 3261545305
        new("Dhankuta", "dhankuta", new GeoPoint(26.9825083, 87.3451034), IsHeadquarters: true, []), // OSM node 3345770104
        new("Myanglung", "terhathum", new GeoPoint(27.1253718, 87.5365953), IsHeadquarters: true, []), // OSM node 13144876978
        new("Khandbari", "sankhuwasabha", new GeoPoint(27.3747717, 87.2091422), IsHeadquarters: true, []), // OSM node 13151626256
        new("Bhojpur", "bhojpur", new GeoPoint(27.166632, 87.0510921), IsHeadquarters: true, []), // OSM node 3502673882
        new("Solududhkunda", "solukhumbu", new GeoPoint(27.5026073, 86.585868), IsHeadquarters: true, []), // OSM node 3188799346
        new("Siddhicharan", "okhaldhunga", new GeoPoint(27.317984, 86.501404), IsHeadquarters: true, []), // OSM node 3388922070
        new("Diktel", "khotang", new GeoPoint(27.2203282, 86.7909174), IsHeadquarters: true, []), // OSM node 13154634550
        new("Triyuga", "udayapur", new GeoPoint(26.791191, 86.698242), IsHeadquarters: true, []), // OSM node 2802955238
        new("Rajbiraj", "saptari", new GeoPoint(26.539598, 86.74855), IsHeadquarters: true, []), // OSM node 2803154322
        new("Siraha", "siraha", new GeoPoint(26.6602708, 86.2126153), IsHeadquarters: true, []), // OSM node 13191590647
        new("Janakpur", "dhanusha", new GeoPoint(26.7284541, 85.9249005), IsHeadquarters: true, []), // OSM node 806482934
        new("Jaleshwar", "mahottari", new GeoPoint(26.649191, 85.800087), IsHeadquarters: true, []), // OSM node 2307153775
        new("Malangawa", "sarlahi", new GeoPoint(26.8558938, 85.5572948), IsHeadquarters: true, []), // OSM node 3384312037
        new("Gaur", "rautahat", new GeoPoint(26.7660963, 85.2749238), IsHeadquarters: true, []), // OSM node 3384458233
        new("Kalaiya", "bara", new GeoPoint(27.032719, 85.00219), IsHeadquarters: true, []), // OSM node 2199663556
        new("Birgunj", "parsa", new GeoPoint(27.0135196, 84.8763804), IsHeadquarters: true, []), // OSM node 575866576
        new("Bhimeshwar", "dolakha", new GeoPoint(27.6635631, 86.0488323), IsHeadquarters: true, []), // OSM node 3383645104
        new("Chautara", "sindhupalchok", new GeoPoint(27.7751357, 85.7147651), IsHeadquarters: true, []), // OSM node 13249206592
        new("Dhunche", "rasuwa", new GeoPoint(28.1127948, 85.2960583), IsHeadquarters: true, []), // OSM node 10315965426
        new("Nilakantha", "dhading", new GeoPoint(27.9108933, 84.8952805), IsHeadquarters: true, []), // OSM node 3387669747
        new("Bidur", "nuwakot", new GeoPoint(27.89526, 85.146446), IsHeadquarters: true, []), // OSM node 3499801960
        new("Kathmandu", "kathmandu", new GeoPoint(27.708317, 85.3205817), IsHeadquarters: true, []), // OSM node 67157058
        new("Bhaktapur", "bhaktapur", new GeoPoint(27.6711133, 85.4261675), IsHeadquarters: true, []), // OSM node 2035862217
        new("Lalitpur", "lalitpur", new GeoPoint(27.6765635, 85.3166069), IsHeadquarters: true, []), // OSM node 723880699
        new("Dhulikhel", "kavrepalanchok", new GeoPoint(27.618921, 85.552917), IsHeadquarters: true, []), // OSM node 1833834697
        new("Manthali", "ramechhap", new GeoPoint(27.3816177, 86.0607558), IsHeadquarters: true, []), // OSM node 3383548157
        new("Kamalamai", "sindhuli", new GeoPoint(27.213335, 85.915497), IsHeadquarters: true, []), // OSM node 3488407133
        new("Hetauda", "makwanpur", new GeoPoint(27.4295853, 85.0326586), IsHeadquarters: true, []), // OSM node 296306218
        new("Bharatpur", "chitwan", new GeoPoint(27.6811896, 84.4301652), IsHeadquarters: true, []), // OSM node 567090203
        new("Gorkha", "gorkha", new GeoPoint(27.9969584, 84.6259428), IsHeadquarters: true, []), // OSM node 4896508190
        new("Chame", "manang", new GeoPoint(28.5507325, 84.2423535), IsHeadquarters: true, []), // OSM node 295052442
        new("Jomsom", "mustang", new GeoPoint(28.7838097, 83.7304953), IsHeadquarters: true, []), // OSM node 346662028
        new("Beni", "myagdi", new GeoPoint(28.344498, 83.566635), IsHeadquarters: true, []), // OSM node 1043691512
        new("Pokhara", "kaski", new GeoPoint(28.209538, 83.991402), IsHeadquarters: true, []), // OSM node 29922899
        new("Besisahar", "lamjung", new GeoPoint(28.231316, 84.376144), IsHeadquarters: true, []), // OSM node 293621413
        new("Damauli", "tanahun", new GeoPoint(27.9767129, 84.2693537), IsHeadquarters: true, ["Vyas"]), // OSM node 279339377
        new("Kawasoti", "nawalparasieast", new GeoPoint(27.6415327, 84.1251637), IsHeadquarters: true, []), // OSM node 2264286090
        new("Syangja", "syangja", new GeoPoint(28.0987529, 83.8734691), IsHeadquarters: true, ["Putalibazar"]), // OSM node 1513673154
        new("Kusma", "parbat", new GeoPoint(28.2192946, 83.6768133), IsHeadquarters: true, []), // OSM node 3385063307
        new("Baglung", "baglung", new GeoPoint(28.2651055, 83.6029924), IsHeadquarters: true, []), // OSM node 1043713484
        new("Burtibang", "baglung", new GeoPoint(28.3317945, 83.1598487), IsHeadquarters: false, []), // OSM node 3095431553
        new("Dhorpatan", "baglung", new GeoPoint(28.4902865, 83.0672741), IsHeadquarters: false, []), // OSM node 3095431554
        new("Rukumkot", "rukumeast", new GeoPoint(28.6254129, 82.601069), IsHeadquarters: true, []), // OSM node 3385204941
        new("Liwang", "rolpa", new GeoPoint(28.3034253, 82.6370932), IsHeadquarters: true, []), // OSM node 1958230582
        new("Pyuthan", "pyuthan", new GeoPoint(28.0886113, 82.8842713), IsHeadquarters: true, []), // OSM node 3111063998
        new("Tamghas", "gulmi", new GeoPoint(28.0687018, 83.248442), IsHeadquarters: true, []), // OSM node 2462935816
        new("Sandhikharka", "arghakhanchi", new GeoPoint(27.975697, 83.127113), IsHeadquarters: true, []), // OSM node 3385949681
        new("Tansen", "palpa", new GeoPoint(27.866366, 83.5469084), IsHeadquarters: true, []), // OSM node 296197839
        new("Ramgram", "nawalparasiwest", new GeoPoint(27.5328566, 83.6665482), IsHeadquarters: true, ["Parasi"]), // OSM node 3480018174
        new("Siddharthanagar", "rupandehi", new GeoPoint(27.5116352, 83.4528807), IsHeadquarters: true, ["Bhairahawa"]), // OSM node 13626439827
        new("Butwal", "rupandehi", new GeoPoint(27.7003986, 83.4657667), IsHeadquarters: false, []), // OSM node 3172617940
        new("Lumbini", "rupandehi", new GeoPoint(27.4702049, 83.2852349), IsHeadquarters: false, []), // OSM node 317017461
        new("Kapilavastu", "kapilvastu", new GeoPoint(27.5436472, 83.0525748), IsHeadquarters: true, []), // OSM node 2246485364
        new("Ghorahi", "dang", new GeoPoint(28.0393605, 82.4866861), IsHeadquarters: true, []), // OSM node 5809166582
        new("Nepalgunj", "banke", new GeoPoint(28.058903, 81.625847), IsHeadquarters: true, []), // OSM node 282864466
        new("Gulariya", "bardiya", new GeoPoint(28.2048538, 81.3455485), IsHeadquarters: true, []), // OSM node 3386151331
        new("Thakurdwara", "bardiya", new GeoPoint(28.449902, 81.2432515), IsHeadquarters: false, []), // OSM node 4989712261
        new("Dunai", "dolpa", new GeoPoint(28.9542898, 82.8942231), IsHeadquarters: true, []), // OSM node 3050641824
        new("Gamgadhi", "mugu", new GeoPoint(29.548027, 82.154404), IsHeadquarters: true, []), // OSM node 3385731493
        new("Simikot", "humla", new GeoPoint(29.9728637, 81.8201602), IsHeadquarters: true, []), // OSM node 3385201652
        new("Chandannath", "jumla", new GeoPoint(29.2749492, 82.1832699), IsHeadquarters: true, []), // OSM node 8157956930; relation 15588737 has no admin_centre
        new("Manma", "kalikot", new GeoPoint(29.1503646, 81.612609), IsHeadquarters: true, []), // OSM node 8309132733
        new("Narayan", "dailekh", new GeoPoint(28.8428782, 81.7110384), IsHeadquarters: true, []), // OSM node 3385301838
        new("Jajarkot", "jajarkot", new GeoPoint(28.698212, 82.198257), IsHeadquarters: true, []), // OSM node 3385201356
        new("Musikot", "rukumwest", new GeoPoint(28.6292298, 82.455626), IsHeadquarters: true, []), // OSM node 3385201358
        new("Salyan Khalanga", "salyan", new GeoPoint(28.376621, 82.1616243), IsHeadquarters: true, []), // OSM node 3386117460
        new("Birendranagar", "surkhet", new GeoPoint(28.595387, 81.628494), IsHeadquarters: true, []), // OSM node 3345770103
        new("Martadi", "bajura", new GeoPoint(29.458003, 81.4795125), IsHeadquarters: true, []), // OSM node 3385743668
        new("Chainpur", "bajhang", new GeoPoint(29.5508851, 81.1963195), IsHeadquarters: true, []), // OSM node 3382453093
        new("Mangalsen", "achham", new GeoPoint(29.1409232, 81.2564508), IsHeadquarters: true, []), // OSM node 990365928
        new("Silgadhi", "doti", new GeoPoint(29.267055, 80.983336), IsHeadquarters: true, []), // OSM node 8465280710
        new("Dhangadhi", "kailali", new GeoPoint(28.703304, 80.567017), IsHeadquarters: true, []), // OSM node 1492980542
        new("Bhimdatta", "kanchanpur", new GeoPoint(28.9665746, 80.1785465), IsHeadquarters: true, ["Mahendranagar"]), // OSM node 2924267068
        new("Amargadhi", "dadeldhura", new GeoPoint(29.3015377, 80.5886638), IsHeadquarters: true, []), // OSM node 2406229072
        new("Dasharathchand", "baitadi", new GeoPoint(29.5549592, 80.4233309), IsHeadquarters: true, ["Khalanga"]), // OSM node 3504784920, named Khalanga there
        new("Khalanga", "darchula", new GeoPoint(29.8435629, 80.5398695), IsHeadquarters: true, []), // OSM node 6968554852
    ];
}
