# CinemaWatch.Web - developer guide (SaaS 0.7.0)

Private web app for the owner and invited friends; the overview for readers is [`docs/WEB.md`](../../docs/WEB.md),
the plan [`docs/SAAS.md`](../../docs/SAAS.md). This file is for developers: how the app behaves in detail, its
configuration, local development and tests. Today (SaaS 0.7.0, MVP 1): accounts by invitation, sign-in, password
reset, account page, data download, account deletion, Polish/English UI, the privacy notice; the wizard and the hall
map for new profiles and for editing any profile, import of desktop profiles (`*.cinemawatch.json`), the profile list,
a profile limit per account; background checks of the watched screenings, notification e-mails with the booking link,
the notification history, "Check now" and a test e-mail; the operator's panel (status of the components, request
statistics, events, a pause switch, poller and wizard settings) and a heartbeat for an external "dead man's switch".
On the server it runs behind Cloudflare Access for the whole site ([`docs/SAAS_INFRA.md`](../../docs/SAAS_INFRA.md)).

| Project | Contents |
| --- | --- |
| `src/CinemaWatch.Server` | No HTTP: database (EF Core + PostgreSQL, migrations in `Data/Migrations`), accounts (`AccountService`), profiles (`ProfileImport` checks a file, `ProfileService` stores it), e-mail over SMTP, the poller (`Polling/`: `PollerCycle`, `RequestGuard`, `UpstreamBlocks`, `CheckService`, `PollerSettings`, the development fake), the wizard's Cinema City data on demand (`Wizard/`: `WizardJobs`, `CinemaCityCache`, `LoadingTokens`; the wizard's state and step 2's lists: `WizardState`, `ScreeningChoices`), the operator's panel and the heartbeat (`Operations/`) |
| `src/CinemaWatch.Web` | ASP.NET Core, Blazor with static server-side rendering (plain HTML forms; one small script for the hall map, every page works without it); hosts the background services in MVP 0; the wizard's pages in `Components/Pages/Wizard*.razor` and `Components/Wizard/` |
| `tests/CinemaWatch.Saas.Tests` | Logic tests, database tests and the whole web flow (real app in memory, e-mails recorded) |

