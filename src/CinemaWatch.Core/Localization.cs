using System.Globalization;
using System.Text.Json;

namespace CinemaWatch.Core;

public enum AppLanguage
{
    Polish,
    English,
}

/// <summary>
/// Current UI language. Texts are written inline as <c>T("polski", "English")</c> pairs, so both languages sit
/// next to each other in the code. Changing the language raises <see cref="LanguageChanged"/>; view models refresh
/// their texts in response, so the UI switches without a restart.
/// </summary>
public static class Localizer
{
    private static readonly CultureInfo PolishCulture = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-GB");

    public static AppLanguage Language { get; private set; } = AppLanguage.Polish;

    /// <summary>Culture for dates and numbers in the current language (pl-PL or en-GB).</summary>
    public static CultureInfo Culture => Language == AppLanguage.Polish ? PolishCulture : EnglishCulture;

    /// <summary>"pl" or "en" (stored in the settings file).</summary>
    public static string Code => Language == AppLanguage.Polish ? "pl" : "en";

    public static event EventHandler? LanguageChanged;

    public static void SetLanguage(AppLanguage language)
    {
        if (Language == language) return;
        Language = language;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string T(string polish, string english) => Language == AppLanguage.Polish ? polish : english;

    /// <summary>Polish for a Polish Windows display language, English for everything else.</summary>
    public static AppLanguage Detect(CultureInfo uiCulture) =>
        string.Equals(uiCulture.TwoLetterISOLanguageName, "pl", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.Polish
            : AppLanguage.English;

    public static AppLanguage? Parse(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "pl" => AppLanguage.Polish,
        "en" => AppLanguage.English,
        _ => null,
    };

    /// <summary>"1 seans / 2 seanse / 5 seansów" or "1 screening / 2 screenings".</summary>
    public static string Count(int count, string plOne, string plFew, string plMany, string enOne, string enMany) =>
        $"{count} {Plural(count, plOne, plFew, plMany, enOne, enMany)}";

    public static string Plural(int count, string plOne, string plFew, string plMany, string enOne, string enMany)
    {
        if (Language == AppLanguage.English)
            return count == 1 ? enOne : enMany;
        if (count == 1) return plOne;
        var lastDigit = count % 10;
        var lastTwo = count % 100;
        return lastDigit is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? plFew : plMany;
    }
}

/// <summary>User settings shared by the window and the background mode.</summary>
public sealed class AppSettings
{
    /// <summary>"pl", "en" or null (= follow the Windows display language).</summary>
    public string? Language { get; set; }

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; never fail because of them.
        }
    }
}

/// <summary>Application version shown in the title bar and sent in the User-Agent.</summary>
public static class AppInfo
{
    /// <summary>"1.0.0" - set at build time (Directory.Build.props or <c>publish.ps1 -Version</c>).</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
}
