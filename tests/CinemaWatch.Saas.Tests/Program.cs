using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CinemaWatch.Core;
using CinemaWatch.Saas.Tests;
using CinemaWatch.Server.Accounts;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Polling;
using CinemaWatch.Server.Profiles;
using CinemaWatch.Tests;
using CinemaWatch.Web;
using CinemaWatch.Web.Components.Account;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static CinemaWatch.Saas.Tests.Browser;

// ---------------------------------------------------------------------------------------------------------------
// Without a database
// ---------------------------------------------------------------------------------------------------------------

TestRunner.Add("Language: the switch's cookie wins, then the browser's preference; Polish without a header", () =>
{
    Assert.Equal(AppLanguage.English, WebLanguage.Resolve("en", "pl-PL,pl;q=0.9"));
    Assert.Equal(AppLanguage.Polish, WebLanguage.Resolve(null, null));
    Assert.Equal(AppLanguage.Polish, WebLanguage.Resolve(null, "pl-PL,pl;q=0.9,en-US;q=0.8"));
    Assert.Equal(AppLanguage.English, WebLanguage.Resolve(null, "en-US,en;q=0.9,pl;q=0.8"));
    Assert.Equal(AppLanguage.English, WebLanguage.Resolve(null, "pl;q=0.3,en;q=0.7"));
    Assert.Equal(AppLanguage.English, WebLanguage.Resolve(null, "de-DE"));
    Assert.Equal(AppLanguage.Polish, WebLanguage.Resolve(null, "de-DE,pl;q=0.5"));
    Assert.Equal(AppLanguage.Polish, WebLanguage.Resolve("xx", "pl"));
});

TestRunner.Add("Redirects: local paths only, never another site", () =>
{
    Assert.Equal("/admin/users", WebLanguage.SafeLocalUrl("/admin/users"));
    Assert.Equal("/Account/Login?ReturnUrl=%2F", WebLanguage.SafeLocalUrl("/Account/Login?ReturnUrl=%2F"));
    foreach (var bad in new[] { null, "", "//evil.example", "/\\evil.example", "https://evil.example", "evil.example", "/x\\y" })
        Assert.Equal("/", WebLanguage.SafeLocalUrl(bad));

    // Pages redirect with app-relative paths such as "Account/Login".
    Assert.Equal("/Account/Login", IdentityRedirectManager.Normalize("Account/Login"));
    Assert.Equal("/admin/users", IdentityRedirectManager.Normalize("/admin/users"));
    Assert.Equal("/", IdentityRedirectManager.Normalize("https://evil.example/x"));
    Assert.Equal("/", IdentityRedirectManager.Normalize("//evil.example"));
    Assert.Equal("/", IdentityRedirectManager.Normalize(null));
});

TestRunner.Add("E-mails: invitation and reset in both languages, the link in the text and the HTML", () =>
{
    const string link = "http://localhost:5180/Account/SetPassword?user=u1&code=abc_-";
    using (Localizer.Use(AppLanguage.Polish))
    {
        var mail = AccountMails.Invitation("a@example.test", link, AccountService.LinkLifetime);
        Assert.Equal("Zaproszenie do CinemaWatch", mail.Subject);
        Assert.True(mail.Text.Contains(link));
        Assert.True(mail.Text.Contains("24 godziny"));
        Assert.True(mail.Html.Contains("href=\"http://localhost:5180/Account/SetPassword?user=u1&amp;code=abc_-\""));
        Assert.True(mail.Html.Contains(">Ustaw hasło</a>"));
    }
    using (Localizer.Use(AppLanguage.English))
    {
        var mail = AccountMails.PasswordReset("a@example.test", link, AccountService.LinkLifetime);
        Assert.Equal("New password for CinemaWatch", mail.Subject);
        Assert.True(mail.Text.Contains("24 hours"));
    }
});

TestRunner.Add("Set-password link: built from Saas:BaseUrl (never from the request), token as Base64Url", () =>
{
    var link = AccountService.BuildSetPasswordLink("https://cinemawatch.example/ignored/path", "id 1", "a+b/c==");
    Assert.True(link.StartsWith("https://cinemawatch.example/Account/SetPassword?user=id%201&code="));
    Assert.True(!link.Contains('+') && !link.Contains("=="));
    var threw = false;
    try
    {
        AccountService.BuildSetPasswordLink("", "id", "token");
    }
    catch (InvalidOperationException)
    {
        threw = true;
    }
    Assert.True(threw);
});

TestRunner.Add("Identity errors: Polish and English", () =>
{
    var errors = new LocalizedIdentityErrors();
    using (Localizer.Use(AppLanguage.Polish))
        Assert.Equal("Hasło musi mieć co najmniej 12 znaków.", errors.PasswordTooShort(12).Description);
    using (Localizer.Use(AppLanguage.English))
        Assert.True(errors.InvalidToken().Description.StartsWith("The link is invalid"));
});

TestRunner.Add("Database model: the migrations match the code (no forgotten migration)", () =>
{
    var options = new DbContextOptionsBuilder<AppDbContext>();
    options.UseCinemaWatchDatabase("Host=localhost");
    using var db = new AppDbContext(options.Options);
    Assert.True(!db.Database.HasPendingModelChanges());
});

TestRunner.Add("Profile import: a desktop profile passes; the name comes from the file when the JSON has none; unknown fields ignored", () =>
{
    using var _ = Localizer.Use(AppLanguage.English);
    var now = PolishTime.Now();
    var json = ProfileStore.Serialize(SampleProfile("Diuna IMAX"));
    var check = ProfileImport.Check("Diuna IMAX.cinemawatch.json", json, now, 20);
    Assert.True(check.IsValid);
    Assert.Equal("Diuna IMAX", check.Name);
    Assert.Equal(json, ProfileStore.Serialize(check.Profile!)); // stored exactly as the desktop app saves it

    var nameless = json.Replace("\"name\": \"Diuna IMAX\",", "\"name\": null, \"futureField\": { \"a\": 1 },");
    check = ProfileImport.Check(@"C:\fakepath\Moje seanse.cinemawatch.json", nameless, now, 20);
    Assert.True(check.IsValid);
    Assert.Equal("Moje seanse", check.Name);
    Assert.Equal("Moje seanse", check.Profile!.Name);
});

TestRunner.Add("Profile import: every broken rule is reported, all at once", () =>
{
    using var _ = Localizer.Use(AppLanguage.English);
    var profile = SampleProfile("Bad\u0007name", screenings: 3);
    profile.CinemaIds = ["1060", "10/60"];
    profile.MinAdjacent = 0;
    profile.Screenings[0].Id = "../cart";
    profile.Screenings[1].SeatKeys = [];
    profile.Screenings[1].BookingLink = "https://cinema-city.pl.evil.example/order";
    profile.Screenings[2].Id = profile.Screenings[1].Id;
    profile.Screenings[2].SeatKeys = ["1_18_4", "1_18_4\n"];
    profile.Screenings[2].StartsAt = DateTime.SpecifyKind(profile.Screenings[2].StartsAt, DateTimeKind.Utc);

    var check = ProfileImport.Check("bad.cinemawatch.json", ProfileStore.Serialize(profile), PolishTime.Now(), 20);
    Assert.True(!check.IsValid && check.Profile is null);
    string[] expected =
    [
        "Profile name: control characters are not allowed.",
        "Cinema id: missing or not allowed characters",
        "adjacent seats must be at least 1",
        "Screening no. 1: missing id or not allowed characters",
        "no watched seats.",
        "the booking link must lead to https://…cinema-city.pl.",
        "invalid seat key",
        "invalid start time",
        "appears twice in the profile.",
    ];
    foreach (var text in expected)
        Assert.True(check.Errors.Any(e => e.Contains(text)), $"missing error: {text}");
    Assert.Equal(expected.Length, check.Errors.Count);
});

TestRunner.Add("Profile import: not JSON, a newer version, too many screenings, all screenings started", () =>
{
    using var _ = Localizer.Use(AppLanguage.Polish);
    var now = PolishTime.Now();
    Assert.SequenceEqual(["To nie jest plik profilu CinemaWatch (niepoprawny JSON)."], ProfileImport.Check("x.json", "<html>", now, 20).Errors);
    Assert.SequenceEqual(["Profil zapisano w nowszej wersji aplikacji (wersja 2)."],
        ProfileImport.Check("x.json", ProfileStore.Serialize(SampleProfile()).Replace("\"version\": 1", "\"version\": 2"), now, 20).Errors);
    Assert.SequenceEqual(["Za dużo seansów: 3 (najwyżej 2 w jednym profilu)."],
        ProfileImport.Check("x.json", ProfileStore.Serialize(SampleProfile(screenings: 3)), now, 2).Errors);
    var past = SampleProfile(start: now.AddMinutes(-5));
    Assert.SequenceEqual(["Wszystkie seanse tego profilu już się zaczęły."], ProfileImport.Check("x.json", ProfileStore.Serialize(past), now, 20).Errors);
    Assert.Equal("profil", ProfileImport.Refuse("\u0001.json", "x").Name);
});

TestRunner.Add("Profile import: booking links only over https on cinema-city.pl", () =>
{
    foreach (var good in new[] { "https://tickets.cinema-city.pl/api/order/1765592?lang=pl", "https://www.cinema-city.pl/", "https://cinema-city.pl" })
        Assert.True(ProfileImport.IsCinemaCityLink(good), good);
    foreach (var bad in new[] { null, "", "http://tickets.cinema-city.pl/x", "https://evilcinema-city.pl/", "https://cinema-city.pl.evil.example/",
                                "https://user@tickets.cinema-city.pl/", "https://tickets.cinema-city.pl:8443/", "javascript:alert(1)", "/api/order/1" })
        Assert.True(!ProfileImport.IsCinemaCityLink(bad), bad ?? "null");
});

TestRunner.Add("Limits: defaults are valid; the server minimum wins over a shorter profile interval", () =>
{
    var limits = new LimitOptions();
    Assert.True(limits.IsValid());
    Assert.Equal(5, limits.MaxProfiles);
    Assert.Equal(10, limits.EffectiveInterval(5));
    Assert.Equal(15, limits.EffectiveInterval(15));
    Assert.True(!new LimitOptions { MaxProfiles = LimitOptions.ProfileLimitCeiling + 1 }.IsValid());
    Assert.True(!new LimitOptions { MinIntervalMinutes = LimitOptions.MinIntervalFloor - 1 }.IsValid());
    Assert.True(new LimitOptions { MinIntervalMinutes = LimitOptions.MinIntervalFloor }.IsValid());
    Assert.Equal("Diuna Część trzecia.cinemawatch.json", ProfileImport.FileNameFor("Diuna: Część trzecia"));
});

