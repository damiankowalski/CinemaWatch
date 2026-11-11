using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CinemaWatch.Core;
using CinemaWatch.Saas.Tests;
using CinemaWatch.Server.Accounts;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Operations;
using CinemaWatch.Server.Polling;
using CinemaWatch.Server.Profiles;
using CinemaWatch.Server.Wizard;
using CinemaWatch.Tests;
using CinemaWatch.Web;
using CinemaWatch.Web.Components.Account;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

static PollerSettings Effective(PollerOptions? poller = null, LimitOptions? limits = null) =>
    new(Microsoft.Extensions.Options.Options.Create(poller ?? new PollerOptions()), Microsoft.Extensions.Options.Options.Create(limits ?? new LimitOptions()));

TestRunner.Add("Request budget: a sixth of the hourly budget saved up at most, refilled steadily; a budget changed in the panel applies at once", () =>
{
    var time = new ManualTime(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
    var settings = Effective(new PollerOptions { RequestsPerHour = 120 });
    var budget = new RequestBudget(settings, time);
    Assert.Equal(20, budget.Available);
    Assert.True(budget.TryTake(20));
    Assert.True(!budget.TryTake());
    time.Advance(TimeSpan.FromSeconds(30)); // 120 an hour = one every 30 seconds
    Assert.Equal(1, budget.Available);
    time.Advance(TimeSpan.FromHours(5));
    Assert.Equal(20, budget.Available);

    settings.Apply(new PollerSettingsRow { RequestsPerHour = 300 });
    Assert.Equal(50, budget.Capacity);
    time.Advance(TimeSpan.FromHours(1));
    Assert.Equal(50, budget.Available);
    settings.Apply(new PollerSettingsRow { RequestsPerHour = 30 }); // a smaller bucket drops what no longer fits
    Assert.Equal((10, 10), (budget.Capacity, budget.Available));
});

TestRunner.Add("Poller settings in the panel: empty or the default = no override; ranges, numbers and pairs checked; the configuration may lie outside the ranges", () =>
{
    var config = new PollerOptions();
    var limits = new LimitOptions();
    static Dictionary<TuningKey, string?> Form(params (TuningKey Key, string? Value)[] values) => values.ToDictionary(v => v.Key, v => v.Value);

    var parse = PollerTuning.Parse(config, limits, Form((TuningKey.RequestsPerHour, " 90 "), (TuningKey.MinIntervalMinutes, "10"), (TuningKey.RequestGapMinSeconds, "2,5"), (TuningKey.JitterPercent, "")), false);
    Assert.True(parse.IsValid);
    Assert.Equal((90, null, 2.5, null), (parse.Row.RequestsPerHour, parse.Row.MinIntervalMinutes, parse.Row.RequestGapMinSeconds, parse.Row.JitterPercent));
    Assert.Equal(null, parse.Row.ReuseScreeningDetails);
    var (poller, effectiveLimits) = PollerTuning.Apply(config, limits, parse.Row);
    Assert.Equal((90, 10, 2.5, 6.0), (poller.RequestsPerHour, effectiveLimits.MinIntervalMinutes, poller.RequestGapMinSeconds, poller.RequestGapMaxSeconds));
    Assert.Equal(120, config.RequestsPerHour); // the configuration itself is never changed

    // Every problem at once: outside the range, not a (whole) number.
    parse = PollerTuning.Parse(config, limits, Form((TuningKey.MinIntervalMinutes, "4"), (TuningKey.RequestsPerHour, "301"), (TuningKey.ManualFreshMinutes, "1.5"), (TuningKey.JitterPercent, "abc")), false);
    Assert.SequenceEqual(
        [(TuningKey.MinIntervalMinutes, SettingProblemKind.OutOfRange), (TuningKey.RequestsPerHour, SettingProblemKind.OutOfRange),
         (TuningKey.ManualFreshMinutes, SettingProblemKind.NotANumber), (TuningKey.JitterPercent, SettingProblemKind.NotANumber)],
        parse.Problems.Select(p => (p.Key, p.Kind)));

    // Pairs: the upper value must not be below the lower one, an empty field counting as the configuration's value.
    parse = PollerTuning.Parse(config, limits, Form((TuningKey.RequestGapMinSeconds, "10"), (TuningKey.RequestGapMaxSeconds, "8"), (TuningKey.FirstBlockPauseMinutes, "720"), (TuningKey.MaxBlockPauseMinutes, "60")), false);
    Assert.SequenceEqual([(TuningKey.RequestGapMaxSeconds, SettingProblemKind.BelowMinimum), (TuningKey.MaxBlockPauseMinutes, SettingProblemKind.BelowMinimum)],
        parse.Problems.Select(p => (p.Key, p.Kind)));
    Assert.True(!PollerTuning.Parse(config, limits, Form((TuningKey.RequestGapMinSeconds, "7")), false).IsValid); // above the configured "to" (6)

    // Development pauses only 2 minutes after a block: the panel shows it and keeps it, but cannot set such a value.
    var development = new PollerOptions { FirstBlockPauseMinutes = 2, MaxBlockPauseMinutes = 10 };
    parse = PollerTuning.Parse(development, limits, Form((TuningKey.FirstBlockPauseMinutes, "2"), (TuningKey.MaxBlockPauseMinutes, "10")), false);
    Assert.True(parse.IsValid && parse.Row.IsEmpty);
    Assert.SequenceEqual([(TuningKey.FirstBlockPauseMinutes, SettingProblemKind.OutOfRange)],
        PollerTuning.Parse(development, limits, Form((TuningKey.FirstBlockPauseMinutes, "3")), false).Problems.Select(p => (p.Key, p.Kind)));

    // The switch: stored only when it differs from the configuration.
    Assert.Equal(true, PollerTuning.Parse(config, limits, Form(), true).Row.ReuseScreeningDetails);
    Assert.True(PollerTuning.Parse(config, limits, Form(), false).Row.IsEmpty);

    // Stored values outside today's range are ignored; a pair the wrong way round falls back to the configuration.
    (poller, effectiveLimits) = PollerTuning.Apply(config, limits, new PollerSettingsRow { RequestsPerHour = 5000, MinIntervalMinutes = 15 });
    Assert.Equal((120, 15), (poller.RequestsPerHour, effectiveLimits.MinIntervalMinutes));
    (poller, _) = PollerTuning.Apply(config, limits, new PollerSettingsRow { RequestGapMinSeconds = 50 });
    Assert.Equal(3.0, poller.RequestGapMinSeconds);

    var settings = Effective();
    Assert.SequenceEqual([new SettingChange("MinIntervalMinutes", "10", "15"), new SettingChange("RequestsPerHour", "120", "90")],
        settings.ChangesTo(new PollerSettingsRow { RequestsPerHour = 90, MinIntervalMinutes = 15 }));
    Assert.Equal("MinIntervalMinutes 10 → 15; RequestsPerHour 120 → 90",
        PollerTuning.Describe(settings.ChangesTo(new PollerSettingsRow { RequestsPerHour = 90, MinIntervalMinutes = 15 })));
    settings.Apply(new PollerSettingsRow { MinIntervalMinutes = 15 });
    Assert.Equal((15, 15, 20), (settings.Limits.MinIntervalMinutes, settings.Limits.EffectiveInterval(10), settings.Limits.EffectiveInterval(20)));
    Assert.True(settings.IsOverridden(PollerTuning.Get(TuningKey.MinIntervalMinutes)) && !settings.IsOverridden(PollerTuning.Get(TuningKey.RequestsPerHour)));
    Assert.Equal(10.0, settings.DefaultOf(PollerTuning.Get(TuningKey.MinIntervalMinutes)));
});

TestRunner.Add("Mail errors kept for the operator lose the addresses they quote", () =>
{
    Assert.Equal("5.1.1 <[e-mail]>: Recipient address rejected", CinemaWatch.Server.Mail.MailErrors.WithoutAddresses("5.1.1 <anna.nowak+kino@example.com>: Recipient address rejected"));
    Assert.Equal("Mailbox [e-mail] full; [e-mail] unknown", CinemaWatch.Server.Mail.MailErrors.WithoutAddresses("Mailbox anna@example.com full; ben@example.org unknown"));
    Assert.Equal("Connection refused (localhost:2525)", CinemaWatch.Server.Mail.MailErrors.WithoutAddresses("Connection refused (localhost:2525)"));
});

TestRunner.Add("Heartbeat settings: empty = off; only an https address without a user name or password; interval 1-60 minutes", () =>
{
    Assert.True(new HeartbeatOptions().IsValid() && !new HeartbeatOptions().IsEnabled && new HeartbeatOptions().Target is null);
    Assert.True(new HeartbeatOptions { Url = " https://hc-ping.com/0f3c " }.IsValid());
    Assert.Equal("hc-ping.com", new HeartbeatOptions { Url = "https://hc-ping.com/0f3c" }.Target!.Host);
    Assert.True(!new HeartbeatOptions { Url = "http://hc-ping.com/0f3c" }.IsValid());
    Assert.True(!new HeartbeatOptions { Url = "https://user:secret@hc-ping.com/0f3c" }.IsValid());
    Assert.True(!new HeartbeatOptions { Url = "hc-ping.com/0f3c" }.IsValid());
    Assert.True(!new HeartbeatOptions { IntervalMinutes = 0 }.IsValid() && !new HeartbeatOptions { IntervalMinutes = 61 }.IsValid());
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

TestRunner.Add("Public pages: /admin only exactly as written (case, encoding, double slash, dot segments); other paths untouched", () =>
{
    foreach (var (path, raw) in new (string, string?)[]
             {
                 ("/admin", "/admin"), ("/admin/users", "/admin/users?delete=x"), ("/admin/", "/admin/"), ("/admin", null),
                 ("/admin/users", "http://host/admin/users"), ("/", "/"), ("/Account/Login", "/account/login"), ("/administrator", "/Administrator"),
             })
        Assert.True(PublicSurface.IsAdminPathAsWritten(new PathString(path), raw), $"{path} sent as {raw} should pass");
    foreach (var (path, raw) in new (string, string?)[]
             {
                 ("/ADMIN", "/ADMIN"), ("/Admin/users", "/Admin/users"), ("/admin", "/%61dmin"), ("/admin", "/%41dmin"), ("//admin", "//admin"),
                 ("/admin/users", "/x/../admin/users"), ("/admin", "http://host/%61dmin"), ("/aDmIn", null),
             })
        Assert.True(!PublicSurface.IsAdminPathAsWritten(new PathString(path), raw), $"{path} sent as {raw} should be refused");
    Assert.Equal("User-agent: *\nDisallow: /\n", PublicSurface.RobotsText);
});

TestRunner.Add("Password help limits: one e-mail per account per 15 minutes, 20 a day for all accounts together, a rolling day", () =>
{
    var clock = new ManualTime(DateTimeOffset.Parse("2026-11-07T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    var limits = new PasswordHelpLimits(clock);
    Assert.Equal(PasswordHelpDecision.Allowed, limits.TryReserve("a"));
    Assert.Equal(PasswordHelpDecision.TooSoonForAccount, limits.TryReserve("a"));
    clock.Advance(TimeSpan.FromMinutes(14));
    Assert.Equal(PasswordHelpDecision.TooSoonForAccount, limits.TryReserve("a"));
    clock.Advance(TimeSpan.FromMinutes(1));
    Assert.Equal(PasswordHelpDecision.Allowed, limits.TryReserve("a"));
    for (var i = 0; i < PasswordHelpLimits.PerDay - 2; i++)
        Assert.Equal(PasswordHelpDecision.Allowed, limits.TryReserve($"u{i}"));
    Assert.Equal(PasswordHelpDecision.DailyLimit, limits.TryReserve("fresh"));
    // Forgetting a (deleted) account does not give back the day's e-mails.
    limits.Forget("a");
    Assert.Equal(PasswordHelpDecision.DailyLimit, limits.TryReserve("a"));
    // 24 hours after the first e-mail its slot is free again, and only that one.
    clock.Advance(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(15));
    Assert.Equal(PasswordHelpDecision.Allowed, limits.TryReserve("fresh"));
    Assert.Equal(PasswordHelpDecision.DailyLimit, limits.TryReserve("fresh2"));
});

TestRunner.Add("E-mail about a deleted account: both languages, no link, the backups' 31 days", () =>
{
    using (Localizer.Use(AppLanguage.Polish))
    {
        var mail = AccountMails.AccountDeleted("a@example.test");
        Assert.Equal("Twoje konto CinemaWatch zostało usunięte", mail.Subject);
        Assert.True(mail.Text.Contains("usunął Twoje konto") && mail.Text.Contains("31 dniach") && !mail.Text.Contains("http"));
        Assert.True(mail.Html.Contains("usunął Twoje konto"));
    }
    using (Localizer.Use(AppLanguage.English))
        Assert.Equal("Your CinemaWatch account has been deleted", AccountMails.AccountDeleted("a@example.test").Subject);
});

// ---------------------------------------------------------------------------------------------------------------
// Wizard data on demand (docs/SAAS.md §5.2, stage 6.4.2) - without a database
// ---------------------------------------------------------------------------------------------------------------

TestRunner.Add("Wizard settings: the decided defaults are valid; a copy shares nothing; the panel's ranges; the configuration is never changed", () =>
{
    var config = new PollerOptions();
    Assert.True(config.IsValid());
    var wizard = config.Wizard;
    Assert.Equal((120, 90, 90, 1.0), (wizard.RequestsPerHour, wizard.Burst, wizard.AccountRequestsPerHour, wizard.GapSeconds));
    Assert.Equal((30, 180, 1440, 10, 30), (wizard.RepertoireNearMinutes, wizard.RepertoireLaterMinutes, wizard.DetailsMinutes, wizard.FreeSeatsMinutes, wizard.KeepResultMinutes));
    Assert.True(!new PollerOptions { Wizard = new() { Burst = 5 } }.IsValid());
    Assert.True(!new PollerOptions { Wizard = new() { RepertoireNearMinutes = 60, RepertoireLaterMinutes = 30 } }.IsValid());

    var copy = config.Copy();
    copy.Wizard.RequestsPerHour = 10;
    Assert.Equal(120, config.Wizard.RequestsPerHour);

    var limits = new LimitOptions();
    var form = new Dictionary<TuningKey, string?> { [TuningKey.WizardRequestsPerHour] = "60", [TuningKey.WizardGapSeconds] = "2,5", [TuningKey.WizardBurst] = "90" };
    var parse = PollerTuning.Parse(config, limits, form, false);
    Assert.True(parse.IsValid);
    Assert.Equal((60, null, null, 2.5), (parse.Row.WizardRequestsPerHour, parse.Row.WizardBurst, parse.Row.WizardAccountRequestsPerHour, parse.Row.WizardGapSeconds));
    var (poller, _) = PollerTuning.Apply(config, limits, parse.Row);
    Assert.Equal((60, 2.5, 120), (poller.Wizard.RequestsPerHour, poller.Wizard.GapSeconds, config.Wizard.RequestsPerHour));
    Assert.Equal("WizardRequestsPerHour 120 → 60; WizardGapSeconds 1 → 2.5", PollerTuning.Describe(PollerTuning.Changes((config, limits), (poller, limits))));

    var bad = PollerTuning.Parse(config, limits, new Dictionary<TuningKey, string?>
    {
        [TuningKey.WizardRequestsPerHour] = "5", [TuningKey.WizardAccountRequestsPerHour] = "1000", [TuningKey.WizardGapSeconds] = "0,5",
    }, false);
    Assert.SequenceEqual([TuningKey.WizardRequestsPerHour, TuningKey.WizardAccountRequestsPerHour, TuningKey.WizardGapSeconds], bad.Problems.Select(p => p.Key));
});

TestRunner.Add("Wizard budgets: its own bucket (90 saved up, 120 an hour); a loading reserves from the wizard and the account together and gives back what it did not use", () =>
{
    var time = new ManualTime(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
    var settings = Effective();
    var wizard = new WizardBudget(settings, time).Bucket;
    var accounts = new AccountBudgets(settings, time);
    Assert.Equal((90, 120, 90), (wizard.Available, wizard.PerHour, accounts.For("a").Capacity));
    Assert.True(ReferenceEquals(accounts.For("a"), accounts.For("a")));

    var tokens = new LoadingTokens(wizard, accounts.For("a"));
    Assert.True(tokens.TryReserve(84)); // the largest desktop search
    Assert.Equal((6, 6, 1), (wizard.Available, accounts.For("a").Available, accounts.ActiveAccounts));
    for (var i = 0; i < 80; i++)
        Assert.True(tokens.TryTake());
    tokens.Release(); // 4 answers came from the cache after all
    Assert.Equal((10, 10), (wizard.Available, accounts.For("a").Available));

    // Another account fits in what is left of the wizard's budget, not more; the wait is the refill time.
    var other = new LoadingTokens(wizard, accounts.For("b"));
    Assert.True(!other.TryReserve(11));
    Assert.Equal((10, 90), (wizard.Available, accounts.For("b").Available)); // a refused reservation takes nothing
    Assert.Equal(TimeSpan.FromSeconds(30), wizard.WaitFor(11)); // 120 an hour = one every 30 s
    Assert.Equal(null, wizard.WaitFor(91)); // never: more than the bucket holds
    Assert.True(other.TryReserve(10));

    // Beyond the reservation a request takes from both buckets directly, and says which one ran dry.
    Assert.True(other.TryTake());
    for (var i = 0; i < 9; i++)
        Assert.True(other.TryTake());
    Assert.True(!other.TryTake());
    Assert.Equal(WizardStopReason.BudgetUsedUp, other.Shortage);
    time.Advance(TimeSpan.FromMinutes(5)); // 10 tokens back in the wizard's bucket
    var account = accounts.For("c");
    Assert.True(account.TryTake(90));
    var third = new LoadingTokens(wizard, account);
    Assert.True(!third.TryTake());
    Assert.Equal(WizardStopReason.AccountLimit, third.Shortage);
    Assert.Equal(10, wizard.Available); // the account's refusal took nothing from the wizard's budget
});

TestRunner.Add("Wizard cache: identical loads at the same time go out once; a failure is not kept; the loader's own budget problem lets a waiter load; kept for its time", async () =>
{
    var time = new ManualTime(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
    var cache = new CinemaCityCache(time);
    var loads = 0;
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    async Task<string> Slow()
    {
        Interlocked.Increment(ref loads);
        await gate.Task;
        return "answer";
    }
    var first = cache.GetAsync("k", _ => TimeSpan.FromMinutes(30), Slow, CancellationToken.None);
    var second = cache.GetAsync("k", _ => TimeSpan.FromMinutes(30), Slow, CancellationToken.None);
    Assert.True(!cache.TryPeek<string>("k", out _)); // still loading
    gate.SetResult();
    Assert.Equal(("answer", "answer", 1), (await first, await second, loads));
    Assert.True(cache.TryPeek<string>("k", out var peeked) && peeked == "answer");
    Assert.Equal("answer", await cache.GetAsync("k", _ => TimeSpan.FromMinutes(30), Slow, CancellationToken.None));
    Assert.Equal((1, 1L, 1L, 1L), (cache.Stats.Entries, cache.Stats.Hits, cache.Stats.Shared, cache.Stats.Loads));
    time.Advance(TimeSpan.FromMinutes(31));
    Assert.True(!cache.TryPeek<string>("k", out _));
    gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    gate.SetResult();
    Assert.Equal("answer", await cache.GetAsync("k", _ => TimeSpan.FromMinutes(30), Slow, CancellationToken.None));
    Assert.Equal(2, loads);

    // A failure goes to whoever waited and is not kept.
    var failing = 0;
    Task<string> Fail()
    {
        failing++;
        throw new CinemaCityException(HttpStatusCode.InternalServerError, "/x");
    }
    for (var i = 0; i < 2; i++)
    {
        var threw = false;
        try
        {
            await cache.GetAsync("f", _ => TimeSpan.FromMinutes(30), Fail, CancellationToken.None);
        }
        catch (CinemaCityException)
        {
            threw = true;
        }
        Assert.True(threw);
    }
    Assert.Equal(2, failing);

    // The loader ran out of its own budget: the waiter loads itself instead of failing.
    var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var owner = cache.GetAsync<string>("b", _ => TimeSpan.FromMinutes(1), async () =>
    {
        await hold.Task;
        throw new BudgetExhaustedException();
    }, CancellationToken.None);
    var waiter = cache.GetAsync("b", _ => TimeSpan.FromMinutes(1), () => Task.FromResult("mine"), CancellationToken.None);
    hold.SetResult();
    var ownerFailed = false;
    try
    {
        await owner;
    }
    catch (BudgetExhaustedException)
    {
        ownerFailed = true;
    }
    Assert.True(ownerFailed);
    Assert.Equal("mine", await waiter);

    // Zero keeping time: shared only while it runs.
    Assert.Equal("x", await cache.GetAsync("z", _ => TimeSpan.Zero, () => Task.FromResult("x"), CancellationToken.None));
    Assert.True(!cache.TryPeek<string>("z", out _));
});

TestRunner.Add("Wizard cache: the repertoire of today and tomorrow is kept 30 minutes, later days 3 hours - never past the moment they become tomorrow", () =>
{
    var options = new WizardOptions();
    // 9 Nov 2026 12:00 UTC = 13:00 in Warsaw (winter time).
    var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    Assert.Equal(new DateOnly(2026, 11, 9), CinemaCityCache.PolishToday(now));
    Assert.Equal(new DateOnly(2026, 11, 10), CinemaCityCache.PolishToday(new DateTimeOffset(2026, 11, 9, 23, 30, 0, TimeSpan.Zero)));
    Assert.Equal(TimeSpan.FromMinutes(30), CinemaCityCache.RepertoireKeep(new DateOnly(2026, 11, 9), now, options));
    Assert.Equal(TimeSpan.FromMinutes(30), CinemaCityCache.RepertoireKeep(new DateOnly(2026, 11, 10), now, options));
    Assert.Equal(TimeSpan.FromHours(3), CinemaCityCache.RepertoireKeep(new DateOnly(2026, 11, 20), now, options));
    // The 11th becomes "tomorrow" at Polish midnight (23:00 UTC): at 21:30 UTC it is kept until 23:30 UTC, not 00:30.
    Assert.Equal(TimeSpan.FromHours(2), CinemaCityCache.RepertoireKeep(new DateOnly(2026, 11, 11), new DateTimeOffset(2026, 11, 9, 21, 30, 0, TimeSpan.Zero), options));
});

TestRunner.Add("Wizard requests: cinemas from the IMAX list, from Polish today, 31 days for one cinema and 14 for several, a profile's number of screenings, ids as at import", () =>
{
    var today = new DateOnly(2026, 11, 9);
    WizardRequestProblem? Check(WizardRequest request) => WizardRequests.Check(request, today, 20);
    var all = Locations.ImaxCinemas.Select(c => c.Id).ToList();
    Assert.Equal(null, Check(new RepertoireRequest(["1060"], today, today.AddDays(30))));
    Assert.Equal(null, Check(new RepertoireRequest(all, today, today.AddDays(13))));
    Assert.Equal(WizardRequestProblem.RangeTooLong, Check(new RepertoireRequest(["1060"], today, today.AddDays(31))));
    Assert.Equal(WizardRequestProblem.RangeTooLong, Check(new RepertoireRequest(["1060", "1064"], today, today.AddDays(14))));
    Assert.Equal(WizardRequestProblem.NoCinema, Check(new RepertoireRequest([], today, today)));
    Assert.Equal(WizardRequestProblem.UnknownCinema, Check(new RepertoireRequest(["1001"], today, today)));
    Assert.Equal(WizardRequestProblem.UnknownCinema, Check(new RepertoireRequest(["1060", "1060"], today, today)));
    Assert.Equal(WizardRequestProblem.DateInPast, Check(new RepertoireRequest(["1060"], today.AddDays(-1), today)));
    Assert.Equal(WizardRequestProblem.DatesReversed, Check(new RepertoireRequest(["1060"], today.AddDays(2), today)));
    Assert.Equal(null, Check(new ScreeningsRequest(["1765592", "106026121613"])));
    Assert.Equal(WizardRequestProblem.NoScreening, Check(new ScreeningsRequest([])));
    Assert.Equal(WizardRequestProblem.TooManyScreenings, Check(new ScreeningsRequest(Enumerable.Range(1, 21).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList())));
    Assert.Equal(WizardRequestProblem.BadScreeningId, Check(new ScreeningsRequest(["../1"])));
    Assert.Equal(WizardRequestProblem.BadScreeningId, Check(new ScreeningsRequest(["1", "1"])));

    // Days first, then the cinemas in city order (as the desktop lists them).
    Assert.SequenceEqual(["repertoire:1064:2026-11-09", "repertoire:1060:2026-11-09", "repertoire:1064:2026-11-10", "repertoire:1060:2026-11-10"],
        WizardJobs.Items(new RepertoireRequest(["1060", "1064"], today, today.AddDays(1))).Select(i => i.Key));
});

TestRunner.Add("Wizard state: read from a form or an address with broken values dropped, written back the same; the desktop's date checks and messages; the selection's limits", () =>
{
    using var _ = Localizer.Use(AppLanguage.Polish);
    var today = new DateOnly(2026, 11, 11);
    var values = new Dictionary<string, string?[]>
    {
        ["city"] = ["1064"], ["from"] = ["2026-11-12"], ["to"] = ["2026-11-14"], ["film"] = ["8105s2r"],
        ["s"] = ["a1,b2", "b2", "bad id", null, "c3"], ["max"] = ["2"],
    };
    var state = WizardState.Read(name => values.GetValueOrDefault(name) ?? []);
    Assert.Equal(("1064", new DateOnly(2026, 11, 12), (DateOnly?)new DateOnly(2026, 11, 14), (string?)"8105s2r", 2),
        (state.City, state.From!.Value, state.To, state.FilmId, state.Furthest));
    Assert.SequenceEqual(["a1", "b2", "c3"], state.ScreeningIds);
    Assert.Equal("city=1064&from=2026-11-12&to=2026-11-14&film=8105s2r&s=a1%2Cb2%2Cc3&max=2", state.Query());
    var fields = state.Fields().ToDictionary(f => f.Key, f => f.Value);
    Assert.Equal(state.Query(), WizardState.Read(name => fields.TryGetValue(name, out var value) ? [value] : []).Query());
    Assert.Equal("city=1064&from=2026-11-12&to=2026-11-14&film=8105s2r&max=2", string.Join("&", state.Fields(withScreenings: false).Select(f => $"{f.Key}={f.Value}")));
    Assert.SequenceEqual([new DateOnly(2026, 11, 12), new DateOnly(2026, 11, 13), new DateOnly(2026, 11, 14)], state.Dates);
    Assert.Equal(null, state.DatesProblem(today));
    var request = state.Repertoire();
    Assert.Equal(("1064", 3), (request.CinemaIds.Single(), request.Days));

    // Unknown city → Sadyba; a date not in yyyy-MM-dd → missing; a broken film id → none; the step is clamped.
    var broken = WizardState.Read(name => name switch { "city" => ["9999"], "from" => ["12.11.2026"], "film" => ["x y"], "max" => ["7"], _ => [] });
    Assert.Equal(("1060", (DateOnly?)null, (string?)null, 4), (broken.City, broken.From, broken.FilmId, broken.Furthest));
    Assert.Equal("Wybierz datę seansu.", broken.DatesProblem(today));
    Assert.Equal(0, broken.Dates.Count);
    // "To" equal to "From" is one day.
    Assert.Equal(null, WizardState.Read(name => name switch { "from" or "to" => ["2026-11-12"], _ => [] }).To);
    Assert.Equal(("1060", today), (WizardState.New(today).City, WizardState.New(today).From!.Value));

    Assert.Equal("Data nie może być z przeszłości.", (state with { From = today.AddDays(-1) }).DatesProblem(today));
    Assert.Equal("Data końcowa nie może być wcześniejsza niż początkowa.", (state with { To = new DateOnly(2026, 11, 11) }).DatesProblem(today));
    Assert.Equal(null, (state with { From = today, To = today.AddDays(30) }).DatesProblem(today));
    Assert.Equal("Zakres może obejmować maksymalnie 31 dni.", (state with { From = today, To = today.AddDays(31) }).DatesProblem(today));
    var all = state with { City = WizardState.AllCities, From = today, To = today.AddDays(13) };
    Assert.Equal((6, (string?)null), (all.CinemaIds.Count, all.DatesProblem(today)));
    Assert.Equal("Dla wszystkich miast zakres może obejmować maksymalnie 14 dni.", (all with { To = today.AddDays(14) }).DatesProblem(today));

    Assert.Equal("Zaznacz co najmniej jeden seans.", (state with { ScreeningIds = [] }).SelectionProblem(2));
    Assert.Equal("Profil może pilnować najwyżej 2 seansów (zaznaczone: 3).", state.SelectionProblem(2));
    Assert.Equal(null, state.SelectionProblem(3));
});

TestRunner.Add("Wizard step 2: films with IMAX screenings without dubbing that have not started, by name; the chosen film's screenings by day, cinemas in city order", () =>
{
    using var _ = Localizer.Use(AppLanguage.Polish);
    var now = new DateTime(2026, 11, 12, 15, 0, 0);
    static Screening S(string id, string film, string cinema, int day, int hour, params string[] attributes) =>
        new(id, film, cinema, new DateTime(2026, 11, day, hour, 0, 0), "IMAX", attributes, ["en"], ["pl"], [], false, 0.5, "https://tickets.cinema-city.pl/api/order/" + id);
    Film[] films = [new("f1", "Zz film", 120, null), new("f2", "Avatar", 180, null)];
    var days = new List<DayRepertoire>
    {
        new(new DateOnly(2026, 11, 12), films,
            [S("1", "f1", "1060", 12, 14, "imax"), S("2", "f1", "1060", 12, 18, "imax"), S("3", "f1", "1060", 12, 20, "imax", "dubbed"), S("4", "f2", "1060", 12, 21, "imax"), S("5", "f2", "1060", 12, 22)]),
        new(new DateOnly(2026, 11, 12), films, [S("6", "f1", "1065", 12, 19, "imax")]),
        new(new DateOnly(2026, 11, 13), films, [S("7", "f1", "1060", 13, 17, "imax")]),
    };

    var choices = ScreeningChoices.Build(days, ScreeningFilter.ImaxWithoutDubbing, now, null);
    Assert.SequenceEqual([("f2", "Avatar", 1), ("f1", "Zz film", 3)], choices.Films.Select(f => (f.FilmId, f.Name, f.ScreeningCount)));
    Assert.Equal("f2", choices.Film!.FilmId); // the first by name
    choices = ScreeningChoices.Build(days, ScreeningFilter.ImaxWithoutDubbing, now, "f1");
    // 14:00 has started, 20:00 is dubbed; on the 12th Katowice comes before Warszawa (city order of the IMAX list).
    Assert.SequenceEqual(["6", "2", "7"], choices.Screenings.Select(s => s.Id));
    Assert.SequenceEqual([2, 1], choices.Days.Select(d => d.Cinemas.Count));
    Assert.Equal(("Katowice", true), (choices.Days[0].Cinemas[0].Cinema!.City, choices.ShowCinemas));
    Assert.Equal("f2", ScreeningChoices.Build(days, ScreeningFilter.ImaxWithoutDubbing, now, "gone").Film!.FilmId);

    var one = ScreeningChoices.Build([days[0], days[2]], ScreeningFilter.ImaxWithoutDubbing, now, "f1");
    Assert.Equal((false, 2), (one.ShowCinemas, one.Screenings.Count()));
    var none = ScreeningChoices.Build([], ScreeningFilter.ImaxWithoutDubbing, now, "f1");
    Assert.Equal((0, (FilmChoice?)null, 0), (none.Films.Count, none.Film, none.Days.Count));
    Assert.Equal("Czwartek, 12 listopada 2026", ScreeningText.DayHeader(new DateOnly(2026, 11, 12)));
    Assert.Equal(("EN · napisy PL", "50% miejsc wolnych"), (ScreeningText.Languages(days[0].Screenings[1]), ScreeningText.Availability(days[0].Screenings[1])));
});

TestRunner.Add("Fake Cinema City: the repertoire of any cinema and day is the captured Sadyba day moved there, with ids made from the cinema, the day and the position", async () =>
{
    var folder = Path.Combine(Path.GetTempPath(), "cinemawatch-fake-" + Guid.NewGuid().ToString("N")[..8]);
    try
    {
        using var client = new CinemaCityClient(new FakeCinemaCityHandler(folder));
        var day = await client.GetRepertoireAsync("1064", new DateOnly(2027, 1, 5));
        Assert.Equal((2, 25), (day.Films.Count, day.Screenings.Count));
        Assert.True(day.Screenings.All(s => s.CinemaId == "1064" && DateOnly.FromDateTime(s.StartsAt) == new DateOnly(2027, 1, 5)));
        Assert.Equal(25, day.Screenings.Select(s => s.Id).Distinct().Count());
        var imax = day.Screenings.Where(s => ScreeningFilter.ImaxWithoutDubbing.Matches(s)).ToList();
        Assert.SequenceEqual(["106427010506", "106427010513", "106427010519", "106427010524"], imax.Select(s => s.Id));
        Assert.Equal((new TimeOnly(16, 0), "https://tickets.cinema-city.pl/api/order/106427010513?lang=pl"),
            (TimeOnly.FromDateTime(imax[1].StartsAt), imax[1].BookingLink));
        Assert.Equal("106427010513", FakeCinemaCityHandler.FakeScreeningId("1064", new DateOnly(2027, 1, 5), 13));
        Assert.Equal(13, (await client.GetPresentationAsync("106427010513")).VenueId); // the fake's hall for every screening
    }
    finally
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }
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

// The text of the page's status message (StatusMessage.razor), or "" without one.
static string StatusText(string page) =>
    Regex.Match(page, "<p class=\"status [^\"]*\" role=\"[^\"]*\">(.*?)</p>", RegexOptions.Singleline).Groups[1].Value;

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
            ["__EFMigrationsHistory", "check_log", "component_status", "data_protection_keys", "notification_state", "notifications",
             "poller_settings", "poller_state", "profiles", "seat_plans", "service_events", "user_claims", "user_logins", "user_tokens",
             "users", "watched_screenings"],
            await database.TablesAsync());
    });

    TestRunner.Add("Database: missing migrations are refused at start-up unless Saas:MigrateDatabaseOnStart applies them (an empty database gets all)", async () =>
    {
        await using var empty = await database.CreateEmptySiblingAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseCinemaWatchDatabase(empty.ConnectionString);
        await using var db = new AppDbContext(options.Options);
        var all = db.Database.GetMigrations().ToList();
        Assert.True(all.Count > 0);

        string? refusal = null;
        try
        {
            await db.EnsureMigratedAsync(applyMissing: false);
        }
        catch (InvalidOperationException ex)
        {
            refusal = ex.Message;
        }
        Assert.True(refusal is not null && refusal.Contains($"missing {all.Count} migration(s)") && refusal.Contains("Saas:MigrateDatabaseOnStart"), refusal ?? "not refused");
        Assert.Equal(0, (await db.Database.GetAppliedMigrationsAsync()).Count());

        Assert.SequenceEqual(all, await db.EnsureMigratedAsync(applyMissing: true));
        Assert.Equal(0, (await db.EnsureMigratedAsync(applyMissing: false)).Count); // up to date: nothing to do, no refusal
        Assert.SequenceEqual(all, await db.Database.GetAppliedMigrationsAsync());
    });

    TestRunner.Add("Behind the proxy: health check without sign-in; forwarded HTTPS only when trusted; an https BaseUrl makes the sign-in cookies Secure", async () =>
    {
        // Health check: anonymous, plain text, no redirect to the sign-in page.
        var anonymous = app.CreateBrowser();
        var health = await anonymous.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ok", await health.Content.ReadAsStringAsync());

        // The form cookie of a fresh visit to the sign-in page, with or without the tunnel's X-Forwarded-Proto.
        static async Task<string> FormCookieAsync(WebApplicationFactory<CinemaWatch.Web.Components.App> factory, bool forwardedHttps)
        {
            using var client = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
            using var request = new HttpRequestMessage(HttpMethod.Get, "/Account/Login");
            if (forwardedHttps)
                request.Headers.Add("X-Forwarded-Proto", "https");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal));
        }
        static bool IsSecure(string cookie) => cookie.Split(';').Any(part => part.Trim().Equals("secure", StringComparison.OrdinalIgnoreCase));
        static WebApplicationFactory<CinemaWatch.Web.Components.App> With(TestApp app, Dictionary<string, string?> settings) =>
            app.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings)));
        static CookieSecurePolicy SignInCookie(WebApplicationFactory<CinemaWatch.Web.Components.App> factory, string scheme) =>
            factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme).Cookie.SecurePolicy;

        // Development and tests (http://localhost, not trusted): a forged header changes nothing.
        Assert.True(!IsSecure(await FormCookieAsync(app, forwardedHttps: false)));
        Assert.True(!IsSecure(await FormCookieAsync(app, forwardedHttps: true)));
        Assert.Equal(CookieSecurePolicy.SameAsRequest, SignInCookie(app, IdentityConstants.ApplicationScheme));

        // The server: https address, trusted tunnel. The visitor's HTTPS reaches the app, so the form cookie is Secure;
        // the sign-in cookies are Secure always. Without the header the pages still work (no Secure form cookie then).
        using var server = With(app, new() { ["Saas:BaseUrl"] = "https://cinemawatch.example", ["Saas:TrustForwardedHeaders"] = "true" });
        Assert.True(IsSecure(await FormCookieAsync(server, forwardedHttps: true)));
        Assert.True(!IsSecure(await FormCookieAsync(server, forwardedHttps: false)));
        Assert.Equal(CookieSecurePolicy.Always, SignInCookie(server, IdentityConstants.ApplicationScheme));
        Assert.Equal(CookieSecurePolicy.Always, SignInCookie(server, IdentityConstants.ExternalScheme));
        var serverHealth = await server.CreateClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, serverHealth.StatusCode);
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
        var privacy = await owner.PageAsync("/privacy");
        Assert.True(privacy.Contains("Prywatność"));
        // The notice names every service that receives personal data, and is no longer a draft.
        foreach (var service in new[] { "OVHcloud", "Cloudflare Access", "Resend (USA)", "Healthchecks.io", "Backblaze B2" })
            Assert.True(privacy.Contains(service), $"privacy notice lacks {service}");
        Assert.True(!privacy.Contains("Wersja robocza"));
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

    TestRunner.Add("Web: five wrong passwords lock the account (even the right password is refused then); an unknown address, a waiting, a locked account all get the same answer, never faster than the set time", async () =>
    {
        await SignedInAsync("locked@example.test", AppLanguage.Polish);
        await WithService(async services => (await services.GetRequiredService<AccountService>().InviteAsync("waiting@example.test", AppLanguage.Polish)).Outcome);
        var browser = app.CreateBrowser();
        var page = await browser.PageAsync("/Account/Login");
        async Task<string> AnswerAsync(string email, string typed)
        {
            var started = Stopwatch.GetTimestamp();
            var response = await browser.PostFormAsync("/Account/Login", page, "login", new() { ["Input.Email"] = email, ["Input.Password"] = typed });
            Assert.True(Stopwatch.GetElapsedTime(started).TotalMilliseconds >= TestApp.SignInAnswerMilliseconds, "the answer came too fast");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return StatusText(WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        }

        var unknown = await AnswerAsync("nobody-here@example.test", "wrong password");
        Assert.True(unknown.StartsWith("Nieprawidłowy e-mail lub hasło.") && unknown.Contains("wstrzymane na 15 minut"), unknown);
        for (var i = 0; i < 5; i++)
            Assert.Equal(unknown, await AnswerAsync("locked@example.test", "wrong password"));
        Assert.Equal(unknown, await AnswerAsync("locked@example.test", password));
        Assert.Equal(unknown, await AnswerAsync("waiting@example.test", "some long password"));
        // The old lockout page is gone (it told who has an account): like any unknown address, the sign-in page.
        Assert.True(Location(await browser.GetAsync("/Account/Lockout")).StartsWith("/Account/Login"));
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
        Assert.True((await EventsAsync(ServiceEventKind.AccountDeleted)).Any(e => e.Message == "Account deleted by its user (profiles: 0)"));
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

    // A clean poller for every test: no profiles, no state, the configuration's settings, no pause; two hours later,
    // so the request budget is full again and the heartbeat may ping.
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
            await db.PollerSettings.ExecuteDeleteAsync();
            await db.ComponentStatus.ExecuteDeleteAsync();
            return 0;
        });
        app.Services.GetRequiredService<PollerSettings>().Apply(null);
        app.Services.GetRequiredService<CinemaCityCache>().Clear();
        app.HeartbeatClient.Status = 200;
        app.HeartbeatClient.Throw = null;
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
    // -----------------------------------------------------------------------------------------------------------
    // Operator's panel (owner only): status, statistics, events, the pause, the settings; the heartbeat.
    // -----------------------------------------------------------------------------------------------------------

    async Task<OperatorOverview> OverviewAsync() =>
        await WithService(async services => await services.GetRequiredService<OperatorService>().GetOverviewAsync());

    TestRunner.Add("Operator's panel: only for the owner (page and forms); checklist, requests hour by hour and per status, the mail server's last success and error", async () =>
    {
        await ResetPollerAsync();
        var member = await SignedInAsync("panel-member@example.test", AppLanguage.Polish);
        var owner = await SignInAsync(TestApp.OwnerEmail);

        // Nobody else gets in, signed in or not; the forms refuse them too.
        Assert.True(Location(await member.GetAsync("/admin")).StartsWith("/Account/AccessDenied"));
        Assert.True(Location(await app.CreateBrowser().GetAsync("/admin")).StartsWith("/Account/Login"));
        var memberPage = await member.PageAsync("/");
        Assert.True(!memberPage.Contains("<a href=\"admin\">Operator</a>"));
        foreach (var path in new[] { "/admin/pause", "/admin/resume", "/admin/settings/defaults" })
            Assert.True(Location(await member.PostFormAsync(path, memberPage, null, [])).StartsWith("/Account/AccessDenied"), path);
        Assert.Equal(0, (await EventsAsync(ServiceEventKind.PausedByOperator)).Count);

        // Before anything happened.
        var page = await owner.PageAsync("/admin");
        Assert.True(page.Contains("<a href=\"admin\">Operator</a>") && page.Contains("Panel operatora"));
        Assert.True(page.Contains("Jeszcze nie było cyklu.") && page.Contains("W dzienniku zapytań (30 dni) nie ma jeszcze żadnego zapytania."));
        Assert.True(page.Contains("Źródło: atrapa Cinema City") && page.Contains("Brak zdarzeń w ostatnich 90 dniach."));
        Assert.True(page.Contains("PostgreSQL ") && page.Contains("_AddWizard.") && page.Contains("check_log"));
        Assert.True(page.Contains("Jeszcze nie było pingu"));

        // Checks: a freed seat (an e-mail goes out), then Cinema City answers HTTP 500.
        await ImportAsync(await UserIdAsync("panel-member@example.test"), PollProfile("Panel", ["2000091", "2000092"]));
        app.Fake("free-2000091.txt", "1_18_4");
        await FirstChecksAsync();
        app.Fake("error500.txt", "");
        app.Time.Advance(afterInterval);
        await CycleAsync();

        var stats = await WithService(async services => await services.GetRequiredService<OperatorService>().GetRequestStatsAsync());
        Assert.Equal((24, 7, 7, 2, 0), (stats.Hours.Count, stats.Total24Hours, stats.Hours.Sum(h => h.Requests), stats.Hours.Sum(h => h.Failed), stats.Blocked24Hours));
        Assert.Equal((5, 2), (stats.Statuses.Single(s => s.Status == 200).Last24Hours, stats.Statuses.Single(s => s.Status == 500).Last7Days));
        Assert.True(stats.Recent.Count == 7 && stats.Recent[0].Status == 500);

        page = await owner.PageAsync("/admin");
        Assert.True(page.Contains("Ostatni cykl: ") && page.Contains("Pilnowane seanse: 2"));
        Assert.True(page.Contains("Ostatnie zapytanie: ") && page.Contains("HTTP 500 (presentation)") && page.Contains("Ostatnia udana odpowiedź: "));
        Assert.True(page.Contains("Ostatnia godzina: 7 z budżetu 120 na godzinę") && page.Contains("Żadnej blokady w ostatnich 90 dniach."));
        Assert.True(page.Contains("Ostatni wysłany: ") && page.Contains("Serwer pocztowy: localhost:2525."));
        var overview = await OverviewAsync();
        Assert.Equal((HealthLevel.Ok, HealthLevel.Warning, HealthLevel.Ok, HealthLevel.Ok, HealthLevel.Warning),
            (overview.Poller.Level, overview.CinemaCity.Level, overview.Mail.Level, overview.Database.Level, overview.Heartbeat.Level));
        Assert.Equal((7, 120), (overview.Budget.LastHour, overview.Budget.PerHour));
        Assert.Equal(7L, overview.Database.Tables.Single(t => t.Name == "check_log").Rows); // counted, not estimated

        // An e-mail that fails (here an invitation) shows until the next one goes out.
        app.Mail.FailNext = true;
        await WithService(async services => (await services.GetRequiredService<AccountService>().InviteAsync("panel-new@example.test", AppLanguage.Polish)).Outcome);
        page = await owner.PageAsync("/admin");
        Assert.True(page.Contains("Ostatni błąd: ") && page.Contains("The mail server is not reachable (test)."));
        Assert.Equal(HealthLevel.Problem, (await OverviewAsync()).Mail.Level);
        await WithService(async services => (await services.GetRequiredService<AccountService>().InviteAsync("panel-new@example.test", AppLanguage.Polish)).Outcome);
        Assert.Equal(HealthLevel.Ok, (await OverviewAsync()).Mail.Level);
    });

    TestRunner.Add("Operator's pause: from the next cycle nothing is sent, \"Check now\" is refused and dropped, users see a notice; resume; events", async () =>
    {
        await ResetPollerAsync();
        var browser = await SignedInAsync("panel-pause@example.test", AppLanguage.Polish);
        var profile = await ImportAsync(await UserIdAsync("panel-pause@example.test"), PollProfile("Pauza", ["2000101"]));
        await FirstChecksAsync();
        var owner = await SignInAsync(TestApp.OwnerEmail);

        var panel = await owner.PageAsync("/admin");
        Assert.True(panel.Contains("Wstrzymaj sprawdzanie"));
        Assert.Equal("/admin", Location(await owner.PostFormAsync("/admin/pause", panel, null, [])));
        panel = await owner.PageAsync("/admin");
        Assert.True(panel.Contains("Sprawdzanie wstrzymane. Od następnego cyklu") && panel.Contains("Wstrzymany przez Ciebie od") && panel.Contains("Wznów sprawdzanie"));
        Assert.True(panel.Contains("Checks paused by the operator")); // the event list
        await owner.PostFormAsync("/admin/pause", panel, null, []);
        Assert.True((await owner.PageAsync("/admin")).Contains("Sprawdzanie jest już wstrzymane."));
        Assert.Equal(1, (await EventsAsync(ServiceEventKind.PausedByOperator)).Count);
        Assert.True((await PollerStateAsync()).PausedByOperatorAt is not null);

        // Users: a notice and a greyed-out button; the page does not reload itself (the pause has no end).
        var page = await browser.PageAsync($"/?profile={profile}");
        Assert.True(page.Contains("Sprawdzanie miejsc jest wstrzymane przez właściciela serwisu."));
        Assert.True(page.Contains("disabled title=\"Sprawdzanie wstrzymane przez właściciela serwisu\"") && !page.Contains("http-equiv=\"refresh\""));
        await browser.PostFormAsync($"/profiles/{profile}/check", page, null, []);
        Assert.True((await browser.PageAsync($"/?profile={profile}")).Contains("Sprawdzanie jest wstrzymane przez właściciela serwisu."));

        // A "Check now" that slipped in before the pause is dropped; the due screening is not checked.
        await WithService(async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            (await db.WatchedScreenings.SingleAsync(r => r.Id == "2000101")).ManualRequestedAt = app.Time.GetUtcNow();
            return await db.SaveChangesAsync();
        });
        app.Time.Advance(afterInterval);
        var mark = app.Network.Requests.Count;
        Assert.Equal(CycleResult.Paused, (await CycleAsync()).Result);
        Assert.Equal(mark, app.Network.Requests.Count);
        Assert.Equal(null, (await RowAsync("2000101")).ManualRequestedAt);
        Assert.Equal(HealthLevel.Warning, (await OverviewAsync()).Poller.Level);

        // Resumed: the next cycle checks again, the notice is gone.
        Assert.Equal("/admin", Location(await owner.PostFormAsync("/admin/resume", panel, null, [])));
        Assert.True((await owner.PageAsync("/admin")).Contains("Sprawdzanie wznowione."));
        Assert.Equal(null, (await PollerStateAsync()).PausedByOperatorAt);
        Assert.Equal(1, (await EventsAsync(ServiceEventKind.ResumedByOperator)).Count);
        Assert.Equal(1, (await CycleAsync()).Checked);
        Assert.True(!(await browser.PageAsync("/")).Contains("wstrzymane przez właściciela"));
        await owner.PostFormAsync("/admin/resume", panel, null, []);
        Assert.True((await owner.PageAsync("/admin")).Contains("Sprawdzanie nie było wstrzymane."));
    });

    TestRunner.Add("Poller settings in the panel: saved within the ranges and used from the next cycle; errors keep what was typed; restore the configuration; events", async () =>
    {
        await ResetPollerAsync();
        var browser = await SignedInAsync("panel-settings@example.test", AppLanguage.Polish);
        var owner = await SignInAsync(TestApp.OwnerEmail);
        var settings = app.Services.GetRequiredService<PollerSettings>();

        // The form as the browser sends it: every field with the value in force, except the changed ones.
        Dictionary<string, string> Form(params (TuningKey Key, string Value)[] changes)
        {
            var fields = PollerTuning.Numbers.ToDictionary(n => $"Input.{n.Key}", n => n.Format(settings.ValueOf(n)));
            foreach (var (key, value) in changes)
                fields[$"Input.{key}"] = value;
            return fields;
        }

        var page = await owner.PageAsync("/admin");
        Assert.True(page.Contains("Ustawienia pollera") && page.Contains("name=\"Input.MinIntervalMinutes\"") && page.Contains("Zakres 5–60 min."));
        Assert.True(!page.Contains("Przywróć wszystkie wartości z konfiguracji"));

        var response = await owner.PostFormAsync("/admin", page, "settings", Form((TuningKey.MinIntervalMinutes, "15"), (TuningKey.RequestsPerHour, "60")));
        Assert.Equal("/admin", Location(response));
        page = await owner.PageAsync("/admin");
        Assert.True(page.Contains("Zapisano: najkrótszy odstęp (min) 10 → 15, budżet (na godzinę) 120 → 60. Działa od następnego cyklu."));
        Assert.True(page.Contains("Zmienione tutaj; w konfiguracji: 10.") && page.Contains("Przywróć wszystkie wartości z konfiguracji"));
        var stored = await WithService(async services => await services.GetRequiredService<AppDbContext>().PollerSettings.AsNoTracking().SingleAsync());
        Assert.Equal((15, 60, null, null), (stored.MinIntervalMinutes, stored.RequestsPerHour, stored.JitterPercent, stored.ReuseScreeningDetails));
        Assert.True((await EventsAsync(ServiceEventKind.SettingsChanged)).Single().Message.EndsWith("MinIntervalMinutes 10 → 15; RequestsPerHour 120 → 60"));
        Assert.Equal(10, app.Services.GetRequiredService<RequestBudget>().Capacity);

        // The next cycle uses them: a profile asking for 10 minutes is checked every 15-18 minutes, and the page says so.
        var profile = await ImportAsync(await UserIdAsync("panel-settings@example.test"), PollProfile("Ustawienia", ["2000111"]));
        await FirstChecksAsync();
        var row = await RowAsync("2000111");
        var now = app.Time.GetUtcNow();
        Assert.True(row.IntervalMinutes == 15 && row.NextCheckAt >= now + TimeSpan.FromMinutes(15) && row.NextCheckAt <= now + TimeSpan.FromMinutes(18), row.NextCheckAt.ToString("O"));
        Assert.True((await browser.PageAsync($"/?profile={profile}")).Contains("Sprawdzanie co 15 min (profil prosi o 10 min, serwer sprawdza najczęściej co 15)."));

        // Errors: shown next to the fields, what was typed stays, nothing is saved.
        response = await owner.PostFormAsync("/admin", page, "settings", Form((TuningKey.RequestsPerHour, "1000"), (TuningKey.MinIntervalMinutes, "abc")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.True(text.Contains("Nie zapisano: popraw zaznaczone pola.") && text.Contains("Dozwolone od 10 do 300.") && text.Contains("Wpisz liczbę całkowitą."));
        Assert.True(text.Contains("value=\"1000\"") && text.Contains("value=\"abc\""));
        Assert.Equal((15, 60), (settings.Limits.MinIntervalMinutes, settings.Poller.RequestsPerHour));

        // The same values again: nothing to save, no event. The switch alone is a change.
        await owner.PostFormAsync("/admin", page, "settings", Form());
        Assert.True((await owner.PageAsync("/admin")).Contains("Nic się nie zmieniło."));
        Assert.Equal(1, (await EventsAsync(ServiceEventKind.SettingsChanged)).Count);
        var withSwitch = Form();
        withSwitch["Input.ReuseScreeningDetails"] = "true";
        await owner.PostFormAsync("/admin", page, "settings", withSwitch);
        Assert.True(settings.Poller.ReuseScreeningDetails && (await owner.PageAsync("/admin")).Contains("Zapisano: zapamiętane szczegóły seansu false → true."));

        // Back to the configuration: the row is gone, the values are the configuration's again.
        Assert.Equal("/admin", Location(await owner.PostFormAsync("/admin/settings/defaults", page, null, [])));
        page = await owner.PageAsync("/admin");
        Assert.True(page.Contains("Przywrócono ustawienia z konfiguracji (zmiany: 3).") && !page.Contains("Przywróć wszystkie wartości z konfiguracji"));
        Assert.Equal(0, await WithService(async services => await services.GetRequiredService<AppDbContext>().PollerSettings.CountAsync()));
        Assert.Equal((10, 120, false), (settings.Limits.MinIntervalMinutes, settings.Poller.RequestsPerHour, settings.Poller.ReuseScreeningDetails));
        Assert.Equal(3, (await EventsAsync(ServiceEventKind.SettingsChanged)).Count);
    });

    TestRunner.Add("Heartbeat: a ping after a finished cycle, at most every 5 minutes; a failure is recorded and stops nothing; the panel never shows the secret part of the address", async () =>
    {
        await ResetPollerAsync();
        var heartbeat = app.Services.GetRequiredService<Heartbeat>();
        var pings = app.HeartbeatClient.Pings.Count;
        Assert.True(await heartbeat.AfterCycleAsync());
        Assert.Equal(new Uri(TestApp.HeartbeatUrl), app.HeartbeatClient.Pings[^1]);
        app.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.True(!await heartbeat.AfterCycleAsync());
        app.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await heartbeat.AfterCycleAsync());
        Assert.Equal(pings + 2, app.HeartbeatClient.Pings.Count);
        Assert.Equal(HealthLevel.Ok, (await OverviewAsync()).Heartbeat.Level);

        var owner = await SignInAsync(TestApp.OwnerEmail);
        var page = await owner.PageAsync("/admin");
        Assert.True(page.Contains("Ostatni ping: ") && page.Contains("Adres: hc-ping.example.test (reszta adresu jest tajna)."));
        Assert.True(!page.Contains("secret-check-id"));

        // The external service answers 500, then cannot be reached: recorded, one event an hour, nothing thrown.
        app.HeartbeatClient.Status = 500;
        app.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(await heartbeat.AfterCycleAsync());
        Assert.Equal("HTTP 500", (await WithService(async services =>
            await services.GetRequiredService<AppDbContext>().ComponentStatus.AsNoTracking().SingleAsync(s => s.Component == ComponentStatusRow.Heartbeat))).LastFailure);
        app.HeartbeatClient.Throw = new HttpRequestException("No such host is known.");
        app.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(await heartbeat.AfterCycleAsync());
        Assert.Equal(1, (await EventsAsync(ServiceEventKind.HeartbeatFailed)).Count);
        page = await owner.PageAsync("/admin");
        Assert.True(page.Contains("Ostatni błąd: ") && page.Contains("HttpRequestException: No such host is known.") && !page.Contains("secret-check-id"));
        Assert.Equal(HealthLevel.Problem, (await OverviewAsync()).Heartbeat.Level);

        // Reachable again: back to normal.
        app.HeartbeatClient.Throw = null;
        app.HeartbeatClient.Status = 200;
        app.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.True(await heartbeat.AfterCycleAsync());
        Assert.Equal(HealthLevel.Ok, (await OverviewAsync()).Heartbeat.Level);
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

        var pings = app.HeartbeatClient.Pings.Count;
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
        // The finished cycle sent one heartbeat; the second cycle came too soon for another.
        Assert.Equal(pings + 1, app.HeartbeatClient.Pings.Count);
    });

    // -----------------------------------------------------------------------------------------------------------
    // Model A (docs/SAAS.md §9): a visitor without an account can cause no cost and no damage; the owner deletes
    // accounts. Last, because they move the test clock.
    // -----------------------------------------------------------------------------------------------------------

    async Task<bool> UserExistsAsync(string userId) =>
        await WithService(async services => await services.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(userId) is not null);

    async Task<int> WaitForMailsAsync(string email, int expected)
    {
        for (var i = 0; i < 50 && MailsTo(email).Count < expected; i++)
            await Task.Delay(100);
        return MailsTo(email).Count;
    }

    TestRunner.Add("Public pages: a visitor without an account writes nothing to the database; robots.txt and noindex; /healthz only from inside; /admin only in lower case", async () =>
    {
        await SignedInAsync("aimed-at@example.test", AppLanguage.Polish);
        async Task<Dictionary<string, long>> RowCountsAsync() => await WithService(async services =>
        {
            var connection = services.GetRequiredService<AppDbContext>().Database.GetDbConnection();
            await connection.OpenAsync();
            var counts = new Dictionary<string, long>();
            foreach (var table in await database.TablesAsync())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "select count(*) from \"" + table.Replace("\"", "\"\"") + "\"";
                counts[table] = Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
            }
            return counts;
        });

        var visitor = app.CreateBrowser();
        await visitor.PageAsync("/Account/Login"); // the form cookie's keys exist from now on, as on a running server
        var before = await RowCountsAsync();
        foreach (var path in new[] { "/", "/Account/Login", "/Account/ForgotPassword", "/privacy", "/robots.txt", "/not-found", "/nothing-here",
                                     "/Account/SetPassword?user=x&code=y", "/admin", "/admin/users", "/ADMIN", "/healthz", "/language/en?returnUrl=/" })
            await visitor.GetAsync(path);
        var page = await visitor.PageAsync("/Account/Login");
        await visitor.PostFormAsync("/Account/Login", page, "login", new() { ["Input.Email"] = "nobody@example.test", ["Input.Password"] = "some long password" });
        // A wrong password for an existing account only raises its failed-sign-in counter (no new row).
        await visitor.PostFormAsync("/Account/Login", page, "login", new() { ["Input.Email"] = "aimed-at@example.test", ["Input.Password"] = "some long password" });
        page = await visitor.PageAsync("/Account/ForgotPassword");
        await visitor.PostFormAsync("/Account/ForgotPassword", page, "forgot-password", new() { ["Input.Email"] = "nobody@example.test" });
        await Task.Delay(300); // the background queue
        var after = await RowCountsAsync();
        foreach (var (table, count) in before)
            Assert.Equal((table, count), (table, after[table]));

        var robots = await visitor.GetAsync("/robots.txt");
        Assert.Equal(PublicSurface.RobotsText, await robots.Content.ReadAsStringAsync());
        foreach (var path in new[] { "/Account/Login", "/robots.txt", "/nothing-here" })
            Assert.Equal("noindex, nofollow", (await visitor.GetAsync(path)).Headers.GetValues("X-Robots-Tag").Single());

        // Inside the server (deploy.sh, the container test) the health check answers; through Cloudflare it does not exist.
        Assert.Equal("ok", await (await visitor.GetAsync("/healthz")).Content.ReadAsStringAsync());
        foreach (var header in new[] { "CF-Connecting-IP", "CF-Ray" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
            request.Headers.Add(header, "203.0.113.7");
            Assert.Equal(HttpStatusCode.NotFound, (await visitor.SendAsync(request)).StatusCode);
        }

        // Another spelling of /admin is 404 even for the owner: only "/admin" matches the Access application.
        var owner = await SignInAsync(TestApp.OwnerEmail);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/admin/users")).StatusCode);
        foreach (var path in new[] { "/ADMIN", "/Admin", "/Admin/users", "/aDmin/users" })
            Assert.Equal((path, HttpStatusCode.NotFound), (path, (await owner.GetAsync(path)).StatusCode));
    });

    TestRunner.Add("Password help page: the same answer for every address, only queued; the e-mail follows in the background; one per account per 15 minutes; the daily limit records an event without an address", async () =>
    {
        await SignedInAsync("helpme@example.test", AppLanguage.English);
        var visitor = app.CreateBrowser();
        async Task<string> AskAsync(string email)
        {
            var form = await visitor.PageAsync("/Account/ForgotPassword");
            var response = await visitor.PostFormAsync("/Account/ForgotPassword", form, "forgot-password", new() { ["Input.Email"] = email });
            Assert.Equal("/Account/Login", Location(response));
            return StatusText(await visitor.PageAsync("/Account/Login"));
        }

        var before = MailsTo("helpme@example.test").Count;
        var forUnknown = await AskAsync("nobody-at-all@example.test");
        var forKnown = await AskAsync("helpme@example.test");
        Assert.True(forKnown.StartsWith("Jeśli to konto istnieje"), forKnown);
        Assert.Equal(forUnknown, forKnown);
        Assert.Equal(before + 1, await WaitForMailsAsync("helpme@example.test", before + 1));
        Assert.Equal("New password for CinemaWatch", MailsTo("helpme@example.test")[^1].Subject);
        Assert.True(!app.Mail.Sent.Any(m => m.To == "nobody-at-all@example.test"));

        // Again within 15 minutes: the same answer, no e-mail; after 15 minutes the next one goes.
        Assert.Equal(forKnown, await AskAsync("helpme@example.test"));
        await Task.Delay(500);
        Assert.Equal(before + 1, MailsTo("helpme@example.test").Count);
        app.Time.Advance(PasswordHelpLimits.PerAccountInterval);
        await AskAsync("helpme@example.test");
        Assert.Equal(before + 2, await WaitForMailsAsync("helpme@example.test", before + 2));

        // The daily limit, with a limiter that has already counted 20: nothing sent, one event, no address in it.
        var mails = app.Mail.Sent.Count;
        var outcome = await WithService(async services =>
        {
            var full = new PasswordHelpLimits(app.Time);
            for (var i = 0; i < PasswordHelpLimits.PerDay; i++)
                full.TryReserve($"someone-{i}");
            return await ActivatorUtilities.CreateInstance<AccountService>(services, full).RequestPasswordHelpAsync("helpme@example.test");
        });
        Assert.Equal((PasswordHelpOutcome.Limited, mails), (outcome, app.Mail.Sent.Count));
        var limited = await EventsAsync(ServiceEventKind.AccountMailLimited);
        Assert.Equal(1, limited.Count);
        Assert.True(!limited[0].Message.Contains('@'));
    });

    TestRunner.Add("Owner deletes accounts in /admin/users: a question first; all data and an e-mail; an invitation withdrawn; never the owner; nobody else; the session ends within a minute", async () =>
    {
        var gone = await SignedInAsync("gone@example.test", AppLanguage.English);
        var goneId = await UserIdAsync("gone@example.test");
        await ImportAsync(goneId, SampleProfile("Do usunięcia"));
        var member = await SignedInAsync("stays@example.test", AppLanguage.Polish);
        var owner = await SignInAsync(TestApp.OwnerEmail);
        var ownerId = await UserIdAsync(TestApp.OwnerEmail);
        await WithService(async services => (await services.GetRequiredService<AccountService>().InviteAsync("never-came@example.test", AppLanguage.Polish)).Outcome);
        var pendingId = await UserIdAsync("never-came@example.test");
        var invitationLink = TestApp.LinkPath(app.Mail.Sent.Last(m => m.To == "never-came@example.test"));

        // Nobody else may; the form token is required.
        var memberPage = await member.PageAsync("/");
        Assert.True(Location(await member.PostFormAsync("/admin/users/delete", memberPage, null, new() { ["userId"] = goneId })).StartsWith("/Account/AccessDenied"));
        var withoutToken = await owner.PostAsync("/admin/users/delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["userId"] = goneId }));
        Assert.Equal((HttpStatusCode.BadRequest, PublicSurface.InvalidFormText), (withoutToken.StatusCode, await withoutToken.Content.ReadAsStringAsync()));
        Assert.True(await UserExistsAsync(goneId));

        // The list links every account but the owner's to its question; the question lives in the address.
        var list = await owner.PageAsync("/admin/users");
        Assert.True(list.Contains($"admin/users?delete={Uri.EscapeDataString(goneId)}") && list.Contains($"admin/users?delete={Uri.EscapeDataString(pendingId)}"));
        Assert.True(!list.Contains($"admin/users?delete={Uri.EscapeDataString(ownerId)}") && list.Contains("Twoje konto"));
        var question = await owner.PageAsync($"/admin/users?delete={Uri.EscapeDataString(goneId)}");
        Assert.True(question.Contains("Usunąć konto gone@example.test? Znikną też jego profile (1)"), "no question");
        Assert.True(!(await owner.PageAsync($"/admin/users?delete={Uri.EscapeDataString(ownerId)}")).Contains("Usunąć konto"));

        // The owner's own account: refused even with a forged form.
        Assert.Equal("/admin/users", Location(await owner.PostFormAsync("/admin/users/delete", question, null, new() { ["userId"] = ownerId })));
        Assert.True(StatusText(await owner.PageAsync("/admin/users")).StartsWith("Konta właściciela nie można usunąć"));
        Assert.True(await UserExistsAsync(ownerId));

        // An active account: everything goes, the person gets an e-mail in their language, the event names nobody.
        Assert.Equal("/admin/users", Location(await owner.PostFormAsync("/admin/users/delete", question, null, new() { ["userId"] = goneId })));
        Assert.Equal("Konto gone@example.test usunięte (profile: 1). Ta osoba dostała e-mail.", StatusText(await owner.PageAsync("/admin/users")));
        Assert.True(!await UserExistsAsync(goneId));
        Assert.Equal(0, await WithService(async services => await services.GetRequiredService<AppDbContext>().Profiles.CountAsync(p => p.UserId == goneId)));
        Assert.Equal("Your CinemaWatch account has been deleted", MailsTo("gone@example.test")[^1].Subject);

        // A waiting account: the invitation is withdrawn without an e-mail and its link stops working.
        var mails = app.Mail.Sent.Count;
        var pendingQuestion = await owner.PageAsync($"/admin/users?delete={Uri.EscapeDataString(pendingId)}");
        Assert.True(pendingQuestion.Contains("Wycofać zaproszenie dla never-came@example.test?"));
        await owner.PostFormAsync("/admin/users/delete", pendingQuestion, null, new() { ["userId"] = pendingId });
        Assert.Equal("Zaproszenie dla never-came@example.test wycofane.", StatusText(await owner.PageAsync("/admin/users")));
        Assert.Equal(mails, app.Mail.Sent.Count);
        Assert.True(!await UserExistsAsync(pendingId));
        Assert.True((await app.CreateBrowser().PageAsync(invitationLink)).Contains("Link jest nieważny albo wygasł."));

        var events = (await EventsAsync(ServiceEventKind.AccountDeleted)).Select(e => e.Message).ToList();
        Assert.True(events.Contains("Account deleted by the owner (profiles: 1)") && events.Contains("Invitation withdrawn by the owner"));
        Assert.True(events.All(m => !m.Contains('@') && !m.Contains(goneId) && !m.Contains(pendingId)));

        // An unknown id: only a message.
        await owner.PostFormAsync("/admin/users/delete", question, null, new() { ["userId"] = "no-such-account" });
        Assert.Equal("Nie ma takiego konta.", StatusText(await owner.PageAsync("/admin/users")));

        // The deleted person's session ends at the next check (every minute, not after 30); the other account is untouched.
        Assert.Equal(HttpStatusCode.OK, (await gone.GetAsync("/")).StatusCode);
        app.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(Location(await gone.GetAsync("/")).StartsWith("/Account/Login"));
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/")).StatusCode);
    });

    // -----------------------------------------------------------------------------------------------------------
    // Wizard data on demand (docs/SAAS.md §5.2, stage 6.4.2): loadings run in the background against the fake.
    // -----------------------------------------------------------------------------------------------------------

    WizardJobs Wizard() => app.Services.GetRequiredService<WizardJobs>();

    DateOnly Today() => CinemaCityCache.PolishToday(app.Time.GetUtcNow());

    async Task<WizardJobView> LoadAsync(string userId, WizardRequest request)
    {
        var start = await Wizard().StartAsync(userId, request);
        Assert.Equal(WizardStartOutcome.Started, start.Outcome);
        await Wizard().WhenEndedAsync(start.Job!.Id);
        return Wizard().Find(start.Job.Id, userId)!;
    }

    async Task<List<CheckLogEntry>> WizardLogAsync() => await WithService(async services =>
        await services.GetRequiredService<AppDbContext>().CheckLog.AsNoTracking().Where(e => e.Source == RequestSource.Wizard).OrderBy(e => e.Id).ToListAsync());

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("The condition did not come true within 10 s.");
            await Task.Delay(10);
        }
    }

    TestRunner.Add("Wizard: a repertoire loading in the background - one request per cinema and day, one after the other, its own budget, logged as the wizard's; the cache answers the next one", async () =>
    {
        await ResetPollerAsync();
        var today = Today();
        var mark = app.Network.Requests.Count;
        var pauses = app.WizardPacer.Pauses;
        var job = await LoadAsync("wizard-first-a", new RepertoireRequest(["1060"], today.AddDays(1), today.AddDays(3)));
        Assert.Equal((WizardJobState.Finished, (WizardStopReason?)null, 3, 3, 0, 0, 3), (job.State, job.StopReason, job.Total, job.Done, job.FromCache, job.Failed, job.Requests));
        Assert.SequenceEqual([today.AddDays(1), today.AddDays(2), today.AddDays(3)], job.Repertoire.Select(r => r.Date));
        Assert.True(job.Repertoire.All(r => r.Error is null && r.CinemaId == "1060" && r.Day!.Screenings.Count == 25));
        Assert.SequenceEqual(["repertoire", "repertoire", "repertoire"], EndpointsSince(mark));
        Assert.Equal(pauses + 2, app.WizardPacer.Pauses); // before the second and the third request
        var requests = app.Network.Requests.Skip(mark).ToList();
        Assert.Equal(1, requests.Select(r => r.Uuid).Distinct().Count()); // one session per loading
        Assert.True(requests.All(r => r.UserAgent!.Contains($"CinemaWatch-Server/{PollerInfo.Version}")));

        var log = await WizardLogAsync();
        Assert.Equal(3, log.Count);
        Assert.True(log.All(e => e.Status == 200 && e.Endpoint == "repertoire" && e.ScreeningId is null && !e.Manual && e.Cycle.StartsWith("wizard-") && e.Cycle.Length == 15));
        var overview = await OverviewAsync();
        Assert.Equal((0, 3, 87, 90, 0), (overview.Budget.LastHour, overview.Wizard.Budget.LastHour, overview.Wizard.Budget.Available, overview.Wizard.Budget.Capacity, overview.Wizard.Running));
        Assert.Equal(20, overview.Budget.Available); // the poller's budget is untouched
        Assert.Equal(3, overview.Wizard.Cache.Entries);

        // Another account asks for the same days: everything from the cache, nothing sent.
        var again = await LoadAsync("wizard-first-b", new RepertoireRequest(["1060"], today.AddDays(1), today.AddDays(3)));
        Assert.Equal((3, 3, 0), (again.Done, again.FromCache, again.Requests));
        Assert.Equal(mark + 3, app.Network.Requests.Count);
        // An overlapping range: only the new day goes out.
        var more = await LoadAsync("wizard-first-a", new RepertoireRequest(["1060"], today.AddDays(2), today.AddDays(4)));
        Assert.Equal((3, 2, 1), (more.Done, more.FromCache, more.Requests));
        // Half an hour later tomorrow is fetched again (it changes during the day); later days are still kept.
        app.Time.Advance(TimeSpan.FromMinutes(31));
        mark = app.Network.Requests.Count;
        var later = await LoadAsync("wizard-first-a", new RepertoireRequest(["1060"], today.AddDays(1), today.AddDays(3)));
        Assert.Equal((1, 2), (later.Requests, later.FromCache));
        Assert.True(app.Network.Requests[mark].Uri.ToString().Contains($"/at-date/{today.AddDays(1):yyyy-MM-dd}"));

        // A loading belongs to its account; a broken request is refused before anything is sent.
        Assert.Equal(null, Wizard().Find(job.Id, "wizard-first-b"));
        var refused = await Wizard().StartAsync("wizard-first-a", new RepertoireRequest(["1060"], today.AddDays(-1), today));
        Assert.Equal((WizardStartOutcome.Invalid, WizardRequestProblem.DateInPast), (refused.Outcome, refused.Problem!.Value));
        refused = await Wizard().StartAsync("wizard-first-a", new RepertoireRequest(["1060", "1064"], today, today.AddDays(14)));
        Assert.Equal(WizardRequestProblem.RangeTooLong, refused.Problem!.Value);
        Assert.Equal(mark + 1, app.Network.Requests.Count);
    });

    TestRunner.Add("Wizard: one loading per account at a time; two accounts asking for the same days at the same time send each request once", async () =>
    {
        await ResetPollerAsync();
        var today = Today();
        var request = new RepertoireRequest(["1060"], today.AddDays(5), today.AddDays(7));
        var mark = app.Network.Requests.Count;
        var pauses = app.WizardPacer.Pauses;
        var release = app.WizardPacer.Hold();
        try
        {
            var a = await Wizard().StartAsync("wizard-flight-a", request);
            Assert.Equal(WizardStartOutcome.Started, a.Outcome);
            // A sent its first request and waits before the second one (which it is already loading for everybody).
            await WaitUntilAsync(() => app.WizardPacer.Pauses == pauses + 1);
            Assert.Equal(mark + 1, app.Network.Requests.Count);

            var second = await Wizard().StartAsync("wizard-flight-a", new RepertoireRequest(["1064"], today, today));
            Assert.Equal((WizardStartOutcome.AlreadyRunning, a.Job!.Id), (second.Outcome, second.Job!.Id));
            Assert.Equal((a.Job.Id, 1), (Wizard().Running("wizard-flight-a")!.Id, Wizard().RunningCount));
            Assert.Equal(WizardJobState.Running, Wizard().Find(a.Job.Id, "wizard-flight-a")!.State);

            var b = await Wizard().StartAsync("wizard-flight-b", request);
            Assert.Equal(WizardStartOutcome.Started, b.Outcome);
            release();
            await Wizard().WhenEndedAsync(a.Job.Id);
            await Wizard().WhenEndedAsync(b.Job!.Id);
            var jobA = Wizard().Find(a.Job.Id, "wizard-flight-a")!;
            var jobB = Wizard().Find(b.Job.Id, "wizard-flight-b")!;
            Assert.Equal((WizardJobState.Finished, WizardJobState.Finished, 3, 3), (jobA.State, jobB.State, jobA.Repertoire.Count, jobB.Repertoire.Count));
            Assert.Equal(mark + 3, app.Network.Requests.Count); // each day once, for both accounts
            Assert.Equal(3, jobA.Requests + jobB.Requests);
            Assert.Equal(0, Wizard().RunningCount);
        }
        finally
        {
            release();
        }
    });

    TestRunner.Add("Wizard limits: the account's limit and the wizard's budget refuse a loading before anything is sent and say when to try again; too large for the limits; an event without an account", async () =>
    {
        await ResetPollerAsync();
        var today = Today();
        await WithService(async services => await services.GetRequiredService<OperatorService>()
            .SaveSettingsAsync(new PollerSettingsRow { WizardAccountRequestsPerHour = 10, WizardBurst = 20 }));
        var now = app.Time.GetUtcNow();

        // Account limit 10: eight days fit, then two are left - eight more need 36 minutes (10 an hour).
        Assert.Equal(8, (await LoadAsync("wizard-limit-a", new RepertoireRequest(["1060"], today.AddDays(10), today.AddDays(17)))).Requests);
        var mark = app.Network.Requests.Count;
        var refused = await Wizard().StartAsync("wizard-limit-a", new RepertoireRequest(["1060"], today.AddDays(20), today.AddDays(27)));
        Assert.Equal((WizardStartOutcome.Refused, WizardStopReason.AccountLimit, now + TimeSpan.FromMinutes(36)),
            (refused.Outcome, refused.Reason!.Value, refused.RetryAt!.Value));
        // What the cache answers costs nothing: the eight days already loaded can be shown again at once.
        Assert.Equal(8, (await LoadAsync("wizard-limit-a", new RepertoireRequest(["1060"], today.AddDays(10), today.AddDays(17)))).FromCache);

        // More than an account's limit can ever hold at once: too large (the limits were lowered).
        refused = await Wizard().StartAsync("wizard-limit-b", new RepertoireRequest(["1064"], today.AddDays(10), today.AddDays(20)));
        Assert.Equal((WizardStartOutcome.Refused, WizardStopReason.TooLarge, (DateTimeOffset?)null), (refused.Outcome, refused.Reason!.Value, refused.RetryAt));

        // The wizard's budget (20 saved up, 12 left): another account's ten days fit, a third account's three do not.
        Assert.Equal(10, (await LoadAsync("wizard-limit-b", new RepertoireRequest(["1064"], today.AddDays(10), today.AddDays(19)))).Requests);
        refused = await Wizard().StartAsync("wizard-limit-c", new RepertoireRequest(["1078"], today.AddDays(10), today.AddDays(12)));
        Assert.Equal((WizardStartOutcome.Refused, WizardStopReason.BudgetUsedUp, now + TimeSpan.FromSeconds(30)),
            (refused.Outcome, refused.Reason!.Value, refused.RetryAt!.Value)); // one token short, 120 an hour
        Assert.Equal(mark + 10, app.Network.Requests.Count);
        var exhausted = (await EventsAsync(ServiceEventKind.WizardBudgetExhausted)).Single();
        Assert.True(exhausted.Message.StartsWith("Wizard budget used up (120/h, 20 saved up)") && !exhausted.Message.Contains("wizard-limit"));
    });

    TestRunner.Add("Wizard and blocks: the owner's pause refuses a loading; a block stops it, pauses the poller too and alerts the owner; after the pause the wizard's first request ends the block", async () =>
    {
        await ResetPollerAsync();
        var today = Today();
        var request = new RepertoireRequest(["1060"], today.AddDays(1), today.AddDays(3));
        var mark = app.Network.Requests.Count;

        await WithService(async services => await services.GetRequiredService<OperatorService>().PauseAsync());
        var refused = await Wizard().StartAsync("wizard-block", request);
        Assert.Equal((WizardStartOutcome.Refused, WizardStopReason.PausedByOperator), (refused.Outcome, refused.Reason!.Value));
        await WithService(async services => await services.GetRequiredService<OperatorService>().ResumeAsync());
        Assert.Equal(mark, app.Network.Requests.Count);

        // Cinema City refuses the first request: nothing more is sent, the loading says until when.
        app.Fake("block.txt", "");
        var ownerMails = MailsTo(TestApp.OwnerEmail).Count;
        var job = await LoadAsync("wizard-block", request);
        var now = app.Time.GetUtcNow();
        Assert.Equal((WizardJobState.Stopped, WizardStopReason.Blocked, now + TimeSpan.FromMinutes(60), 0, 1),
            (job.State, job.StopReason!.Value, job.RetryAt!.Value, job.Done, job.Requests));
        Assert.Equal(mark + 1, app.Network.Requests.Count);
        var state = await PollerStateAsync();
        Assert.Equal((now + TimeSpan.FromMinutes(60), 1), (state.BlockedUntil!.Value, state.BlockCount));
        var blocked = (await EventsAsync(ServiceEventKind.Blocked)).Single();
        Assert.True(blocked.Message.StartsWith("Wizard: HTTP 403 (cf-mitigated: challenge)") && blocked.Data!.Contains("Fake Cloudflare challenge"));
        Assert.Equal((ownerMails + 1, "CinemaWatch: sprawdzanie wstrzymane"), (MailsTo(TestApp.OwnerEmail).Count, MailsTo(TestApp.OwnerEmail)[^1].Subject));
        Assert.Equal(403, (await WizardLogAsync()).Single().Status);

        // One address, one block: the poller waits too, and the next loading is refused until the pause ends.
        Assert.Equal(CycleResult.Paused, (await CycleAsync()).Result);
        refused = await Wizard().StartAsync("wizard-block", request);
        Assert.Equal((WizardStartOutcome.Refused, WizardStopReason.Blocked, now + TimeSpan.FromMinutes(60)), (refused.Outcome, refused.Reason!.Value, refused.RetryAt!.Value));
        Assert.Equal(mark + 1, app.Network.Requests.Count);

        // After the pause the wizard's first request gets through and ends the block - once, with the owner's e-mail.
        app.Fake("block.txt", null);
        app.Time.Advance(TimeSpan.FromMinutes(61));
        job = await LoadAsync("wizard-block", request);
        Assert.Equal((WizardJobState.Finished, 3), (job.State, job.Requests));
        state = await PollerStateAsync();
        Assert.Equal(((DateTimeOffset?)null, 0, (string?)null), (state.BlockedUntil, state.BlockCount, state.BlockReason));
        Assert.Equal(1, (await EventsAsync(ServiceEventKind.Resumed)).Count);
        Assert.Equal("CinemaWatch: sprawdzanie wznowione", MailsTo(TestApp.OwnerEmail)[^1].Subject);
        Assert.Equal(CycleResult.Idle, (await CycleAsync()).Result);
        Assert.Equal(1, (await EventsAsync(ServiceEventKind.Resumed)).Count);
    });

    TestRunner.Add("Wizard: the hall map's data - the poller's fresh result costs nothing; otherwise details, the layout from seat_plans and the free seats; after 10 minutes only the free seats again", async () =>
    {
        await ResetPollerAsync();
        await SignedInAsync("wizard-map@example.test", AppLanguage.Polish);
        var userId = await UserIdAsync("wizard-map@example.test");
        await ImportAsync(userId, PollProfile("Mapa", ["2000201"]));
        app.Fake("free-2000201.txt", "1_18_4");
        app.Fake("free-2000202.txt", "1_19_4 1_20_4");
        await FirstChecksAsync(); // the poller reads 2000201: details, layout, free seats
        var checkedAt = (await RowAsync("2000201")).LastSucceededAt!.Value;

        var mark = app.Network.Requests.Count;
        var job = await LoadAsync(userId, new ScreeningsRequest(["2000201", "2000202"]));
        Assert.SequenceEqual(["presentation", "seats-status"], EndpointsSince(mark)); // only for 2000202; the layout is stored
        Assert.Equal((WizardJobState.Finished, 2, 1, 2), (job.State, job.Done, job.FromCache, job.Requests));
        var watched = job.Screenings[0];
        Assert.Equal(("2000201", 13, 385, checkedAt), (watched.ScreeningId, watched.Details!.VenueId, watched.Plan!.Seats.Count, watched.FreeSeatsAt!.Value));
        Assert.SequenceEqual(["1_18_4"], watched.FreeSeatKeys!.Order());
        var other = job.Screenings[1];
        Assert.Equal((13, 1, app.Time.GetUtcNow()), (other.Details!.VenueId, other.Details.SeatplanId, other.FreeSeatsAt!.Value));
        Assert.SequenceEqual(["1_19_4", "1_20_4"], other.FreeSeatKeys!.Order());
        Assert.SequenceEqual(["2000202", "2000202"], (await WizardLogAsync()).Select(e => e.ScreeningId));

        // Eleven minutes later both free-seat answers are too old for the map; the details are kept for a day.
        app.Time.Advance(TimeSpan.FromMinutes(11));
        mark = app.Network.Requests.Count;
        job = await LoadAsync(userId, new ScreeningsRequest(["2000201", "2000202"]));
        Assert.SequenceEqual(["seats-status", "seats-status"], EndpointsSince(mark));
        Assert.True(job.Screenings.All(s => s.FreeSeatsAt == app.Time.GetUtcNow()));

        // A screening Cinema City answers with HTTP 500 is reported, the rest goes on.
        app.Fake("error500-2000203.txt", "");
        job = await LoadAsync(userId, new ScreeningsRequest(["2000203", "2000202"]));
        Assert.Equal((WizardJobState.Finished, 2, 1), (job.State, job.Done, job.Failed));
        Assert.True(job.Screenings[0].Error!.StartsWith("HTTP 500") && job.Screenings[1].Error is null);
    });

    TestRunner.Add("Operator's panel: the wizard's requests apart from the poller's, its budget, the cache and its settings", async () =>
    {
        await ResetPollerAsync();
        var owner = await SignInAsync(TestApp.OwnerEmail);
        var today = Today();
        await LoadAsync("wizard-panel", new RepertoireRequest(["1060"], today.AddDays(1), today.AddDays(2)));

        var page = await owner.PageAsync("/admin");
        Assert.True(page.Contains("Ostatnia godzina: 0 z budżetu 120 na godzinę"));
        Assert.True(page.Contains("Kreator (dane na żądanie, osobny budżet): ostatnia godzina 2 z budżetu 120 na godzinę; w puli teraz 88 z 90."));
        Assert.True(page.Contains("Limit konta: 90 zapytań spoza pamięci na godzinę") && page.Contains("Ładowania w toku: 0."));
        Assert.True(page.Contains("Pamięć podręczna od startu procesu: wpisy 2,"));
        Assert.True(page.Contains(" (kreator)</td>") && page.Contains("name=\"Input.WizardRequestsPerHour\""));
        var stats = await WithService(async services => await services.GetRequiredService<OperatorService>().GetRequestStatsAsync());
        Assert.Equal((2, 2), (stats.Hours.Sum(h => h.Requests), stats.Hours.Sum(h => h.Wizard)));

        var settings = app.Services.GetRequiredService<PollerSettings>();
        var fields = PollerTuning.Numbers.ToDictionary(n => $"Input.{n.Key}", n => n.Format(settings.ValueOf(n)));
        fields[$"Input.{TuningKey.WizardRequestsPerHour}"] = "60";
        fields[$"Input.{TuningKey.WizardGapSeconds}"] = "0.5";
        var rejected = WebUtility.HtmlDecode(await (await owner.PostFormAsync("/admin", page, "settings", fields)).Content.ReadAsStringAsync());
        Assert.True(rejected.Contains("Nie zapisano: popraw zaznaczone pola.") && rejected.Contains("Dozwolone od 1 do 10.")); // the pause is checked, nothing saved
        Assert.Equal(120, settings.Poller.Wizard.RequestsPerHour);
        fields[$"Input.{TuningKey.WizardGapSeconds}"] = "2";
        Assert.Equal("/admin", Location(await owner.PostFormAsync("/admin", page, "settings", fields)));
        page = await owner.PageAsync("/admin");
        Assert.True(page.Contains("Zapisano: budżet kreatora (na godzinę) 120 → 60, odstęp kreatora (s) 1 → 2."));
        Assert.Equal((60, 2.0), (settings.Poller.Wizard.RequestsPerHour, settings.Poller.Wizard.GapSeconds));
        Assert.True((await EventsAsync(ServiceEventKind.SettingsChanged)).Single().Message.EndsWith("WizardRequestsPerHour 120 → 60; WizardGapSeconds 1 → 2"));
    });

    // -----------------------------------------------------------------------------------------------------------
    // Wizard pages, steps 1-2 (docs/SAAS.md §5.2, stage 6.4.3)
    // -----------------------------------------------------------------------------------------------------------

    // Step 2's form as the browser sends it: the hidden fields, the film, the ticked screenings, the pressed button.
    static Dictionary<string, string> StepForm(string page, string go, IEnumerable<string>? ticked = null)
    {
        var form = new Dictionary<string, string> { ["go"] = go };
        foreach (Match hidden in Regex.Matches(page, "<input type=\"hidden\" name=\"([a-z]+)\" value=\"([^\"]*)\""))
            form[hidden.Groups[1].Value] = WebUtility.HtmlDecode(hidden.Groups[2].Value);
        var films = page.IndexOf("<select id=\"film\"", StringComparison.Ordinal);
        if (films >= 0 && Regex.Match(page[films..], "<option value=\"([^\"]+)\" selected") is { Success: true } film)
            form["film"] = film.Groups[1].Value;
        if (ticked is not null)
            form["s"] = string.Join(",", ticked);
        return form;
    }

    TestRunner.Add("Wizard pages: step 1 checks the dates as the desktop app; a loading's progress page reloads itself and opens step 2; films and screenings from the cache; back is instant, forward again costs nothing; the state rides in the form and the address", async () =>
    {
        await ResetPollerAsync();
        var browser = await SignedInAsync("wizard-pages@example.test", AppLanguage.Polish);
        var today = Today();
        string Day(int days) => WizardState.Text(today.AddDays(days));

        Assert.True((await browser.PageAsync("/")).Contains("href=\"wizard\""));
        var page = await browser.PageAsync("/wizard");
        Assert.True(page.Contains("Kino i termin") && page.Contains($"name=\"from\" value=\"{Day(0)}\"") && Regex.IsMatch(page, "<option value=\"1060\" selected"));

        // The desktop's checks, nothing sent.
        var mark = app.Network.Requests.Count;
        var response = await browser.PostFormAsync("/wizard/go", page, null, new() { ["step"] = "1", ["go"] = "2", ["city"] = "1078", ["from"] = Day(-1) });
        Assert.Equal($"/wizard?city=1078&from={Day(-1)}&max=1", Location(response));
        page = await browser.PageAsync(Location(response));
        Assert.True(StatusText(page).Contains("Data nie może być z przeszłości.") && Regex.IsMatch(page, "<option value=\"1078\" selected"));
        response = await browser.PostFormAsync("/wizard/go", page, null, new() { ["step"] = "1", ["go"] = "2", ["city"] = "all", ["from"] = Day(1), ["to"] = Day(15) });
        Assert.True(StatusText(await browser.PageAsync(Location(response))).Contains("Dla wszystkich miast zakres może obejmować maksymalnie 14 dni."));
        Assert.Equal(mark, app.Network.Requests.Count);

        // A loading in the background: its page shows the progress and reloads itself; another account sees nothing.
        var release = app.WizardPacer.Hold();
        response = await browser.PostFormAsync("/wizard/go", page, null, new() { ["step"] = "1", ["go"] = "2", ["city"] = "1078", ["from"] = Day(1), ["to"] = Day(2), ["s"] = "bad id" });
        var loading = Location(response);
        Assert.True(loading.StartsWith("/wizard/loading/") && loading.EndsWith($"?city=1078&from={Day(1)}&to={Day(2)}&max=2"));
        var jobId = Guid.Parse(loading["/wizard/loading/".Length..loading.IndexOf('?')]);
        var userId = await UserIdAsync("wizard-pages@example.test");
        await WaitUntilAsync(() => Wizard().Find(jobId, userId)!.Done == 1);
        page = await browser.PageAsync(loading);
        Assert.True(page.Contains("Wczytano 1 z 2") && page.Contains($"<meta http-equiv=\"refresh\" content=\"2; url={loading}\""));
        var other = await SignedInAsync("wizard-pages-other@example.test", AppLanguage.Polish);
        Assert.True((await other.PageAsync(loading)).Contains("Tego wczytywania już nie ma"));
        // One loading per account: a second search goes to the running one with a message.
        response = await browser.PostFormAsync("/wizard/go", page, null, new() { ["step"] = "1", ["go"] = "2", ["city"] = "1060", ["from"] = Day(3) });
        Assert.True(Location(response).StartsWith($"/wizard/loading/{jobId}?city=1060"));
        Assert.True(StatusText(await browser.PageAsync(Location(response))).Contains("Twoje poprzednie ładowanie jeszcze trwa."));
        release();
        await Wizard().WhenEndedAsync(jobId);
        Assert.Equal(mark + 2, app.Network.Requests.Count);
        response = await browser.GetAsync(loading);
        Assert.Equal($"/wizard/2?city=1078&from={Day(1)}&to={Day(2)}&max=2", Location(response));

        // Step 2 from the cache: the fake has four IMAX screenings of one film a day.
        page = await browser.PageAsync(Location(response));
        List<string> ids = [.. new[] { 1, 2 }.SelectMany(d => new[] { 6, 13, 19, 24 }.Select(p => FakeCinemaCityHandler.FakeScreeningId("1078", today.AddDays(d), p)))];
        Assert.True(page.Contains("Film i seanse") && page.Contains("Zaznaczono 0 z 8") && page.Contains("Poznań, "));
        Assert.True(ids.All(id => page.Contains($"name=\"s\" value=\"{id}\"")) && page.Contains(ScreeningText.DayHeader(today.AddDays(1))));
        Assert.True(page.Contains("<button type=\"submit\" name=\"go\" value=\"1\" class=\"step\"")); // step 1 is a button of the form
        Assert.Equal(mark + 2, app.Network.Requests.Count);

        response = await browser.PostFormAsync("/wizard/go", page, null, StepForm(page, "all"));
        page = await browser.PageAsync(Location(response));
        Assert.True(Location(response).Contains($"&s={string.Join("%2C", ids)}&") && page.Contains("Zaznaczono 8 z 8"));

        // Next checks the selection; a screening that is not listed is dropped; step 3 is not built yet.
        response = await browser.PostFormAsync("/wizard/go", page, null, StepForm(page, "3", []));
        Assert.True(StatusText(await browser.PageAsync(Location(response))).Contains("Zaznacz co najmniej jeden seans."));
        response = await browser.PostFormAsync("/wizard/go", page, null, StepForm(page, "3", [ids[0], ids[5], "999999"]));
        Assert.True(Location(response).Contains($"&s={ids[0]}%2C{ids[5]}&"));
        page = await browser.PageAsync(Location(response));
        Assert.True(StatusText(page).Contains("Krok 3 (mapa sali) jest jeszcze w budowie.") && page.Contains("Zaznaczono 2 z 8"));

        // Back is instant and keeps the ticks; forward again opens step 2 from the cache, nothing sent.
        response = await browser.PostFormAsync("/wizard/go", page, null, StepForm(page, "1", [ids[0], ids[5]]));
        Assert.True(Location(response).StartsWith("/wizard?city=1078"));
        page = await browser.PageAsync(Location(response));
        Assert.True(page.Contains($"name=\"s\" value=\"{ids[0]},{ids[5]}\"") && page.Contains("<button type=\"submit\" name=\"go\" value=\"2\" class=\"step\""));
        var stepOne = StepForm(page, "2");
        Assert.True(Regex.IsMatch(page, "<option value=\"1078\" selected") && page.Contains($"name=\"from\" value=\"{Day(1)}\"") && page.Contains($"name=\"to\" value=\"{Day(2)}\""));
        (stepOne["city"], stepOne["from"], stepOne["to"]) = ("1078", Day(1), Day(2)); // the visible fields, as the page shows them
        response = await browser.PostFormAsync("/wizard/go", page, null, stepOne);
        Assert.True(Location(response).StartsWith("/wizard/2?city=1078"));
        Assert.True((await browser.PageAsync(Location(response))).Contains("Zaznaczono 2 z 8"));
        Assert.Equal(mark + 2, app.Network.Requests.Count);

        // A refused loading says why on step 1; an expired repertoire asks to load again; no form without a token.
        await WithService(async services => await services.GetRequiredService<OperatorService>().PauseAsync());
        response = await browser.PostFormAsync("/wizard/go", page, null, new() { ["step"] = "1", ["go"] = "2", ["city"] = "1080", ["from"] = Day(1) });
        Assert.True(StatusText(await browser.PageAsync(Location(response))).Contains("wstrzymane przez właściciela serwisu"));
        await WithService(async services => await services.GetRequiredService<OperatorService>().ResumeAsync());
        app.Services.GetRequiredService<CinemaCityCache>().Clear();
        page = await browser.PageAsync($"/wizard/2?city=1078&from={Day(1)}&to={Day(2)}&max=2");
        Assert.True(page.Contains("Repertuaru dla tego wyszukiwania nie ma już w pamięci serwera") && page.Contains("value=\"reload\""));
        Assert.Equal($"/wizard?city=1060&max=2", Location(await browser.GetAsync("/wizard/2?city=1060&max=2")));
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/wizard/go", new FormUrlEncodedContent(new Dictionary<string, string> { ["go"] = "2" }))).StatusCode);
        Assert.Equal(mark + 2, app.Network.Requests.Count);
    });
}
