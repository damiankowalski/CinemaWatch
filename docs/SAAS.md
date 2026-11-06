# CinemaWatch SaaS - Knowledge Base

> Companion to [`KNOWLEDGE_BASE.md`](KNOWLEDGE_BASE.md). The product, the Cinema City API, the domain rules and the
> profile format live there and are not repeated here. This document covers the SaaS (web) track: goals, risks,
> target architecture, the MVP, the plan and the decisions. What has actually been set up (accounts, domain, DNS,
> the VPS), with a work log, runbooks and an infrastructure glossary, lives in [`SAAS_INFRA.md`](SAAS_INFRA.md).
> Order of trust: **repository code** > **KNOWLEDGE_BASE.md** > **this document** > chat history. For live
> infrastructure settings the provider panels are the source of truth; `SAAS_INFRA.md` records them.
> Keep it current: update *Plan* (§7), *Decision log* (§9) and *Open questions* (§10) after every step, and
> `SAAS_INFRA.md` after every infrastructure change.
>
> Last updated: 6 November 2026. Status: **stage 4 in progress** (deploy to the VPS, staging). Stage 3 done: MVP 0
> complete on the development PC. Risk test R1 closed **green** on 5 Nov 2026 (§7 stage 4, §9). 4.1 done (container
> image on GHCR, container test in CI). 4.2 done: e-mail through Resend, Cloudflare tunnel and Access, Healthchecks.io.
> 4.3 done (SaaS 0.5.1): the VPS reinstalled and configured by `infra/ansible` (hardening, swap, Docker), checked in CI;
> the privacy notice names the services. Next: 4.4 (first deployment).

---

## 1. Goals and ground rules

- **Learning first.** CinemaWatch is the reference project for learning how a SaaS is built and run: hosting,
  accounts, per-user data, background work, e-mail, limits, payments, containers. Real users and real money come
  later, if ever.
- **Smallest thing that runs.** Add a component only when the current step needs it. The full vision (§4) is a map,
  not a to-do list.
- **Same guardrails as the desktop app.** Read-only use of the Cinema City API; no cart, reservations or purchases;
  never bypass Cloudflare Turnstile; respect the request budget (KB §3.6). No secrets or personal data in the
  repository.
- **Desktop keeps priority.** SaaS work must not destabilise desktop releases (§6).
- **Hosted only.** The SaaS runs on rented infrastructure. The development PC is used for coding only; an on-prem lab
  is parked (§11).

---

## 2. Key risks (read first)

| # | Risk | What we do about it |
| --- | --- | --- |
| R1 | Cloudflare in front of Cinema City blocks or challenges requests from datacenter IPs (KB §10) | Tested first (stage 2): green on 5 Nov 2026 - 4,997 requests from the VPS in 3.1 days, no block or challenge (§7 stage 4). The poller's request log (`check_log`, operator's panel) keeps measuring. Fallback: pollers on residential IPs (§11) |
| R2 | Terms of use: a public (and possibly paid) service built on an unofficial API is a different category than a personal tool | Terms read in Oct 2026: personal and learning use within the guardrails; no public launch or payments without written consent from Cinema City (§9) |
| R3 | Personal data (GDPR): e-mail addresses, notification history | EU hosting, privacy notice, account deletion, store only what the service needs. Exception: the e-mail provider Resend keeps account data (recipients, logs) in the US; the privacy notice names it (§9) |
| R4 | The request budget grows with users | De-duplicate screenings, adaptive intervals, global rate limiter, limits per account (§4.3, §5) |
| R5 | Real payments mean being a seller (VAT, consumer law, invoices) | Payments only in test mode; a Merchant of Record if it ever becomes real (§4.5) |

### 2.1 Load estimate

50 users × 3 profiles × 3 screenings ≈ 450 watched screenings; after de-duplication (many people watch the same
premiere) roughly 200-300 unique screenings. With a check every 10 minutes and 1-2 requests per screening this is
40,000-90,000 requests a day from one IP (about one per second).

- For the server this is nothing: the smallest VPS handles it, and profiles are kilobytes of JSON.
- For Cloudflare in front of Cinema City this can look like a bot. **The bottleneck is the upstream request budget,
  not hosting.** Limits per account (§4.5, §5.1) are this budget expressed as a product.

---

## 3. From the desktop app to the SaaS

| Desktop (1.4.0) | SaaS |
| --- | --- |
| Wizard window (`CinemaWatch.exe`) | Web app in the browser (MVP 1) |
| `--profile` headless run by Task Scheduler | Poller: a background service on the server |
| `*.cinemawatch.json` files in `Profiles\` | Rows in PostgreSQL; the profile JSON (format v1, KB §5) stored as is |
| `State\{hash}.json` (`NotificationState`) | Notification state table per profile and screening |
| `Cache\seatplan-*.json` (7 days) | Server-side cache shared by all users (layouts, later the repertoire) |
| Windows toast with a booking link | E-mail with a booking link |
| *Check all* de-duplication | Fan-out: each screening checked once for every profile that watches it |
| Time: Europe/Warsaw (KB §4) | Same; the server must not assume its own time zone |

`CinemaWatch.Core` is UI-free `net10.0` without packages, so the API client, parser, `SeatMatcher`, the
notification-state logic and the profile format can be reused directly. `CinemaWatch.ViewModels` have no WPF
references; whether a Blazor UI can use them is an open question (§10).

---

## 4. Target architecture (full vision)

```
Browser
   |  HTTPS
Cloudflare (DNS, TLS, WAF, Tunnel, Access, Turnstile)
   |  tunnel: no inbound web ports on the server
Server (containers)
   |- Web app + API (ASP.NET Core): accounts, profiles, history; later wizard + hall map (SVG)
   |- Identity: ASP.NET Core Identity first; an OIDC provider (Keycloak / managed) later
   |- PostgreSQL: users, profiles (JSONB), watched screenings, state, history; later plans and payments
   |- Poller (worker): unique screenings, rate limit, backoff   --> Cinema City API (read-only)
   '- Notifier: e-mail (later web push)                          --> e-mail provider