TestRunner.Add("Poller settings: defaults are valid; the block pause doubles up to the maximum; the fake needs a folder", () =>
{
    var settings = new PollerOptions();
    Assert.True(settings.IsValid());
    Assert.Equal((CinemaCitySource.CinemaCity, 120, 3.0, 6.0, 1, false),
        (settings.Source, settings.RequestsPerHour, settings.RequestGapMinSeconds, settings.RequestGapMaxSeconds, settings.ManualFreshMinutes, settings.ReuseScreeningDetails));
    Assert.SequenceEqual([60.0, 120.0, 240.0, 480.0, 960.0, 1440.0, 1440.0], Enumerable.Range(1, 7).Select(n => settings.BlockPause(n).TotalMinutes));
    Assert.True(!new PollerOptions { Source = CinemaCitySource.Fake }.IsValid());
    Assert.True(new PollerOptions { Source = CinemaCitySource.Fake, FakeDirectory = "x" }.IsValid());
    Assert.True(!new PollerOptions { RequestGapMinSeconds = 5, RequestGapMaxSeconds = 3 }.IsValid());
    Assert.True(!new PollerOptions { RequestsPerHour = 5 }.IsValid());
    Assert.True(PollerInfo.UserAgent.StartsWith("Mozilla/5.0 (") && PollerInfo.UserAgent.Contains($") CinemaWatch-Server/{PollerInfo.Version} (+https://github.com/damiankowalski/CinemaWatch)"));
    Assert.True(new CinemaWatch.Server.Mail.MailOptions().IsLocal && !new CinemaWatch.Server.Mail.MailOptions { Host = "smtp.example.com" }.IsLocal);
});

TestRunner.Add("Block detection: 403, 429, a Cloudflare challenge and HTML instead of JSON are blocks; 500 and 404 are not", () =>
{
    Assert.True(RequestGuard.IsBlock(403, null, "application/json"));
    Assert.True(RequestGuard.IsBlock(429, null, null));
    Assert.True(RequestGuard.IsBlock(503, "challenge", "text/html"));
    Assert.True(RequestGuard.IsBlock(200, null, "text/html"));
    Assert.True(!RequestGuard.IsBlock(200, null, "application/json"));
    Assert.True(!RequestGuard.IsBlock(500, null, "text/html"));
    Assert.True(!RequestGuard.IsBlock(404, null, "application/json"));
});

TestRunner.Add("Request budget: a sixth of the hourly budget saved up at most, refilled steadily", () =>
{
    var time = new ManualTime(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    var budget = new RequestBudget(Microsoft.Extensions.Options.Options.Create(new PollerOptions { RequestsPerHour = 120 }), time);
    Assert.Equal(20, budget.Available);
    Assert.True(budget.TryTake(20));
    Assert.True(!budget.TryTake());
    time.Advance(TimeSpan.FromSeconds(30)); // 120 an hour = one every 30 seconds
    Assert.Equal(1, budget.Available);
    time.Advance(TimeSpan.FromHours(5));
    Assert.Equal(20, budget.Available);
});

TestRunner.Add("Fake Cinema City: free seats from a file, sold out without it; block.txt (403 challenge, 429 + Retry-After) and error500 files", async () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "cinemawatch-fake-" + Guid.NewGuid().ToString("N")[..8]);
    FakeCinemaCityHandler.PrepareFolder(folder);
    try
    {
        Assert.True(File.Exists(Path.Combine(folder, FakeCinemaCityHandler.ReadmeFile)));
        using var client = new CinemaCityClient(new FakeCinemaCityHandler(folder));
        var presentation = await client.GetPresentationAsync("1234567");
        Assert.Equal(("1234567", 13, 1), (presentation.Id, presentation.VenueId, presentation.SeatplanId));
        Assert.Equal(385, (await client.GetSeatPlanAsync(13, 1)).Seats.Count);
        Assert.Equal(0, (await client.GetFreeSeatsAsync("1234567", 1, 0)).Count);
        File.WriteAllText(Path.Combine(folder, "free-1234567.txt"), "1_18_4, 1_19_4\n1_20_4");
        Assert.SequenceEqual(["1_18_4", "1_19_4", "1_20_4"], (await client.GetFreeSeatsAsync("1234567", 1, 0)).Order());

        using var raw = new HttpClient(new FakeCinemaCityHandler(folder));
        File.WriteAllText(Path.Combine(folder, "error500-1234567.txt"), "");
        Assert.Equal(HttpStatusCode.InternalServerError, (await raw.GetAsync("https://tickets.cinema-city.pl/api/presentations/1234567")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await raw.GetAsync("https://tickets.cinema-city.pl/api/presentations/7654321")).StatusCode);
        File.WriteAllText(Path.Combine(folder, "block.txt"), "");
        var challenge = await raw.GetAsync("https://tickets.cinema-city.pl/api/presentations/7654321");
        Assert.Equal((HttpStatusCode.Forbidden, "challenge", "text/html"), (challenge.StatusCode, challenge.Headers.GetValues("cf-mitigated").Single(), challenge.Content.Headers.ContentType?.MediaType));
        File.WriteAllText(Path.Combine(folder, "block.txt"), "429 120");
        var tooMany = await raw.GetAsync("https://tickets.cinema-city.pl/api/presentations/7654321");
        Assert.Equal((HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(120)), (tooMany.StatusCode, tooMany.Headers.RetryAfter?.Delta));
    }
    finally
    {
        Directory.Delete(folder, recursive: true);
    }
});

TestRunner.Add("Poller e-mails: one block per screening with its booking link, the profile link; test e-mail; owner's alerts", () =>
{
    var plan = CinemaCityParser.ParseSeatPlan(FakePlanJson());
    var profile = SampleProfile("Diuna IMAX", screenings: 2);
    var seats = plan.Seats.Where(s => s.Key is "1_18_4" or "1_19_4").ToList();
    var news = new List<(WatchedScreening, IReadOnlyList<CinemaWatch.Core.Seat>)> { (profile.Screenings[1], seats), (profile.Screenings[0], [seats[0]]) };
    var profileLink = SiteLinks.Profile("http://localhost:5180/ignored", Guid.Empty);
    Assert.Equal($"http://localhost:5180/?profile={Guid.Empty}#profile-{Guid.Empty}", profileLink);
    using (Localizer.Use(AppLanguage.Polish))
    {
        var mail = PollerMails.SeatsAvailable("a@example.test", profile, "Diuna IMAX", news, profileLink);
        Assert.Equal("Wolne miejsca: Diuna: Część trzecia", mail.Subject);
        Assert.True(mail.Text.StartsWith("Zwolniły się pilnowane miejsca (profil „Diuna IMAX”):"));
        // Ordered by start time; each screening with its own link.
        var first = mail.Text.IndexOf("Kup bilet: https://tickets.cinema-city.pl/api/order/1765592?lang=pl", StringComparison.Ordinal);
        var second = mail.Text.IndexOf("Kup bilet: https://tickets.cinema-city.pl/api/order/1765593?lang=pl", StringComparison.Ordinal);
        Assert.True(first > 0 && second > first);
        Assert.True(mail.Text.Contains("Warszawa · ") && mail.Text.Contains("rząd 4: 18, 19") && mail.Text.Contains(profileLink));
        Assert.True(mail.Html.Contains("<a href=\"https://tickets.cinema-city.pl/api/order/1765592?lang=pl\">Kup bilet</a>"));

        var test = PollerMails.Test("a@example.test", new DateTime(2026, 11, 3, 12, 0, 0), "http://localhost/");
        Assert.Equal("[TEST] Wolne miejsca: Przykładowy film", test.Subject);
        Assert.True(test.Text.Contains("Kup bilet: https://www.cinema-city.pl/") && test.Text.Contains("śr. 4.11, 19:00"));

        var blocked = PollerMails.Blocked("o@example.test", "HTTP 403 (cf-mitigated: challenge) on GET x", new DateTimeOffset(2026, 11, 3, 13, 0, 0, TimeSpan.Zero), 2, "http://localhost/");
        Assert.Equal("CinemaWatch: sprawdzanie wstrzymane", blocked.Subject);
        Assert.True(blocked.Text.Contains("do 3.11.2026 14:00 (czasu polskiego)") && blocked.Text.Contains("blokada nr 2 z rzędu"));
        Assert.Equal("CinemaWatch: sprawdzanie wznowione", PollerMails.Resumed("o@example.test", null, "http://localhost/").Subject);
    }
    using (Localizer.Use(AppLanguage.English))
    {
        var mail = PollerMails.SeatsAvailable("a@example.test", profile, "Diuna IMAX", news, profileLink);
        Assert.Equal("Seats available: Diuna: Część trzecia", mail.Subject);
        Assert.True(mail.Text.Contains("Warsaw · ") && mail.Text.Contains("row 4: 18, 19") && mail.Text.Contains("Buy a ticket: "));
    }
});

TestRunner.Add("Time: Polish screening times become UTC (winter, summer, the hour skipped in spring)", () =>
{
    Assert.Equal(new DateTimeOffset(2027, 1, 15, 15, 0, 0, TimeSpan.Zero), PollerTime.ToUtc(new DateTime(2027, 1, 15, 16, 0, 0)));
    Assert.Equal(new DateTimeOffset(2027, 7, 15, 14, 0, 0, TimeSpan.Zero), PollerTime.ToUtc(new DateTime(2027, 7, 15, 16, 0, 0)));
    Assert.Equal(new DateTimeOffset(2027, 3, 28, 1, 30, 0, TimeSpan.Zero), PollerTime.ToUtc(new DateTime(2027, 3, 28, 2, 30, 0)));
    Assert.Equal("16:00", PollerTime.ClockText(new DateTimeOffset(2027, 1, 15, 15, 0, 0, TimeSpan.Zero)));
});

// ---------------------------------------------------------------------------------------------------------------
// With a database: the real web app in memory (CINEMAWATCH_TEST_DB, see src/CinemaWatch.Web/README.md)
// ---------------------------------------------------------------------------------------------------------------

var adminConnection = Environment.GetEnvironmentVariable("CINEMAWATCH_TEST_DB");
TestDatabase? database = null;
TestApp? app = null;

