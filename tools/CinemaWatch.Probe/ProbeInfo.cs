using System.Runtime.InteropServices;
using CinemaWatch.Core;

namespace CinemaWatch.Probe;

/// <summary>Version and User-Agent of the probe.</summary>
public static class ProbeInfo
{
    public const string ProjectUrl = "https://github.com/damiankowalski/CinemaWatch";

    public static string Version { get; } = AppInfo.VersionOf(typeof(ProbeInfo).Assembly);

    /// <summary>Honest User-Agent: the real platform, the probe's name and version, and where to find the project.</summary>
    public static string UserAgent => BuildUserAgent(PlatformToken(), Version);

    public static string BuildUserAgent(string platform, string version) =>
        $"Mozilla/5.0 ({platform}) CinemaWatch-Probe/{version} (+{ProjectUrl})";

    public static string PlatformToken()
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
        if (OperatingSystem.IsMacOS())
            return "Macintosh";
        return "Unknown";
    }
}
