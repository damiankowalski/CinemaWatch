using CinemaWatch.Core;
using CinemaWatch.Probe;

// CinemaWatch.Probe - SaaS stage 2 risk test (R1). See README.md in this folder.
Localizer.SetLanguage(AppLanguage.English);

if (ProbeOptions.IsHelp(args))
{
    Console.WriteLine($"CinemaWatch.Probe {ProbeInfo.Version}");
    Console.WriteLine(ProbeOptions.Usage);
    return ProbeExitCodes.Checked;
}

var options = ProbeOptions.Parse(args, out var error);
if (options is null)
{
    Console.WriteLine($"ERROR: {error}");
    Console.WriteLine(ProbeOptions.Usage);
    return ProbeExitCodes.Configuration;
}

using var network = CinemaCityClient.CreateDefaultHandler();
var runner = new ProbeRunner(options, network, TimeProvider.System, Console.Out, ProbeInfo.UserAgent);
return await runner.RunAsync();
