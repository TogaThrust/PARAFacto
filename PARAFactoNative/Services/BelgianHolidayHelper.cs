using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace PARAFactoNative.Services;

/// <summary>
/// Jours fériés légaux fédéraux en Belgique : catalogue JSON (embarqué + fichier optionnel à côté de l’exe),
/// avec repli sur le calcul grégorien (Pâques, etc.) pour toute date absente du catalogue (années au-delà du fichier, etc.).
/// </summary>
public static class BelgianHolidayHelper
{
    private static readonly object InitLock = new();
    private static Dictionary<DateTime, string>? _catalog;
    private static bool _initialized;

    /// <summary>À appeler au démarrage de l’application (idempotent).</summary>
    public static void Initialize()
    {
        lock (InitLock)
        {
            if (_initialized) return;
            _initialized = true;
            _catalog = new Dictionary<DateTime, string>();
            try
            {
                LoadEmbeddedJson(_catalog);
                LoadExternalJsonIfPresent(_catalog);
            }
            catch
            {
                // Catalogue optionnel : en cas d’erreur, le repli algorithmique suffit.
            }
        }
    }

    private static void LoadEmbeddedJson(Dictionary<DateTime, string> target)
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("belgian_holidays.json", StringComparison.OrdinalIgnoreCase));
        if (res is null) return;
        using var stream = asm.GetManifestResourceStream(res);
        if (stream is null) return;
        MergeJsonStream(stream, target);
    }

    private static void LoadExternalJsonIfPresent(Dictionary<DateTime, string> target)
    {
        foreach (var rel in new[] { "belgian_holidays.json", Path.Combine("Data", "belgian_holidays.json") })
        {
            var path = Path.Combine(AppContext.BaseDirectory, rel);
            if (!File.Exists(path)) continue;
            using var stream = File.OpenRead(path);
            MergeJsonStream(stream, target);
            return;
        }
    }

    private static void MergeJsonStream(Stream stream, Dictionary<DateTime, string> target)
    {
        var entries = JsonSerializer.Deserialize<List<HolidayJsonEntry>>(stream, JsonReadOptions);
        if (entries is null) return;
        foreach (var e in entries)
        {
            if (string.IsNullOrWhiteSpace(e.Date) || string.IsNullOrWhiteSpace(e.Name)) continue;
            if (!DateTime.TryParseExact(e.Date.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var d)) continue;
            target[d.Date] = e.Name.Trim();
        }
    }

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class HolidayJsonEntry
    {
        public string Date { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public static bool IsPublicHoliday(DateTime date) => TryGetName(date.Date, out _);

    public static bool TryGetName(DateTime date, out string name)
    {
        Initialize();
        var d = date.Date;
        name = "";

        if (_catalog is not null && _catalog.TryGetValue(d, out var n) && !string.IsNullOrWhiteSpace(n))
        {
            name = n;
            return true;
        }

        return TryGetNameComputed(d, out name);
    }

    private static bool TryGetNameComputed(DateTime d, out string name)
    {
        name = "";
        var y = d.Year;

        if (d.Month == 1 && d.Day == 1) { name = "Nouvel An"; return true; }
        if (d.Month == 5 && d.Day == 1) { name = "Fête du Travail"; return true; }
        if (d.Month == 7 && d.Day == 21) { name = "Fête nationale"; return true; }
        if (d.Month == 8 && d.Day == 15) { name = "Assomption"; return true; }
        if (d.Month == 11 && d.Day == 1) { name = "Toussaint"; return true; }
        if (d.Month == 11 && d.Day == 11) { name = "Armistice 1918"; return true; }
        if (d.Month == 12 && d.Day == 25) { name = "Noël"; return true; }

        var easter = EasterSundayGregorian(y);
        if (d == easter.AddDays(1)) { name = "Lundi de Pâques"; return true; }
        if (d == easter.AddDays(39)) { name = "Ascension"; return true; }
        if (d == easter.AddDays(50)) { name = "Lundi de Pentecôte"; return true; }

        return false;
    }

    /// <summary>Dimanche de Pâques (calendrier grégorien, algorithme d’Anonymous).</summary>
    private static DateTime EasterSundayGregorian(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(year, month, day);
    }
}