if (string.IsNullOrWhiteSpace(adminConnection))
{
    Console.WriteLine("  SKIP  Database and web tests: CINEMAWATCH_TEST_DB is not set (src/CinemaWatch.Web/README.md)");
    if (Environment.GetEnvironmentVariable("CINEMAWATCH_REQUIRE_DB") == "1")
        TestRunner.Add("Database tests are required here (CINEMAWATCH_REQUIRE_DB=1)", () =>
            throw new InvalidOperationException("CINEMAWATCH_TEST_DB is not set"));
}
else
{
    database = await TestDatabase.CreateAsync(adminConnection);
    app = new TestApp(database.ConnectionString);
    RegisterDatabaseTests(app, database);
}

try
{
    return await TestRunner.RunAsync();
}
finally
{
    app?.Dispose();
    if (database is not null)
        await database.DisposeAsync();
}

// A profile like the desktop app saves it, with screenings in the future (or from the given Polish start time).
static WatchProfile SampleProfile(string name = "Diuna IMAX", int screenings = 1, DateTime? start = null)
{
    var first = start ?? PolishTime.Now().Date.AddDays(30).AddHours(16);
    return new WatchProfile
    {
        Name = name,
        CinemaId = "1060",
        CinemaName = "Cinema City Sadyba",
        CinemaIds = ["1060"],
        FilmId = "8105s2r",
        FilmName = "Diuna: Część trzecia",
        DateFrom = DateOnly.FromDateTime(first),
        DateTo = DateOnly.FromDateTime(first.AddDays(screenings)),
        MinAdjacent = 1,
        IntervalMinutes = 10,
        Screenings = Enumerable.Range(0, screenings).Select(i => new WatchedScreening
        {
            Id = (1765592 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
            CinemaId = "1060",
            City = "Warszawa",
            StartsAt = first.AddDays(i),
            Auditorium = "IMAX BNP Paribas",
            BookingLink = $"https://tickets.cinema-city.pl/api/order/{1765592 + i}?lang=pl",
            SeatKeys = ["1_18_4", "1_19_4"],
        }).ToList(),
    };
}

// The captured Sadyba IMAX hall (13/1), as the fake serves it.
static string FakePlanJson()
{
    var folder = Path.Combine(Path.GetTempPath(), "cinemawatch-plan-" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        using var client = new CinemaCityClient(new FakeCinemaCityHandler(folder));
        return client.GetSeatPlanJsonAsync(13, 1).GetAwaiter().GetResult();
    }
    finally
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }
}