Stripe (later): Checkout, Billing, webhooks                       --> web app
```

### 4.1 Components

| Component | Role | MVP 0 | Later |
| --- | --- | --- | --- |
| Cloudflare | DNS, TLS, protection, tunnel to the server, login gate for staging | Yes | Turnstile on sign-up |
| Web app + API | Accounts, profile import and list, history | Yes | Wizard and hall map (MVP 1) |
| Identity | Sign-up, e-mail confirmation, login, password reset | ASP.NET Core Identity | OIDC provider |
| PostgreSQL | All data | Yes | Row-Level Security |
| Poller | Checks watched screenings (KB §5.4) | Yes (in the worker) | Separate service, several instances |
| Notifier | Sends e-mails about newly free seats | Yes (in the worker) | Separate service behind a queue |
| Operator's panel | Status of the components, request statistics, events, pause switch, poller settings (owner only) | Yes (`/admin`) | Alerts beyond e-mail |
| Heartbeat monitor (external) | E-mails the owner when the server stops reporting ("dead man's switch") | Yes: Healthchecks.io (account and check since 4.2) | - |
| Queue | Poller → notifier events | No | RabbitMQ (learning) |
| E-mail provider | Transactional e-mail with SPF / DKIM / DMARC | Yes: Resend over SMTP, from `mail.damiankowalski.pl` (4.2) | - |
| Stripe | Payments | No | Test mode first |

### 4.2 Data model (sketch)

- `users` - ASP.NET Core Identity tables (e-mail, password hash, confirmation); `max_profiles` = the account's own
  profile limit set by the owner (null = the default from configuration).
- `profiles` - `id` (UUID v7), `user_id` (cascade on account deletion), `name` (unique per account: re-importing a
  profile with the same name replaces it), `enabled`, `ends_at` (start of the last screening, UTC), `created_at`,
  `updated_at`, `document` (JSONB: profile format v1, normalised as the desktop saves it, so import and export with
  desktop and Android stay free).
- `profiles` also keep `last_manual_check_at` and `check_pending` ("check now", increment 3).
- `watched_screenings` - one row per unique screening id: `cinema_id`, `starts_at` (UTC, converted from the
  profile's Polish time), `interval_minutes` (the shortest of the profiles that watch it), `next_check_at`,
  `manual_requested_at`, the last result (`last_checked_at`, `last_succeeded_at`, `last_outcome` Ok / Failed /
  Blocked, `last_http_status`, `last_error`, `free_seat_keys`, `free_count`, `total_count`) and the cached details
  (`venue_id`, `seatplan_id`, `venue_type_id`, `is_reserved`). Derived from the profiles at every poller cycle; the
  poller works on this table, not on profiles.
- `seat_plans` - hall layouts as Cinema City returned them, shared by all users (`venue_id`, `seatplan_id`, `json`,
  `fetched_at`; fetched again after 7 days, deleted after 30 days unused).
- `notification_state` - `profile_id`, `screening_id`, the set of available watched seats from the last check
  (KB §4 "notify only about new seats"); deleted with the profile, dropped for screenings that are over.
- `notifications` - history: when, which profile, subject, one line per screening, delivered or not (and the error);
  deleted with the profile, kept 90 days.
- `check_log` - per request, the probe's columns: time, cycle, screening, manual or not, method, host, endpoint,
  target, HTTP status, duration, content type, `cf-mitigated`, `cf-ray`, `Retry-After`, error. No personal data; kept
  30 days.
- `poller_state` - one row: `blocked_until`, `block_count` (blocks in a row), `blocked_at`, `block_reason`,
  `last_cycle_at`, `last_cleanup_at`, `paused_by_operator_at` (the owner's pause, no end time); survives a restart.
- `service_events` - blocks, resumptions, an exhausted budget, mail failures, failing cycles, starts, the owner's
  settings changes and pauses, heartbeat failures; kept 90 days; shown in the operator's panel.
- `poller_settings` - one row of overrides set in the operator's panel (null = the configuration's value): minimum
  interval, random addition, hourly budget, pauses between screenings (background, "check now"), how fresh a stored
  result "check now" reuses, pauses after a block, `reuse_screening_details`; `updated_at`.
- `component_status` - one row per component the database cannot see otherwise (`mail`, `heartbeat`): `last_ok_at`,
  `last_failure_at`, `last_failure` (e-mail addresses cut out).
- All times in the database are UTC (`timestamptz`); Polish time only at the edges (pages, e-mails).
- Later: `plans` / entitlements, `stripe_customers`, `subscriptions`, `payment_events`.

Multi-tenancy: **one database, every row has an owner** (`user_id`). A database per user is a pattern for business
customers with isolation requirements and an anti-pattern here. PostgreSQL Row-Level Security can later act as a
safety net against application bugs.

### 4.3 Poller rules (as built in increment 3)

- Check each unique screening once and fan the result out to every enabled profile that watches it. The poller is
  global: one queue for all users; profiles only decide which screenings are in it and how often.
- Settings: the configuration gives the defaults; the operator's panel changes some of them within ranges fixed in
  code (`PollerTuning`): minimum interval 5-60 min, random addition 0-50%, budget 10-300 requests an hour, pauses
  between screenings 2-60 s (background) and 1-10 s ("check now"), fresh result 0-10 min, first pause after a block
  15-720 min, longest 15 min-7 days, `ReuseScreeningDetails` on/off. Only differences from the configuration are
  stored; each cycle reads the effective settings at its start; every change is an event with old and new values.
- Interval per screening: the shortest profile interval of its watchers, never below the server minimum
  (`Saas:Limits:MinIntervalMinutes`, 10 min; cannot be configured below 5), plus up to 20% at random (10-12 min). A
  new screening is checked within a minute of the import.
- One check = screening details + free seats, the pair the R1 probe measured; the layout comes from `seat_plans`.
  Reusing the details (1 request per check) exists as a switch (`ReuseScreeningDetails`), off until measured.
- Pace and budget: the two requests of a screening go directly one after the other (like a browser opening the
  screening's page); between screenings 3-6 s at random in the background and 1-2 s for "check now" (somebody waits);
  a global budget of 120 requests an hour (scheduled and manual), refilled steadily, at most a sixth of an hour saved
  up. When it is used up, due screenings wait in the
  order of their due times: everybody is checked a little less often instead of the server sending more.
- Failed check (HTTP 5xx, another 4xx such as 404, timeout, network error, unreadable answer): the notification
  state stays, no retry, the next attempt at the normal time (R1 data: one HTTP 500 in 1,144 requests on 3 Nov 2026).
- Block (HTTP 403 / 429, a response marked `cf-mitigated`, HTML instead of JSON): nothing more is sent in the cycle;
  all requests pause for 1 hour, doubling with every block in a row up to 24 hours, a longer `Retry-After` wins
  (at most 7 days); the pause survives a restart. Pending "check now" requests are dropped. After the pause the
  blocked screening is tried first, alone; once a request gets through, the counter returns to 0. The owner gets an
  e-mail when a block starts and when it ends; users see a banner and a greyed-out "check now".
- Notifications: at most one e-mail per profile per cycle, in the account's language, one line per screening with
  its booking link; the state is saved only after the e-mail went out.
- "Check now": no cooldown; unavailable (greyed out) only while the profile's check runs or checks are paused.
  Cinema City is asked only about screenings checked more than a minute ago, fresher ones answer at once from the
  stored result. Results are saved screening by screening, so the page fills in row by row.
- The owner's pause (operator's panel): from the next cycle no requests at all, scheduled or "check now", until
  resumed, restarts included; pending "check now" requests are dropped; users see a notice and a greyed-out button;
  nobody is e-mailed. Independent of a block's pause, which a resume does not shorten.
- Heartbeat: after every cycle that finished (idle, paused and blocked ones included), at most every 5 minutes, a GET
  to the external "dead man's switch" (`Saas:Heartbeat:Url`, a secret; 10 s timeout; not a Cinema City request). A
  cycle that throws sends nothing, so a failing or stuck poller raises the alarm too. A failed ping is recorded and
  never stops the poller.
- Past screenings drop out; a profile whose screenings are all over (`ends_at`) is skipped (not switched off: the
  page already shows it as ended).
- One session (`uuid` header, cookies, connection) per cycle, like one probe run. Honest User-Agent:
  `Mozilla/5.0 (<platform>) CinemaWatch-Server/<version> (+<repository URL>)` (KB §3.1).
- Development and tests use a fake of Cinema City (captured responses steered by files); the app refuses it outside
  the Development environment.
- Adaptive interval (proposal, not built): screenings weeks away less often, the last 48 hours more often; never more
  often than the minimum allowed for the profile.

### 4.4 Hosting and infrastructure

- **Server:** one Linux VPS in the EU with Docker and Docker Compose: OVHcloud VPS-1 in Warsaw with Ubuntu 26.04 LTS,
  ordered in stage 2 (state and runbooks: [`SAAS_INFRA.md`](SAAS_INFRA.md)). One server, many containers; future
  products get their own subdomains on the same server. Requirements and sizing: §4.6.
- **Addresses:** the server's IP addresses and host name never go into this public repository; with the tunnel the
  origin stays unknown, so nobody can bypass Cloudflare.
- **Edge:** Cloudflare Free plan in front. The server runs `cloudflared` (Cloudflare Tunnel): it opens an outbound
  connection to Cloudflare, so no web ports are opened on the server (only SSH) and no TLS certificates are needed
  on the server. Cloudflare Access protects staging (team domain `damiankowalski.cloudflareaccess.com`, one-time PIN by
  e-mail; the whole host for the owner and testers, `/admin` for the owner only). The host firewall (`ufw`) lets in SSH only. Containers publish no
  ports: ports published by Docker bypass `ufw`.
- **Domain:** `damiankowalski.pl` - one "umbrella" domain for all future products, subdomains per product and
  environment (e.g. `cinemawatch.damiankowalski.pl`, `staging-cinemawatch.damiankowalski.pl`; first-level names, so
  Cloudflare's free certificate covers them). Registrar OVHcloud, DNS on Cloudflare, DNSSEC on. Current state and
  runbooks: [`SAAS_INFRA.md`](SAAS_INFRA.md).
- **Images and deployment:** GitHub Actions builds container images → GitHub Container Registry (GHCR, public
  package: the server pulls without a token) → the server pulls a named version (never `latest`). No build tools on
  the server. The web image: `aspnet:10.0-resolute-chiseled-extra` (Ubuntu 26.04, no shell, non-root, ICU and
  tzdata), read-only file system; `infra/compose/compose.yaml` with `postgres` (18), `web`, `cloudflared`, log size
  limits; secrets in `.env` on the server (template `infra/compose/env.example`). The app applies database migrations
  at start-up (`Saas:MigrateDatabaseOnStart`; one instance, a `pg_dump` before every deployment). Deployment starts by
  hand (one script on the server); automation from GitHub Actions is optional (§7, 4.6).
- **Docker on the server:** from Docker's own repository, with its origin added to `unattended-upgrades` (security
  updates arrive by themselves); configured by Ansible run on the server itself (§9).
- **Backups:** daily `pg_dump` copied off the server, plus the provider's backup.
- **Kubernetes:** not needed at this scale; a learning item for later (k3s), same images.

### 4.5 Plans, limits and payments (after MVP)

- **Audience:** possibly only the owner (§9). Plans, a trial and test-mode payments stay on the roadmap even then,
  as self-tests.
- **Free plan vs trial:** CinemaWatch is used around premieres, so a permanent free plan with limits fits better than
  a time-limited trial.
- **Entitlements:** a plan defines limits - profiles, screenings per profile, date range, watched seats,
  minimum interval, channels. The first exists since increment 2: profiles per account, default from configuration,
  set per account by the owner (`users.max_profiles`); a plan would set the same value. Enforced on the server (API and worker), never only in the UI. The interval is the
  most expensive limit (request budget).
- **Payments:** Stripe in test mode - Checkout (card data never touches our server), Billing, Customer Portal,
  webhooks. Build both a one-time "pass" (e.g. 30 days around a premiere) and a subscription to learn both. Stripe is
  the source of truth; we keep a mirror updated from webhooks plus an event log with ids (idempotency) and derive
  entitlements from it. BLIK and Przelewy24 matter for Polish users.
- **Real money** means being a seller (VAT, consumer law, invoices, terms). A Merchant of Record (Paddle, Lemon
  Squeezy, Stripe Managed Payments) takes this over for a higher fee. Not without written consent from Cinema City
  (R2, §9).

### 4.6 VPS requirements and sizing (Nov 2026)

What a server must offer for the whole plan, from MVP 0 to the full vision:

| Requirement | Why |
| --- | --- |
| Full virtualisation (KVM, VMware, Xen HVM), not OpenVZ / LXC | Docker, k3s and the firewall need their own kernel |
| x86-64 (amd64) | Images are built on GitHub Actions for amd64; ARM means multi-arch builds |
| Ubuntu LTS image or own ISO | Ubuntu 26.04 LTS in use |
| EU location, preferably Poland | GDPR (R3); latency for Polish users |
| Own static public IPv4 (+ IPv6), no NAT | SSH; the Cinema City polling comes from this IP (R1) |
| Inbound TCP and UDP ports can be opened | Today SSH only; TURN or a game server needs more (below) |
| Emergency console (KVM / VNC), rescue mode, reinstall from the panel | Way back when SSH is locked out |
| Provider backup or snapshot at least every 24 h | Plus our own `pg_dump` off the server (stage 4) |
| Monthly or hourly billing | No yearly commitment while learning |
| 2FA on the provider panel | Whoever has the panel has the server |
| Terms allow automated HTTP requests (and game servers) | Polling; possible game server |
| NVMe disk, at least 40 GB; any transfer limit (we use a few GB a day) | Images, logs, database |

Sizing (estimates; measure with `docker stats` in stage 4):

| Scope | Components | RAM | Server |
| --- | --- | --- | --- |
| MVP 0 | OS + Docker, PostgreSQL, web, worker, `cloudflared` | about 1-1.5 GB | 2 vCPU / 4 GB / 40 GB (current VPS-1) |
| Full vision | MVP 0 + staging next to production, Keycloak, RabbitMQ, Prometheus / Grafana, k3s | about 4-6 GB | 4 vCPU / 8 GB / 80 GB |

OVHcloud upgrades a VPS in place within the same data centre; a smaller plan or another location means a new order
and a migration.

**Game servers (other projects, e.g. swapDuel).** A static game page and a PeerJS signalling server fit behind the
tunnel. A TURN server (coturn) or an authoritative UDP game server does not: it needs direct inbound UDP/TCP ports,
"DNS only" records, temporary TURN credentials, anti-DDoS better than blackholing, steady CPU and a location close to
the players (Poland). TURN over TLS also needs a certificate on the server (DNS-01 with a Cloudflare API token, which
the account cannot create today, or HTTP-01 on port 80). Such a server goes on a separate small VPS (or a managed TURN
service): an attack on the game's IP must not take down this server's SSH and polling, and this server keeps the
"SSH only" model (§9).

---

## 5. MVP

### 5.1 MVP 0 - "server-side profile proxy"

The desktop app stays the profile editor; the SaaS checks the profiles and sends e-mails. This is the Android concept
(KB §2.2) on a server, with the same contract (KB §5).

In scope:
- The whole site sits behind the login: `cinemawatch.damiankowalski.pl` shows only the login page (with "forgot
  password" by e-mail) to anyone not signed in.
- Accounts by invitation only (the owner creates them; no open sign-up form): e-mail confirmation, login, logout,
  password reset, account deletion (with all data).
- Import of a `*.cinemawatch.json` file (validation as in KB §5.3: refuse a higher `version`, ignore unknown fields).
- Profile list: screenings, number of watched seats, enabled/disabled, last check, last notification; enable,
  disable, delete, download the stored JSON.
- "Check now" per profile, like the desktop, without a cooldown; a screening checked in the last minute is not asked
  again (§4.3).
- Background checks per KB §5.4 with de-duplication and the poller rules (§4.3), at the profile's own interval
  (`intervalMinutes`) but never more often than a server minimum (e.g. 10 minutes).
- E-mail "Wolne miejsca: {film}" with one line per screening and the booking link; a "send a test e-mail" button
  to check delivery and spam placement.
- Notification history per profile.
- Limits in configuration (`Saas:Limits`): 5 profiles per account (enabled, disabled and ended alike; the owner can
  set another limit per account, 1-50), 20 screenings per profile, minimum interval 10 minutes; a global request
  budget (`Saas:Poller`, 120 an hour).
- Privacy notice page (names the services that receive personal data: OVHcloud, Cloudflare with Access, Resend,
  Healthchecks.io; since SaaS 0.5.1); UI in Polish and English like the desktop app.
- Operator's panel at `/admin` (owner only): status of the components as a checklist (poller, Cinema City, e-mail,
  database, heartbeat), events, requests per hour, answers per HTTP status, the latest requests, poller settings and a
  pause switch; a heartbeat for an external "dead man's switch".

Out of scope: wizard and hall map, plans and payments, OIDC provider, queue broker, Kubernetes, web push, mobile
integration.

Done when: it runs on the VPS for 2 weeks with a few test users, notifications arrive within one interval, there are
no 403/429 blocks, e-mails land in the inbox (not spam) and requests stay within the budget.

### 5.2 MVP 1 - wizard and hall map in the browser

Steps as in the desktop app (city & dates → film & screenings → hall map → summary). The hall map is drawn as SVG:
the layout's `cx/cy` (+ group offset) and `seatRadius` map directly to SVG circles (KB §3.3). The repertoire and
layouts are shared by all users, so a server-side cache saves requests. Desktop and SaaS profiles stay
interchangeable.

---

## 6. Repository and engineering (monorepo)

- The SaaS lives in this repository. Projects: `tools/CinemaWatch.Probe` (risk test, stage 2; tests in
  `tests/CinemaWatch.Probe.Tests`); `src/CinemaWatch.Server` (no HTTP: database, accounts, e-mail; later the poller
  and the notifier); `src/CinemaWatch.Web` (ASP.NET Core, Blazor static server-side rendering; hosts the background
  services in MVP 0, a separate worker host can follow); `tests/CinemaWatch.Saas.Tests`. Local setup and runbook:
  [`src/CinemaWatch.Web/README.md`](../src/CinemaWatch.Web/README.md).
- `CinemaWatch.Core` is shared. A change in Core needs the desktop tests to pass and must not change desktop
  behaviour unintentionally.
- Branches `feature/saas-<name>`; the same Squash-and-merge flow as the desktop (KB §7.1).
- **Separate versioning:** the root `Directory.Build.props` holds the desktop version; SaaS projects set their own
  `<Version>` in the project file (`CinemaWatch.Server` and `CinemaWatch.Web` 0.5.1). Tags `web-vX.Y.Z` do not match the desktop `release.yml`
  filter `v*`, so no desktop release is created by mistake.
- CI: the Windows job (tests + desktop build) stays and also runs the probe tests and the SaaS tests without a
  database; the `linux` job runs the shared tests, the probe tests and all SaaS tests on `ubuntu-26.04` (the server's
  release) with a PostgreSQL 18 service container (`CINEMAWATCH_REQUIRE_DB=1`: a missing database fails the job) and
  publishes the probe; the `container` job builds the web image and runs `infra/compose/smoke-test.sh` (postgres + web
  from the compose file, no tunnel, no e-mail, no poller). Tags `web-vX.Y.Z` (betas `web-vX.Y.Z-beta.N`) run
  `web-image.yml`: the same build and test, then the image goes to GHCR as `cinemawatch-web:X.Y.Z` and `sha-<commit>`;
  no GitHub release. The `ansible` job checks `infra/ansible` with the Ubuntu 26.04 packages (`ansible`,
  `ansible-lint`): `--syntax-check` and `ansible-lint --profile production`. Actions: `actions/checkout@v7`, `actions/setup-dotnet@v6` (Node 24). The probe itself is not released: it has its own version in its project file and no tag.
- Conventions from KB §7 apply: English code and docs, UI texts via `T("polski", "English")`, tests for every
  behaviour change, no secrets in the repository (configuration via environment variables and GitHub Secrets).

---

## 7. Plan (SaaS only)

Each stage ends with an update of this document.

### Stage 0 - Documents (done Oct 2026)
- [x] This file in the repository; KB §2.3 links here.

### Stage 1 - Accounts and decisions (done 31 Oct 2026; details in `SAAS_INFRA.md` §3)
- [x] Password manager entries and 2FA: GitHub, Google, OVHcloud (hosting provider and registrar). Cloudflare:
      Google sign-in only, no own 2FA (accepted risk, §9). E-mail provider: moved to stage 4.
- [x] Cloudflare account (Free plan).
- [x] Umbrella domain `damiankowalski.pl` bought at OVHcloud; DNS moved to Cloudflare; zone cleaned to
      `v=spf1 -all`; DNSSEC on.
- [x] Hosting account (OVHcloud). The VPS itself is ordered at the start of stage 2 (monthly billing).
- [x] Cinema City terms of use read (R2); findings in §9.

### Stage 2 - First server and the risk test (R1) (done 2 Nov 2026; details in `SAAS_INFRA.md` §1.4, §3)
- [x] SSH key on Windows (built-in OpenSSH; ed25519 with a passphrase, backup in the password manager); VPS ordered:
      OVHcloud VPS-1 in Warsaw, monthly, **Ubuntu 26.04 LTS** instead of 24.04 (§9); first login.
- [x] Basic hardening: system updated; key-only SSH, no root login; `ufw` lets in SSH only; automatic security updates
      with an automatic reboot at 03:30 UTC. Changed from the plan (§9): the image's `ubuntu` user is kept instead of
      an own user; the time zone stays UTC. Emergency access through the KVM console tested.
- [x] `CinemaWatch.Probe` 0.1.0 ([README](../tools/CinemaWatch.Probe/README.md)):
      console app on `CinemaWatch.Core`, published as a self-contained `linux-x64` single file. One run checks every
      profile once (KB §5.4, de-duplicated, layouts cached), logs every request to a monthly CSV (time, endpoint,
      status, duration, size, Cloudflare headers), stops the run at the first 403 / 429 / challenge, logs what it
      would e-mail (no notifications) and fetches the repertoire of the watched cinemas once an hour.
- [x] Smoke test from home (Windows), then deployment (2 Nov): `systemd` service with a temporary user
      (`DynamicUser`) and a timer every 10-12 minutes.
- [x] Run started 2 Nov 2026 on two profiles with six IMAX screenings in Sadyba (21 Nov - 18 Dec). Control group: the
      same profiles in the desktop app's Task Scheduler at home. It keeps running during stage 3.
- [x] Provisional gate (2 Nov, first runs): green - 38 requests, all HTTP 200, no Cloudflare challenges, both hosts;
      "would notify" matched the desktop toast. Thin evidence on purpose: the full check moves to stage 4 (§9).
- Gate criteria (full check before stage 4). Green: at least 5 days without 403 / 429 and without Cloudflare
      challenges; other errors (timeouts, 5xx) under 1% and also seen from home → deploy in stage 4. Red: repeated
      403 / 429 / challenges from the server while home works at the same time → evaluate the residential-poller
      fallback (§11) first. Unclear (single incidents) → collect a few more days. Limit: about 1,600 requests a day
      is a desktop user's load, not the SaaS load of §2.1; green means "the datacenter IP is not blocked outright",
      not "full load is fine".
- How the result is checked: the server's log (statuses and "would notify" lines) against the desktop's toasts at
      home for the same seats at about the same time. From stage 3 the proof is the e-mail (a test inbox on the
      development PC first, then the real inbox).

### Stage 3 - MVP 0 on the development PC (done 5 Nov 2026)
Four increments, each a `feature/saas-<name>` branch squash-merged into `main` after CI; nothing is deployed or tagged.
Every increment ends with something testable in the browser on the PC (`src/CinemaWatch.Web/README.md`).
- [x] 1 `saas-skeleton` (2 Nov 2026): `CinemaWatch.Server` and `CinemaWatch.Web`, PostgreSQL 18 on the PC (EDB zip, no
      service), EF Core migrations; accounts by invitation only (owner from configuration, invitation and reset links,
      sign-in with lockout, account page, password change, data download, account deletion); e-mail over SMTP to a
      local test inbox (smtp4dev); Polish/English UI; privacy page (draft); `Localizer.Use` in Core (language per
      request / e-mail); tests incl. the whole web flow on a real database; CI with PostgreSQL.
- [x] 2 `saas-profiles` (3 Nov 2026): import of one or more `*.cinemawatch.json` files with a server-side check
      (`ProfileImport`, rules in §9), a file with the name of an existing profile replaces it; profile list with
      screenings (cinema from the IMAX list, so profiles from before desktop 1.4 show it too) and watched-seat counts;
      enable / disable / delete (question with "Keep it") / download (the desktop's file and file name); 5 profiles per
      account, the owner sets a different limit per account on the users page; profiles in the
      personal data download, deleted with the account; privacy text; SaaS 0.2.0. No Cinema City requests.
- [x] 3 `saas-poller` (3 Nov 2026): background loop in the web app; `watched_screenings` derived from the enabled
      profiles at every cycle; scheduling (profile interval, server minimum 10 min, up to 20% jitter, new screenings
      within a minute); details + free seats per check, layouts in `seat_plans`; 3-6 s between requests; global budget
      120 requests an hour; failed check (5xx etc.) without retry and without a state change; block (403 / 429 /
      challenge / HTML) stops the cycle, pauses 1 h doubling up to 24 h, one screening first afterwards, e-mails to the
      owner, banner for users; notification state in the database, e-mail "Wolne miejsca" per profile with booking
      links, history; "check now" (no cooldown, greyed out while running, stored result under 1 min, results row by
      row) with a self-refreshing page; test e-mail (the page says when it went to the local test inbox); `check_log`, `service_events`, retention (30 / 90 days); watched and free seat names in the profile list;
      the development fake of Cinema City (files in `C:\DEV\CINEMAWATCH\saas-fake`), refused outside Development;
      SaaS 0.3.3 (0.3.0 plus the owner's first tests: check now without a cooldown and with 1-2 s between screenings,
      the blocked screening first after a pause, a page that reloads itself also after "check now" and around a pause,
      several profiles open at once, kept in the address). Rules in §4.3, decisions in §9.
- [x] 4 `saas-operator` (5 Nov 2026): the operator's panel at `/admin` (owner only, all owner pages and forms under
      `/admin`): status of the components as a checklist (poller: last cycle, pauses; Cinema City: last request, last
      success, last block; e-mail: last sent, last error of any e-mail; database: version, size, last migration,
      largest tables; heartbeat), requests of the last 24 hours hour by hour, answers per HTTP status (24 h, 7 days), the
      latest 20 requests, the latest 100 events; a pause / resume switch (`poller_state.paused_by_operator_at`); poller
      settings stored as overrides in `poller_settings` within fixed ranges, applied from the next cycle, every change
      an event, "restore all values from the configuration"; `component_status` for the mail server and the heartbeat;
      the heartbeat after every finished cycle (at most every 5 min; address a secret, empty = off); e-mail addresses cut
      out of stored mail errors; privacy text reviewed against what is stored; SaaS 0.4.0. Rules in §4.3, decisions
      in §9.
- Done when: the whole flow (invitation → import → freed seat → e-mail with the booking link) works on the PC, one
      manual check against the real API passes, and the SaaS and desktop tests are green in CI. Checked by the owner
      with increment 4's test, before its merge.

### Stage 4 - Deploy to the VPS (staging)
Increments like stage 3; code on `feature/saas-<name>` branches, server and account work recorded in `SAAS_INFRA.md`.
Order: 4.0 → 4.1 and 4.2 side by side → 4.3 → 4.4 → 4.5 → 4.6.
- [x] 4.0 R1 final check (5 Nov 2026, brought forward from "at least 5 days", §9): 3.1 days of probe data
      (2 Nov 15:44 - 5 Nov 19:21 UTC), 410 runs without a gap, 4,997 requests on both hosts: 4,995 HTTP 200, 2 HTTP 500
      (0.04%, about 5 s each, one screening), no 403 / 429, no `cf-mitigated`, every answer JSON, every request through
      Cloudflare Warsaw; median 66-118 ms, 25 answers over 2 s (longest 13.4 s). Control group at home: one HTTP 500
      too (3 Nov), so 5xx are Cinema City's, not a block. Result: **green**. The probe keeps running until the
      reinstall in 4.3 (then removed, `SAAS_INFRA.md` §4.19); the poller's `check_log` keeps measuring after it.
- [x] 4.1 `saas-container` (SaaS 0.5.0; merged 6 Nov 2026, #8): `Saas:TrustForwardedHeaders` (forwarded headers from the tunnel), Secure
      sign-in cookies for an https `Saas:BaseUrl`, `Saas:MigrateDatabaseOnStart`, a start-up check for tzdata and ICU,
      `GET /healthz`; `src/CinemaWatch.Web/Dockerfile`, `infra/compose/` (compose file, `.env` template, container
      test); CI job `container`; `web-image.yml` (GHCR on `web-v*`). Done: CI green, `web-v0.5.0-beta.1` published as
      `0.5.0-beta.1` and `sha-75f16cb`, package public.
- [x] 4.2 Accounts and DNS (6 Nov 2026; `SAAS_INFRA.md` §1.5-1.7): Resend (TOTP) with the domain
      `mail.damiankowalski.pl` in region Ireland; DKIM TXT and two return-path CNAMEs (DNS only) in Cloudflare; test
      e-mail from the PC: Gmail inbox, SPF / DKIM / DMARC pass; DMARC on the apex tightened from `p=none` to `p=reject`;
      the apex keeps `v=spf1 -all`. Cloudflare Zero Trust Free (a card on file), team `damiankowalski`; tunnel
      `staging-cinemawatch` with the route `staging-cinemawatch.damiankowalski.pl` → `http://web:8080` (Inactive until
      4.4); one-time PIN; Access applications for the whole host (owner and testers, 1 week) and for the path `admin`
      (owner only, 24 h); checked from outside (530 before Access, 302 to the login after). Healthchecks.io (2FA),
      check `cinemawatch-staging`, period 5 min, grace 10 min. No secret copied anywhere yet: the Resend key, the tunnel
      token and the ping address go straight into `.env` in 4.4.
