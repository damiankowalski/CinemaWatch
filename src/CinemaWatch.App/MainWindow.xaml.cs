using System.IO;
using System.Text.Json;
using System.Windows;
using CinemaWatch.Core;
using CinemaWatch.ViewModels;

namespace CinemaWatch;

public partial class MainWindow : Window
{
    private static readonly string PlacementPath = Path.Combine(AppPaths.DataDirectory, "window.json");

    public MainWindow()
    {
        InitializeComponent();
        RestorePlacement();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is MainViewModel { HasUnsavedChanges: true } &&
            MessageBox.Show(this, UiText.Instance.UnsavedOnExit, UiText.Instance.UnsavedOnExitTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        SavePlacement();
    }

    /// <summary>Reopens the window with the size, position and maximized state it had when closed.</summary>
    private void RestorePlacement()
    {
        try
        {
            if (!File.Exists(PlacementPath)) return;
            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(PlacementPath));
            if (placement is null || placement.Width < MinWidth || placement.Height < MinHeight) return;

            // Only restore when the title bar would be visible (monitor setup may have changed).
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var titleBar = new Rect(placement.Left + 50, placement.Top, Math.Max(1, placement.Width - 100), 30);
            if (!screen.IntersectsWith(titleBar)) return;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = placement.Left;
            Top = placement.Top;
            Width = placement.Width;
            Height = placement.Height;
            if (placement.Maximized)
                WindowState = WindowState.Maximized;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file must never prevent the app from starting.
        }
    }

    private void SavePlacement()
    {
        try
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (bounds.IsEmpty) return;
            var placement = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                WindowState == WindowState.Maximized);
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(PlacementPath, JsonSerializer.Serialize(placement));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);
}
