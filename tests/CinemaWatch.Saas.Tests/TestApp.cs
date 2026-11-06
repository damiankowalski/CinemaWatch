using System.Net;
using System.Text.RegularExpressions;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Mail;
using CinemaWatch.Server.Operations;
using CinemaWatch.Server.Polling;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CinemaWatch.Saas.Tests;

/// <summary>
/// The real web app in memory, on its own test database, with e-mails recorded instead of sent (behind the same
/// status-recording wrapper as SMTP). The type argument only identifies the web app's assembly (its Program class is
/// internal). The poller is enabled but its background loop is removed: the tests run the cycles themselves, against the
/// development fake of Cinema City in a temporary folder, on a clock that moves only when a test moves it, and without
/// pauses between requests. The heartbeat has an address, but its pings are only recorded. Nothing reaches the real
/// Cinema City or any other site.
/// </summary>
internal sealed class TestApp : WebApplicationFactory<CinemaWatch.Web.Components.App>
{
    public const string OwnerEmail = "owner@example.test";
    public const string Password = "correct horse battery staple";

    /// <summary>The heartbeat's address in the tests: its path is the secret part.</summary>
    public const string HeartbeatUrl = "https://hc-ping.example.test/secret-check-id";

    public RecordingMailSender Mail { get; } = new();

    /// <summary>The app's clock: starts at the real time, moves only with <see cref="ManualTime.Advance"/>.</summary>
    public ManualTime Time { get; } = new(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    /// <summary>Folder that steers the fake (free-*.txt, block.txt, error500*.txt).</summary>
    public string FakeFolder { get; } = Path.Combine(Path.GetTempPath(), "cinemawatch-test-fake-" + Guid.NewGuid().ToString("N")[..8]);

    public TestNetwork Network { get; }

    public RecordingPacer Pacer { get; } = new();

    public RecordingHeartbeatClient HeartbeatClient { get; } = new();

    public TestApp(string connectionString)
    {
        Directory.CreateDirectory(FakeFolder);
        Network = new TestNetwork(FakeFolder);
        // Read by WebApplication.CreateBuilder in Program.cs (environment variables beat appsettings.json).
        Environment.SetEnvironmentVariable($"ConnectionStrings__{DatabaseSetup.ConnectionStringName}", connectionString);
        Environment.SetEnvironmentVariable("Saas__BaseUrl", "http://localhost");
        Environment.SetEnvironmentVariable("Saas__OwnerEmail", OwnerEmail);
        Environment.SetEnvironmentVariable("Saas__Poller__Source", "Fake");
        Environment.SetEnvironmentVariable("Saas__Poller__FakeDirectory", FakeFolder);
        Environment.SetEnvironmentVariable("Saas__Heartbeat__Url", HeartbeatUrl);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development": the developer's user secrets must not leak into the tests.
        builder.UseEnvironment("Testing");
        builder.UseContentRoot(WebProjectFolder());
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            services.Remove(services.Single(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService) && d.ImplementationType == typeof(PollerHostedService)));
            services.AddMailSender(_ => Mail);
            services.AddSingleton<IHeartbeatClient>(HeartbeatClient);
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<ICinemaCityNetwork>(Network);
            services.AddSingleton<IRequestPacer>(Pacer);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(FakeFolder))
            Directory.Delete(FakeFolder, recursive: true);
    }

    /// <summary>Writes (content) or deletes (null) a file in the fake's folder.</summary>
    public void Fake(string fileName, string? content)
    {
        var path = Path.Combine(FakeFolder, fileName);
        if (content is null)
            File.Delete(path);
        else
            File.WriteAllText(path, content);
    }

    /// <summary>src/CinemaWatch.Web, found from the test's output folder (wwwroot and appsettings.json live there).</summary>
    private static string WebProjectFolder()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "CinemaWatch.slnx")))
                return Path.Combine(folder.FullName, "src", "CinemaWatch.Web");
        }
        throw new DirectoryNotFoundException("CinemaWatch.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A browser-like client: keeps cookies, does not follow redirects (the tests check them).</summary>
    public HttpClient CreateBrowser() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>"/Account/SetPassword?user=...&amp;code=..." from an invitation or reset e-mail.</summary>
    public static string LinkPath(OutgoingMail mail) =>
        Regex.Match(mail.Text, @"http://localhost(/Account/SetPassword\?\S+)").Groups[1].Value;

    public static string QueryValue(string pathAndQuery, string name) =>
        Uri.UnescapeDataString(Regex.Match(pathAndQuery, $@"[?&]{name}=([^&]+)").Groups[1].Value);
}

/// <summary>A clock for the tests: fixed until a test moves it.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Pauses between screenings are only recorded (manual or not), never waited for.</summary>
internal sealed class RecordingPacer : IRequestPacer
{
    private readonly List<bool> _pauses = [];

    public int Pauses
    {
        get { lock (_pauses) return _pauses.Count; }
    }

    /// <summary>Per pause: true when it came before a "Check now" screening.</summary>
    public IReadOnlyList<bool> Manual
    {
        get { lock (_pauses) return _pauses.ToList(); }
    }