- [x] 4.3 `saas-ansible` (SaaS 0.5.1; 6 Nov 2026): `infra/ansible/site.yml` run on the server itself (`ansible-playbook` with a
      local connection, repository cloned there): the stage 2 hardening (SSH drop-in, `ufw`, automatic updates with
      the 03:30 reboot), swap file (2 GB, swappiness 10), Docker from Docker's repository (key fingerprint checked,
      origin in `unattended-upgrades`, `daemon.json` with log limits and `live-restore`), `/opt/cinemawatch`; refuses
      to switch passwords off without a key in `authorized_keys`; CI job `ansible` (syntax check, `ansible-lint
      --profile production`, Ubuntu 26.04 packages). The privacy notice names OVHcloud, Cloudflare (Access), Resend and
      Healthchecks.io and is no longer a draft. Verified by reinstalling the VPS from scratch (`SAAS_INFRA.md` §4.13,
      §3): dry run `failed=0`, real run, reboot for the new kernel, second run `changed=0`, SSH tests, Docker 29.9.3 /
      Compose v5.7.0, swap 2 GB, containers reach the internet. The probe went with the old system.
- [ ] 4.4 First deployment (staging): `.env` on the server (root only), `docker compose up`, the owner's invitation
      by real e-mail, test e-mail (inbox or spam), profile import, one "Check now" from the server's IP, heartbeat
      tested by stopping the container (the alert arrives), `docker stats`; the deployment script (`pg_dump` → pull a
      named version → `up -d` → `/healthz`). Before it: the privacy notice names Resend; stable tag `web-v0.5.0`.
