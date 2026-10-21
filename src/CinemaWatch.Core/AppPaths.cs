using System.Security.Cryptography;
using System.Text;

namespace CinemaWatch.Core;

/// <summary>Where the app keeps its data. Nothing here is part of the repository.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CinemaWatch");

    public static string LogFile => Path.Combine(DataDirectory, "Logs", "cinemawatch.log");

    /// <summary>Static data that rarely changes (hall layouts).</summary>
    public static string CacheDirectory => Path.Combine(DataDirectory, "Cache");

    /// <summary>Profiles live next to the exe (portable) when that folder is writable, otherwise in LocalAppData.</summary>
    public static string ProfilesDirectory(string? exePath)
    {
        var exeDirectory = string.IsNullOrEmpty(exePath) ? null : Path.GetDirectoryName(exePath);
        if (exeDirectory is not null)
        {
            var portable = Path.Combine(exeDirectory, "Profiles");
            if (IsWritable(exeDirectory, portable))
                return portable;
        }
        var local = Path.Combine(DataDirectory, "Profiles");
        Directory.CreateDirectory(local);
        return local;
    }

    /// <summary>One state file per profile path (dedupe of notifications).</summary>
    public static string StateFileFor(string profilePath)
    {
        var full = Path.GetFullPath(profilePath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16];
        return Path.Combine(DataDirectory, "State", $"{hash}.json");
    }

    private static bool IsWritable(string directory, string target)
    {
        try
        {
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}

/// <summary>Plain text log with simple size-based rotation.</summary>
public sealed class FileLog(string path, long maxBytes = 1_000_000)
{
    public string Path { get; } = path;

    public void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path) && new FileInfo(Path).Length > maxBytes)
                File.Move(Path, Path + ".old", overwrite: true);
            File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never break a check.
        }
    }
}
