using CinemaWatch.Core;
using CinemaWatch.ViewModels;

namespace CinemaWatch.Services;

/// <summary>Remembered choices stored in %LOCALAPPDATA%\CinemaWatch\settings.json.</summary>
internal sealed class SettingsPreferences(AppSettings settings) : IPreferences
{
    public string? LastCinemaChoice
    {
        get => settings.LastCinema;
        set
        {
            if (settings.LastCinema == value) return;
            settings.LastCinema = value;
            settings.Save();
        }
    }
}