- [ ] 4.5 Backups: daily `pg_dump` off the server (destination chosen then); restore tested once.
- [ ] 4.6 (optional) Deployment from GitHub Actions: a `deploy` user whose key may only run the deployment script
      (`command=` in `authorized_keys`), not membership of the `docker` group (that equals root).

### Stage 5 - First SaaS test
- [ ] 3-5 invited people (possibly only the owner, §9), free, behind Cloudflare Access, 2 weeks. Watch requests
      per hour, 403/429, notification latency, spam placement, errors.
- [ ] Write the lessons into §9 and plan MVP 1 (§5.2): creating profiles on the site - wizard and hall map with seat
      selection like the desktop app - is the next product step (confirmed Nov 2026), not an option.

### Stage 6 - MVP 1 (§5.2)

### After the MVP (order flexible)
Plans and limits → Stripe test mode → OIDC provider (Keycloak) → queue between poller and notifier → Kubernetes (k3s)
as a learning exercise → Cloudflare Workers experiment → Android app as a client of the SaaS.

---

## 8. Hosting for tests (checked Oct 2026)

### 8.1 Chosen: OVHcloud VPS, range "VPS 2027", model VPS-1 (ordered 1 Nov 2026, Warsaw)

- 2 vCores, 4 GB RAM, 40 GB NVMe, daily automated backup (last 24 h), IPv4 + IPv6, 500 Mbps, unlimited traffic,
  anti-DDoS, KVM console in the panel - enough for MVP 0 (web, worker, PostgreSQL, cloudflared).
