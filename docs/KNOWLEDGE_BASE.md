# CinemaWatch - Knowledge Base

> Single source of truth for the product, the Cinema City API, the rules the app follows and the decisions taken.
> Order of trust when something disagrees: **repository code** > **this document** > chat history.
> Keep it current: update *Status*, *Version history* and *Decision log* with every release or decision.
>
> Last updated: October 2026 (desktop 1.4.0).

---

## 1. Product

**CinemaWatch** notifies you when a seat you want frees up for a sold-out Cinema City screening. Popular IMAX
screenings sell out weeks ahead, but seats keep coming back (unpaid reservations expire, tickets are returned).
CinemaWatch watches the rows / seats you would actually buy and tells you the moment one becomes free, with a
direct link to the booking page.

It only **reads** public seat information. It never logs in, never creates a cart, never reserves or buys anything.

| Track | State | Notes |
| --- | --- | --- |
| Original PowerShell script | Done, kept in `scripts/CinemaWatch.ps1` | E-mail notifications via Gmail SMTP; the origin of the project |
| **Desktop (Windows)** | **1.4.0 stable** - feature-complete for now | WPF, .NET 10; repo `github.com/damiankowalski/CinemaWatch` (MIT) |
| Android | Planned - concept agreed, nothing built | "Proxy" that checks profiles exported by the desktop app |
| SaaS (web) | Future idea - exploration / tests only | Fully web-based app |

---

## 2. Roadmap and product tracks

### 2.1 Desktop - done for now (1.4.0)

Wizard (city & dates → film & screenings → hall map → summary), clickable steps, profile tabs, check now / check
all, background checks via Windows Task Scheduler with Windows notifications, Polish/English UI, six IMAX cinemas.
Desktop has priority; work on other tracks must not take ideas away from it.

Backlog ideas (not planned yet):
- Other technologies (2D, 3D, 4DX) and the remaining Cinema City cinemas - the data model is ready.
- Run `scripts/CinemaWatch.ps1` on a schedule via GitHub Actions (raised early on; open problems: secrets instead of
  DPAPI credentials, state between runs, Cloudflare possibly blocking GitHub runner IPs).

### 2.2 Android - planned ("profile proxy")

**Concept (agreed):** the desktop app creates the profile (`*.cinemawatch.json`); the phone app imports it,
checks Cinema City on a schedule with minimal battery impact, and shows a notification with the booking link.
- No seat map, no wizard, no profile editing on the phone (full native port with seat-map drawing is shelved).
- No own server/backend for now.
- Android first; iOS maybe later.

What the phone needs per check - see the algorithm in **§5.6**. Good news for the mobile design: matching works
from the profile's seat keys alone (the seat position is part of the key), so the phone does not need the seat plan
to decide *whether* to notify; it needs it (or an optional future `seatLabels` field) only to *name* the seats.

Open decisions:
| Question | Options / notes |
| --- | --- |
| Tech stack | .NET (MAUI / .NET for Android) reusing `CinemaWatch.Core` vs Kotlin + Jetpack Compose (native; the checking logic in §5.4 re-implemented) |
| Profile transfer | file share / open-with, cloud folder, QR code (profiles are small JSON) |
| Profile updates | re-import replaces; identity = file name or a future `profileId` |
| Scheduling | WorkManager periodic work: **minimum 15 min**, delayed in Doze / battery saver |
| Notifications | Android 13+ needs the `POST_NOTIFICATIONS` permission; tap → booking link |
| Distribution | APK via GitHub Releases first (signing key only in GitHub Secrets); Play Store later |
| Profile extensions for mobile | optional `seatLabels` (key → "row 8, seat 12"), `venueTypeId` / `isReserved` per screening to save a request |

### 2.3 SaaS (web) - future, exploratory

Fully web-based app (accounts, seat map in the browser, server-side checks, web push / e-mail).
Things to validate before building:
- **Server-side polling from datacenter IPs** is far more likely to be blocked by Cloudflare than a home PC.
- **Fan-out**: poll each screening once for all users who watch it (the "check all" de-duplication, at scale).
- Cinema City terms of use; accounts, privacy (GDPR); cost; abuse prevention.
- Reuse: `CinemaWatch.Core` is UI-free .NET and could run in an ASP.NET Core worker.