    public Task PauseAsync(bool manual, CancellationToken cancellationToken)
    {
        lock (_pauses)
            _pauses.Add(manual);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The development fake (<see cref="FakeCinemaCityHandler"/>) with every request recorded; <see cref="Override"/> can
/// answer a request instead of the fake (e.g. HTML instead of JSON).
/// </summary>
internal sealed class TestNetwork(string folder) : ICinemaCityNetwork
{
    private readonly List<(HttpMethod Method, Uri Uri, string? UserAgent, string? Uuid)> _requests = [];

    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    public IReadOnlyList<(HttpMethod Method, Uri Uri, string? UserAgent, string? Uuid)> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public HttpMessageHandler CreateHandler() => new Recording(this, new FakeCinemaCityHandler(folder));

    private sealed class Recording(TestNetwork owner, HttpMessageHandler fake) : DelegatingHandler(fake)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (owner._requests)
                owner._requests.Add((request.Method, request.RequestUri!, request.Headers.UserAgent.ToString(),
                    request.Headers.TryGetValues("uuid", out var uuid) ? uuid.FirstOrDefault() : null));
            return owner.Override?.Invoke(request) is { } answer ? Task.FromResult(answer) : base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>Records the heartbeat's pings; answers <see cref="Status"/>, or throws <see cref="Throw"/> when set.</summary>
internal sealed class RecordingHeartbeatClient : IHeartbeatClient
{
    private readonly List<Uri> _pings = [];

    public int Status { get; set; } = 200;

    public Exception? Throw { get; set; }

    public IReadOnlyList<Uri> Pings
    {
        get { lock (_pings) return _pings.ToList(); }
    }

    public Task<int> PingAsync(Uri target, CancellationToken cancellationToken)
    {
        lock (_pings)
            _pings.Add(target);
        return Throw is { } exception ? Task.FromException<int>(exception) : Task.FromResult(Status);
    }
}

/// <summary>Records e-mails; <see cref="FailNext"/> simulates an unreachable mail server once.</summary>
internal sealed class RecordingMailSender : IMailSender
{
    private readonly List<OutgoingMail> _sent = [];

    public bool FailNext { get; set; }

    public IReadOnlyList<OutgoingMail> Sent
    {
        get { lock (_sent) return _sent.ToList(); }
    }

    public Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken = default)
    {
        lock (_sent)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("The mail server is not reachable (test).");
            }
            _sent.Add(mail);
        }
        return Task.CompletedTask;
    }
}

/// <summary>A fresh database per test run (name cinemawatch_test_xxxxxxxx), migrated, dropped at the end.</summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _name;

    private TestDatabase(string adminConnectionString, string name, string connectionString)
    {
        _adminConnectionString = adminConnectionString;
        _name = name;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static async Task<TestDatabase> CreateAsync(string adminConnectionString, bool migrate = true)
    {
        var name = "cinemawatch_test_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync(adminConnectionString, $"CREATE DATABASE \"{name}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name }.ConnectionString;

        if (migrate)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>();
            options.UseCinemaWatchDatabase(connectionString);
            await using var db = new AppDbContext(options.Options);
            await db.Database.MigrateAsync();
        }
        return new TestDatabase(adminConnectionString, name, connectionString);
    }

    /// <summary>Another database on the same server, without any migration (like a fresh container's).</summary>
    public Task<TestDatabase> CreateEmptySiblingAsync() => CreateAsync(_adminConnectionString, migrate: false);

    public async Task<IReadOnlyList<string>> TablesAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("select table_name from information_schema.tables where table_schema = 'public' order by table_name", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));
        return tables;
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await ExecuteAsync(_adminConnectionString, $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Form posts the way a browser sends them: the page's antiforgery token, the form's name, the fields.</summary>
internal static class Browser
{
    public static async Task<HttpResponseMessage> PostFormAsync(
        this HttpClient client, string path, string page, string? formName, Dictionary<string, string> fields)
    {
        var values = new Dictionary<string, string>(fields)
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(page),
        };
        if (formName is not null)
            values["_handler"] = formName;
        return await client.PostAsync(path, new FormUrlEncodedContent(values));
    }

    /// <summary>A file upload the way a browser sends it (multipart/form-data) from a Blazor form.</summary>
    public static async Task<HttpResponseMessage> PostFilesAsync(
        this HttpClient client, string path, string page, string formName, string field, IEnumerable<(string FileName, byte[] Content)> files)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(AntiforgeryToken(page)), "__RequestVerificationToken" },
            { new StringContent(formName), "_handler" },
        };
        foreach (var (fileName, bytes) in files)
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            content.Add(file, field, fileName);
        }
        return await client.PostAsync(path, content);
    }

    /// <summary>The page as text, HTML entities decoded, so assertions can use plain Polish text.</summary>
    public static async Task<string> PageAsync(this HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"GET {path}: expected 200, got {(int)response.StatusCode} {Location(response)}");
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    /// <summary>The redirect target as a local path with query ("/Account/Login?ReturnUrl=%2F").</summary>
    public static string Location(HttpResponseMessage response) =>
        response.Headers.Location is not { } location ? ""
        : location.IsAbsoluteUri ? location.PathAndQuery
        : location.OriginalString;

    private static string AntiforgeryToken(string page)
    {
        foreach (Match input in Regex.Matches(page, "<input[^>]*>"))
        {
            if (!input.Value.Contains("name=\"__RequestVerificationToken\""))
                continue;
            var value = Regex.Match(input.Value, "value=\"([^\"]*)\"");
            if (value.Success)
                return WebUtility.HtmlDecode(value.Groups[1].Value);
        }
        throw new InvalidOperationException("The page has no antiforgery token.");
    }
}
