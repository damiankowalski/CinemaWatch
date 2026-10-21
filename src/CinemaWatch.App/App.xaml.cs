using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using CinemaWatch.Core;
using CinemaWatch.Services;
using CinemaWatch.ViewModels;

namespace CinemaWatch;

/// <summary>
/// Two modes:
///  - no arguments: the wizard window;
///  - <c>--profile "path"</c>: no window, one check, Windows notification about newly free seats, exit
///    (this is what Task Scheduler runs). Exit codes: 0 ok, 1 profile error, 2 all screenings failed, 3 crash.
/// </summary>
public partial class App : Application
{
    private CinemaCityClient? _client;
    private bool _headless;

    public App()
    {
        // Polish UI (DatePicker format, day names). Must run before any window is created.
        var polish = Formatting.Polish;
        CultureInfo.DefaultThreadCurrentCulture = polish;
        CultureInfo.DefaultThreadCurrentUICulture = polish;
        CultureInfo.CurrentCulture = polish;
        CultureInfo.CurrentUICulture = polish;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(polish.IetfLanguageTag)));

        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var profilePath = HeadlessRunner.GetProfileArgument(e.Args);
        if (profilePath is not null)
        {
            _headless = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await RunHeadlessAsync(profilePath));
            return;
        }

        _client = new CinemaCityClient();
        var viewModel = new MainViewModel(
            _client,
            new WpfUserInteraction(),
            new SchtasksScheduler(),
            new AppEnvironment(Environment.ProcessPath, AppPaths.ProfilesDirectory(Environment.ProcessPath), AppPaths.CacheDirectory));
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _client?.Dispose();
        base.OnExit(e);
    }

    private static async Task<int> RunHeadlessAsync(string profilePath)
    {
        var log = new FileLog(AppPaths.LogFile);
        try
        {
            using var client = new CinemaCityClient();
            var runner = new HeadlessRunner(
                new SeatAvailabilityService(client, AppPaths.CacheDirectory), new WindowsToastNotifier(), log,
                scheduler: new SchtasksScheduler());
            var exitCode = await runner.RunAsync(profilePath);
            await Task.Delay(500); // give Windows a moment to take over the toast before the process exits
            return exitCode;
        }
        catch (Exception ex)
        {
            log.Write($"BŁĄD: {ex}");
            return 3;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_headless)
        {
            new FileLog(AppPaths.LogFile).Write($"BŁĄD: {e.Exception}");
            Shutdown(3);
            return;
        }
        MessageBox.Show(e.Exception.Message, "CinemaWatch - nieoczekiwany błąd", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
