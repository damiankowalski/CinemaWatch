using System.Runtime.InteropServices;
using CinemaWatch.Core;

namespace CinemaWatch.Server.Polling;

/// <summary>Version and User-Agent of the server's requests (the same honest pattern as the R1 probe).</summary>
public static class PollerInfo
{
    public const string ProjectUrl = "https://github.com/damiankowalski/CinemaWatch";

    public static string Version { get; } = AppInfo.VersionOf(typeof(PollerInfo).Assembly);

    /// <summary>"Mozilla/5.0 (X11; Linux x86_64) CinemaWatch-Server/0.3.0 (+https://github.com/…)"</summary>
    public static string UserAgent => $"Mozilla/5.0 ({PlatformToken()}) CinemaWatch-Server/{Version} (+{ProjectUrl})";

    private static string PlatformToken()
    {
        if (OperatingSystem.IsWindows())
            return "Windows NT 10.0; Win64; x64";
        if (OperatingSystem.IsLinux())
            return RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "X11; Linux x86_64",
                Architecture.Arm64 => "X11; Linux aarch64",
                var other => $"X11; Linux {other.ToString().ToLowerInvariant()}",
            };
        return OperatingSystem.IsMacOS() ? "Macintosh" : "Unknown";
    }
}