---

## 3. Cinema City API (unofficial, read-only)

Same endpoints the website uses. Unofficial: may change without notice. All responses are JSON (UTF-8).

### 3.1 Request basics

| Item | Value |
| --- | --- |
| Tenant (Poland) | `10103` |
| Required header | `uuid: <random GUID>` - one per client/session, like the browser |
| User-Agent | `Mozilla/5.0 (Windows NT 10.0; Win64; x64) CinemaWatch/<version>` (honest, identifies the app) |
| Cookies | keep a cookie container per session |
| Never used | `POST tickets.cinema-city.pl/api/cart/token` (cart, seat locking) - protected by Cloudflare Turnstile |

### 3.2 Endpoints

| Purpose | Request |
| --- | --- |
| Cinema list | `GET https://www.cinema-city.pl/pl/data-api-service/v1/quickbook/10103/cinemas/with-event/until/{yyyy-MM-dd}?attr=&lang=pl_PL` |
| Repertoire of a cinema on a day | `GET https://www.cinema-city.pl/pl/data-api-service/v1/quickbook/10103/film-events/in-cinema/{cinemaId}/at-date/{yyyy-MM-dd}?attr=&lang=pl_PL` |
| Screening details | `GET https://tickets.cinema-city.pl/api/presentations/{screeningId}?referralMiniSiteId=0` |
| Hall layout (static) | `POST https://tickets.cinema-city.pl/api/seats/seatplanV2?venueId={venueId}&seatplanId={seatplanId}` with body `{}` |
| Free seats | `GET https://tickets.cinema-city.pl/api/seats/seats-statusV2?presentationId={id}&venueTypeId={venueTypeId}&isReserved={isReserved}` |
| Booking page (for people) | `bookingLink` of the event, e.g. `https://tickets.cinema-city.pl/api/order/{screeningId}?lang=pl` |

### 3.3 Payloads (fields the app uses)

- **Cinema list** → `body.cinemas[]`: `id`, `displayName`, `addressInfo.city`, `blockOnlineSales` (36 cinemas in Oct 2026).
- **Repertoire** → `body.films[]`: `id`, `name`, `length`, `link`; `body.events[]`: `id` (screening id),
  `filmId`, `cinemaId`, `eventDateTime` (local time, no offset), `attributeIds[]`, `languages{original, subtitles, dubbed}`,
  `bookingLink`, `soldOut`, `availabilityRatio`, `auditorium`.
- **Screening details** → `presentation`: `id`, `featureName`, `dateTime` (`yyyy-MM-dd HH:mm:ss`), `venueName`,
  `venueId`, `seatplanId`, `venueTypeId`, `isReserved`, `soldout`, `ticketSaleStop`.
- **Hall layout** → `rd.seatRadius` (30); sections `S{section}.G{group}` with group offset `rd{x,y,rotate}`;
  rows `R{rowKey}` with `n` (row label) and `rd{l{x,y}, r{x,y}}`; seats `S{position}` with `n` (seat label),
  `tg`, optional `hc` (wheelchair), `rd{cx, cy}`. Absolute position = seat `cx/cy` + group offset.
  Maps may arrive as JSON objects or, for sequential keys, as arrays - handle both.
- **Free seats** → `seats{ "<seatKey>": 0, ... }` - lists **only free seats** (value 0). A sold-out screening may
  return `{}` or `[]`.

### 3.4 Identifiers and quirks (verified)

- **Seat key** = `{section}_{position}_{rowKey}` (e.g. `1_18_4`). It links the layout and the free-seat list.
- **Position** grows left to right; **consecutive positions are physically adjacent**. Aisles show up as gaps in
  positions (Kraków rows 9-12: positions 3 → 7 although the labels 25 → 24 are consecutive).
- **Row key ≠ row label** in some halls (Kraków: key `4` is row `1`). Always display labels, compute with keys.
- Seat labels are numbered right to left (seat 1 on the right, as seen from the screen).
- **Wheelchair spaces** have `hc: 1` and labels like `1N`, `2N`. The free-seat list includes them even when the
  website shows the screening as sold out; `availabilityRatio` counts them too (2 of 385 free = "under 1%").
