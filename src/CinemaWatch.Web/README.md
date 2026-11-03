# CinemaWatch.Web - SaaS web app (MVP 0)

Private web app for the owner and invited friends (plan: [`docs/SAAS.md`](../../docs/SAAS.md) §5.1). Today
(increments 1 and 2): accounts by invitation, sign-in, password reset, account page, data download, account deletion,
Polish/English UI, privacy page (draft); import of desktop profiles (`*.cinemawatch.json`), the profile list, a profile
limit per account. Background checks and notification e-mails follow in the next increment.

| Project | Contents |
| --- | --- |
| `src/CinemaWatch.Server` | No HTTP: database (EF Core + PostgreSQL, migrations in `Data/Migrations`), accounts (`AccountService`), profiles (`ProfileImport` checks a file, `ProfileService` stores it), e-mail over SMTP; later the poller and the notifier |
| `src/CinemaWatch.Web` | ASP.NET Core, Blazor with static server-side rendering (plain HTML forms, no JavaScript); hosts the background services in MVP 0 |
| `tests/CinemaWatch.Saas.Tests` | Logic tests, database tests and the whole web flow (real app in memory, e-mails recorded) |

How it works:
- Every page needs a signed-in user, except sign-in, password help, set-password, privacy, errors.
- Accounts exist only by invitation. At start-up the owner's account (`Saas:OwnerEmail`) is created and invited once;
  only the owner sees **Users** and can invite. The invited person sets a password through the e-mail link, which also
  confirms the address. Links work 24 hours and once. "Forgot your password?" sends a reset link to an active account
  and a new invitation to an account still waiting for activation - the page never reveals which addresses exist.
- Links in e-mails are built from `Saas:BaseUrl`, never from the request.
- Passwords: at least 12 characters, no character rules; 5 wrong passwords lock the account for 15 minutes.
- Language: the PL / EN switch (cookie, saved to the account when signed in), otherwise the browser's preference
  (Polish if preferred, English for any other language). E-mails use the account's language.
- The database also stores the keys that protect sign-in cookies and links (`data_protection_keys`).
- Profiles: the home page lists the account's profiles and imports up to 10 files at once. Each file is checked
  (format, identifiers, seat keys, booking links on `cinema-city.pl`, sizes; `ProfileImport`) and stored as the
  desktop app saves it; a file with the name of an existing profile replaces it. A profile can be enabled, disabled,
  downloaded and deleted (after a question with "Nie usuwaj"). Someone else's profile id answers 404. No Cinema City
  requests yet.
- Page state that must survive a reload is in the address: `/?profile=<id>` opens a profile, `&confirm=delete` shows
  the deletion question. No JavaScript anywhere; light / dark follows the system setting.
- Profile limit: 5 per account (enabled, disabled and ended alike). The owner sets another limit per account (1-50)
  in the **Users** list; an empty field means the default. Lowering a limit deletes nothing.

## Configuration

| Key | Development | Notes |
| --- | --- | --- |
| `ConnectionStrings:CinemaWatch` | user secrets | PostgreSQL; contains a password |
| `Saas:OwnerEmail` | user secrets | personal data: never in a file in the repository |
| `Saas:BaseUrl` | `appsettings.Development.json`: `http://localhost:5180` | start-up fails without an absolute address |
| `Saas:Limits:MaxProfiles`, `Saas:Limits:MaxScreeningsPerProfile`, `Saas:Limits:MinIntervalMinutes` | `appsettings.json`: `5`, `20`, `10` | start-up fails outside 1-50 profiles or below 1 |
| `Mail:Host`, `Mail:Port`, `Mail:Security`, `Mail:UserName`, `Mail:Password`, `Mail:FromAddress`, `Mail:FromName` | `appsettings.json`: `localhost`, `2525`, `None` (smtp4dev) | the provider is chosen in stage 4; its password only in secrets |

User secrets are a .NET development feature: a JSON file outside the repository,
`%APPDATA%\Microsoft\UserSecrets\cinemawatch-web\secrets.json`, read only in the Development environment.
It is not encrypted - it only keeps secrets out of git.

## Local development (Windows)

Ports: app `http://localhost:5180`, test inbox `http://localhost:5081` (web) and `localhost:2525` (SMTP),
PostgreSQL `localhost:5432` - all on this PC only.

### One-time setup

1. PostgreSQL 18 from the EDB zip archive (no installer, no Windows service): binaries in `C:\DEV\CINEMAWATCH\pgsql`,
   cluster in `C:\DEV\CINEMAWATCH\pgdata`, role `cinemawatch` with `CREATEDB`, database `cinemawatch_dev`:
   ```powershell
   & C:\DEV\CINEMAWATCH\pgsql\bin\initdb.exe -D C:\DEV\CINEMAWATCH\pgdata -U postgres -E UTF8 --no-locale -A scram-sha-256 -W
   & C:\DEV\CINEMAWATCH\pgsql\bin\pg_ctl.exe -D C:\DEV\CINEMAWATCH\pgdata -l C:\DEV\CINEMAWATCH\pgdata.log start
   & C:\DEV\CINEMAWATCH\pgsql\bin\createuser.exe -h localhost -U postgres --createdb --pwprompt cinemawatch
   & C:\DEV\CINEMAWATCH\pgsql\bin\createdb.exe -h localhost -U postgres -O cinemawatch cinemawatch_dev
   ```
