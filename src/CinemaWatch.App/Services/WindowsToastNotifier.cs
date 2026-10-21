using System.IO;
using System.Reflection;
using CinemaWatch.Core;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace CinemaWatch.Services;

/// <summary>
/// Windows toast notifications for an unpackaged app: the app id is registered under HKCU (name and icon shown
/// in the notification), no Start menu shortcut or COM activator is needed. Clicking the toast or its button
/// opens the booking page through protocol activation, so it works even after the process has exited.
/// </summary>
internal sealed class WindowsToastNotifier : INotifier
{
    public const string AppUserModelId = "CinemaWatch.Desktop";

    public void Show(AvailabilityNotification notification)
    {
        RegisterAppUserModelId();
        var xml = new global::Windows.Data.Xml.Dom.XmlDocument();
        xml.LoadXml(ToastXml.Build(notification));
        ToastNotificationManager.CreateToastNotifier(AppUserModelId).Show(new ToastNotification(xml));
    }

    private static void RegisterAppUserModelId()
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}");
        key.SetValue("DisplayName", "CinemaWatch");
        var icon = ExtractIcon();
        if (icon is not null)
            key.SetValue("IconUri", icon);
    }

    /// <summary>Notifications need the icon as a file; it is copied out of the exe once.</summary>
    private static string? ExtractIcon()
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDirectory, "CinemaWatch.png");
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("CinemaWatch.png");
            if (resource is null) return null;
            if (!File.Exists(path) || new FileInfo(path).Length != resource.Length)
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                using var file = File.Create(path);
                resource.CopyTo(file);
            }
            return path;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