- **Screening ids are unique across all cinemas**; film ids are the same in every cinema.
- Repertoire attributes used: `imax`, `2d`, `3d`, `subbed`, `dubbed`, `original-lang-*`, `first-subbed-lang-pl`.
  Polish films in IMAX (e.g. "Lalka") have neither `subbed` nor `dubbed`.
- One request for the hall layout can be cached for days (layouts are static per `venueId/seatplanId`).

### 3.5 IMAX cinemas (Cinema City has exclusive IMAX rights in Poland; checked Oct 2026)

| City | Cinema (id) | Hall | venueId / seatplanId | Seats (wheelchair) |
| --- | --- | --- | --- | --- |
| Katowice | Punkt 44 (1065) | Imax | 66 / 3 | 378 (2) |
| Kraków | Zakopianka (1064) | Sala 10 IMAX | 54 / 1 | 333 (2) |
| Łódź | Manufaktura (1080) | IMAX BNP Paribas | 205 / 1 | 403 (4) |
| Poznań | Plaza (1078) | Sala IMAX | 177 / 2 | 449 (4) |
| Warszawa | Sadyba (1060) | IMAX BNP Paribas | 13 / 1 | 385 (2) |
| Wrocław | Wroclavia (1097) | IMAX BNP Paribas | 387 / 1 | 407 (1) - last row is a single wheelchair space |

All six layouts: one section, one group, no rotation, seat radius 30. The list is fixed in code
(`Locations.ImaxCinemas`); a one-time discovery run (cinema list + repertoire scan of every cinema) found them.

### 3.6 Responsible use (request budget)

| Action | Requests |
| --- | --- |
| One background check | 2 per watched screening (details + free seats); layout cached 7 days |
| Wizard step 2 | 1 per cinema per day; max range 31 days (one city), 14 days (all six cities); 3 in parallel |
| Check all (tabs) | each screening fetched once even if several profiles watch it |

Recommended interval 10-15 min (5 min allowed). On HTTP 403/429 the app logs and waits for the next run - no
retry storms. Observed risk of an IP block: low but not zero; it would be temporary and account-free.

---

## 4. Domain rules

- **Watched seats**: a set of seat keys per screening. Clicking a row number toggles all *regular* seats of the row;
  wheelchair spaces are selected individually only. Taken seats can be watched (that is the point).
- **Counts** shown to people exclude wheelchair spaces (`regular` seats), which are reported separately.
- **Available for a notification** (`SeatMatcher`): watched ∧ free ∧ part of a run of at least `minAdjacent`
  consecutive positions in the same (section, rowKey).
- **Notify only about new seats** (`NotificationState`): per profile, remember the available watched seats of the last
  check per screening; notify only for seats not in that set. A seat taken and freed again notifies again. A failed
  check keeps the previous state. A past screening is dropped. State is saved only after the notification succeeded.
- **Past screenings** (start ≤ now) are skipped. When all screenings of a profile are over, the background run removes
  its own scheduled task and state file.
- **Time**: all screening times are **local Polish time (Europe/Warsaw)** without an offset. Any other client (Android,
  SaaS server in another zone) must interpret them as Europe/Warsaw.
- **Screening filter**: IMAX required, dubbed excluded ("IMAX without dubbing").

---

## 5. Profile format (`*.cinemawatch.json`) - the contract between clients

Profiles are created by the desktop app and consumed by the desktop (wizard, background checks) and - planned - by
the Android app and a SaaS backend. JSON, UTF-8, camelCase, readable Polish characters.

### 5.1 Fields (format version 1)

