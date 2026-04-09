using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;

namespace PARAFactoNative.Services;

public static class UiLanguageService
{
    public const string Fr = "fr";
    public const string En = "en";
    public const string Nl = "nl";

    private static readonly string[] Supported = [Fr, En, Nl];
    private static string _current = Fr;

    public static string Current => _current;
    public static event Action<string>? LanguageChanged;

    public static void Initialize(string? preferred)
    {
        SetLanguageInternal(Normalize(preferred), raiseEvent: false);
    }

    public static void SetLanguage(string? languageCode)
    {
        SetLanguageInternal(Normalize(languageCode), raiseEvent: true);
    }

    public static string Normalize(string? code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        return Supported.Contains(c) ? c : Fr;
    }

    private static void SetLanguageInternal(string code, bool raiseEvent)
    {
        if (_current == code && Application.Current?.Resources is not null)
        {
            ApplyCulture(code);
            return;
        }

        _current = code;
        ApplyCulture(code);
        ApplyResourceDictionary(code);
        if (raiseEvent)
            LanguageChanged?.Invoke(_current);
    }

    private static void ApplyCulture(string code)
    {
        var cultureName = code switch
        {
            En => "en-GB",
            Nl => "nl-BE",
            _ => "fr-BE"
        };
        var ci = CultureInfo.GetCultureInfo(cultureName);
        CultureInfo.CurrentCulture = ci;
        CultureInfo.CurrentUICulture = ci;
        CultureInfo.DefaultThreadCurrentCulture = ci;
        CultureInfo.DefaultThreadCurrentUICulture = ci;
        Thread.CurrentThread.CurrentCulture = ci;
        Thread.CurrentThread.CurrentUICulture = ci;
    }

    private static void ApplyResourceDictionary(string code)
    {
        var app = Application.Current;
        if (app is null) return;
        var merged = app.Resources.MergedDictionaries;
        if (merged is null) return;

        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var src = merged[i].Source?.ToString() ?? "";
            if (src.Contains("/Resources/Strings.", StringComparison.OrdinalIgnoreCase))
                merged.RemoveAt(i);
        }

        var uri = new Uri($"Resources/Strings.{code}.xaml", UriKind.Relative);
        merged.Add(new ResourceDictionary { Source = uri });
    }
}