- Price (Nov 2026): from 16.32 PLN net per month with a 12-month commitment; 19.20 PLN net (23.62 PLN gross) per
  month without commitment in the cart (June 2026: from €3.81 net). Monthly billing, no hourly billing.
- European provider with a Polish company and a data centre in Warsaw.
- Availability changes quickly: on 1 Nov 2026 the configurator first offered VPS-1 only in Canada (Beauharnois) and
  in Europe only VPS-2 (France, about twice the price); hours later VPS-1 in Warsaw could be ordered.
- Avoid "Local Zone" locations: they do not support Docker.
- Weak points: support reputation.

### 8.2 Alternatives considered

Prices per month excluding VAT, checked Oct-Nov 2026.

| Option | Notes |
| --- | --- |
| Hetzner Cloud | Best price / spec and docs, hourly billing; but the cheap CX / CAX line could not be ordered (Sep-Oct 2026). CX23 (2 vCPU / 4 GB) €5.99 with IPv4 when available; CPX22 €19.99 |
| Netcup | VPS 500 G12 (2 vCPU / 4 GB / 128 GB NVMe) €4.97 with IPv4, but a 12-month minimum term; Germany, Austria, Netherlands |
| Scaleway | Warsaw location and hourly billing; DEV1-S (2 vCPU / 2 GB) about €10-11 with IPv4 - tight for MVP 0 |
| Contabo | Many resources for little money (about €6.60 for 4 vCPU / 8 GB, monthly); weak performance in tests, IP ranges popular with bots (bad for R1) |
| OVHcloud Public Cloud | Hourly billing; d2-4 (2 vCore / 4 GB) 0.09 PLN net per hour, about 66 PLN a month 24/7; OpenStack concepts |
| Webh (Poland, Łódź) | KVM mini from 25 PLN (1-2 vCPU / 2-4 GB), hourly backups kept 21 days; 2 vCPU / 4 GB (KVM PRO) about 80 PLN; network protection is automatic blackholing only |
| cyber_Folks (Poland) | vroot START (1 vCPU / 2 GB) 24.90 PLN, renewal 34 PLN; vroot RUN (2 vCPU / 4 GB / 60 GB NVMe) 49.90 PLN; extended anti-DDoS, backups up to 28 days |
| Hostinger, Cal.pl, LH.pl | From a Polish affiliate ranking (jakwybrachosting.pl); not a fit: weekly backups only (Hostinger), not checked (Cal.pl), managed with DirectAdmin (LH.pl) |
| DigitalOcean, Vultr, Linode | Good docs, hourly billing; about €21 ($18-24) for 2 vCPU / 4 GB |
| Azure | Common in enterprises, free credits; expensive for a 24/7 server |
| PaaS (Render, Railway, Fly.io) | Less server work; pay per component (web, worker, database) |
| Cloudflare Workers / Containers | Possible, but means TypeScript (no `CinemaWatch.Core`) and lock-in; Containers need the $5 Workers Paid plan |

### 8.3 Other services

- E-mail provider: **Resend** (chosen 6 Nov 2026, §9). Compared: Brevo (EU data, 300 e-mails a day, but its logo in every
  e-mail on the free plan and a manual review before transactional sending); Scaleway TEM noted as an EU fallback (free
  300 e-mails a month); Postmark, Amazon SES and Cloudflare Email Sending dropped earlier.
- Heartbeat monitor: Healthchecks.io (Hobbyist, free, 20 checks; 4.2).
- Registrar: OVHcloud (chosen in stage 1, §9). Cloudflare Registrar does not sell `.pl`.

---

## 9. Decision log

