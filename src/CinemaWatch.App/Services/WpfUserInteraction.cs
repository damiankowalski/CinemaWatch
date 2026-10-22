using System.Diagnostics;
using System.Windows;
using CinemaWatch.Core;
using CinemaWatch.ViewModels;
using Microsoft.Win32;

namespace CinemaWatch.Services;

/// <summary>WPF implementation of dialogs and shell actions used by the view models.</summary>
internal sealed class WpfUserInteraction : IUserInteraction
{

    private static Window? Owner => Application.Current?.MainWindow;

    public string? PickProfileToOpen(string initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Title = UiText.Instance.OpenProfileDialogTitle,
            Filter = UiText.Instance.ProfileFileFilter,
            InitialDirectory = initialDirectory,
            CheckFileExists = true,
        };
        var ok = Owner is null ? dialog.ShowDialog() : dialog.ShowDialog(Owner);
        return ok == true ? dialog.FileName : null;
    }

    public string? PickProfileToSave(string initialDirectory, string fileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = UiText.Instance.SaveProfileDialogTitle,
            Filter = UiText.Instance.ProfileFileFilter,
            InitialDirectory = initialDirectory,
            FileName = fileName,
            AddExtension = false,
            OverwritePrompt = true,
        };
        var ok = Owner is null ? dialog.ShowDialog() : dialog.ShowDialog(Owner);
        if (ok != true) return null;

        var path = dialog.FileName;
        return path.EndsWith(ProfileStore.FileExtension, StringComparison.OrdinalIgnoreCase)
            ? path
            : path + ProfileStore.FileExtension;
    }

    public bool Confirm(string title, string message)
    {
        var result = Owner is null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }

    public void CopyText(string text) => Clipboard.SetText(text);

    public void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
