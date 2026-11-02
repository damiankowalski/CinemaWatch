using System.Net;
using CinemaWatch.Core;
using CinemaWatch.Saas.Tests;
using CinemaWatch.Server.Accounts;
using CinemaWatch.Server.Data;
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
        var browser = app.CreateBrowser();
        var page = await browser.PageAsync("/Account/Login");
        var response = await browser.PostFormAsync("/Account/Login", page, "login", new() { ["Input.Email"] = email, ["Input.Password"] = password });
        Assert.Equal("/", Location(response));
        return browser;
    }

    TestRunner.Add("Database: the migration creates the snake_case tables", async () =>
    {
        Assert.SequenceEqual(
            ["__EFMigrationsHistory", "data_protection_keys", "user_claims", "user_logins", "user_tokens", "users"],
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
}