| When | Decision | Why |
| --- | --- | --- |
| Oct 2026 | SaaS in this repository (monorepo) | Reuse `CinemaWatch.Core` directly; one fix serves desktop and SaaS |
| Oct 2026 | Full vision kept in this document; build MVP 0 first | Large scope: nothing gets lost, the focus stays small |
| Oct 2026 | MVP 0 = server-side profile proxy (import JSON, check, e-mail); wizard in MVP 1 | Tests the biggest risk (server-side polling) before the biggest UI work |
| Oct 2026 | Hosted infrastructure only; on-prem lab parked | One environment to learn and operate |
| Oct 2026 | Risk test from a datacenter IP before writing the web app | R1 decides the architecture |
| Oct 2026 | One PostgreSQL database, rows owned by `user_id`; profiles stored as JSONB v1 | Standard B2C multi-tenancy; the profile contract stays shared |
| Oct 2026 | Cloudflare in front; server reachable only via Cloudflare Tunnel (+ SSH) | No open web ports, no certificates on the server, staging behind Access |
| Oct 2026 | ASP.NET Core Identity for MVP 0; OIDC provider later | Smallest working auth; standards-based upgrade path |
| Oct 2026 | Payments only in Stripe test mode | Learning value without legal and tax obligations |
| Oct 2026 | Proposed: OVHcloud VPS 1 for tests | Price, EU provider with a Polish location; Hetzner's cheap line not orderable |
| Oct 2026 | Umbrella domain `damiankowalski.pl`: registrar OVHcloud, DNS on Cloudflare (Free) | Neutral personal name that fits any product; `.pl` natural for Polish users with a moderate renewal at OVHcloud (58.99 PLN net per year); one account fewer (OVHcloud is also the VPS provider); Cloudflare Registrar does not sell `.pl` |
| Oct 2026 | DNS zone holds only `TXT v=spf1 -all` until stage 4 | The domain sends no e-mail; makes spoofing of the personal-name domain harder |
| Oct 2026 | DNSSEC on: Cloudflare signs the zone, DS set at the registry via OVHcloud | Integrity of DNS answers, incl. future mail records and certificate checks; free, keys managed by Cloudflare. Rule: remove the DS first before disabling DNSSEC or changing the DNS provider (`SAAS_INFRA.md` §4.3) |
| Oct 2026 | Cloudflare account: Google sign-in only, no Cloudflare password or own 2FA (accepted risk) | Setting a password via "forgot password" did not work; the Google account has 2FA. A Cloudflare password is needed for API tokens; MVP 0 should not need one (§10) |
| Oct 2026 | Cinema City terms (R2): personal and learning use within the guardrails; stage 5 testers invite-only and free; no public launch or payments without written consent from Cinema City; no Cinema City / IMAX logos, state independence | E-services terms (current text in force since Oct 2023): no explicit ban on automated reads; forbid reproducing or distributing site content and data without consent; links only to the home page and without benefit; users are natural persons; access can be withdrawn at any time |
| Oct 2026 | Likely audience: the owner only. Plans, trial and Stripe test-mode payments stay on the roadmap as self-tests | Learning value even without outside users |
| Oct 2026 | Infrastructure as built documented in `SAAS_INFRA.md` (state, work log, runbooks, glossary) | This file stays plan and decisions; the infrastructure log grows with every stage |
| Nov 2026 | VPS ordered: OVHcloud VPS-1 in Warsaw, monthly without commitment | As proposed in §8.1; the R1 test runs from a Polish datacenter IP, data stays in Poland (R3). VPS-1 in Canada (first offered) rejected: R1 not representative, against EU hosting, the location cannot change later |
| Nov 2026 | Ubuntu 26.04 LTS instead of 24.04 | LTS with standard support until 2031; officially supported by Docker; the Probe is self-contained, so no .NET is needed on the server |
| Nov 2026 | SSH: one ed25519 key with a passphrase (backup of the key file in the password manager); key-only login, no root login, port 22 | Bots cannot guess a key; another port would be cosmetic |
| Nov 2026 | Keep the image's `ubuntu` user (passwordless sudo) instead of an own user | It already has the key and sudo; with key-only login a user name protects nothing; moving to a new user only adds lock-out risk. A limited `deploy` user comes in stage 4. Passwordless sudo is an accepted risk (`SAAS_INFRA.md` §1.2) |
| Nov 2026 | Server time zone stays UTC | Server standard, no daylight-saving jumps in logs; the app converts to Europe/Warsaw itself (KB §4) |
| Nov 2026 | `ufw`: incoming SSH only, outgoing open; containers publish no ports | The tunnel and the polling are outbound; ports published by Docker bypass `ufw` |
| Nov 2026 | Automatic security updates with an automatic reboot at 03:30 UTC when needed | Kernel fixes take effect only after a reboot; a test server can reboot at night |
| Nov 2026 | Server IP addresses and host name stay out of the repository | Public repository; with the tunnel the origin stays unknown, so Cloudflare cannot be bypassed |
| Nov 2026 | Ansible playbook at the start of stage 4, in this repository (`infra/ansible/`), real inventory outside git | Most of the configuration comes in stage 4; until then the runbooks in `SAAS_INFRA.md` §4 rebuild the server by hand (about 30 minutes) |
| Nov 2026 | No fail2ban for now | With key-only SSH there is nothing to guess; it mainly reduces log noise. Revisit when services with logins are exposed (§11) |
| Nov 2026 | A game server for another project (e.g. swapDuel) goes on a separate VPS | It needs open UDP ports and anti-DDoS; an attack on it must not take this server down (§4.6) |
| Nov 2026 | Direction confirmed: private tool for the owner and invited friends at `cinemawatch.damiankowalski.pl`, everything behind the login, same features as the desktop (wizard and hall map in MVP 1), profiles in PostgreSQL per user, manual and scheduled checks, e-mail with the booking link; no commercial use | Matches §1 and the R2 decision; built in stages (MVP 0 imports desktop profiles) |
| Nov 2026 | MVP 0 refinements: invitation-only accounts; "check now" with a rate limit; the profile's own interval with a server minimum (about 10 min) instead of a fixed 15; "send a test e-mail" | Closed group (R2); same behaviour as the desktop; delivery can be checked without waiting for free seats |
| Nov 2026 | Booking links in e-mails stay as in the desktop toasts (deep links to the ticketing page) | The terms allow links to the home page only; in a closed, free group the practical risk is low; a public launch would need Cinema City's consent anyway (R2) |
| Nov 2026 | Probe: every 10 min + up to 2 min random delay; one CSV row per request; stop the run at the first 403 / 429 / Cloudflare challenge and keep the response; "would notify" log instead of notifications; repertoire once an hour per watched cinema | Slightly stricter than the MVP 0 interval within KB §3.6; data for the gate; no retry storms; checks the notification logic and the second host (`www.cinema-city.pl`, MVP 1) in the same run |
| Nov 2026 | Probe User-Agent `Mozilla/5.0 (X11; Linux x86_64) CinemaWatch-Probe/<version> (+<repository URL>)` | Honest platform and name with a way to find the project (KB §3.1); the desktop UA would claim Windows |
| Nov 2026 | Probe as a `systemd` service with `DynamicUser` and a timer, not as `ubuntu` | No account to maintain; the process cannot use sudo or touch the system - `ubuntu` has passwordless sudo |
| Nov 2026 | Probe built without ICU (invariant globalization); "now" from `PolishTime` (Europe/Warsaw) | Nothing to install on the server; `Localizer` needs no ICU then; the server clock is UTC (KB §4) |
| Nov 2026 | Control group for R1: the same profiles in the desktop app at home during the test | Separates "the datacenter IP is blocked" from "the API changed" |
| Nov 2026 | R1 gate brought forward: provisionally green after the first runs on 2 Nov; the probe keeps running during stage 3 and the full check with at least 5 days of data moves to the start of stage 4 | Stage 3 runs on the development PC and does not need the server; accounts, profiles and e-mail of MVP 0 are needed even with the residential-poller fallback (§11), so little work is at risk |
| Nov 2026 | Probe 0.1.0 stays unchanged while it measures; the empty `bytes` column (compressed responses) is a known issue | Changing the instrument in the middle of the measurement is worse than one missing column; status, `cf_mitigated`, content type and duration decide R1 |
| Nov 2026 | CI: actions on Node 24 (`checkout@v7`, `setup-dotnet@v6`, also in `release.yml`); the Linux job pinned to `ubuntu-26.04`; `*.service` / `*.timer` always with LF line ends | GitHub retires Node 20 for actions; `ubuntu-latest` moves on its own schedule, the pinned label matches the server; a CR from a Windows checkout would break the systemd units |
| Nov 2026 | Stage 3 in four increments (skeleton, profiles, poller, operator view), each its own branch and pull request | Small reviewable steps; `main` holds unfinished SaaS code but nothing is deployed or tagged |
| Nov 2026 | Front end: Blazor Web App with static server-side rendering, no JavaScript; interactivity only in MVP 1 (hall map) | Plain HTML forms cover MVP 0; C# and the Core texts are shared; no WebSocket circuit to keep alive |
| Nov 2026 | Development database: PostgreSQL 18 from the EDB zip archive (`C:\DEV\CINEMAWATCH\pgsql`, cluster `pgdata`), started by hand; role `cinemawatch` with `CREATEDB` | No installer, no Windows service, undo = delete the folders; the same major version as the stage 4 container (19 was still a release candidate); Docker Desktop would need WSL2 |
| Nov 2026 | One process in MVP 0: the web app hosts the background services; their logic lives in the library `CinemaWatch.Server` | One container in stage 4; a separate worker later is only a new host; "check now" goes through the database, so it survives the split |
| Nov 2026 | E-mail over SMTP (MailKit); smtp4dev as the test inbox on the PC (local tool, in memory, localhost only); provider chosen in stage 4 | Every candidate provider offers SMTP, so the provider is configuration; nothing installed globally |
| Nov 2026 | During development the poller uses a fake Cinema City client with captured responses; the real API only on demand | The home IP already carries the R1 control group; fixtures produce a "freed seat" on demand |
| Nov 2026 | `Localizer.Use` (AsyncLocal) in Core: language per web request and per e-mail next to the process-wide language | A server serves people in different languages at once; the desktop is unchanged (tested) |
| Nov 2026 | Accounts: Identity without roles, owner = `Saas:OwnerEmail` (user secrets / environment); invitation and reset use the same one-time link (24 h) and page; "Forgot your password?" re-sends an invitation to a waiting account; links built from `Saas:BaseUrl` | Smallest model for invitation-only accounts; an expired invitation needs no owner; no host-header injection in e-mail links |
| Nov 2026 | Passwords: at least 12 characters, no character rules; 5 failed sign-ins lock the account for 15 minutes | Length beats composition rules, passwords come from a password manager; slows down guessing |
| Nov 2026 | Database names in snake_case (`users`, `user_tokens`); data-protection keys in the database | No quoting in psql; sign-in cookies and e-mail links survive restarts and redeployments |
| Nov 2026 | Development over `http://localhost:5180`, no development certificate | Nothing to install or trust on the PC; HTTPS comes from Cloudflare in stage 4 |
| Nov 2026 | Profile identity in the SaaS = the profile's `name` within an account: importing a profile with the same name replaces it (id and enabled state stay; later its notification state and history) | Same as the desktop (re-adding overwrites the scheduled task) and the Android concept ("re-import replaces"); no `profileId` needed yet |
| Nov 2026 | Profiles stored normalised: read into `WatchProfile` and written as the desktop saves it (`ProfileStore.Serialize`); a download is that JSON with the desktop's file name | JSONB keeps neither key order nor formatting anyway; the download opens in the desktop app; unknown fields of a newer desktop are dropped, exactly as when desktop 1.4 opens and saves such a file |
| Nov 2026 | One limit: 5 profiles per account, counting enabled, disabled and ended ones; a new profile over the limit is refused ("delete one"), replacing an existing one always works; count and insert under a row lock on the account | Bounds the disk per account (5 × 256 KB at worst); simpler than separate "stored" and "active" limits; two browser tabs cannot both take the last place |
| Nov 2026 | The owner sets an account's own profile limit (1-50, empty = the default) on the users page: the first entitlement; roles stay as they are (owner by e-mail, everyone else a user) | A paid plan would later set the same value; the ceiling stops typos; lowering a limit deletes nothing, it only blocks new profiles |
| Nov 2026 | Uploaded profiles are checked more strictly than the desktop's `Load`: at most 10 files of 256 KB per import (2.6 MB per request, larger requests refused with 400); identifiers letters and digits only (they go into request URLs); seat keys `a_b_c`, 1-500 per screening; booking links https on `cinema-city.pl` or a subdomain (they go into e-mails); texts at most 200 characters without control characters; times without an offset; at least one future screening; all problems of a file reported together | The file comes from a browser, not from the user's own disk; nothing from it can reach a URL, an e-mail header or a link unchecked |
| Nov 2026 | The import is a Blazor form on the profile page (static SSR binds uploaded files); enable / disable / delete are form endpoints with a redirect; someone else's profile id answers 404 | The page can show the full result of every file; a successful import redirects, so a reload does not post the files again |
| Nov 2026 | Page state that must survive a reload lives in the address: `?profile=<id>` opens a profile (after enable / disable), `&confirm=delete` adds the deletion question with "Yes, delete" and "Keep it" | No JavaScript (decision above): an open `<details>` exists only in the browser and cannot be closed by a button; a reload keeps the question; someone else's id opens nothing |
| Nov 2026 | Light / dark theme follows the system (`prefers-color-scheme`), no switch in the web app | Same as the desktop app; nothing to store; can be added later as a per-device cookie |
| Nov 2026 | Poller is global: one queue of unique screenings for all users; a block on the server's IP stops checks for everybody | The block is on the IP, not on an account; profiles decide only which screenings are watched and how often |
| Nov 2026 | Each check asks for the screening details and the free seats (2 requests), as the R1 probe did; reusing the details is a switch, off | R1 measured this pair; whether `seats-status` alone passes from a fresh session is unknown. Halving the requests matters at scale, not for MVP 0 |
| Nov 2026 | Pace: the two requests of a screening back to back; between screenings 3-6 s at random in the background, 1-2 s for "check now"; global budget 120 requests an hour for scheduled and manual checks, refilled steadily; when used up, due screenings wait in order | Gentler than the probe's bursts (12-14 requests in 2.5 s, green); a pause before every request made a "check now" of 5 screenings take about 40 s against a few seconds on the desktop, now about 10 s; the budget, not the pause, caps the volume; waiting in order stretches everybody's interval instead of skipping some |
| Nov 2026 | Server minimum interval stays 10 minutes; it cannot be configured below 5 | 10 minutes is what R1 measured; a freed IMAX seat can be gone within minutes, 15 would delay notifications by 2-3 minutes on average |
| Nov 2026 | Failed check (HTTP 5xx, another 4xx, timeout, network error, unreadable answer): notification state unchanged, no retry, next attempt at the normal time | R1 data: one HTTP 500 in 1,144 requests (3 Nov 2026); retries would only add load to a struggling server |
| Nov 2026 | Block = HTTP 403 / 429, a response marked `cf-mitigated`, or HTML instead of JSON from the API: stop the cycle, drop pending "check now" requests, pause 1 h doubling up to 24 h (a longer `Retry-After` wins, at most 7 days), the pause survives restarts; afterwards the blocked screening is tried first, alone | Cloudflare announces no end of a block; a single request after a growing pause is the cheapest way to find out; HTML on an API endpoint means something stands in the way. The owner's first test showed a stale "blocked" row after the pause and a "checking" button during it: the blocked screening goes first, and the pause, not a pending check, is what the page shows |
| Nov 2026 | Alerts: e-mail to the owner (`Saas:OwnerEmail`) at the start and the end of a block; users see a banner, no e-mails to users about pauses | The owner can act; mass e-mails to friends would be noise and hurt the sender's reputation |
| Nov 2026 | A dead process cannot report its own death: an external heartbeat ("dead man's switch") comes with the operator's panel (code) and stage 4 (service) | The only way to notice that the server, the app or the e-mail is down |
| Nov 2026 | Operator's panel in increment 4 grows: status of the components as a checklist, events, request statistics, poller settings in the database with fixed ranges and a pause switch; increment 3 already records the data (`check_log`, `service_events`, `poller_state`) | The owner wanted one place to see the state of the services; settings without SSH; increment 3 stays reviewable |
| Nov 2026 | "Check now" without a cooldown: greyed out only while the profile's check runs or checks are paused; Cinema City is asked only about screenings checked more than a minute ago, fresher ones answer at once from the stored result; new seats from it are e-mailed like any check (first built with a 2-minute cooldown and 5 minutes, changed after the owner's test) | Waiting for the button was unintuitive; the pause between requests (3-6 s, unchanged) and the global budget already cap what reaches Cinema City however often it is pressed; one shared notification state, so a seat seen on the page is never left out of the e-mails |
| Nov 2026 | The page refreshes itself with `<meta http-equiv="refresh">` every 3 s while "check now" runs (at most 5 minutes), when a pause ends and every 10 s until the first request after it got through; the target is the page's address with a new `refresh=` value each time; results are saved screening by screening, so the rows fill in one by one | Keeps the no-JavaScript decision; a check of 5 screenings takes about half a minute at 3-6 s between requests, so progress matters more than speed. A refresh to the same address does nothing when it ends with `#profile-…` (the browser only jumps to the anchor) - found in the owner's test |
| Nov 2026 | The profile header is a link that opens or closes the profile; the open profiles live in the address (`?profile=` in focus, `?open=` others), forms carry them and the redirects restore them | An open `<details>` exists only in the browser, so every self-refresh closed the profiles the owner had opened; without JavaScript the address is the only place that survives a reload |
| Nov 2026 | With a local mail server (`Mail:Host` = localhost) the account page and the test e-mail's message say the e-mail went to the local test inbox, not to the real mailbox | In the owner's first test the message "check your inbox" suggested a real delivery to Gmail |
| Nov 2026 | Notifications: at most one e-mail per profile per cycle, one line per screening with its own booking link and a link to the profile; state saved only after the e-mail went out; a refused e-mail is recorded in the history and notified again at the next check | Same rule as the desktop (KB §4); e-mails can carry several links, unlike a toast |
| Nov 2026 | `watched_screenings` is derived from the profiles at every cycle (no hooks in the import, enable, disable or delete); all database times UTC | Nothing can get out of step; at this scale reading all profiles every 30 s costs nothing |
| Nov 2026 | Development fake of Cinema City: the real client and pipeline on captured responses (Sadyba hall) steered by files (`free-<id>.txt`, `block.txt`, `error500*.txt`); refused outside the Development environment; the tests use the same fake in a temporary folder | The whole pipeline incl. block detection is exercised; seats can be freed and blocks simulated by hand; the fake can never answer on the real server |
| Nov 2026 | Retention: request log 30 days, notification history and events 90 days, unused layouts 30 days; cleanup once a day by the poller | The log and the history, not the profiles, grow the disk |
| Nov 2026 | Test e-mail on the account page: a sample notification to the own address, at most every 5 minutes, no Cinema City request | Checks delivery and spam placement without waiting for a freed seat |
| Nov 2026 | Operator's panel at `/admin`, open to the owner only by the same rule as the users page (the signed-in account's e-mail equals `Saas:OwnerEmail`, checked on the server for the page and every form); all owner pages and forms under `/admin`; in stage 4 Cloudflare Access on `/admin*` (owner's Google account with 2FA) as a second gate; no own 2FA in the app for now | The e-mail cannot be changed in the app, is unique and accounts exist only by invitation, so the rule is safe here; the named policy can switch to roles later without touching the pages; Access gives a second factor without code |
| Nov 2026 | Poller settings in the database as overrides of the configuration (`poller_settings`, null = configuration); ranges fixed in code (`PollerTuning`, §4.3); a value equal to the configuration is not stored; changes apply from the next cycle and are events with old and new values; "restore" removes them all; infrastructure values (`Enabled`, `Source`, tick, retention) and profile limits stay in the configuration | Settings without SSH; the configuration stays the source of defaults (a later change still applies); a range is a code decision, so a mistyped value cannot overload Cinema City; a separate worker would see the same settings |
| Nov 2026 | The configuration may lie outside the panel's ranges (Development: 2-minute pause after a block): the panel shows such a value and keeps it, but cannot set one | Development and tests need short pauses; the panel's ranges are for the real service |
| Nov 2026 | The owner's pause: until resumed, survives restarts, from the next cycle (a running cycle finishes), drops pending "check now", users see a notice without self-refresh, nobody is e-mailed; no button to end a block's pause early | One switch for "stop everything now"; ending a block's pause by hand would bypass the growing pause that protects against retry storms (KB §3.6) |
| Nov 2026 | Heartbeat: GET to `Saas:Heartbeat:Url` after every finished cycle, at most every 5 minutes, 10 s timeout, outside the request budget; https only; the address is a secret (user secrets / environment), never logged; only its host shown in the panel; empty = off, also on the PC; the service (Healthchecks.io, free plan) in stage 4 | Without it a dead server, process or poller goes unnoticed - the block alerts come from the same process; a cycle that throws sends nothing, so a failing poller is caught too; free, a small class of code |
| Nov 2026 | `component_status` table for the mail server and the heartbeat (last success, last failure), written by a wrapper around the mail sender and by the heartbeat | In the database, not in memory: survives restarts and would work after a split into web and worker, like "check now" |
| Nov 2026 | E-mail addresses are cut out of mail error texts kept for the operator (events, `component_status`) | SMTP replies often quote the recipient; events are kept 90 days and must hold no personal data |
| Nov 2026 | The panel does not reload itself; "Odśwież" reloads it; the database section counts rows exactly | A reload would wipe the settings form; PostgreSQL's statistics lag behind and the tables are small |
| Nov 2026 | Privacy text reviewed against what is stored (owner's view of accounts, request log and events without personal data, heartbeat without user data, retention); stays a draft until stage 4 names the e-mail provider | The notice must describe the real service before anyone else uses it |
| Nov 2026 | R1 gate closed early: green on 5 Nov 2026 with 3.1 days instead of "at least 5 days" | 4,997 requests, no block or challenge, 5xx 0.04% and also seen from home; two more days add little - the probe never measured the SaaS load anyway (§7 stage 2). The probe keeps running until the reinstall, and the poller's `check_log` and its block handling (§4.3) keep measuring on staging |
| Nov 2026 | The R1 control group covered one of the two profiles: only the Diuna profile (5 of 6 screenings) ran in the home Task Scheduler, and only while the PC was on (about 23 h of the 75 h) | Found in the final check; enough for the question "blocked or not", since the control group only had to show that Cinema City answers from home at the same time |
| Nov 2026 | Database migrations are applied by the app at start-up when `Saas:MigrateDatabaseOnStart` is on (the container); off everywhere else, where missing migrations still refuse the start; a `pg_dump` before every deployment | One instance, so no race; an EF Core migration bundle would be a second artefact to build and run |
| Nov 2026 | Web image: build on `sdk:10.0-resolute`, run on `aspnet:10.0-resolute-chiseled-extra` (Ubuntu 26.04 like the server: no shell, non-root `app`, ICU and tzdata), read-only file system with a `/tmp` in memory; GHCR package public; deployments name a version, never `latest` | Smaller attack surface; the repository is public anyway, so a public image needs no token on the server; a named version makes going back exact |
| Nov 2026 | The app refuses to start without tzdata (Europe/Warsaw) or ICU (Polish day names) | A wrong image would otherwise fail at the first check or show dates in the wrong language; the container test in CI catches it before deployment |
| Nov 2026 | Behind the tunnel: `Saas:TrustForwardedHeaders` trusts `X-Forwarded-Proto` / `-For` from any sender in the container network; an https `Saas:BaseUrl` makes the sign-in cookies Secure always; the form (antiforgery) cookie follows the request | Only `cloudflared` can reach the app (no published ports). Forcing Secure on the antiforgery cookie makes ASP.NET Core refuse every form on a non-HTTPS request (found in the tests): a missing header would break all pages instead of dropping one flag |
| Nov 2026 | `GET /healthz`: anonymous, "ok" or 503 (database), nothing else | The container test and the deployment script need a check without a sign-in |
| Nov 2026 | No Docker on the development PC: the image is built and started only in CI (job `container`: postgres + web from `infra/compose/compose.yaml`, throwaway `.env`, no tunnel, no e-mail, poller off) | Docker Desktop needs WSL2 (a system change); CI tests the very compose file the server uses |
| Nov 2026 | Images published by `web-image.yml` on tags `web-v*` (betas allowed, tag must match the project version), as `X.Y.Z` and `sha-<commit>`; no GitHub release for SaaS versions | Betas can be deployed to staging; the desktop's `release.yml` (`v*`) never matches |
| Nov 2026 | Pinned images: `postgres:18-trixie` (major only; minor updates with a pull), `cloudflare/cloudflared:2026.10.3` (the newest release at least two weeks old on 5 Nov) | Data on disk belong to the major version; a fresh release can carry a regression or worse |
| Nov 2026 | Ansible runs on the server itself (local connection, repository cloned there), not from WSL2 or GitHub Actions | No system change on the PC and no SSH key with sudo in GitHub Secrets; the same playbook can later run remotely with another inventory |
| Nov 2026 | Docker from Docker's own repository, added to `unattended-upgrades`; container log limits in the compose file (3 × 10 MB) | Docker's supported route for 26.04; without the origin its updates would not install by themselves; default logs grow without limit on a 38 GB disk |
| Nov 2026 | Deployment by hand first (one script on the server); automation later and only with a key restricted to that script | A user in the `docker` group is root in practice, so "limited rights" means a forced command, not a group |
| Nov 2026 | E-mail from a subdomain used only for mail; the apex keeps `v=spf1 -all`; shortlist Brevo and Resend | A name with the tunnel's CNAME (`cinemawatch.…`) cannot carry other records; mail reputation stays apart from other products |
| Nov 2026 | Cloudflare Access login by one-time PIN e-mailed to the owner or tester (supersedes "owner's Google account" for `/admin*`) | No OAuth client in Google Cloud to maintain; the Gmail inbox has 2FA anyway; Google as identity provider can be added later |
| Nov 2026 | Stage 4 runs staging only; the poller is on in exactly one environment on this server's IP | A second poller would double the traffic to Cinema City from the same address |
| Nov 2026 | E-mail provider: Resend (free plan: 3,000 a month, 100 a day, one domain; SMTP; region Ireland) | No logo in the e-mails and no manual review before sending (Brevo's free plan has both); a sending-only key limited to one domain is the narrowest secret on the server; 100 a day covers MVP 0. Cost: Resend keeps account data (recipients, logs) in the US whatever the region - an exception to R3, named in the privacy notice; switching provider is configuration only |
| Nov 2026 | Sender `CinemaWatch <cinemawatch@mail.damiankowalski.pl>`; the subdomain `mail.` serves all future products (the free plan has one domain); no mailbox, nothing received | One verified domain on the free plan; replies are not part of the product |
| Nov 2026 | DMARC on the apex: `p=none` until a real e-mail passed SPF, DKIM and DMARC, then `p=reject` (6 Nov 2026); no reports (no mailbox) | The apex and every subdomain cannot be spoofed; our mail passes aligned DKIM with `mail.damiankowalski.pl` |
| Nov 2026 | DNS records for providers are typed in Cloudflare by hand, never by a provider's "auto configure" | Auto configure would give the provider access to the whole zone, which carries everything else |
| Nov 2026 | Cloudflare Zero Trust Free with a payment card on file (required even for the free plan); team `damiankowalski` | The tunnel and Access live only in Zero Trust; nothing is charged without a paid plan or add-on; the team name serves future products too |
| Nov 2026 | One tunnel per environment (`staging-cinemawatch`), remotely managed in the dashboard; its route `staging-cinemawatch.damiankowalski.pl` → `http://web:8080` | A leaked token exposes one environment only; no configuration file on the server |
| Nov 2026 | Access: two applications - the whole host (owner and testers, session 1 week) and the path `admin` (owner only, 24 h); login by one-time PIN only, with Instant Auth | The more specific path wins and inherits nothing, so testers never reach `/admin`; a weekly PIN is enough because the app has its own sign-in |
| Nov 2026 | A tester is added to the Access policy before the app invites them (`SAAS_INFRA.md` §4.22) | Otherwise the invitation link stops at Access |
| Nov 2026 | Healthchecks.io check per environment: period 5 min, grace 10 min, e-mail to the owner | The app pings at most every 5 minutes; the nightly reboot (1-2 min) stays below the grace |
| Nov 2026 | Secrets from provider panels (Resend key, tunnel token, ping address) are copied only once, straight into `.env` on the server; never into the chat, the PC or a file in the repository; a temporary test key is deleted right after the test | Each one can be shown again or replaced in its panel; fewer copies, fewer leaks |
| Nov 2026 | MVP 1 (wizard and hall map with seat selection in the browser, profiles created on the site) confirmed as the next product step after the first SaaS test (stage 6) | The owner wants the site to create profiles, not only import them from the desktop app |
| Nov 2026 | Ansible as one playbook (`infra/ansible/site.yml`) with tagged parts and a `files/` folder, no roles | One server and a handful of tasks: a single file reads top to bottom; roles can follow if a second kind of server appears |
| Nov 2026 | Ansible from Ubuntu's own packages on the server (`ansible` 13.1, `ansible-core` 2.20, `python3-debian` for the repository module); CI checks with the same packages | Nothing installed from outside the distribution; CI and the server agree on versions. The GitHub runner brings its own newer Ansible (pipx) earlier on `PATH`, which mixed with Ubuntu's broke `ansible-lint`; the CI job puts `/usr/bin` first |
| Nov 2026 | The inventory (`localhost`, local connection) lives in git (supersedes "the real inventory stays out of git") | It holds no address; a remote inventory with the address would stay private |
| Nov 2026 | Docker's release key is checked against its published fingerprint before the repository is added | A wrong or swapped key stops the run instead of being trusted for root-level packages |
| Nov 2026 | `daemon.json`: default log limits (3 × 10 MB) for every container and `live-restore` | Covers containers started by hand too; an automatic Docker update no longer restarts the app |
| Nov 2026 | Swap file 2 GB with `vm.swappiness=10` | A safety net against out-of-memory kills, not extra memory; RAM stays preferred |
| Nov 2026 | The playbook refuses to switch password login off while the admin user has no key in `authorized_keys` | The one step that could lock the owner out is guarded in code, not only in the runbook |
| Nov 2026 | After a reinstall the `ubuntu` user gets a new password (password manager) for the KVM console | A key-only install leaves no usable console password; the console is the way back when SSH fails |
| Nov 2026 | The privacy notice names OVHcloud, Cloudflare (Access), Resend (USA) and Healthchecks.io and is no longer a draft (SaaS 0.5.1) | Everyone who signs in is told which services see their data before the first deployment |

---

## 10. Open questions

| Question | Options / notes |
| --- | --- |
| Check interval | MVP 0: the profile's interval with a server minimum of 10 minutes (decided Nov 2026); the adaptive rules (§4.3) tuned with the stage 4 data |
| One request per check | `ReuseScreeningDetails` (details reused for a day) halves the requests; measure in stage 4 whether `seats-status` alone passes from a fresh session before switching it on |
| Backup destination | Chosen in 4.5: OVHcloud Object Storage, Backblaze B2 or a copy pulled to the PC; the dumps are megabytes |
| Cloudflare password | Needed only for API tokens and Cloudflare's own 2FA; "forgot password" did not work. Revisit with Cloudflare support if a token is ever needed (e.g. DNS-01 certificates for a TURN server, §4.6) |
| Reuse of `CinemaWatch.ViewModels` in the browser | Open until MVP 1 (wizard and hall map need interactivity; MVP 0 pages are static) |

---

## 11. Backlog and parked ideas

- **On-prem lab (parked):** a laptop with Windows 11 Home → WSL2 + Docker, k3s, Cloudflare Tunnel or Tailscale for
  access; or a used mini PC with Proxmox. A MacBook works too (containers run in a Linux VM; Apple Silicon needs
  ARM64 images). Revisit after MVP 0.
- **Residential pollers:** the cloud keeps accounts, profiles and e-mail; agents on home IPs (a PC, possibly the
  Android app) fetch check jobs. Fallback if R1 fails. Downside: a block hits the owner's home IP.
- **Cloudflare Workers experiment:** one component (e.g. the notifier) on Workers to compare serverless with
  containers.
- **More channels:** web push, Telegram / Signal bots.
- **Users e-mailed about long pauses:** e.g. when a block lasts longer than a few hours; today only the owner is
  e-mailed and users see a banner.
- **`profileId`** in the profile format for sync between desktop, Android and SaaS (KB §5.3).
- **Own 2FA (TOTP) for the owner's account** if the operator's panel is ever reachable without Cloudflare Access in
  front of it (needs a QR code page and recovery codes).
- **Learning items:** Kubernetes (k3s), RabbitMQ, Keycloak, Row-Level Security, monitoring (Prometheus / Grafana),
  Ansible (planned for stage 4).
- **SSH brute-force noise:** not a risk with key-only login. If the log noise bothers: `ufw limit 22/tcp` first;
  fail2ban when services with password logins are exposed.
- **Second SSH key** on another device as a further way in (today: one key plus the KVM console).
- **Ubuntu Pro (ESM):** extra security updates for packages outside the main repository; not needed now.
- **Game server for swapDuel:** separate small VPS in Poland with real anti-DDoS, or a managed TURN service (§4.6).

---

## 12. Glossary (SaaS terms)

Infrastructure terms (domains, DNS, DNSSEC, e-mail authentication, accounts) are in
[`SAAS_INFRA.md`](SAAS_INFRA.md) §6.

| Term | Meaning |
| --- | --- |
| VPS | Virtual private server: a rented Linux machine you manage yourself |
| PaaS | Platform as a service: you hand over code or a container, the provider runs it |
| Container / image | An app with its dependencies packaged as an image (like a package); a container is a running instance |
| Registry | Store of images (GHCR) - like a distribution point |
| Docker Compose | One YAML file describing several containers started together |
| Reverse proxy / tunnel | Routes incoming HTTPS to the right container; Cloudflare Tunnel does it without open ports |
| OAuth 2.0 / OIDC | Standards for delegated access and login (Entra ID speaks them too) |
| Multi-tenancy | Many customers in one system; here one database with rows owned by `user_id` |
| Row-Level Security | PostgreSQL rules that filter rows per user inside the database |
| Entitlements | What a user's plan allows (limits), checked on the server |
| Webhook | HTTP call from a service (e.g. Stripe) to our app when something happens |
| Idempotency | Processing the same event twice has the same effect as processing it once |
| Merchant of Record | A company that legally sells on your behalf and handles VAT and consumer law |
| SPF / DKIM / DMARC | DNS records that prove e-mails from a domain are genuine |
| Kubernetes | Orchestrator that keeps a declared state of containers across machines |
| Microservices | Independently deployed services with their own data; mainly solves a many-teams problem |
