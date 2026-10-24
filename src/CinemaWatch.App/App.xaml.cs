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
/// The UI language is the one saved in settings, otherwise Polish for a Polish Windows display language and
/// English for any other.
/// </summary>
public partial class App : Application
{
    private readonly AppSettings _settings;
    private CinemaCityClient? _client;
    private MainViewModel? _viewModel;
    private bool _headless;

    public App()
    {
        _settings = AppSettings.Load();
        Localizer.SetLanguage(Localizer.Parse(_settings.Language) ?? Localizer.Detect(CultureInfo.CurrentUICulture));
        ApplyCulture();

        // Must run before any window is created (also covers popups such as the DatePicker calendar).
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Localizer.Culture.IetfLanguageTag)));

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

        // Translated UI texts, bound in XAML as {Binding Key, Source={StaticResource T}}.
        Resources["T"] = UiText.Instance;

        _client = new CinemaCityClient();
        _viewModel = new MainViewModel(
            _client,
            new WpfUserInteraction(),
            new SchtasksScheduler(),
            new AppEnvironment(Environment.ProcessPath, AppPaths.ProfilesDirectory(Environment.ProcessPath), AppPaths.CacheDirectory),
            languageChanged: OnLanguageChanged,
            preferences: new SettingsPreferences(_settings));
        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        window.Show();

        // Restore tabs from the profiles folder (each tab loads when it is first shown).
        await _viewModel.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Dispose();
        _client?.Dispose();
        base.OnExit(e);
    }

    /// <summary>The user picked a language in the window: remember it and switch WPF formatting.</summary>
    private void OnLanguageChanged()
    {
        ApplyCulture();
        if (MainWindow is not null)
            MainWindow.Language = XmlLanguage.GetLanguage(Localizer.Culture.IetfLanguageTag);
        _settings.Language = Localizer.Code;
        _settings.Save();
    }

    private static void ApplyCulture()
    {
        var culture = Localizer.Culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
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
            log.Write($"ERROR: {ex}");
            return 3;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_headless)
        {
            new FileLog(AppPaths.LogFile).Write($"ERROR: {e.Exception}");
            Shutdown(3);
            return;
        }
        MessageBox.Show(e.Exception.Message, UiText.Instance.UnexpectedError, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
