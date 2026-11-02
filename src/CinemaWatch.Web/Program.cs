using CinemaWatch.Server;
using CinemaWatch.Server.Accounts;
using CinemaWatch.Server.Data;
using CinemaWatch.Server.Mail;
using CinemaWatch.Web;
using CinemaWatch.Web.Components;
using CinemaWatch.Web.Components.Account;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.WebEncoders;
using System.Text.Encodings.Web;
using System.Text.Unicode;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString(DatabaseSetup.ConnectionStringName)
    ?? throw new InvalidOperationException(
        "The connection string 'CinemaWatch' is missing. Development: set it with dotnet user-secrets (src/CinemaWatch.Web/README.md).");

// Settings
builder.Services.AddOptions<SaasOptions>()
    .Bind(builder.Configuration.GetSection(SaasOptions.Section))
    .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _), "Saas:BaseUrl must be an absolute address, e.g. http://localhost:5180")
    .ValidateOnStart();
builder.Services.AddOptions<MailOptions>().Bind(builder.Configuration.GetSection(MailOptions.Section));

// Database; the data-protection keys (sign-in cookies, e-mail links) are kept there too.
builder.Services.AddDbContext<AppDbContext>(options => options.UseCinemaWatchDatabase(connectionString));
builder.Services.AddDataProtection().PersistKeysToDbContext<AppDbContext>().SetApplicationName("CinemaWatch");

// Accounts (ASP.NET Core Identity): invitation only, e-mail = user name, long passwords instead of character rules.
builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "cinemawatch.auth";
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromDays(14);
    options.SlidingExpiration = true;
});
builder.Services.AddIdentityCore<AppUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = true;
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddErrorDescriber<LocalizedIdentityErrors>();
builder.Services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = AccountService.LinkLifetime);

// Everything needs a signed-in user unless a page or endpoint says [AllowAnonymous]; invitations need the owner.
builder.Services.AddSingleton<IAuthorizationHandler, OwnerAuthorizationHandler>();
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(OwnerAuthorizationHandler.Policy, policy => policy.RequireAuthenticatedUser().AddRequirements(new OwnerRequirement()));

builder.Services.AddRazorComponents();
// Polish letters stay readable in the HTML instead of becoming numeric entities.
builder.Services.Configure<WebEncoderOptions>(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IMailSender, SmtpMailSender>();
builder.Services.AddScoped<AccountService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseWebLanguage();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>();
app.MapAccountEndpoints();
app.MapLanguageEndpoint();

await app.PrepareDatabaseAsync();
app.Run();