2. Tools pinned in `dotnet-tools.json` (`dotnet-ef`, `smtp4dev`), downloaded into the NuGet cache:
   ```powershell
   cd C:\DEV\CINEMAWATCH\repo\CinemaWatch
   dotnet tool restore
   ```
3. User secrets. The password is typed into a hidden prompt (not into the command history) and passed as JSON, so
   characters such as `;` or `"` are safe:
   ```powershell
   $password = [System.Net.NetworkCredential]::new('', (Read-Host 'Password of the role cinemawatch' -AsSecureString)).Password
   $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
   $builder['Host'] = 'localhost'; $builder['Database'] = 'cinemawatch_dev'; $builder['Username'] = 'cinemawatch'; $builder['Password'] = $password
   @{ ConnectionStrings = @{ CinemaWatch = $builder.ConnectionString }; Saas = @{ OwnerEmail = '<your e-mail>' } } | ConvertTo-Json -Compress | dotnet user-secrets set --project src\CinemaWatch.Web
   Remove-Variable password, builder
   ```
4. Database schema:
   ```powershell
   dotnet ef database update --project src\CinemaWatch.Server --startup-project src\CinemaWatch.Web
   ```
   The first run prints one `fail: ... Failed executing DbCommand` about `__EFMigrationsHistory` (EF looks for its
   bookkeeping table before creating it), then `Applying migration '..._InitialAccounts'` and `Done.`
   Run the same command after every update that brings a new migration: the app refuses to start on a database without
   it and names the missing migration.

### Every time

Three PowerShell windows, each in `C:\DEV\CINEMAWATCH\repo\CinemaWatch`:

```powershell
# 1. PostgreSQL (stays running in the background; check with "status", stop with "stop")
& C:\DEV\CINEMAWATCH\pgsql\bin\pg_ctl.exe -D C:\DEV\CINEMAWATCH\pgdata -l C:\DEV\CINEMAWATCH\pgdata.log start

# 2. Test inbox (Ctrl+C stops it; the messages live in memory only)
dotnet smtp4dev --urls=http://localhost:5081 --smtpport=2525 --imapport= --pop3port= --db= --nousersettings --allowremoteconnections- --baseappdatapath=C:\DEV\CINEMAWATCH\smtp4dev

# 3. The app (Ctrl+C stops it)
dotnet run --project src\CinemaWatch.Web
```

On the very first start the app creates the owner's account and sends the invitation: open `http://localhost:5081`,
open the e-mail "Zaproszenie do CinemaWatch", follow the link and set a password. If the inbox was not running at that
moment, the log says so; start it and use "Nie pamiętasz hasła?" on the sign-in page to get a new invitation.

### Tests

Without a database (as in the Windows CI job):
```powershell
dotnet run --project tests\CinemaWatch.Saas.Tests
```
With the database and web tests (PostgreSQL running; each run creates and drops its own `cinemawatch_test_*` database):
```powershell
$password = [System.Net.NetworkCredential]::new('', (Read-Host 'Password of the role cinemawatch' -AsSecureString)).Password
$builder = [System.Data.Common.DbConnectionStringBuilder]::new()
$builder['Host'] = 'localhost'; $builder['Database'] = 'postgres'; $builder['Username'] = 'cinemawatch'; $builder['Password'] = $password
$env:CINEMAWATCH_TEST_DB = $builder.ConnectionString
Remove-Variable password, builder
dotnet run --project tests\CinemaWatch.Saas.Tests
Remove-Item Env:CINEMAWATCH_TEST_DB
```

### A new migration (after changing the data model)

```powershell
dotnet ef migrations add <Name> --project src\CinemaWatch.Server --startup-project src\CinemaWatch.Web --output-dir Data/Migrations
dotnet ef database update --project src\CinemaWatch.Server --startup-project src\CinemaWatch.Web
```
The test "the migrations match the code" fails when a model change has no migration. Undo a migration that is already
applied: `dotnet ef database update <previous migration>` (its `Down` runs), then
`dotnet ef migrations remove` (same `--project` / `--startup-project` arguments).

### What lands outside the repository, and how to remove it

| What | Where | Remove |
| --- | --- | --- |
| PostgreSQL binaries, cluster, log | `C:\DEV\CINEMAWATCH\pgsql`, `pgdata`, `pgdata.log` | stop the server, delete the folders and the log |
| User secrets | `%APPDATA%\Microsoft\UserSecrets\cinemawatch-web` | `dotnet user-secrets clear --project src\CinemaWatch.Web`, then delete the folder |
| smtp4dev settings folder | `C:\DEV\CINEMAWATCH\smtp4dev` | delete the folder |
| smtp4dev's ASP.NET key | `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys` | the folder is shared by all ASP.NET Core apps run as you: delete it only if no other app uses it |
| Tools and packages | NuGet cache `%USERPROFILE%\.nuget\packages` | `dotnet nuget locals all --clear` (all projects re-download) |
