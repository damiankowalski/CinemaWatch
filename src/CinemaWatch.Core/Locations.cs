namespace CinemaWatch.Core;

/// <summary>
/// Locations supported by the app. For now the app is fixed to Cinema City Sadyba (Warsaw), IMAX.
/// The ids come from the Cinema City API, so adding more cinemas later is a data change.
/// </summary>
public static class Locations
{
    /// <summary>Cinema City tenant id for Poland.</summary>
    public const string TenantPoland = "10103";

    public static Cinema Sadyba { get; } = new("1060", "Cinema City Sadyba", "Warszawa");

    public const string ImaxTechnology = "IMAX";
}
