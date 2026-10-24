namespace CinemaWatch.ViewModels;

/// <summary>Dialogs and shell actions, implemented by the WPF app (and by fakes in tests).</summary>
public interface IUserInteraction
{
    /// <summary>Returns the chosen files (several can be selected); empty when cancelled.</summary>
    IReadOnlyList<string> PickProfilesToOpen(string initialDirectory);
    string? PickProfileToSave(string initialDirectory, string fileName);
    bool Confirm(string title, string message);
    void CopyText(string text);
    void OpenUrl(string url);
}

/// <summary>Small remembered choices (stored in the app settings file).</summary>
public interface IPreferences
{
    /// <summary>Last city choice in step 1: a cinema id or "all".</summary>
    string? LastCinemaChoice { get; set; }
}

/// <summary>Preferences kept in memory only (tests, or when nothing should be saved).</summary>
public sealed class InMemoryPreferences : IPreferences
{
    public string? LastCinemaChoice { get; set; }
}

/// <summary>Facts about the running app needed by the view models.</summary>
/// <param name="ExePath">Full path of CinemaWatch.exe (used in scheduled tasks and the copied command line).</param>
/// <param name="ProfilesDirectory">Default folder for profile dialogs.</param>
/// <param name="CacheDirectory">Disk cache for hall layouts (null = memory only).</param>
public sealed record AppEnvironment(string? ExePath, string ProfilesDirectory, string? CacheDirectory = null)
{
    /// <summary>True when started from a build folder (dotnet run) instead of a published exe.</summary>
    public bool IsBuildOutput =>
        ExePath is not null &&
        (ExePath.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
         ExePath.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase));
}
