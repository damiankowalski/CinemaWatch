using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CinemaWatch.Core;

public sealed record SchedulerResult(bool Success, string Message);

/// <summary>Registers periodic checks in Windows Task Scheduler.</summary>
public interface ITaskScheduler
{
    Task<SchedulerResult> RegisterAsync(string taskName, string exePath, string profilePath, int intervalMinutes);
    Task<bool> ExistsAsync(string taskName);
    Task<SchedulerResult> DeleteAsync(string taskName);
}

/// <summary>
/// Uses schtasks.exe. Tasks are created for the current user without a stored password, i.e. "run only when
/// the user is logged on" - which is also what Windows notifications need.
/// </summary>
public sealed class SchtasksScheduler : ITaskScheduler
{
    public const string TaskFolder = "CinemaWatch";
    private const int MaxTaskCommandLength = 261; // schtasks /TR limit

    public static string TaskNameFor(string profilePath) => $@"\{TaskFolder}\{ProfileStore.NameFromPath(profilePath)}";

    public static string BuildTaskCommand(string exePath, string profilePath) => $"\"{exePath}\" --profile \"{profilePath}\"";

    public static IReadOnlyList<string> BuildCreateArguments(string taskName, string exePath, string profilePath, int intervalMinutes) =>
    [
        "/Create", "/F",
        "/TN", taskName,
        "/SC", "MINUTE",
        "/MO", intervalMinutes.ToString(CultureInfo.InvariantCulture),
        "/TR", BuildTaskCommand(exePath, profilePath),
    ];

    public async Task<SchedulerResult> RegisterAsync(string taskName, string exePath, string profilePath, int intervalMinutes)
    {
        if (intervalMinutes is < 1 or > 1439)
            return new SchedulerResult(false, "Interwał musi wynosić od 1 do 1439 minut.");
        var command = BuildTaskCommand(exePath, profilePath);
        if (command.Length > MaxTaskCommandLength)
            return new SchedulerResult(false, $"Ścieżki są za długie dla Harmonogramu zadań ({command.Length} > {MaxTaskCommandLength} znaków). Przenieś aplikację lub profil do krótszej ścieżki.");

        var (exitCode, output) = await RunAsync(BuildCreateArguments(taskName, exePath, profilePath, intervalMinutes));
        return exitCode == 0
            ? new SchedulerResult(true, $"Dodano zadanie {taskName} (co {intervalMinutes} min).")
            : new SchedulerResult(false, $"schtasks zakończył się kodem {exitCode}: {output}");
    }

    public async Task<bool> ExistsAsync(string taskName) => (await RunAsync(["/Query", "/TN", taskName])).ExitCode == 0;

    public async Task<SchedulerResult> DeleteAsync(string taskName)
    {
        var (exitCode, output) = await RunAsync(["/Delete", "/F", "/TN", taskName]);
        return exitCode == 0
            ? new SchedulerResult(true, $"Usunięto zadanie {taskName}.")
            : new SchedulerResult(false, $"schtasks zakończył się kodem {exitCode}: {output}");
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(IEnumerable<string> arguments)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = oem,
            StandardErrorEncoding = oem,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Nie udało się uruchomić schtasks.exe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = string.Join(" ", new[] { await stderr, await stdout }.Where(t => !string.IsNullOrWhiteSpace(t))).Trim();
        return (process.ExitCode, output);
    }
}