static void RegisterDatabaseTests(TestApp app, TestDatabase database)
{
    const string password = TestApp.Password;

    async Task<T> WithService<T>(Func<IServiceProvider, Task<T>> body)
    {
        using var scope = app.Services.CreateScope();
        return await body(scope.ServiceProvider);
    }

    async Task<HttpClient> SignedInAsync(string email, AppLanguage language)
    {
        await WithService(async services =>
        {
            var accounts = services.GetRequiredService<AccountService>();
            await accounts.InviteAsync(email, language);
            var link = TestApp.LinkPath(app.Mail.Sent.Last(m => m.To == email));
            var result = await accounts.SetPasswordAsync(TestApp.QueryValue(link, "user"), TestApp.QueryValue(link, "code"), password);
            Assert.True(result.Succeeded);
            return 0;
        });
        return await SignInAsync(email);
    }

    async Task<HttpClient> SignInAsync(string email)
    {
        var browser = app.CreateBrowser();
        var page = await browser.PageAsync("/Account/Login");
        var response = await browser.PostFormAsync("/Account/Login", page, "login", new() { ["Input.Email"] = email, ["Input.Password"] = password });
        Assert.Equal("/", Location(response));
        return browser;
    }

    async Task<string> UserIdAsync(string email) =>
        await WithService(async services => (await services.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email))!.Id);

    static ProfileUpload Upload(string fileName, string json) => new(fileName, Encoding.UTF8.GetByteCount(json), new MemoryStream(Encoding.UTF8.GetBytes(json)));

    TestRunner.Add("Database: the migration creates the snake_case tables", async () =>
    {
        Assert.SequenceEqual(
            ["__EFMigrationsHistory", "check_log", "data_protection_keys", "notification_state", "notifications", "poller_state", "profiles",
             "seat_plans", "service_events", "user_claims", "user_logins", "user_tokens", "users", "watched_screenings"],
            await database.TablesAsync());
    });

    TestRunner.Add("Start-up: the owner's account is created once and invited in Polish", async () =>
    {
        _ = app.Services; // starts the app
        var invitation = app.Mail.Sent.Single(m => m.To == TestApp.OwnerEmail);
        Assert.Equal("Zaproszenie do CinemaWatch", invitation.Subject);
        await WithService(async services =>
        {
            await services.GetRequiredService<AccountService>().EnsureOwnerAsync();
            return 0;
        });
        Assert.Equal(1, app.Mail.Sent.Count(m => m.To == TestApp.OwnerEmail));
    });

    TestRunner.Add("Invitations: created, sent again while waiting, refused once active; a mail failure keeps the account", async () =>
    {
        await WithService(async services =>
        {
            var accounts = services.GetRequiredService<AccountService>();
            var users = services.GetRequiredService<UserManager<AppUser>>();

            Assert.Equal(InvitationOutcome.Created, (await accounts.InviteAsync("anna@example.test", AppLanguage.English)).Outcome);
            Assert.Equal("Your invitation to CinemaWatch", app.Mail.Sent[^1].Subject);
            Assert.Equal(InvitationOutcome.Resent, (await accounts.InviteAsync(" Anna@Example.test ", AppLanguage.Polish)).Outcome);
            Assert.Equal("Your invitation to CinemaWatch", app.Mail.Sent[^1].Subject); // the account's language stays

            var link = TestApp.LinkPath(app.Mail.Sent[^1]);
            var userId = TestApp.QueryValue(link, "user");
            Assert.True((await accounts.SetPasswordAsync(userId, TestApp.QueryValue(link, "code"), "too short")).Errors
                .Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordTooShort)));
            Assert.True((await accounts.SetPasswordAsync(userId, TestApp.QueryValue(link, "code"), password)).Succeeded);
            Assert.True(!(await users.FindByIdAsync(userId))!.IsPending);
            Assert.Equal(InvitationOutcome.AlreadyActive, (await accounts.InviteAsync("anna@example.test", AppLanguage.Polish)).Outcome);

            // The link works once: setting the password changes the account's security stamp.
            Assert.Equal(nameof(IdentityErrorDescriber.InvalidToken),
                (await accounts.SetPasswordAsync(userId, TestApp.QueryValue(link, "code"), "another long password")).Errors.Single().Code);

            app.Mail.FailNext = true;
            var failed = await accounts.InviteAsync("bob@example.test", AppLanguage.Polish);
            Assert.Equal(InvitationOutcome.MailFailed, failed.Outcome);
            Assert.True((await users.FindByEmailAsync("bob@example.test"))!.IsPending);
            return 0;
        });
    });

    TestRunner.Add("Password help: nothing for unknown addresses, a new invitation while waiting, a reset link once active", async () =>
    {
        await WithService(async services =>
        {
            var accounts = services.GetRequiredService<AccountService>();
            var before = app.Mail.Sent.Count;
            await accounts.RequestPasswordHelpAsync("nobody@example.test");
            Assert.Equal(before, app.Mail.Sent.Count);

            await accounts.RequestPasswordHelpAsync("bob@example.test");
            Assert.Equal("Zaproszenie do CinemaWatch", app.Mail.Sent[^1].Subject);

            await accounts.RequestPasswordHelpAsync("anna@example.test");
            var reset = app.Mail.Sent[^1];
            Assert.Equal(("anna@example.test", "New password for CinemaWatch"), (reset.To, reset.Subject));
            var link = TestApp.LinkPath(reset);
            Assert.True((await accounts.SetPasswordAsync(TestApp.QueryValue(link, "user"), TestApp.QueryValue(link, "code"), "a brand new long password")).Succeeded);
            return 0;
        });
    });

    TestRunner.Add("Web: sign-in wall, invitation link, sign-in, inviting, access denied for others, sign-out", async () =>
    {
        var owner = app.CreateBrowser();
        Assert.Equal("/Account/Login?ReturnUrl=%2F", Location(await owner.GetAsync("/")));
        Assert.True((await owner.GetAsync("/Account/Register")).StatusCode != HttpStatusCode.OK);
        Assert.True((await owner.PageAsync("/privacy")).Contains("Prywatność"));
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/app.css")).StatusCode);

        // The owner's invitation: set the password, which activates the account.
        var link = TestApp.LinkPath(app.Mail.Sent.First(m => m.To == TestApp.OwnerEmail));
        var page = await owner.PageAsync(link);
        Assert.True(page.Contains("Aktywuj konto"));
        var response = await owner.PostFormAsync(link, page, "set-password", new() { ["Input.Password"] = password, ["Input.Repeated"] = "different" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()).Contains("Hasła się różnią."));
        response = await owner.PostFormAsync(link, page, "set-password", new() { ["Input.Password"] = password, ["Input.Repeated"] = password });
        Assert.Equal("/Account/Login", Location(response));

        // Sign in; the message from the previous page arrives with the redirect.
        page = await owner.PageAsync("/Account/Login");
        Assert.True(page.Contains("Hasło zapisane. Możesz się zalogować."));
        Assert.True((await owner.PageAsync(link)).Contains("Link jest nieważny"));
        response = await owner.PostFormAsync("/Account/Login", page, "login", new() { ["Input.Email"] = TestApp.OwnerEmail, ["Input.Password"] = "wrong password" });
        Assert.True(WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()).Contains("Nieprawidłowy e-mail lub hasło."));
        response = await owner.PostFormAsync("/Account/Login", page, "login", new() { ["Input.Email"] = TestApp.OwnerEmail, ["Input.Password"] = password });
        Assert.Equal("/", Location(response));
        Assert.True((await owner.PageAsync("/")).Contains("Użytkownicy"));

        // Invite a friend who reads English.
        page = await owner.PageAsync("/admin/users");
        response = await owner.PostFormAsync("/admin/users", page, "invite", new() { ["Input.Email"] = "friend@example.test", ["Input.Language"] = "en" });
        Assert.Equal("/admin/users", Location(response));
        page = await owner.PageAsync("/admin/users");
        Assert.True(page.Contains("Zaproszenie wysłane na friend@example.test."));
        Assert.True(page.Contains("czeka na aktywację"));
        var friendMail = app.Mail.Sent.Last(m => m.To == "friend@example.test");
        Assert.Equal("Your invitation to CinemaWatch", friendMail.Subject);

        // "Send again" for a waiting account.
        response = await owner.PostFormAsync("/admin/users/resend", page, null, new() { ["email"] = "friend@example.test" });
        Assert.Equal("/admin/users", Location(response));
        Assert.Equal(2, app.Mail.Sent.Count(m => m.To == "friend@example.test"));

        // The friend activates and signs in: English UI from the account, no owner's page.
        var friend = app.CreateBrowser();
        link = TestApp.LinkPath(app.Mail.Sent.Last(m => m.To == "friend@example.test"));
        page = await friend.PageAsync(link);
        await friend.PostFormAsync(link, page, "set-password", new() { ["Input.Password"] = password, ["Input.Repeated"] = password });
        page = await friend.PageAsync("/Account/Login");
        response = await friend.PostFormAsync("/Account/Login", page, "login", new() { ["Input.Email"] = "friend@example.test", ["Input.Password"] = password });
        Assert.Equal("/", Location(response));
        page = await friend.PageAsync("/");
        Assert.True(page.Contains("Profiles") && !page.Contains("admin/users"));
        Assert.True(Location(await friend.GetAsync("/admin/users")).StartsWith("/Account/AccessDenied"));
        Assert.True(Location(await friend.PostFormAsync("/admin/users/resend", page, null, new() { ["email"] = "x@example.test" })).StartsWith("/Account/AccessDenied"));

        // Sign-out ends the session.
        page = await owner.PageAsync("/");
        Assert.Equal("/Account/Login", Location(await owner.PostFormAsync("/Account/Logout", page, null, [])));
        Assert.True(Location(await owner.GetAsync("/")).StartsWith("/Account/Login"));
    });

    TestRunner.Add("Web: the language switch is saved to the account and redirects only locally", async () =>
    {
        var browser = await SignedInAsync("lang@example.test", AppLanguage.Polish);
        Assert.True((await browser.PageAsync("/")).Contains("Profile"));
        Assert.Equal("/", Location(await browser.GetAsync("/language/en?returnUrl=//evil.example")));
        Assert.True((await browser.PageAsync("/")).Contains("Profiles"));
        Assert.Equal("/Account/Manage", Location(await browser.GetAsync("/language/pl?returnUrl=%2FAccount%2FManage")));
        var language = await WithService(async services =>
            (await services.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync("lang@example.test"))!.Language);
        Assert.Equal("pl", language);
    });

    TestRunner.Add("Web: five wrong passwords lock the account (even the right password is refused then)", async () =>
    {
        await SignedInAsync("locked@example.test", AppLanguage.Polish);
        var browser = app.CreateBrowser();
        var page = await browser.PageAsync("/Account/Login");
        HttpResponseMessage response = null!;
        for (var i = 0; i < 5; i++)
            response = await browser.PostFormAsync("/Account/Login", page, "login", new() { ["Input.Email"] = "locked@example.test", ["Input.Password"] = "wrong password" });
        Assert.Equal("/Account/Lockout", Location(response));
        response = await browser.PostFormAsync("/Account/Login", page, "login", new() { ["Input.Email"] = "locked@example.test", ["Input.Password"] = password });
        Assert.Equal("/Account/Lockout", Location(response));
    });

    TestRunner.Add("Web: personal data download, password change, account deletion", async () =>
    {
        var browser = await SignedInAsync("leaving@example.test", AppLanguage.Polish);
        var page = await browser.PageAsync("/Account/Manage");
        var download = await browser.PostFormAsync("/Account/Manage/DownloadPersonalData", page, null, []);
        Assert.Equal("application/json", download.Content.Headers.ContentType?.MediaType);
        Assert.True((await download.Content.ReadAsStringAsync()).Contains("leaving@example.test"));

        page = await browser.PageAsync("/Account/Manage/ChangePassword");
        var response = await browser.PostFormAsync("/Account/Manage/ChangePassword", page, "change-password",
            new() { ["Input.Current"] = password, ["Input.Password"] = "the new long password", ["Input.Repeated"] = "the new long password" });
        Assert.Equal("/Account/Manage", Location(response));
        Assert.True((await browser.PageAsync("/Account/Manage")).Contains("Hasło zmienione."));

        page = await browser.PageAsync("/Account/Manage/DeleteAccount");
        response = await browser.PostFormAsync("/Account/Manage/DeleteAccount", page, "delete-account", new() { ["Input.Password"] = password });
        Assert.True(WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()).Contains("Nieprawidłowe hasło."));
        response = await browser.PostFormAsync("/Account/Manage/DeleteAccount", page, "delete-account", new() { ["Input.Password"] = "the new long password" });
        Assert.Equal("/Account/Login", Location(response));
        var gone = await WithService(async services =>
            await services.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync("leaving@example.test") is null);
        Assert.True(gone);
        Assert.True(Location(await browser.GetAsync("/")).StartsWith("/Account/Login"));
    });

    TestRunner.Add("Profiles: added up to the limit, same name replaces (id and state kept), owner's limit raises and lowers it", async () =>
    {
        await SignedInAsync("collector@example.test", AppLanguage.English);
        var userId = await UserIdAsync("collector@example.test");
        await WithService(async services =>
        {
            using var _ = Localizer.Use(AppLanguage.English);
            var profiles = services.GetRequiredService<ProfileService>();
            var json = (string name) => ProfileStore.Serialize(SampleProfile(name));

            // Six files at once, one broken: five fit, the sixth is over the limit, the broken one is refused.
            var results = await profiles.ImportAsync(userId,
                [.. "ABCDE".Select(c => Upload($"{c}.cinemawatch.json", json($"Profile {c}"))), Upload("broken.json", "{"), Upload("F.cinemawatch.json", json("Profile F"))]);
            Assert.SequenceEqual(
                [ImportOutcome.Added, ImportOutcome.Added, ImportOutcome.Added, ImportOutcome.Added, ImportOutcome.Added, ImportOutcome.Invalid, ImportOutcome.LimitReached],
                results.Select(r => r.Outcome));
            Assert.Equal("broken", results[5].ProfileName);
            var list = await profiles.ListAsync(userId);
            Assert.Equal((5, 5), (list.Profiles.Count, list.Limit));
            Assert.True(list.Profiles.All(p => p.Enabled && !p.Ended));

            // Re-importing a changed "Profile A" replaces it: same id, still disabled, new seats.
            var a = list.Profiles.Single(p => p.Name == "Profile A");
            Assert.Equal("Profile A", await profiles.SetEnabledAsync(userId, a.Id, false));
            var changed = SampleProfile("Profile A");
            changed.Screenings[0].SeatKeys = ["1_20_4"];
            Assert.Equal(ImportOutcome.Replaced, (await profiles.ImportAsync(userId, [Upload("other file name.json", ProfileStore.Serialize(changed))])).Single().Outcome);
            var replaced = (await profiles.ListAsync(userId)).Profiles.Single(p => p.Name == "Profile A");
            Assert.Equal((a.Id, false), (replaced.Id, replaced.Enabled));
            Assert.SequenceEqual(["1_20_4"], replaced.Document.Screenings[0].SeatKeys);

            // Owner's limit: 6 lets one more in; 3 deletes nothing but blocks new ones; out of range is refused; null = default.
            Assert.Equal(LimitChange.Saved, await profiles.SetLimitAsync(userId, 6));
            Assert.Equal(ImportOutcome.Added, (await profiles.ImportAsync(userId, [Upload("G.json", json("Profile G"))])).Single().Outcome);
            Assert.Equal(LimitChange.Saved, await profiles.SetLimitAsync(userId, 3));
            Assert.Equal(ImportOutcome.LimitReached, (await profiles.ImportAsync(userId, [Upload("H.json", json("Profile H"))])).Single().Outcome);
            Assert.Equal(ImportOutcome.Replaced, (await profiles.ImportAsync(userId, [Upload("G.json", json("Profile G"))])).Single().Outcome);
            Assert.Equal((6, 3), ((await profiles.ListAsync(userId)).Profiles.Count, (await profiles.ListAsync(userId)).Limit));
            Assert.Equal(LimitChange.OutOfRange, await profiles.SetLimitAsync(userId, 0));
            Assert.Equal(LimitChange.OutOfRange, await profiles.SetLimitAsync(userId, LimitOptions.ProfileLimitCeiling + 1));
            Assert.Equal(LimitChange.NotFound, await profiles.SetLimitAsync("no-such-user", 5));
            Assert.Equal(LimitChange.Saved, await profiles.SetLimitAsync(userId, null));
            Assert.Equal(5, (await profiles.ListAsync(userId)).Limit);
            Assert.Equal(6, (await profiles.CountByAccountAsync())[userId]);

            // A file over 256 KB is refused before its content is read.
            var large = await profiles.ImportAsync(userId, [new ProfileUpload("big.json", ProfileImport.MaxFileBytes + 1, new MemoryStream())]);
            Assert.True(large.Single().Errors.Single().StartsWith("The file is too large"));
            var lying = await profiles.ImportAsync(userId, [new ProfileUpload("big.json", 10, new MemoryStream(new byte[ProfileImport.MaxFileBytes + 100]))]);
            Assert.True(lying.Single().Errors.Single().StartsWith("The file is too large"));
            return 0;
        });
    });

    TestRunner.Add("Profiles: the end time is stored in UTC; another account's profile behaves as missing; account deletion removes profiles", async () =>
    {
        await SignedInAsync("first@example.test", AppLanguage.Polish);
        await SignedInAsync("second@example.test", AppLanguage.Polish);
        var first = await UserIdAsync("first@example.test");
        var second = await UserIdAsync("second@example.test");
        var profileId = await WithService(async services =>
        {
            var profiles = services.GetRequiredService<ProfileService>();
            // 16:00 in Warsaw in winter (UTC+1) is 15:00 UTC; the last screening decides.
            var winter = new DateTime(DateTime.UtcNow.Year + 1, 1, 15, 16, 0, 0);
            await profiles.ImportAsync(first, [Upload("winter.json", ProfileStore.Serialize(SampleProfile("Winter", screenings: 2, start: winter)))]);
            var stored = await services.GetRequiredService<AppDbContext>().Profiles.AsNoTracking().SingleAsync(p => p.UserId == first);
            Assert.Equal(new DateTimeOffset(winter.Year, 1, 16, 15, 0, 0, TimeSpan.Zero), stored.EndsAt);

            Assert.Equal(null, await profiles.SetEnabledAsync(second, stored.Id, false));
            Assert.Equal(null, await profiles.DeleteAsync(second, stored.Id));
            Assert.Equal(null, await profiles.DownloadAsync(second, stored.Id));
            Assert.Equal(0, (await profiles.ListAsync(second)).Profiles.Count);
            Assert.True(await profiles.DownloadAsync(first, stored.Id) is { FileName: "Winter.cinemawatch.json" });
            return stored.Id;
        });

        // The database cascade removes the profiles (they are not loaded when the account is deleted).
        await WithService(async services =>
        {
            var users = services.GetRequiredService<UserManager<AppUser>>();
            Assert.True((await users.DeleteAsync((await users.FindByIdAsync(first))!)).Succeeded);
            return 0;
        });
        var left = await WithService(async services => await services.GetRequiredService<AppDbContext>().Profiles.CountAsync(p => p.Id == profileId));
        Assert.Equal(0, left);
    });

    TestRunner.Add("Web: import from the browser, list, disable, download as the desktop saves it, delete; someone else gets 404", async () =>
    {
        var browser = await SignedInAsync("web-profiles@example.test", AppLanguage.Polish);
        var page = await browser.PageAsync("/");
        Assert.True(page.Contains("0 z 5 profili") && page.Contains("Nie masz jeszcze profili."));

        // Saved before desktop 1.4: no city per screening; the "Cinema" column comes from the IMAX cinema list.
        var sample = SampleProfile("Diuna IMAX", screenings: 2);
        sample.Screenings.ForEach(s => s.City = "");
        var json = ProfileStore.Serialize(sample);
        var response = await browser.PostFilesAsync("/", page, "import", "Input.Files", [("Diuna IMAX.cinemawatch.json", Encoding.UTF8.GetBytes(json))]);
        Assert.Equal("/", Location(response));
        page = await browser.PageAsync("/");
        Assert.True(page.Contains("Dodano profil „Diuna IMAX”."));
        Assert.True(page.Contains("1 z 5 profili") && page.Contains("Diuna: Część trzecia, 2 seanse, 4 pilnowane miejsca") && page.Contains("włączony"));
        var id = Regex.Match(page, "profiles/([0-9a-f-]{36})/download").Groups[1].Value;
        Assert.True(page.Contains("<td>Warszawa, Sadyba</td>"));
        Assert.True(page.Contains($"<section class=\"profile\" id=\"profile-{id}\">") && page.Contains($"id=\"profile-body-{id}\" hidden")); // closed

        // A broken file next to a good one: the page shows what happened to each.
        response = await browser.PostFilesAsync("/", page, "import", "Input.Files",
            [("zly.cinemawatch.json", Encoding.UTF8.GetBytes("{ nope")), ("Diuna IMAX.cinemawatch.json", Encoding.UTF8.GetBytes(json))]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.True(result.Contains("Plik zly.cinemawatch.json odrzucony:") && result.Contains("niepoprawny JSON"));
        Assert.True(result.Contains("Zaktualizowano profil „Diuna IMAX” (poprzednia wersja zastąpiona)."));

        // Download: exactly what the desktop app would save, with its file name.
        var download = await browser.GetAsync($"/profiles/{id}/download");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(json, await download.Content.ReadAsStringAsync());
        Assert.Equal("Diuna IMAX.cinemawatch.json", download.Content.Headers.ContentDisposition?.FileNameStar ?? download.Content.Headers.ContentDisposition?.FileName);

        // Disable; a form without the antiforgery token is refused. Afterwards the profile stays open.
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync($"/profiles/{id}/disable", new FormUrlEncodedContent([]))).StatusCode);
        var opened = $"/?profile={id}#profile-{id}";
        Assert.Equal(opened, Location(await browser.PostFormAsync($"/profiles/{id}/disable", page, null, [])));
        page = await browser.PageAsync($"/?profile={id}");
        Assert.True(page.Contains("Profil „Diuna IMAX” wyłączony.") && page.Contains("wyłączony") && page.Contains("Włącz"));
        Assert.True(page.Contains($"<section class=\"profile open\" id=\"profile-{id}\">") && !page.Contains($"id=\"profile-body-{id}\" hidden"));

        // "Delete" asks first; the question lives in the address and "Keep it" goes back to the opened profile.
        Assert.True(!page.Contains("Tak, usuń profil") && page.Contains($"href=\"/?profile={id}&confirm=delete#profile-{id}\""));
        page = await browser.PageAsync($"/?profile={id}&confirm=delete");
        Assert.True(page.Contains("Usunąć profil „Diuna IMAX”? Plik w aplikacji na Windows zostaje.") && page.Contains("Tak, usuń profil"));
        Assert.True(page.Contains($"href=\"/?profile={id}#profile-{id}\">Nie usuwaj</a>"));

        // Someone else: the profile does not exist for them.
        var stranger = await SignedInAsync("stranger@example.test", AppLanguage.Polish);
        var strangerPage = await stranger.PageAsync($"/?profile={id}&confirm=delete");
        Assert.True(!strangerPage.Contains("Diuna IMAX") && !strangerPage.Contains("Tak, usuń profil"));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/profiles/{id}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostFormAsync($"/profiles/{id}/delete", strangerPage, null, [])).StatusCode);

        // Personal data includes the profile; then delete it.
        var data = await (await browser.PostFormAsync("/Account/Manage/DownloadPersonalData", await browser.PageAsync("/Account/Manage"), null, [])).Content.ReadAsStringAsync();
        Assert.True(data.Contains("\"profileLimit\": 5") && data.Contains("\"filmName\": \"Diuna: Część trzecia\""));
        Assert.Equal("/", Location(await browser.PostFormAsync($"/profiles/{id}/delete", page, null, [])));
        page = await browser.PageAsync("/");
        Assert.True(page.Contains("Profil „Diuna IMAX” usunięty.") && page.Contains("0 z 5 profili"));
    });

    TestRunner.Add("Web: the owner sets an account's profile limit (empty = default, out of range refused); nobody else can", async () =>
    {
        var member = await SignedInAsync("member@example.test", AppLanguage.Polish);
        var memberId = await UserIdAsync("member@example.test");
        var owner = await SignInAsync(TestApp.OwnerEmail);
        var page = await owner.PageAsync("/admin/users");
        Assert.True(page.Contains("Limit profili") && page.Contains($"value=\"{memberId}\""));

        Assert.Equal("/admin/users", Location(await owner.PostFormAsync("/admin/users/limit", page, null, new() { ["userId"] = memberId, ["maxProfiles"] = "7" })));
        Assert.True((await owner.PageAsync("/admin/users")).Contains("Limit profili dla member@example.test: 7."));
        Assert.True((await member.PageAsync("/")).Contains("0 z 7 profili"));

        foreach (var bad in new[] { "abc", "0", "51", "-3" })
        {
            await owner.PostFormAsync("/admin/users/limit", page, null, new() { ["userId"] = memberId, ["maxProfiles"] = bad });
            Assert.True((await owner.PageAsync("/admin/users")).Contains("Limit musi być liczbą od 1 do 50."), bad);
        }
        Assert.True((await member.PageAsync("/")).Contains("0 z 7 profili"));

        await owner.PostFormAsync("/admin/users/limit", page, null, new() { ["userId"] = memberId, ["maxProfiles"] = "" });
        Assert.True((await owner.PageAsync("/admin/users")).Contains("Limit profili dla member@example.test: domyślny (5)."));
        Assert.True((await member.PageAsync("/")).Contains("0 z 5 profili"));

        var memberPage = await member.PageAsync("/");
        Assert.True(Location(await member.PostFormAsync("/admin/users/limit", memberPage, null, new() { ["userId"] = memberId, ["maxProfiles"] = "50" })).StartsWith("/Account/AccessDenied"));
        Assert.True((await member.PageAsync("/")).Contains("0 z 5 profili"));
    });

    // -----------------------------------------------------------------------------------------------------------
    // Poller: the cycles run here, on the test clock, against the fake (TestApp); nothing reaches Cinema City.
    // -----------------------------------------------------------------------------------------------------------

    Task<CycleReport> CycleAsync() => app.Services.GetRequiredService<PollerCycle>().RunAsync();

    TimeSpan afterInterval = TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(1); // interval 10 min + at most 20%

    // A clean poller for every test: no profiles, no state; two hours later, so the request budget is full again.
    async Task ResetPollerAsync()
    {
        app.Time.Advance(TimeSpan.FromHours(2));
        foreach (var file in Directory.GetFiles(app.FakeFolder))
            File.Delete(file);
        app.Network.Override = null;
        await WithService(async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            await db.Profiles.ExecuteDeleteAsync(); // the database cascade removes their state and history
            await db.WatchedScreenings.ExecuteDeleteAsync();
            await db.PollerState.ExecuteDeleteAsync();
            await db.CheckLog.ExecuteDeleteAsync();
            await db.ServiceEvents.ExecuteDeleteAsync();
            await db.SeatPlans.ExecuteDeleteAsync();
            return 0;
        });
    }

    static WatchProfile PollProfile(string name, IReadOnlyList<string> ids, int minAdjacent = 1)
    {
        var profile = SampleProfile(name, screenings: ids.Count);
        profile.MinAdjacent = minAdjacent;
        for (var i = 0; i < ids.Count; i++)
        {
            profile.Screenings[i].Id = ids[i];
            profile.Screenings[i].BookingLink = $"https://tickets.cinema-city.pl/api/order/{ids[i]}?lang=pl";
        }
        return profile;
    }

    async Task<Guid> ImportAsync(string userId, WatchProfile profile) => await WithService(async services =>
    {
        var result = (await services.GetRequiredService<ProfileService>().ImportAsync(userId, [Upload("p.json", ProfileStore.Serialize(profile))])).Single();
        Assert.True(result.Outcome is ImportOutcome.Added or ImportOutcome.Replaced, result.Outcome.ToString());
        return (await services.GetRequiredService<AppDbContext>().Profiles.SingleAsync(p => p.UserId == userId && p.Name == profile.Name)).Id;
    });

    async Task<WatchedScreeningRow> RowAsync(string id) =>
        await WithService(async services => await services.GetRequiredService<AppDbContext>().WatchedScreenings.AsNoTracking().SingleAsync(r => r.Id == id));

    async Task<PollerStateRow> PollerStateAsync() =>
        await WithService(async services => await services.GetRequiredService<AppDbContext>().PollerState.AsNoTracking().SingleAsync());

    async Task<List<ServiceEvent>> EventsAsync(ServiceEventKind kind) =>
        await WithService(async services => await services.GetRequiredService<AppDbContext>().ServiceEvents.AsNoTracking().Where(e => e.Kind == kind).ToListAsync());

    // E-mails to an address, without the invitation every test account gets.
    List<CinemaWatch.Server.Mail.OutgoingMail> MailsTo(string email) =>
        app.Mail.Sent.Where(m => m.To == email && !m.Subject.StartsWith("Zaproszenie") && !m.Subject.StartsWith("Your invitation")).ToList();

    List<string> EndpointsSince(int mark) => app.Network.Requests.Skip(mark).Select(r => RequestRecord.EndpointOf(r.Uri)).ToList();

    // Rows appear at the first cycle and are due within a minute; the second cycle checks them.
    async Task<CycleReport> FirstChecksAsync()
    {
        await CycleAsync();
        app.Time.Advance(TimeSpan.FromSeconds(61));
        return await CycleAsync();
    }

    void AssertNextCheckInRhythm(WatchedScreeningRow row)
    {
        var now = app.Time.GetUtcNow();
        Assert.True(row.NextCheckAt >= now + TimeSpan.FromMinutes(10) && row.NextCheckAt <= now + TimeSpan.FromMinutes(12), $"next check {row.NextCheckAt:O}, now {now:O}");
    }

    TestRunner.Add("Poller: new screenings are checked within a minute (details, hall layout once, free seats, pauses between requests); next check in 10-12 minutes", async () =>
    {
        await ResetPollerAsync();
        var browser = await SignedInAsync("poll-first@example.test", AppLanguage.Polish);
        var profile = await ImportAsync(await UserIdAsync("poll-first@example.test"), PollProfile("Pierwszy", ["2000001", "2000002"]));
        var mark = app.Network.Requests.Count;
        var pauses = app.Pacer.Pauses;

        Assert.Equal(CycleResult.Idle, (await CycleAsync()).Result); // rows exist now, due within a minute
        Assert.Equal(mark, app.Network.Requests.Count);
        app.Time.Advance(TimeSpan.FromSeconds(61));
        var report = await CycleAsync();
        Assert.Equal((2, 0, 5, false), (report.Checked, report.Failed, report.Requests, report.Blocked));
        Assert.SequenceEqual(["presentation", "seatplan", "seats-status", "presentation", "seats-status"], EndpointsSince(mark));
        // One pause, before the second screening: the requests of one screening follow each other directly.
        Assert.SequenceEqual([false], app.Pacer.Manual.Skip(pauses));
        var requests = app.Network.Requests.Skip(mark).ToList();
        Assert.True(requests.All(r => r.UserAgent!.Contains($"CinemaWatch-Server/{PollerInfo.Version}")));
        Assert.Equal(1, requests.Select(r => r.Uuid).Distinct().Count()); // one session per cycle

        foreach (var id in new[] { "2000001", "2000002" })
        {
            var row = await RowAsync(id);
            Assert.Equal((CheckOutcome.Ok, 0, 383), (row.LastOutcome!.Value, row.FreeCount!.Value, row.TotalCount!.Value));
            Assert.Equal((13, 1), (row.VenueId!.Value, row.SeatplanId!.Value));
            AssertNextCheckInRhythm(row);
        }
        var logged = await WithService(async services => await services.GetRequiredService<AppDbContext>().CheckLog.AsNoTracking().ToListAsync());
        Assert.Equal(5, logged.Count);
        Assert.True(logged.All(e => e.Status == 200 && e.ScreeningId is "2000001" or "2000002" && !e.Manual && e.Cycle.EndsWith('Z')));
        Assert.Equal(0, MailsTo("poll-first@example.test").Count); // sold out: nothing to say

        app.Time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(CycleResult.Idle, (await CycleAsync()).Result);

        // "Check now": the same pattern, with the shorter pause meant for somebody waiting.
        pauses = app.Pacer.Pauses;
        await browser.PostFormAsync($"/profiles/{profile}/check", await browser.PageAsync("/"), null, []);
        Assert.Equal((2, 4), ((report = await CycleAsync()).Checked, report.Requests));
        Assert.SequenceEqual([true], app.Pacer.Manual.Skip(pauses));
    });

    TestRunner.Add("Poller: a freed seat is e-mailed once per profile (adjacent-seat rule, language, links); freed again = e-mailed again; history; account deletion removes it", async () =>
    {
        await ResetPollerAsync();
        var anna = await SignedInAsync("poll-anna@example.test", AppLanguage.Polish);
        await SignedInAsync("poll-ben@example.test", AppLanguage.English);
        var annaProfile = await ImportAsync(await UserIdAsync("poll-anna@example.test"), PollProfile("Anna", ["2000011"]));
        await ImportAsync(await UserIdAsync("poll-ben@example.test"), PollProfile("Ben", ["2000011"], minAdjacent: 2));

        app.Fake("free-2000011.txt", "1_18_4");
        var mark = app.Network.Requests.Count;
        var report = await FirstChecksAsync();
        Assert.Equal((1, 1), (report.Checked, report.MailsSent));
        Assert.Equal(1, EndpointsSince(mark).Count(e => e == "presentation")); // one check serves both profiles
        var mail = MailsTo("poll-anna@example.test").Single();
        Assert.Equal("Wolne miejsca: Diuna: Część trzecia", mail.Subject);
        Assert.True(mail.Text.Contains("Warszawa · ") && mail.Text.Contains("· rząd 4: 19\n"));
        Assert.True(mail.Text.Contains("Kup bilet: https://tickets.cinema-city.pl/api/order/2000011?lang=pl"));
        Assert.True(mail.Text.Contains($"http://localhost/?profile={annaProfile}#profile-{annaProfile}"));
        Assert.Equal(0, MailsTo("poll-ben@example.test").Count); // Ben wants two seats next to each other

        // The same seat at the next check: nothing new.
        app.Time.Advance(afterInterval);
        Assert.Equal(0, (await CycleAsync()).MailsSent);

        // A second seat next to it: Anna hears about the new seat only, Ben (English) about both.
        app.Fake("free-2000011.txt", "1_18_4 1_19_4");
        app.Time.Advance(afterInterval);
        Assert.Equal(2, (await CycleAsync()).MailsSent);
        Assert.True(MailsTo("poll-anna@example.test")[^1].Text.Contains("· rząd 4: 18\n"));
        var ben = MailsTo("poll-ben@example.test").Single();
        Assert.Equal("Seats available: Diuna: Część trzecia", ben.Subject);
        Assert.True(ben.Text.Contains("Warsaw · ") && ben.Text.Contains("row 4: 18, 19") && ben.Text.Contains("Buy a ticket: "));

        // Taken again, then freed again: notified again.
        app.Fake("free-2000011.txt", null);
        app.Time.Advance(afterInterval);
        Assert.Equal(0, (await CycleAsync()).MailsSent);
        app.Fake("free-2000011.txt", "1_18_4");
        app.Time.Advance(afterInterval);
        Assert.Equal(1, (await CycleAsync()).MailsSent);
        Assert.Equal(3, MailsTo("poll-anna@example.test").Count);

        // The page: free seats now, the check, the history.
        var page = await anna.PageAsync($"/?profile={annaProfile}");
        Assert.True(page.Contains("są wolne miejsca") && page.Contains("wolne: rząd 4: 19") && page.Contains("sprawdzono"));
        Assert.True(page.Contains("Powiadomienia") && page.Contains("Warszawa · ") && !page.Contains("Jeszcze nie było powiadomień"));
        Assert.True(page.Contains("w sali wolne 1 z 383"));

        // Personal data includes the history; deleting the account removes the state and the history.
        var data = await (await anna.PostFormAsync("/Account/Manage/DownloadPersonalData", await anna.PageAsync("/Account/Manage"), null, [])).Content.ReadAsStringAsync();
        Assert.True(data.Contains("\"notifications\"") && data.Contains("\"subject\": \"Wolne miejsca: Diuna: Część trzecia\""));
        await WithService(async services =>
        {
            var users = services.GetRequiredService<UserManager<AppUser>>();
            Assert.True((await users.DeleteAsync((await users.FindByEmailAsync("poll-anna@example.test"))!)).Succeeded);
            return 0;
        });
        var left = await WithService(async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            return await db.NotificationStates.CountAsync(s => s.ProfileId == annaProfile) + await db.Notifications.CountAsync(n => n.ProfileId == annaProfile);
        });
        Assert.Equal(0, left);
    });

    TestRunner.Add("Poller: HTTP 500 is a failed check - no retry, notification state unchanged, next attempt in the normal rhythm, the other screenings go on", async () =>
    {
        await ResetPollerAsync();
        var browser = await SignedInAsync("poll-error@example.test", AppLanguage.Polish);
        var profile = await ImportAsync(await UserIdAsync("poll-error@example.test"), PollProfile("Błędy", ["2000021", "2000022"]));
        app.Fake("free-2000021.txt", "1_18_4");
        app.Fake("free-2000022.txt", "1_18_4");
        Assert.Equal(1, (await FirstChecksAsync()).MailsSent); // both screenings in one e-mail
        var mail = MailsTo("poll-error@example.test").Single();
        Assert.True(mail.Text.Contains("order/2000021") && mail.Text.Contains("order/2000022"));
        var succeeded = (await RowAsync("2000021")).LastSucceededAt;

        app.Fake("error500-2000021.txt", "");
        app.Time.Advance(afterInterval);
        var mark = app.Network.Requests.Count;
        var report = await CycleAsync();
        Assert.Equal((1, 1, false), (report.Checked, report.Failed, report.Blocked));
        Assert.Equal(1, app.Network.Requests.Skip(mark).Count(r => r.Uri.ToString().Contains("2000021"))); // no retry
        var failed = await RowAsync("2000021");
        Assert.Equal((CheckOutcome.Failed, 500), (failed.LastOutcome!.Value, failed.LastHttpStatus!.Value));
        Assert.Equal(succeeded, failed.LastSucceededAt);
        AssertNextCheckInRhythm(failed);
        Assert.Equal(1, MailsTo("poll-error@example.test").Count);
        Assert.True((await browser.PageAsync($"/?profile={profile}")).Contains("błąd Cinema City (HTTP 500)"));

        // The failure kept the state: the seat that was already notified is not notified again.
        app.Fake("error500-2000021.txt", null);
        app.Time.Advance(afterInterval);
        report = await CycleAsync();
        Assert.Equal((2, 0, 0), (report.Checked, report.Failed, report.MailsSent));

        // Every request answers 500: everything fails, nothing is blocked, the log has the statuses.
        app.Fake("error500.txt", "");
        app.Time.Advance(afterInterval);
        report = await CycleAsync();
        Assert.Equal((0, 2, false), (report.Checked, report.Failed, report.Blocked));
        Assert.Equal(null, (await PollerStateAsync()).BlockedUntil);
        var errors = await WithService(async services => await services.GetRequiredService<AppDbContext>().CheckLog.CountAsync(e => e.Status == 500));
        Assert.Equal(3, errors);
    });

    TestRunner.Add("Poller: a block (403 challenge) stops the cycle at the first request, drops pending \"Check now\", pauses everything, alerts the owner; after the pause the blocked screening first", async () =>
    {
        await ResetPollerAsync();
        var browser = await SignedInAsync("poll-block@example.test", AppLanguage.Polish);
        var profile = await ImportAsync(await UserIdAsync("poll-block@example.test"), PollProfile("Blokada", ["2000031", "2000032", "2000033"]));
        Assert.Equal(3, (await FirstChecksAsync()).Checked);
        var firstCheck = app.Time.GetUtcNow();

        // "Check now" runs into a block: the first request is refused, nothing more is sent, the pending check is dropped.
        app.Fake("block.txt", "");
        app.Time.Advance(TimeSpan.FromMinutes(2));
        await browser.PostFormAsync($"/profiles/{profile}/check", await browser.PageAsync("/"), null, []);
        var ownerMails = MailsTo(TestApp.OwnerEmail).Count;
        var mark = app.Network.Requests.Count;
        var report = await CycleAsync();
        Assert.Equal((true, 0, 1), (report.Blocked, report.Checked, report.Requests));
        Assert.Equal(mark + 1, app.Network.Requests.Count);
        var now = app.Time.GetUtcNow();
        var state = await PollerStateAsync();
        Assert.Equal((now + TimeSpan.FromMinutes(60), 1), (state.BlockedUntil!.Value, state.BlockCount));
        var blocked = (await EventsAsync(ServiceEventKind.Blocked)).Single();
        Assert.True(blocked.Message.Contains("HTTP 403 (cf-mitigated: challenge)") && blocked.Data!.Contains("Fake Cloudflare challenge"));
        var alert = MailsTo(TestApp.OwnerEmail)[^1];
        Assert.Equal((ownerMails + 1, "CinemaWatch: sprawdzanie wstrzymane"), (MailsTo(TestApp.OwnerEmail).Count, alert.Subject));
        Assert.True(alert.Text.Contains("HTTP 403"));
        var rows = new List<WatchedScreeningRow>();
        foreach (var id in new[] { "2000031", "2000032", "2000033" })
            rows.Add(await RowAsync(id));
        var blockedId = rows.Single(r => r.LastOutcome == CheckOutcome.Blocked).Id;
        Assert.Equal(2, rows.Count(r => r.LastOutcome == CheckOutcome.Ok));
        Assert.True(rows.All(r => r.ManualRequestedAt is null));

        // Users see the pause and a greyed-out button, not a check that cannot run; "Check now" is refused.
        var page = await browser.PageAsync($"/?profile={profile}");
        Assert.True(page.Contains("Sprawdzanie miejsc jest wstrzymane do") && page.Contains("zablokowane (HTTP 403)"));
        Assert.True(!page.Contains("Sprawdzam…") && page.Contains("disabled title=\"Sprawdzanie wstrzymane do"));
        Assert.True(page.Contains($"http-equiv=\"refresh\" content=\"3605; url=/?profile={profile}&refresh=")); // reloads when the pause ends
        await browser.PostFormAsync($"/profiles/{profile}/check", page, null, []);
        Assert.True((await browser.PageAsync($"/?profile={profile}")).Contains("Sprawdzanie jest wstrzymane do"));

        // During the pause nothing is sent; a "Check now" that slipped in is dropped.
        app.Time.Advance(TimeSpan.FromMinutes(30));
        await WithService(async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            (await db.WatchedScreenings.SingleAsync(r => r.Id == blockedId)).ManualRequestedAt = app.Time.GetUtcNow();
            return await db.SaveChangesAsync();
        });
        mark = app.Network.Requests.Count;
        Assert.Equal(CycleResult.Paused, (await CycleAsync()).Result);
        Assert.Equal(mark, app.Network.Requests.Count);
        Assert.Equal(null, (await RowAsync(blockedId)).ManualRequestedAt);

        // After the pause: one request, for the blocked screening, blocked again - the pause doubles.
        app.Time.Advance(TimeSpan.FromMinutes(31));
        mark = app.Network.Requests.Count;
        report = await CycleAsync();
        Assert.Equal((true, 1), (report.Blocked, report.Requests));
        Assert.True(app.Network.Requests[mark].Uri.ToString().Contains(blockedId));
        state = await PollerStateAsync();
        Assert.Equal((app.Time.GetUtcNow() + TimeSpan.FromMinutes(120), 2), (state.BlockedUntil!.Value, state.BlockCount));

        // The block is over: until the first request gets through, the page says so and reloads every 10 s.
        app.Fake("block.txt", null);
        app.Time.Advance(TimeSpan.FromMinutes(121));
        page = await browser.PageAsync($"/?profile={profile}");
        Assert.True(page.Contains("Przerwa minęła: serwer wznawia sprawdzanie") && page.Contains("http-equiv=\"refresh\" content=\"10; url="));
        Assert.True(page.Contains("Sprawdź teraz</button>") && !page.Contains("Sprawdzanie miejsc jest wstrzymane"));

        // The blocked screening is checked first and alone, its red status goes away; then the rest.
        report = await CycleAsync();
        Assert.Equal((1, false), (report.Checked, report.Blocked));
        Assert.Equal(CheckOutcome.Ok, (await RowAsync(blockedId)).LastOutcome!.Value);
        Assert.Equal(2, (await Task.WhenAll(new[] { "2000031", "2000032", "2000033" }.Where(id => id != blockedId).Select(RowAsync)))
            .Count(r => r.LastCheckedAt == firstCheck));
        state = await PollerStateAsync();
        Assert.Equal((null, 0), (state.BlockedUntil, state.BlockCount));
        Assert.Equal(1, (await EventsAsync(ServiceEventKind.Resumed)).Count);
        Assert.Equal("CinemaWatch: sprawdzanie wznowione", MailsTo(TestApp.OwnerEmail)[^1].Subject);
        page = await browser.PageAsync($"/?profile={profile}");
        Assert.True(!page.Contains("Sprawdzanie miejsc jest wstrzymane") && !page.Contains("zablokowane") && page.Contains("Sprawdź teraz</button>"));
        Assert.True(!page.Contains("Przerwa minęła") && !page.Contains("http-equiv=\"refresh\""));
        Assert.Equal(2, (await CycleAsync()).Checked);
    });

    TestRunner.Add("Poller: a longer Retry-After (429) is respected; HTML instead of JSON is a block too", async () =>
    {
        await ResetPollerAsync();
        await SignedInAsync("poll-429@example.test", AppLanguage.Polish);
        var userId = await UserIdAsync("poll-429@example.test");
        await ImportAsync(userId, PollProfile("Limit", ["2000041"]));
        await FirstChecksAsync();
        app.Fake("block.txt", "429 7200");
        app.Time.Advance(afterInterval);
        Assert.True((await CycleAsync()).Blocked);
        Assert.Equal(app.Time.GetUtcNow() + TimeSpan.FromHours(2), (await PollerStateAsync()).BlockedUntil!.Value);

        await ResetPollerAsync();
        await ImportAsync(userId, PollProfile("Limit", ["2000041"]));
        app.Network.Override = request => request.RequestUri!.AbsolutePath.Contains("/presentations/")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Just a moment...</html>", Encoding.UTF8, "text/html") }
            : null;
        var report = await FirstChecksAsync();
        Assert.Equal((true, 1), (report.Blocked, report.Requests));
        Assert.True((await PollerStateAsync()).BlockReason!.Contains("text/html"));
    });

    TestRunner.Add("Check now: no cooldown, greyed out only while the profile's check runs; results under a minute old answer at once; the page refreshes itself meanwhile", async () =>
    {
        await ResetPollerAsync();
        var browser = await SignedInAsync("poll-now@example.test", AppLanguage.Polish);
        var profile = await ImportAsync(await UserIdAsync("poll-now@example.test"), PollProfile("Teraz", ["2000051"]));
        await FirstChecksAsync();
        var page = await browser.PageAsync("/");
        Assert.True(page.Contains("Sprawdź teraz</button>") && page.Contains("brak wolnych") && page.Contains("Następne automatyczne sprawdzenie ok."));
        Assert.True(!page.Contains("http-equiv=\"refresh\""));

        // While it runs: a greyed-out "Checking…", "checking…" in the row, the page reloads every 3 s; a second click is refused.
        var opened = $"/?profile={profile}#profile-{profile}";
        Assert.Equal(opened, Location(await browser.PostFormAsync($"/profiles/{profile}/check", page, null, [])));
        page = await browser.PageAsync($"/?profile={profile}");
        Assert.True(page.Contains("Sprawdzam profil „Teraz”") && page.Contains("<button type=\"button\" class=\"button\" disabled>Sprawdzam…</button>"));
        Assert.True(page.Contains("sprawdzam…") && !page.Contains("Sprawdź teraz</button>"));
        // A reload to a new address each time (the same address with #profile-… would only scroll), placed in <head>.
        var refresh = Regex.Match(page, "<meta http-equiv=\"refresh\" content=\"3; url=/\\?profile=" + profile + "&refresh=\\d+#profile-" + profile + "\"");
        Assert.True(refresh.Success && refresh.Index < page.IndexOf("</head>", StringComparison.Ordinal), "refresh meta");
        await browser.PostFormAsync($"/profiles/{profile}/check", page, null, []);
        Assert.True((await browser.PageAsync($"/?profile={profile}")).Contains("Profil „Teraz” jest właśnie sprawdzany."));

        // Checked under a minute ago: the stored result at once, no request; the button is back immediately.
        var mark = app.Network.Requests.Count;
        var report = await CycleAsync();
        Assert.Equal((1, 0), (report.FromStoredResult, report.Requests));
        page = await browser.PageAsync($"/?profile={profile}");
        Assert.True(page.Contains("Sprawdź teraz</button>") && !page.Contains("Sprawdzam…") && !page.Contains("http-equiv=\"refresh\""));
        await browser.PostFormAsync($"/profiles/{profile}/check", page, null, []);
        Assert.Equal((1, 0), ((report = await CycleAsync()).FromStoredResult, report.Requests));

        // A minute later Cinema City is asked; the freed seat is shown and e-mailed.
        app.Time.Advance(TimeSpan.FromSeconds(61));
        app.Fake("free-2000051.txt", "1_18_4");
        await browser.PostFormAsync($"/profiles/{profile}/check", await browser.PageAsync("/"), null, []);
        report = await CycleAsync();
        Assert.Equal((1, 2, 1), (report.Checked, report.Requests, report.MailsSent));
        Assert.Equal(mark + 2, app.Network.Requests.Count);
        var manual = await WithService(async services => await services.GetRequiredService<AppDbContext>().CheckLog.CountAsync(e => e.Manual));
        Assert.Equal(2, manual);
        Assert.True((await browser.PageAsync($"/?profile={profile}")).Contains("wolne: rząd 4: 19"));

        // A disabled profile has no button and cannot be checked.
        page = await browser.PageAsync("/");
        await browser.PostFormAsync($"/profiles/{profile}/disable", page, null, []);
        page = await browser.PageAsync($"/?profile={profile}");
        Assert.True(!page.Contains("Sprawdź teraz") && page.Contains("Profil jest wyłączony: serwer go nie sprawdza."));
        await browser.PostFormAsync($"/profiles/{profile}/check", page, null, []);
        Assert.True((await browser.PageAsync("/")).Contains("Profil „Teraz” jest wyłączony"));
    });

    TestRunner.Add("Profiles: several stay open - the header link opens or closes one and keeps the others; actions and the self-refresh keep them", async () =>
    {
        await ResetPollerAsync();
        var browser = await SignedInAsync("poll-open@example.test", AppLanguage.Polish);
        var userId = await UserIdAsync("poll-open@example.test");
        var a = await ImportAsync(userId, PollProfile("A", ["2000091"]));
        var b = await ImportAsync(userId, PollProfile("B", ["2000092"]));
        var page = await browser.PageAsync("/");
        Assert.True(page.Contains($"href=\"/?profile={a}#profile-{a}\" aria-expanded=\"false\"") && page.Contains($"id=\"profile-body-{a}\" hidden"));

        // A is open; B's header opens B and keeps A.
        page = await browser.PageAsync($"/?profile={a}");
        Assert.True(page.Contains($"href=\"/?profile={b}&open={a}#profile-{b}\" aria-expanded=\"false\""));
        page = await browser.PageAsync($"/?profile={b}&open={a}");
        Assert.True(page.Contains($"<section class=\"profile open\" id=\"profile-{a}\">") && page.Contains($"<section class=\"profile open\" id=\"profile-{b}\">"));
        // Closing B keeps A (and stays at B's place); closing A keeps B in focus.
        Assert.True(page.Contains($"href=\"/?open={a}#profile-{b}\" aria-expanded=\"true\""));
        Assert.True(page.Contains($"href=\"/?profile={b}#profile-{a}\" aria-expanded=\"true\""));

        // "Check now" on A with B open: back with both open; the self-refresh keeps both.
        await FirstChecksAsync();
        page = await browser.PageAsync($"/?profile={b}&open={a}");
        Assert.True(page.Contains($"name=\"open\" value=\"{b}\""));
        Assert.Equal($"/?profile={a}&open={b}#profile-{a}", Location(await browser.PostFormAsync($"/profiles/{a}/check", page, null, new() { ["open"] = b.ToString() })));
        page = await browser.PageAsync($"/?profile={a}&open={b}");
        Assert.True(Regex.IsMatch(page, $"content=\"3; url=/\\?profile={a}&open={b}&refresh=\\d+#profile-{a}\""));
        Assert.True(page.Contains($"<section class=\"profile open\" id=\"profile-{a}\">") && page.Contains($"<section class=\"profile open\" id=\"profile-{b}\">"));

        // Disabling B keeps A open; deleting A keeps B open. Anything that is not an id is dropped.
        Assert.Equal($"/?profile={b}&open={a}#profile-{b}", Location(await browser.PostFormAsync($"/profiles/{b}/disable", page, null, new() { ["open"] = $"{a},nonsense" })));
        Assert.Equal($"/?open={b}", Location(await browser.PostFormAsync($"/profiles/{a}/delete", page, null, new() { ["open"] = b.ToString() })));
    });

    TestRunner.Add("Test e-mail: to the account's own address in its language, at most every 5 minutes, without a Cinema City request", async () =>
    {
        var browser = await SignedInAsync("poll-test@example.test", AppLanguage.English);
        var mark = app.Network.Requests.Count;
        var page = await browser.PageAsync("/Account/Manage");
        Assert.Equal("/Account/Manage", Location(await browser.PostFormAsync("/Account/Manage/TestMail", page, null, [])));
        var mail = MailsTo("poll-test@example.test").Single();
        Assert.Equal("[TEST] Seats available: Sample film", mail.Subject);
        Assert.True(mail.Text.Contains("Buy a ticket: https://www.cinema-city.pl/") && mail.Text.Contains("http://localhost/"));
        page = await browser.PageAsync("/Account/Manage");
        Assert.True(page.Contains("went to the local test inbox on this computer") && page.Contains("Test mode: the mail server runs on this computer"));

        await browser.PostFormAsync("/Account/Manage/TestMail", page, null, []);
        Assert.Equal(1, MailsTo("poll-test@example.test").Count);
        app.Time.Advance(TimeSpan.FromMinutes(5));
        await browser.PostFormAsync("/Account/Manage/TestMail", page, null, []);
        Assert.Equal(2, MailsTo("poll-test@example.test").Count);
        Assert.Equal(mark, app.Network.Requests.Count);
    });

    TestRunner.Add("Poller: when the request budget is used up, due screenings wait in order (none skipped) and an event is recorded", async () =>
    {
        await ResetPollerAsync();
        await SignedInAsync("poll-budget@example.test", AppLanguage.Polish);
        var ids = Enumerable.Range(2000061, 15).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        await ImportAsync(await UserIdAsync("poll-budget@example.test"), PollProfile("Budżet", ids));
        var report = await FirstChecksAsync();
        Assert.True(report.BudgetExhausted && report.Checked is > 0 and < 15 && report.Requests <= 20, report.ToString());
        Assert.Equal(1, (await EventsAsync(ServiceEventKind.BudgetExhausted)).Count);
        var notChecked = await WithService(async services => await services.GetRequiredService<AppDbContext>().WatchedScreenings.CountAsync(r => r.LastCheckedAt == null));
        Assert.Equal(15 - report.Checked, notChecked);

        app.Time.Advance(TimeSpan.FromMinutes(10)); // 20 requests' worth refilled
        await CycleAsync();
        notChecked = await WithService(async services => await services.GetRequiredService<AppDbContext>().WatchedScreenings.CountAsync(r => r.LastCheckedAt == null));
        Assert.Equal(0, notChecked);
    });

    TestRunner.Add("Poller: old log rows, history and events are deleted; disabled and ended profiles are not checked", async () =>
    {
        await ResetPollerAsync();
        await SignedInAsync("poll-old@example.test", AppLanguage.Polish);
        var userId = await UserIdAsync("poll-old@example.test");
        var kept = await ImportAsync(userId, PollProfile("Włączony", ["2000081"]));
        var off = await ImportAsync(userId, PollProfile("Wyłączony", ["2000082"]));
        var ended = await ImportAsync(userId, PollProfile("Zakończony", ["2000083"]));
        var now = app.Time.GetUtcNow();
        await WithService(async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            await services.GetRequiredService<ProfileService>().SetEnabledAsync(userId, off, false);
            (await db.Profiles.SingleAsync(p => p.Id == ended)).EndsAt = now - TimeSpan.FromMinutes(1);
            db.CheckLog.AddRange(
                new CheckLogEntry { At = now - TimeSpan.FromDays(31), Cycle = "old", Method = "GET", Host = "x", Endpoint = "x", Target = "/" },
                new CheckLogEntry { At = now - TimeSpan.FromDays(1), Cycle = "new", Method = "GET", Host = "x", Endpoint = "x", Target = "/" });
            db.Notifications.AddRange(
                new NotificationRecord { Id = Guid.NewGuid(), ProfileId = kept, CreatedAt = now - TimeSpan.FromDays(91), Subject = "old", Summary = "" },
                new NotificationRecord { Id = Guid.NewGuid(), ProfileId = kept, CreatedAt = now - TimeSpan.FromDays(1), Subject = "new", Summary = "" });
            db.ServiceEvents.Add(new ServiceEvent { At = now - TimeSpan.FromDays(91), Kind = ServiceEventKind.Resumed, Message = "old" });
            await db.SaveChangesAsync();
            return 0;
        });

        await CycleAsync();
        var (logs, notifications, events, watched) = await WithService(async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            return (await db.CheckLog.Select(e => e.Cycle).ToListAsync(), await db.Notifications.Select(n => n.Subject).ToListAsync(),
                await db.ServiceEvents.CountAsync(), await db.WatchedScreenings.Select(r => r.Id).ToListAsync());
        });
        Assert.SequenceEqual(["new"], logs);
        Assert.SequenceEqual(["new"], notifications);
        Assert.Equal(0, events);
        Assert.SequenceEqual(["2000081"], watched);
    });
    TestRunner.Add("Background loop: records its start, runs a cycle at once, wakes up early on \"Check now\", stops cleanly", async () =>
    {
        await ResetPollerAsync();
        async Task<DateTimeOffset?> LastCycleAsync() =>
            await WithService(async services => await services.GetRequiredService<AppDbContext>().PollerState.AsNoTracking().Select(s => s.LastCycleAt).SingleOrDefaultAsync());
        async Task<DateTimeOffset?> WaitForCycleAfterAsync(DateTimeOffset? after)
        {
            for (var i = 0; i < 100; i++)
            {
                if (await LastCycleAsync() is { } last && (after is null || last > after))
                    return last;
                await Task.Delay(50);
            }
            return null;
        }

        var loop = ActivatorUtilities.CreateInstance<PollerHostedService>(app.Services);
        await loop.StartAsync(CancellationToken.None);
        try
        {
            var first = await WaitForCycleAfterAsync(null);
            Assert.True(first is not null, "no cycle after the start");
            app.Time.Advance(TimeSpan.FromSeconds(1));
            app.Services.GetRequiredService<PollerSignal>().Wake(); // the tick is 30 s; the signal must not wait for it
            Assert.True(await WaitForCycleAfterAsync(first) is not null, "the signal did not wake the loop");
        }
        finally
        {
            await loop.StopAsync(CancellationToken.None);
        }
        Assert.Equal(1, (await EventsAsync(ServiceEventKind.PollerStarted)).Count);
        Assert.True(File.Exists(Path.Combine(app.FakeFolder, FakeCinemaCityHandler.ReadmeFile)));
    });
}