| Field | Type | Since | Meaning |
| --- | --- | --- | --- |
| `version` | int | 1.0 | Format version, currently `1`. Readers refuse higher versions. |
| `name` | string | 1.0 | Display name (also the scheduled task name source via the file name) |
| `cinemaId` | string | 1.0 | Main cinema (first watched screening's cinema). Required. |
| `cinemaName` | string | 1.0 | Display only |
| `cinemaIds` | string[] | 1.4 | Cinemas searched in the wizard (one city or all six). Empty in older profiles → use `[cinemaId]`. |
| `filmId`, `filmName` | string | 1.0 | Film |
| `dateFrom`, `dateTo` | `yyyy-MM-dd` | 1.0 | Date range used in the wizard |
| `minAdjacent` | int ≥ 1 | 1.0 | Notify only when this many watched seats are free next to each other |
| `intervalMinutes` | int | 1.0 | Interval used when added to Task Scheduler |
| `screenings[]` | array | 1.0 | Watched screenings (at least one) |
| `screenings[].id` | string | 1.0 | Screening id (= presentation id) |
| `screenings[].cinemaId` | string | 1.4 | Cinema of the screening |
| `screenings[].city` | string | 1.4 | City in Polish (display) |
| `screenings[].startsAt` | `yyyy-MM-ddTHH:mm:ss` | 1.0 | Local Polish time, no offset |
| `screenings[].auditorium` | string | 1.0 | Hall name |
| `screenings[].bookingLink` | URL | 1.0 | Booking page for the notification |
| `screenings[].seatKeys` | string[] | 1.0 | Watched seat keys (`section_position_rowKey`) |

### 5.2 Example

```json
{
  "version": 1,
  "name": "Diuna: Część trzecia Warszawa 15.12–18.12",
  "cinemaId": "1060",
  "cinemaName": "Cinema City Sadyba",
  "cinemaIds": [ "1060" ],
  "filmId": "8105s2r",
  "filmName": "Diuna: Część trzecia",
  "dateFrom": "2026-12-15",
  "dateTo": "2026-12-18",
  "minAdjacent": 1,
  "intervalMinutes": 10,
  "screenings": [
    {
      "id": "1765592",
      "cinemaId": "1060",
      "city": "Warszawa",
      "startsAt": "2026-12-16T16:00:00",
      "auditorium": "IMAX BNP Paribas",
      "bookingLink": "https://tickets.cinema-city.pl/api/order/1765592?lang=pl",
      "seatKeys": [ "1_18_4", "1_19_4" ]
    }
  ]
}
```

### 5.3 Compatibility rules

- **Add, don't change**: new fields are optional; readers ignore unknown fields. Profiles saved by 1.4 open in 1.3.
- Bump `version` only for breaking changes; old readers then refuse the file with a clear message.
- Writers save atomically (temp file + replace).
- Identity of a profile today = its file path (state file = SHA-256 of the full path). A future `profileId` would help
  mobile/SaaS sync.

### 5.4 Checking a profile (what any client must do)

```
for each screening in profile.screenings where startsAt (Europe/Warsaw) > now:
    details = GET presentations/{screening.id}                    -> venueId, seatplanId, venueTypeId, isReserved
    free    = GET seats-statusV2?presentationId=…&venueTypeId=…&isReserved=…   -> set of free seat keys
    watchedFree = screening.seatKeys ∩ free
    runs = group watchedFree by (section, rowKey), split where positions are not consecutive
    available = seats in runs with length ≥ profile.minAdjacent
newSeats = available minus the available set remembered from the previous check (per screening)
if newSeats: notify (title "Wolne miejsca: {film}", one line per screening: city · day time · rows/seats), tap → bookingLink
remember available per screening (only after the notification succeeded); keep previous state for failed screenings
```

Seat names for the text ("row 8: 12, 13") come from the hall layout (`n` labels; cached per venue/seatplan).

---

## 6. Desktop app architecture (1.4.0)

### 6.1 Projects

| Project | Target | Contents |
| --- | --- | --- |
| `src/CinemaWatch.Core` | net10.0, no packages | API client & parser, models, `SeatMatcher`, `SeatSelectionText`, profiles (`WatchProfile`, `ProfileStore`), `ProfileChecker`, `NotificationState`, notifications (`NotificationBuilder`, `ToastXml`, `INotifier`), `HeadlessRunner`, Task Scheduler (`SchtasksScheduler`), `SeatAvailabilityService` (layout cache), `Locations`, localization (`Localizer`, `AppSettings`, `AppInfo`), `AppPaths`, `FileLog`, `ErrorMessages` |
| `src/CinemaWatch.ViewModels` | net10.0, no WPF | `MainViewModel` (window: tabs, check all, language), `ProfileSessionViewModel` (one tab = whole wizard), step view models, `HallMapViewModel`, `UiText`, `Formatting`, interfaces `IUserInteraction`, `IPreferences`, `AppEnvironment` |
| `src/CinemaWatch.App` | net10.0-windows10.0.19041.0, WPF | Views (XAML), Fluent theme, `App.xaml.cs` (two modes), services: `WpfUserInteraction`, `WindowsToastNotifier`, `SettingsPreferences`; icon |
| `tests/CinemaWatch.Tests` | net10.0 console | Dependency-free test runner + captured API responses in `Fixtures/` |

### 6.2 Modes

- `CinemaWatch.exe` - the wizard window.
- `CinemaWatch.exe --profile "path"` - no window: one check, notification about newly free seats, log, exit.
  Exit codes: `0` checked, `1` profile unreadable, `2` every screening failed, `3` unexpected error.

### 6.3 Background checks and notifications

- Task Scheduler via `schtasks.exe`: folder `\CinemaWatch\`, task name = profile file name, `/SC MINUTE /MO n`,
  current user, runs only while logged on (needed for notifications), `/TR` ≤ 261 characters. Created only after a
  confirmation dialog; re-adding overwrites (e.g. new exe path).
- Windows toasts for an unpackaged app: AUMID `CinemaWatch.Desktop` registered under
  `HKCU\Software\Classes\AppUserModelId` (name + icon); protocol activation opens the booking link even after exit.

### 6.4 Data on disk (`%LOCALAPPDATA%\CinemaWatch`)

| File / folder | Contents |
| --- | --- |
| `settings.json` | `Language` (`pl`/`en`/null = follow Windows), `LastCinema` (cinema id or `all`) |
| `window.json` | window size/position/maximized |
| `State\{hash}.json` | notification state per profile path |
| `Cache\seatplan-{venue}-{plan}.json` | hall layouts (7 days) |
| `Logs\cinemawatch.log` | background runs (rotated at 1 MB) |
| `CinemaWatch.png` | icon for toasts |
| `Profiles\` | profiles if the folder next to the exe is not writable (default: `Profiles\` next to the exe) |

### 6.5 UI behaviour worth knowing

- Clickable steps: back is instant; forward runs the same checks/loading as *Next*; data with unchanged inputs and
  younger than 5 minutes is reused.
- Tabs: restored at start-up from the profiles folder (expired profiles skipped), loaded lazily, unsaved-changes marker,
  confirmation on close; *Check all* de-duplicates screenings.
- Language: follows the Windows **display** language (Polish → PL, else EN); switchable at runtime; remembered.
- Step 1: city drop-down (6 IMAX cities + *All cities*), cinema follows the city, technology fixed (IMAX).

---

## 7. Engineering conventions

- **Languages**: code, comments, docs, commit messages and README in **English**; UI texts in **Polish + English**,
  written inline as `T("polski", "English")`; fixed view texts via `UiText` bound as `{Binding X, Source={StaticResource T}}`.
- **C#**: latest language version, nullable enabled, file-scoped namespaces, records for data, no MVVM framework
  (small `ObservableObject`, `RelayCommand`, `AsyncRelayCommand`).
- **ViewModels never reference WPF** - so the whole wizard is testable without a window.
- **Tests**: `dotnet run --project tests/CinemaWatch.Tests`; every behaviour change gets a test; real captured API
  responses as fixtures (Sadyba, Kraków, Wrocław). No test packages needed.
- **WPF pitfalls already met**: `System.IO` is not an implicit using in WPF projects; `ContentControl` reuses the same
  view when switching tabs → share option lists across tabs and ignore `null` pushed back by selectors; never override
  Fluent control styles with implicit styles; theme colours via `DynamicResource`.
- **Versioning**: SemVer in `Directory.Build.props`; betas `X.Y.Z-beta.N` (shown in the title bar).
- **CI/CD (GitHub Actions)**: `ci.yml` on `main`, `feature/**` and pull requests (tests + build);
  `release.yml` on tags `v*` → tests, `publish.ps1 -Version`, `gh release create` with `CinemaWatch.exe` + `.sha256`
  and `.github/RELEASE_NOTES.md`. Tags with a suffix → **draft pre-release** (visible only to maintainers).
- **Build**: .NET 10 SDK only (no Visual Studio); `publish.ps1` → single-file, self-contained `artifacts\CinemaWatch.exe`.

### 7.1 Git workflow

- Small / docs-only changes: commit straight to `main` (no tag → no release).
- Features: branch `feature/<name>` → push (CI) → beta tag `vX.Y.Z-beta.N` (draft release) → test → set the stable
  version → pull request → **Squash and merge** → tag `vX.Y.Z` on `main` → delete the branch and the beta release.
- Never rewrite `main` (no reset / force push). Undo after a release = `git revert` + patch release.
- A flop on a feature branch: close the PR unmerged with a comment, delete the draft release; `main` stays untouched.

---

## 8. Version history

| Version | Highlights |
| --- | --- |
| script | PowerShell monitor with Gmail notifications (Task Scheduler) |
| 1.0.0 | WPF wizard, hall map, profiles, `--profile` mode, Task Scheduler, Windows toasts, icon, GitHub release pipeline |
| 1.1.0 | English UI (runtime switch), collapsible summary cards, version in the title bar |
| 1.2.0 | Clickable wizard steps with data reuse |
| 1.3.0 | Profile tabs, *Check all*, restore tabs at start-up, CI on feature branches, pre-release tags |
| 1.4.0 | All six Cinema City IMAX cinemas, grouping by day and city, aligned row numbers, draft betas |

---

## 9. Decision log

| When | Decision | Why |
| --- | --- | --- |
| Oct 2026 | Read-only API use; never touch the cart endpoint | Turnstile-protected, legal/ethical line; buying stays manual |
| Oct 2026 | WPF on .NET 10 with the built-in Fluent theme, single exe | Modern look, async UI, no console flash for background runs |
| Oct 2026 | Core and ViewModels without WPF references | Testable logic; reuse for mobile/SaaS |
| Oct 2026 | Windows toasts (not e-mail) in the app | Desktop-native; e-mail stays in the PowerShell script |
| Oct 2026 | Sold-out screenings stay selectable | Monitoring sold-out screenings is the whole point |
| Oct 2026 | Wheelchair spaces counted separately | API lists them as free on sold-out screenings |
| Oct 2026 | Dubbed screenings excluded | Owner's preference; filter "IMAX without dubbing" |
| Oct 2026 | Task created only after confirmation; auto-removed after the last screening | No unexpected changes to the PC; no leftovers |
| Oct 2026 | Hall layouts cached on disk for 7 days | Fewer requests: 2 per screening per check |
| Oct 2026 | Language follows the Windows display language, switchable, remembered | PL for Polish Windows, EN otherwise |
| Oct 2026 | Tabs at the top; restore profiles from the profiles folder | Readability; map keeps its full width |
| Oct 2026 | IMAX cinema list fixed in code | Dynamic discovery would cost ~36 requests per start |
| Oct 2026 | *All cities* limited to 14 days; last city remembered | Request budget; most users pick one city |
| Oct 2026 | Row numbers in two edge columns (not the API anchors) | Matches the website, neat in irregular halls |
| Oct 2026 | Betas as draft releases | Visible only to the owner until published |
| Oct 2026 | Mobile = profile-consuming proxy, no seat map, no backend yet | Smallest useful mobile scope; desktop keeps priority |

---

## 10. Known limitations and risks

- Unofficial API: format changes break parsing (tests with captured responses help spot it quickly).
- Cloudflare may block automated traffic (temporary IP block); higher risk from datacenter IPs (SaaS) and possibly
  mobile networks.
- Background checks run only while the PC is on and the user is signed in; none during sleep.
- The exe is not code-signed (SmartScreen warning on first start).
- Profile identity is the file path; moving a profile file breaks its scheduled task and resets its state.

---

## 11. Glossary

| Term | Meaning |
| --- | --- |
| Seans / screening / presentation | One showing of a film; `presentation` in the ticketing API, `event` in the repertoire API |
| Sala / hall / venue | Auditorium (`venueId`, layout `seatplanId`) |
| Seat key | `section_position_rowKey` |
| Watched seats | Seats selected for monitoring (row or single seats) |
| Profile | Saved watch setup (`*.cinemawatch.json`) |
| Check now / check all | Immediate check of one profile / of all open tabs |
| Background check | `CinemaWatch.exe --profile` run by Task Scheduler |
