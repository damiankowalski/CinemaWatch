using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CinemaWatch.Core;
using CinemaWatch.Saas.Tests;
using CinemaWatch.Server.Accounts;
using CinemaWatch.Server.Data;
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
    Assert.Equal("Diuna Część trzecia.cinemawatch.json", ProfileImport.FileNameFor("Diuna: Część trzecia"));
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
            ["__EFMigrationsHistory", "data_protection_keys", "profiles", "user_claims", "user_logins", "user_tokens", "users"],
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
        Assert.True(page.Contains($"<details class=\"profile\" id=\"profile-{id}\">")); // closed

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
        Assert.True(page.Contains($"<details class=\"profile\" id=\"profile-{id}\" open>"));

        // "Delete" asks first; the question lives in the address and "Keep it" goes back to the opened profile.
        Assert.True(!page.Contains("Tak, usuń profil") && page.Contains($"href=\"?profile={id}&confirm=delete#profile-{id}\""));
        page = await browser.PageAsync($"/?profile={id}&confirm=delete");
        Assert.True(page.Contains("Usunąć profil „Diuna IMAX”? Plik w aplikacji na Windows zostaje.") && page.Contains("Tak, usuń profil"));
        Assert.True(page.Contains($"href=\"?profile={id}#profile-{id}\">Nie usuwaj</a>"));

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
}
