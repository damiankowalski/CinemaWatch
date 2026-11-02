using System.Globalization;

namespace CinemaWatch.Probe;

/// <summary>Command line of the probe.</summary>
public sealed record ProbeOptions(string ProfilesDirectory, string DataDirectory, TimeSpan RepertoireInterval)
{
    public static readonly TimeSpan DefaultRepertoireInterval = TimeSpan.FromMinutes(60);

    public const string Usage = """
        Usage: CinemaWatch.Probe --profiles <folder> --data <folder> [--repertoire-every <minutes>]

          --profiles          Folder with *.cinemawatch.json profiles (read only).
          --data              Folder for the request log, notification state and caches.
          --repertoire-every  Minutes between repertoire requests per watched cinema; 0 = never. Default: 60.

        One run checks every profile once and exits; a scheduler (systemd timer) repeats it.
        Exit codes: 0 checked, 1 configuration error, 2 every screening failed, 3 blocked (HTTP 403 / 429 or a
        Cloudflare challenge - the run stopped), 4 nothing to check (all screenings are over).
        """;

    public static bool IsHelp(IReadOnlyList<string> args) =>
        args.Any(a => a is "--help" or "-h" or "/?");

    /// <summary>Accepts "--name value" and "--name=value".</summary>
    public static ProbeOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        string? profiles = null, data = null, repertoire = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string name, value;
            var equals = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                name = arg[..equals];
                value = arg[(equals + 1)..];
            }
            else if (arg.StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Count)
            {
                name = arg;
                value = args[++i];
            }
            else
            {
                error = $"Unexpected argument: {arg}";
                return null;
            }

            switch (name)
            {
                case "--profiles": profiles = value.Trim('"'); break;
                case "--data": data = value.Trim('"'); break;
                case "--repertoire-every": repertoire = value; break;
                default:
                    error = $"Unknown option: {name}";
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(profiles) || string.IsNullOrWhiteSpace(data))
        {
            error = "Both --profiles and --data are required.";
            return null;
        }

        var interval = DefaultRepertoireInterval;
        if (repertoire is not null)
        {
            if (!int.TryParse(repertoire, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            {
                error = $"--repertoire-every expects whole minutes, got: {repertoire}";
                return null;
            }
            interval = TimeSpan.FromMinutes(minutes);
        }

        error = null;
        return new ProbeOptions(profiles, data, interval);
    }
}
