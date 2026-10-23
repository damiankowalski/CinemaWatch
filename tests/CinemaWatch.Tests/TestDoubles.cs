using System.Net;
using System.Text;
using CinemaWatch.Core;
using CinemaWatch.ViewModels;

namespace CinemaWatch.Tests;

/// <summary>Serves the same captured responses for every screening id (several maps of the same hall).</summary>
internal sealed class AnyScreeningHandler : HttpMessageHandler
{
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        var path = request.RequestUri!.AbsolutePath;
        var fixture = path switch
        {
            _ when path.Contains("/film-events/") && path.EndsWith("2026-12-16") => "repertoire-1060-2026-12-16.json",
            _ when path.Contains("/presentations/") => "presentation-1765592.json",
            _ when path.EndsWith("/seatplanV2") => "seatplan-13-1.json",
            _ when path.EndsWith("/seats-statusV2") => "seats-status-1765592.json",
            _ => null,
        };
        if (fixture is null)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}

internal sealed class FakeUi : IUserInteraction
{
    public List<string> OpenPaths { get; } = [];
    public string? SavePath { get; set; }
    public bool ConfirmAnswer { get; set; } = true;
    public List<string> Confirms { get; } = [];
    public string? Copied { get; private set; }
    public string? Opened { get; private set; }
    public string? LastSaveSuggestion { get; private set; }

    public IReadOnlyList<string> PickProfilesToOpen(string initialDirectory) => OpenPaths.ToList();

    public string? PickProfileToSave(string initialDirectory, string fileName)
    {
        LastSaveSuggestion = fileName;
        return SavePath;
    }

    public bool Confirm(string title, string message)
    {
        Confirms.Add(title + "\n" + message);
        return ConfirmAnswer;
    }

    public void CopyText(string text) => Copied = text;
    public void OpenUrl(string url) => Opened = url;
}

internal sealed class FakeScheduler : ITaskScheduler
{
    public Dictionary<string, int> Tasks { get; } = [];

    public Task<SchedulerResult> RegisterAsync(string taskName, string exePath, string profilePath, int intervalMinutes)
    {
        Tasks[taskName] = intervalMinutes;
        return Task.FromResult(new SchedulerResult(true, $"Dodano zadanie {taskName}."));
    }

    public Task<bool> ExistsAsync(string taskName) => Task.FromResult(Tasks.ContainsKey(taskName));

    public Task<SchedulerResult> DeleteAsync(string taskName)
    {
        Tasks.Remove(taskName);
        return Task.FromResult(new SchedulerResult(true, $"Usunięto zadanie {taskName}."));
    }
}