How it works:
- Every page needs a signed-in user, except sign-in, password help, set-password, privacy, errors, `/robots.txt` and
  `/healthz`. A visitor without an account can cause no cost and no damage (docs/SAAS.md §9): those pages create no
  database rows (a wrong password only raises an existing account's failed-sign-in counter), send no request to
  Cinema City and only a limited number of e-mails, and nothing they answer tells whether an address has an account.
- Accounts exist only by invitation. At start-up the owner's account (`Saas:OwnerEmail`) is created and invited once;
  only the owner sees **Operator** and **Users** and can invite. The invited person sets a password through the e-mail link, which also
  confirms the address. Links work 24 hours and once. "Forgot your password?" sends a reset link to an active account
  and a new invitation to an account still waiting for activation. The page only queues the address
  (`PasswordHelpQueue`, bounded, 100) and answers at once, the same for every address; the e-mail follows in the
  background, at most one per account per 15 minutes and 20 a day for all accounts (`PasswordHelpLimits`, in memory;
  reaching the daily limit records an event).
- The owner deletes an account in **Users** (`/admin/users`): a question first (`?delete=<id>` in the address), then the
  account with all its data (profiles, notification state, history); an active account's person gets a short e-mail,
  a waiting account is an invitation withdrawn without one; never the owner's own account (refused on the server).
  The event in the panel names no address. A person can delete their own account on the account page.
- A signed-in session is checked against the database every minute: a deleted account or a changed password ends it
  within a minute.
- Links in e-mails are built from `Saas:BaseUrl`, never from the request.
- Passwords: at least 12 characters, no character rules; 5 wrong passwords lock the account for 15 minutes. The
  sign-in form answers no faster than `Saas:SignInAnswerMilliseconds` and, unless it succeeds, says the same for an
  unknown address, a waiting, a locked or an existing account (the old lockout page is gone).
- Every answer carries `X-Robots-Tag: noindex, nofollow`; `/robots.txt` disallows everything.
- `/admin` is answered only exactly as written: `/ADMIN`, `/%61dmin`, `//admin` and the like get 404, so nothing reaches
  the owner's pages past a path rule at the edge (the Cloudflare Access application for `/admin`).
- A form posted without a valid antiforgery token gets a short plain-text 400 (the form has expired).
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
- Several profiles can be open at once. The header of a profile is a link that opens or closes it; the open profiles
  live in the address (`?profile=…` for the one in focus, `?open=…` for the others), so the page's own reloads,
  "Check now", enable, disable and delete keep them open until you close them.
- **New profile and editing (wizard, SaaS 0.7.0):** the button on the home page opens `/wizard`, the desktop app's
  steps (step bar at the top: the steps reached so far and the next one are buttons). Step 1: the city (one IMAX
  cinema each, or all six) and a date or a range ("To" empty = one day; 31 days, 14 for all cities; the desktop's
  messages). *Next* opens step 2 at once when the shared cache holds the whole repertoire; otherwise a loading starts
  in the background and `/wizard/loading/<id>` shows its progress, reloads itself every 2 s and then opens step 2. A
  refused loading (the owner's pause, a block, a used-up budget or limit) says on step 1 when to try again. Step 2
  (`/wizard/2`): the films with IMAX screenings without dubbing that have not started, the chosen film's screenings
  by day and cinema with check boxes, "select all / clear all", "Show screenings" after choosing another film (no
  JavaScript); missing days can be loaded again. *Next* loads the hall maps (details, the layout from `seat_plans`,
  free seats) the same way, unless everything is known. Step 3 (`/wizard/3`): one hall map per tab, drawn from the
  geometry the desktop map uses (`CinemaWatch.Core` `HallLayout`): every seat a check box drawn as a circle (free
  green, taken grey, watched ringed), row numbers that toggle the row, the minimum of adjacent seats, "Copy to
  screenings in the same hall", "Clear", "Refresh the occupancy" when it is older than 10 minutes. The site's only
  script, `wwwroot/seatmap.js` (no libraries, loaded on every page, active only on a map), adds painting (press on
  a seat and drag), the rectangle (press on the background and drag), Esc, row numbers without a reload and live
  counts - the rules of `CinemaWatch.Core` `SeatSelection`, tested against the same case file. With a finger a tap
  toggles a seat and dragging scrolls; without JavaScript everything works through the form. Step 4 (`/wizard/4`):
  the desktop's summary - the screenings with their watched seats row by row (screenings without seats are skipped),
  the notification condition, the name (default as in the desktop: film, city, dates) and the interval (the
  desktop's 5/10/15/30/60 minutes, none below `Saas:Limits:MinIntervalMinutes`). *Save* builds the profile as the
  desktop app does (`Server/Wizard/WizardProfile`) and stores it through the import's checks and limits
  (`ProfileService.SaveAsync`); a name another profile has is refused, never replaced. **Edit** on the profile list
  (`POST /wizard/edit/<id>`) opens the wizard on a stored profile: started screenings drop out, the dates start
  today, the seats come from the stored profile once the hall maps are known (missing ones load first, then
  `/wizard/seed` fills them in) and it ends on step 4. Saving an edit keeps the id and the enabled state, and the
  notification state of the screenings and seats still watched (a seat already reported free is not reported
  again). All of it posts to `/wizard/go`; the state (`city`, `from`, `to`, `film`, `s`, `max`, `m` - each
  screening's seats as one bit per seat of its layout, Base64Url (`SeatCode`) -, `tab`, `adj`, `name`, `int`, and
  when editing `p` and `seed`) travels in hidden fields and in the address of the page the answer redirects to -
  nothing in the database until *Save*.
- Profile limit: 5 per account (enabled, disabled and ended alike). The owner sets another limit per account (1-50)
  in the **Users** list; an empty field means the default. Lowering a limit deletes nothing.

### The poller (background checks)

- A background loop in the web app (`PollerHostedService`) runs a cycle every 30 seconds (`PollerCycle`). It keeps
  `watched_screenings` in step with the enabled, unfinished profiles: one row per unique screening, checked once for
  every profile that watches it. A new screening is checked within a minute; afterwards every 10-12 minutes (the
  profile's interval, never below `Saas:Limits:MinIntervalMinutes`, plus up to 20% at random).
- One check = screening details + free seats (the pair the R1 probe measured), sent directly one after the other;
  the hall layout comes from the database (`seat_plans`, fetched again after 7 days). Between two screenings 3-6
  seconds at random in the background, 1-2 seconds for "Check now" (5 screenings take about 10 seconds). A global budget
  (120 requests an hour, refilled steadily) covers scheduled and manual checks; when it is used up, due screenings
  wait in order.
- Failed check (HTTP 5xx, another 4xx, timeout, network error, unreadable answer): the notification state stays,
  no retry, the next attempt at the normal time. Block (HTTP 403 / 429, a Cloudflare challenge, HTML instead of JSON):
  nothing more is sent in the cycle, all requests pause for 1 hour, doubling with every block in a row up to 24 hours
  (a longer `Retry-After` wins); pending "Check now" requests are dropped; after the pause the blocked screening is
  tried first, alone. The owner gets an e-mail at the start of a block and when it ends; users see a banner and a
  greyed-out "Check now". Blocks, resumptions, an exhausted budget and mail failures are recorded in
  `service_events`; every request in `check_log` (the probe's columns).
- Notifications: per profile at most one e-mail per cycle, "Wolne miejsca: {film}" in the account's language, one
  line per screening with its booking link, plus a link to the profile. The state (`notification_state`) is saved only
  after the e-mail went out; a refused e-mail is recorded in the history and the seats are notified again at the next
  check. History is kept 90 days, the request log 30 days.
- "Check now" (per profile, no cooldown): greyed out only while that profile's check runs or checks are paused. Cinema
  City is asked only about screenings checked more than a minute ago; fresher ones answer at once from the stored
  result. Each screening's result is saved as soon as it is known and the page reloads itself every 3 seconds
  (`<meta http-equiv="refresh">`), so the rows fill in one by one ("sprawdzam…" until then). During a pause the
  page reloads when the pause ends, then every 10 seconds until the first request has got through. The reload goes to
  the page's address with a new `refresh=` value each time: a reload to the same address ending in `#profile-…` would
  only jump to the anchor. The pause between
  requests and the global budget limit what reaches Cinema City, however often the button is pressed.
- "Send a test e-mail" on the account page: a sample notification, at most every 5 minutes, no Cinema City request.
  When `Mail:Host` is `localhost`, the page says the e-mail went to the local test inbox (smtp4dev), not to the real
  mailbox.
- The numbers above are the defaults from the configuration; the operator's panel can change some of them (below).

### The operator's panel (owner only)

`/admin`, **Operator** in the menu. Like **Users** it is open only to the account whose e-mail is `Saas:OwnerEmail`,
checked on the server for the page and for every form. All owner pages and forms live under `/admin`, so stage 4 can
put Cloudflare Access on `/admin*` as a second, independent gate. The page does not reload itself (a reload would wipe
what is being typed into the settings form): **Odśwież** reloads it.

- **Status of the components**, each with a dot and a word (working / attention / problem / off):
  - poller: the last cycle (none for 5 minutes = problem), the owner's pause, a block's pause, watched and due
    screenings, the source (the fake is named);
  - Cinema City: the last request and its answer, the last successful answer, the last block (90 days);
  - e-mail: the last e-mail sent and the last error of any e-mail (invitations, test e-mails, notifications, alerts;
    `component_status`), the mail server; addresses are cut out of stored error texts;
  - database: PostgreSQL version, size, last migration, the largest tables with exact row counts;
  - heartbeat: off without an address; otherwise the last ping and the last error. Only the host of the address is
    shown: the rest is a secret.
- **Pause**: "Wstrzymaj sprawdzanie" stops all requests to Cinema City (background and "Check now") from the next
  cycle until "Wznów sprawdzanie", restarts included (`poller_state.paused_by_operator_at`). Pending "Check now"
  requests are dropped; users see a notice and a greyed-out button; nobody is e-mailed. A block's pause, if one is in
  force, is independent of it.
- **Requests**: the last hour against the budget, the last 24 hours hour by hour (with a bar for the hour's share of
  the budget), answers per HTTP status for 24 hours and 7 days, the latest 20 requests (from `check_log`).
- **Events**: the latest 100 of `service_events` (kept 90 days), with details such as a block's response.
- **Poller settings**: the minimum interval, the random addition, the hourly budget, the pauses between screenings
  (background and "Check now"), how fresh a stored result "Check now" may reuse, the pauses after a block and
  `ReuseScreeningDetails`, each within a range fixed in code (`PollerTuning`; e.g. budget 10-300 an hour, minimum
  interval 5-60 minutes). The panel stores only what differs from the configuration (`poller_settings`); an empty field
  or the configuration's value means "from the configuration", so a later change of `appsettings.json` applies again.
  A configuration value outside the panel's range (Development pauses only 2 minutes after a block) is shown and kept,
  but cannot be set in the panel. A change applies from the next cycle (`PollerSettings` is read at the start of every
  cycle) and is recorded as an event with the old and new values. "Przywróć wszystkie wartości z konfiguracji"
  removes all changes. Infrastructure values (`Enabled`, `Source`, `TickSeconds`, retention) and the profile limits stay
  in the configuration.

### Heartbeat ("dead man's switch")

After every poller cycle that finished (idle, paused and blocked cycles included), at most every
`Saas:Heartbeat:IntervalMinutes` (5), the server sends a GET to `Saas:Heartbeat:Url` (10-second timeout, not a Cinema
City request: no budget, no `check_log`). An external service such as Healthchecks.io e-mails the owner when the
pings stop: the server, the process or the poller is down, or every cycle fails. A failed ping is recorded and never
stops the poller. The address works like a password (whoever knows it can report "alive" for the server): user
secrets or an environment variable only, never a file in the repository; https only; empty = off (the default, also on
the PC: every stop of the app would otherwise raise an alarm). The service and its account come in stage 4
([`docs/SAAS.md`](../../docs/SAAS.md) §7).

## Configuration

| Key | Development | Notes |
| --- | --- | --- |
| `ConnectionStrings:CinemaWatch` | user secrets | PostgreSQL; contains a password |
| `Saas:OwnerEmail` | user secrets | personal data: never in a file in the repository |
| `Saas:BaseUrl` | `appsettings.Development.json`: `http://localhost:5180` | start-up fails without an absolute address |
| `Saas:Limits:MaxProfiles`, `Saas:Limits:MaxScreeningsPerProfile`, `Saas:Limits:MinIntervalMinutes` | `appsettings.json`: `5`, `20`, `10` | start-up fails outside 1-50 profiles or below 1 |
| `Saas:Poller:Source` | `appsettings.Development.json`: `Fake` (`appsettings.json`: `CinemaCity`) | `Fake` is refused outside the Development environment |
| `Saas:Poller:FakeDirectory` | `appsettings.Development.json`: `C:\DEV\CINEMAWATCH\saas-fake` | created with a `README.txt` at start-up |
| `Saas:Poller:RequestsPerHour`, `RequestGapMinSeconds`, `RequestGapMaxSeconds` | `appsettings.json`: `120`, `3`, `6` | global budget and pause between screenings in the background |
| `Saas:Poller:ManualGapMinSeconds`, `ManualGapMaxSeconds` | `appsettings.json`: `1`, `2` | pause between screenings for "Check now" |
| `Saas:Poller:ManualFreshMinutes` | `appsettings.json`: `1`; Development: `0` | how old a result "Check now" may reuse instead of asking |
| `Saas:Poller:FirstBlockPauseMinutes`, `MaxBlockPauseMinutes` | `appsettings.json`: `60`, `1440`; Development: `2`, `10` | pause after a block, doubling |
| `Saas:Poller:Enabled`, `TickSeconds`, `JitterPercent`, `ReuseScreeningDetails`, `CheckLogDays`, `HistoryDays` | `appsettings.json`: `true`, `30`, `20`, `false`, `30`, `90` | `ReuseScreeningDetails = true`: one request per check instead of two (not measured yet) |
| `Saas:Poller:Wizard:RequestsPerHour`, `Burst`, `AccountRequestsPerHour`, `GapSeconds` | `appsettings.json`: `120`, `90`, `90`, `1` | the wizard's own budget (next to the poller's), how much of it can be saved up, the limit of one account (cache misses only) and the pause between its requests |
| `Saas:Poller:Wizard:RepertoireNearMinutes`, `RepertoireLaterMinutes`, `DetailsMinutes`, `FreeSeatsMinutes`, `KeepResultMinutes` | `appsettings.json`: `30`, `180`, `1440`, `10`, `30` | how long the shared cache keeps the repertoire (today and tomorrow / later days), screening details and free seats; how long a finished loading's result is kept |
| `Saas:Heartbeat:Url` | empty (off); user secrets for a test | a secret; empty or https; start-up fails otherwise |
| `Saas:Heartbeat:IntervalMinutes` | `appsettings.json`: `5` | 1-60; the external check's period should be at least this |
| `Mail:Host`, `Mail:Port`, `Mail:Security`, `Mail:UserName`, `Mail:Password`, `Mail:FromAddress`, `Mail:FromName` | `appsettings.json`: `localhost`, `2525`, `None` (smtp4dev) | the provider is chosen in stage 4; its password only in secrets |
| `Saas:TrustForwardedHeaders` | `false` | server: `true` - trust `X-Forwarded-Proto` / `X-Forwarded-For` from the tunnel (only where nothing else can reach the app) |
| `Saas:MigrateDatabaseOnStart` | `false` (start refused with missing migrations) | server: `true` - missing migrations are applied at start-up |
| `Saas:SignInAnswerMilliseconds` | `1000` | 0-5000; the sign-in form never answers faster (the tests use 40) |

An https `Saas:BaseUrl` also makes the sign-in cookies Secure. The form (antiforgery) cookie follows the request: it is
Secure behind the trusted tunnel, and a page never fails because a header is missing.

At start-up the app checks that the system has the time-zone data (Europe/Warsaw) and ICU (Polish dates) and refuses
to start without them. `GET /healthz` answers `ok` (200) when the database answers, `503` otherwise; it needs no
sign-in and reveals nothing else. It answers only inside the server (the deployment script, the container test): a
request that came through Cloudflare (`CF-Connecting-IP` or `CF-Ray` header) gets 404.

The minimum interval and the `Saas:Poller` values for the budget, the pauses, `ManualFreshMinutes`, `JitterPercent`
and `ReuseScreeningDetails`, and the wizard's budget, burst, account limit and pause are defaults: the operator's panel
can change them within fixed ranges. The cache times are configuration only.

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

### Checks against the fake (Development)

In Development the poller never talks to Cinema City: it uses captured responses (the Sadyba IMAX hall) steered by
files in `C:\DEV\CINEMAWATCH\saas-fake` (the app creates the folder and a `README.txt` in it). The log says
`Poller uses the development FAKE of Cinema City`. Screening ids are those of your profiles (shown in the page
address of the booking link, e.g. `.../order/1765592`). The wizard (from SaaS 0.7.0) gets every cinema's repertoire on
any day from the fake too: the captured Sadyba day of 16 Dec 2026 moved to that cinema and day, with screening ids
`<cinema><yyMMdd><position>` - e.g. `106026121613` is the 16:00 IMAX screening in Sadyba on 16 Dec 2026. A fake folder
from an older version keeps its old `README.txt`; delete the file and it is written again at the next start.

```powershell
# A seat frees up (keys as in the profile, e.g. row 4 seat 19 in Sadyba = 1_18_4)
Set-Content C:\DEV\CINEMAWATCH\saas-fake\free-1765592.txt '1_18_4'
# ...and is taken again
Remove-Item C:\DEV\CINEMAWATCH\saas-fake\free-1765592.txt
# Cinema City answers HTTP 500 (all screenings / one screening); delete the file to stop
Set-Content C:\DEV\CINEMAWATCH\saas-fake\error500.txt ''
Set-Content C:\DEV\CINEMAWATCH\saas-fake\error500-1765592.txt ''
# A block: 403 with a Cloudflare challenge ('429 120' inside = HTTP 429 with Retry-After 120 s)
Set-Content C:\DEV\CINEMAWATCH\saas-fake\block.txt ''
```

Changes apply to the next request; "Sprawdź teraz" on the profile page checks at once (in Development it always asks
the fake). All e-mails, the alerts to the owner included, go to the local test inbox (`http://localhost:5081`), never
to a real mailbox. Results: the e-mail in `http://localhost:5081`, the state and history on the profile page, the log in the
app window. In Development a block pauses for 2 minutes (doubling up to 10).

### One check against the real Cinema City (on demand only)

Your home IP also carries the desktop control group of the R1 test, so the real API is used once, by hand: stop the
app, start it with the real source for one run, press "Sprawdź teraz" on one profile, stop it again.

```powershell
$env:Saas__Poller__Source = 'CinemaCity'
dotnet run --project src\CinemaWatch.Web
# ... "Sprawdź teraz" once, check the page and the log (Cycle ...: 1 checked ...), then Ctrl+C
Remove-Item Env:Saas__Poller__Source
```

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

The hall map's script against the case file shared with the C# tests (Node.js; on the development PC the zip in
`C:\DEV\CINEMAWATCH\node`, not on PATH; CI uses the runner's Node):
```powershell
& C:\DEV\CINEMAWATCH\node\node.exe --test tests\web\seatmap.test.js
```

### A new migration (after changing the data model)

```powershell
dotnet ef migrations add <Name> --project src\CinemaWatch.Server --startup-project src\CinemaWatch.Web --output-dir Data/Migrations
dotnet ef database update --project src\CinemaWatch.Server --startup-project src\CinemaWatch.Web
```
The test "the migrations match the code" fails when a model change has no migration. Undo a migration that is already
applied: `dotnet ef database update <previous migration>` (its `Down` runs), then
`dotnet ef migrations remove` (same `--project` / `--startup-project` arguments).

## Container image (server)

The server runs the app as a container ([`docs/SAAS.md`](../../docs/SAAS.md) §4.4):

| File | Contents |
| --- | --- |
| `src/CinemaWatch.Web/Dockerfile` | Build on `sdk:10.0-resolute`, run on `aspnet:10.0-resolute-chiseled-extra` (Ubuntu 26.04: no shell, non-root user `app`, port 8080, ICU and tzdata); build context = the repository root (`.dockerignore`) |
| `infra/compose/compose.yaml` | `postgres` (18), `web` (the image, read-only file system), `cloudflared` (the tunnel); no published ports; log size limits |
| `infra/compose/env.example` | Template of the server's `.env` (secrets and settings; never in git) |
| `infra/compose/smoke-test.sh` | Container test: starts `postgres` and `web` from `compose.yaml` with throwaway settings and checks the running app |
| `infra/compose/deploy.sh` | Deployment on the server: `pg_dump`, the compose file of the tag `web-vX.Y.Z`, the version into `.env`, pull, start, wait for `/healthz` ([`docs/SAAS_INFRA.md`](../../docs/SAAS_INFRA.md) §4.24) |

CI (`.github/workflows/ci.yml`, job `container`) builds the image and runs the container test on every push - no
Docker is needed on the PC. A tag `web-vX.Y.Z` (or `web-vX.Y.Z-beta.N`) runs `.github/workflows/web-image.yml`: the
same build and test, then the image is published as `ghcr.io/damiankowalski/cinemawatch-web:X.Y.Z` (and
`sha-<commit>`). The tag must match `<Version>` in `CinemaWatch.Web.csproj`. Desktop tags (`v*`) never match it.

### What lands outside the repository, and how to remove it

| What | Where | Remove |
| --- | --- | --- |
| PostgreSQL binaries, cluster, log | `C:\DEV\CINEMAWATCH\pgsql`, `pgdata`, `pgdata.log` | stop the server, delete the folders and the log |
| User secrets | `%APPDATA%\Microsoft\UserSecrets\cinemawatch-web` | `dotnet user-secrets clear --project src\CinemaWatch.Web`, then delete the folder |
| smtp4dev settings folder | `C:\DEV\CINEMAWATCH\smtp4dev` | delete the folder |
| Fake Cinema City folder | `C:\DEV\CINEMAWATCH\saas-fake` | delete the folder (recreated at the next start) |
| smtp4dev's ASP.NET key | `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys` | the folder is shared by all ASP.NET Core apps run as you: delete it only if no other app uses it |
| Tools and packages | NuGet cache `%USERPROFILE%\.nuget\packages` | `dotnet nuget locals all --clear` (all projects re-download) |
