using System.Globalization;
using System.IO;
using System.Text.Json;

namespace WrenchDownloader;

public static class AppLocalization
{
    private static Dictionary<string, string> _english = new();
    private static Dictionary<string, string> _current = new();

    public static string CurrentLanguage { get; private set; } = "en";
    public static bool IsRightToLeft => CultureInfo.GetCultureInfo(CurrentLanguage).TextInfo.IsRightToLeft;

    public static void Initialize() => Reload();

    public static void Reload()
    {
        CurrentLanguage = Normalize(SettingsHelper.Language);
        _english = Load("en");
        _current = CurrentLanguage == "en" ? _english : Load(CurrentLanguage);

        CultureInfo culture = CultureInfo.GetCultureInfo(CurrentLanguage);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static string Get(string key)
    {
        if (_current.TryGetValue(key, out string? value)) return value;
        if (_english.TryGetValue(key, out value)) return value;
        return key;
    }

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    private static string Normalize(string? language) => language switch
    {
        "fr" => "fr",
        "ar" => "ar",
        _ => "en"
    };

    private static Dictionary<string, string> Load(string language)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Languages", $"{language}.json");
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
            }
        }
        catch { }

        return new Dictionary<string, string>();
    }
}