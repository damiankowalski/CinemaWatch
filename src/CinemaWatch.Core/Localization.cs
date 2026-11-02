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
/// <para>
/// The desktop app has one language for the whole process (<see cref="SetLanguage"/>). A server handles many people
/// at once, so it sets the language per request or per e-mail with <see cref="Use"/>; that value belongs to the
/// current asynchronous flow only and wins over the process-wide one.
/// </para>
/// </summary>
public static class Localizer
{
    private static readonly CultureInfo PolishCulture = CultureInfo.GetCultureInfo("pl-PL");
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-GB");

    private static readonly AsyncLocal<AppLanguage?> ScopedLanguage = new();
    private static AppLanguage _processLanguage = AppLanguage.Polish;

    /// <summary>The language of the current flow (see <see cref="Use"/>), otherwise the process-wide language.</summary>
    public static AppLanguage Language => ScopedLanguage.Value ?? _processLanguage;

    /// <summary>Culture for dates and numbers in the current language (pl-PL or en-GB).</summary>
    public static CultureInfo Culture => Language == AppLanguage.Polish ? PolishCulture : EnglishCulture;

    /// <summary>"pl" or "en" (stored in the settings file).</summary>
    public static string Code => Language == AppLanguage.Polish ? "pl" : "en";

    public static event EventHandler? LanguageChanged;

    /// <summary>Sets the process-wide language (desktop app).</summary>
    public static void SetLanguage(AppLanguage language)
    {
        if (_processLanguage == language) return;
        _processLanguage = language;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Sets the language for the current asynchronous flow only - one web request or one e-mail - until the
    /// returned scope is disposed. Code awaited inside the scope sees this language; other requests do not.
    /// Raises no <see cref="LanguageChanged"/> event.
    /// </summary>
    public static IDisposable Use(AppLanguage language)
    {
        var previous = ScopedLanguage.Value;
        ScopedLanguage.Value = language;
        return new LanguageScope(previous);
    }

    private sealed class LanguageScope(AppLanguage? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ScopedLanguage.Value = previous;
        }
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

    /// <summary>Last city choice in step 1: a cinema id or "all" (null = Warsaw).</summary>
    public string? LastCinema { get; set; }

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
    /// <summary>
    /// "1.3.0" or a pre-release such as "1.4.0-beta.1" - set at build time (Directory.Build.props or
    /// <c>publish.ps1 -Version</c>). Build metadata after "+" (commit hash) is dropped.
    /// </summary>
    public static string Version { get; } = VersionOf(typeof(AppInfo).Assembly);

    /// <summary>Version of any assembly in the same format (tools such as the SaaS probe have their own version).</summary>
    public static string VersionOf(System.Reflection.Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0];
        return assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
    }
}
