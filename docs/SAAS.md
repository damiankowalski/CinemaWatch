# CinemaWatch SaaS - Knowledge Base

> Companion to [`KNOWLEDGE_BASE.md`](KNOWLEDGE_BASE.md). The product, the Cinema City API, the domain rules and the
> profile format live there and are not repeated here. This document covers the SaaS (web) track: goals, risks,
> target architecture, the MVP, the plan and the decisions.
> Order of trust: **repository code** > **KNOWLEDGE_BASE.md** > **this document** > chat history.
> Keep it current: update *Plan* (§7), *Decision log* (§9) and *Open questions* (§10) after every step.
>
> Last updated: October 2026. Status: **planning** - nothing built, no server yet.

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
| R1 | Cloudflare in front of Cinema City blocks or challenges requests from datacenter IPs (KB §10) | Test it first, before building the web app (plan stage 2). Fallback: pollers on residential IPs (§11) |
| R2 | Terms of use: a public (and possibly paid) service built on an unofficial API is a different category than a personal tool | Read the Cinema City terms before the first outside user; record the findings in §9 |
| R3 | Personal data (GDPR): e-mail addresses, notification history | EU hosting, privacy notice, account deletion, store only what the service needs |
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
| Queue | Poller → notifier events | No | RabbitMQ (learning) |
| E-mail provider | Transactional e-mail with SPF / DKIM / DMARC | Yes | - |
| Stripe | Payments | No | Test mode first |

### 4.2 Data model (sketch)

- `users` - ASP.NET Core Identity tables (e-mail, password hash, confirmation).
- `profiles` - `id`, `user_id`, `enabled`, `created_at`, `updated_at`, `document` (JSONB: profile format v1,
  unchanged, so import and export with desktop and Android stay free).
- `watched_screenings` - one row per unique screening id: `cinema_id`, `starts_at` (Europe/Warsaw), cached
  `venue_id`, `seatplan_id`, `venue_type_id`, `is_reserved`, `next_check_at`, last result. The poller works on this
  table, not on profiles.
- `notification_state` - `profile_id`, `screening_id`, the set of available watched seats from the last check
  (KB §4 "notify only about new seats").
- `notifications` - history: when, which profile, which seats, delivery status.
- `check_log` - per request: time, endpoint, HTTP status, duration (needed for R1 and the request budget).
- Later: `plans` / entitlements, `stripe_customers`, `subscriptions`, `payment_events`.

Multi-tenancy: **one database, every row has an owner** (`user_id`). A database per user is a pattern for business
customers with isolation requirements and an anti-pattern here. PostgreSQL Row-Level Security can later act as a
safety net against application bugs.

### 4.3 Poller rules

- Check each unique screening once and fan the result out to every enabled profile that watches it.
- Cache the screening details (`venueTypeId`, `isReserved` do not change), so a check usually needs 1 request
  instead of 2. Layouts are cached and shared (static per `venueId/seatplanId`).
- Adaptive interval (proposal): screenings weeks away less often, the last 48 hours more often; never more often
  than the minimum allowed for the profile.
- Global rate limiter and jitter (no bursts at :00); on HTTP 403/429 back off and log, no retry storms (KB §3.6).
- Past screenings are skipped; a profile whose screenings are all over is disabled automatically.
- Honest User-Agent identifying the app (KB §3.1).

### 4.4 Hosting and infrastructure

- **Server:** one Linux VPS in the EU with Docker and Docker Compose. One server, many containers; future products
  get their own subdomains on the same server.
- **Edge:** Cloudflare Free plan in front. The server runs `cloudflared` (Cloudflare Tunnel): it opens an outbound
  connection to Cloudflare, so no web ports are opened on the server (only SSH) and no TLS certificates are needed
  on the server. Cloudflare Access protects staging.
- **Domain:** one "umbrella" domain for all future products, subdomains per product and environment (e.g.
  `cinemawatch.<domain>`, `staging-cinemawatch.<domain>`). Cloudflare Registrar sells at cost but did not offer
  `.pl` in Oct 2026; for `.pl` use a Polish registrar. DNS on Cloudflare either way.
- **Images and deployment:** GitHub Actions builds container images → GitHub Container Registry (GHCR) → the server
  pulls them. No build tools on the server.
- **Backups:** daily `pg_dump` copied off the server, plus the provider's backup.
- **Kubernetes:** not needed at this scale; a learning item for later (k3s), same images.

### 4.5 Plans, limits and payments (after MVP)

- **Free plan vs trial:** CinemaWatch is used around premieres, so a permanent free plan with limits fits better than
  a time-limited trial.
- **Entitlements:** a plan defines limits - active profiles, screenings per profile, date range, watched seats,
  minimum interval, channels. Enforced on the server (API and worker), never only in the UI. The interval is the
  most expensive limit (request budget).
- **Payments:** Stripe in test mode - Checkout (card data never touches our server), Billing, Customer Portal,
  webhooks. Build both a one-time "pass" (e.g. 30 days around a premiere) and a subscription to learn both. Stripe is
  the source of truth; we keep a mirror updated from webhooks plus an event log with ids (idempotency) and derive
  entitlements from it. BLIK and Przelewy24 matter for Polish users.
- **Real money** means being a seller (VAT, consumer law, invoices, terms). A Merchant of Record (Paddle, Lemon
  Squeezy, Stripe Managed Payments) takes this over for a higher fee. Not before R2 is cleared.

---

## 5. MVP

### 5.1 MVP 0 - "server-side profile proxy"

The desktop app stays the profile editor; the SaaS checks the profiles and sends e-mails. This is the Android concept
(KB §2.2) on a server, with the same contract (KB §5).

In scope:
- Account: sign-up with e-mail and password, e-mail confirmation, login, logout, password reset, account deletion
  (with all data).
- Import of a `*.cinemawatch.json` file (validation as in KB §5.3: refuse a higher `version`, ignore unknown fields).
- Profile list: screenings, number of watched seats, enabled/disabled, last check, last notification; enable,
  disable, delete, download the stored JSON.
- Background checks per KB §5.4 with de-duplication and the poller rules (§4.3).
- E-mail "Wolne miejsca: {film}" with one line per screening and the booking link.
- Notification history per profile.
- Fixed limits in configuration (e.g. 3 active profiles per account, minimum interval 15 minutes).
- Privacy notice page; UI in Polish and English like the desktop app.
- Operator view (owner only): requests per hour, HTTP status breakdown, last errors.

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

- The SaaS lives in this repository. New projects (names proposed): `src/CinemaWatch.Web` (ASP.NET Core),
  `src/CinemaWatch.Worker` (poller + notifier), `tools/CinemaWatch.Probe` (risk test, stage 2).
- `CinemaWatch.Core` is shared. A change in Core needs the desktop tests to pass and must not change desktop
  behaviour unintentionally.
- Branches `feature/saas-<name>`; the same Squash-and-merge flow as the desktop (KB §7.1).
- **Separate versioning:** the root `Directory.Build.props` holds the desktop version; SaaS projects get their own
  version (e.g. a `Directory.Build.props` in their folder). Tags `web-vX.Y.Z` do not match the desktop `release.yml`
  filter `v*`, so no desktop release is created by mistake.
- CI: the current `ci.yml` (Windows: tests + desktop build) stays; the SaaS gets its own job (Linux build, tests,
  container image) when the first SaaS project lands.
- Conventions from KB §7 apply: English code and docs, UI texts via `T("polski", "English")`, tests for every
  behaviour change, no secrets in the repository (configuration via environment variables and GitHub Secrets).

---

## 7. Plan (SaaS only)

Each stage ends with an update of this document.

### Stage 0 - Documents
- [ ] This file in the repository; KB §2.3 links here.

### Stage 1 - Accounts and decisions (no code, almost no cost)
- [ ] Password manager entries and 2FA for GitHub, Cloudflare, the hosting provider, the registrar and the e-mail
      provider.
- [ ] Cloudflare account (Free plan).
- [ ] Choose the umbrella domain name (§10); buy it; move its DNS to Cloudflare.
- [ ] Hosting account (§8). The VPS itself is ordered at the start of stage 2 (monthly billing).
- [ ] Read the Cinema City terms of use (R2); write the findings into §9.

### Stage 2 - First server and the risk test (R1)
- [ ] SSH key on Windows (built-in OpenSSH); order the VPS (Ubuntu 24.04 LTS); first login.
- [ ] Basic hardening: own user with sudo, key-only SSH, no root login, firewall (SSH only), automatic security
      updates, time zone.
- [ ] `CinemaWatch.Probe`: console app on `CinemaWatch.Core`, published as a self-contained `linux-x64` single file;
      runs one check of a profile (KB §5.4) and logs time, endpoint, HTTP status and duration. No notifications.
- [ ] Run it from a systemd timer every 10-15 minutes for 5-7 days on 2-3 real screenings.
- [ ] Decision gate: no blocks → stage 3. Blocks → evaluate the residential-poller fallback (§11) first.

### Stage 3 - MVP 0 on the development PC
- [ ] Solution structure (§6), PostgreSQL for development (§10), EF Core migrations.
- [ ] Identity, profile import and list, poller and notifier as background services, e-mail via the provider's
      test mode.
- [ ] Tests (reuse the fixtures from `tests/CinemaWatch.Tests`).

### Stage 4 - Deploy to the VPS (staging)
- [ ] Docker + Compose on the VPS: `web`, `worker`, `postgres`, `cloudflared`.
- [ ] GitHub Actions: build images → GHCR; deploy = pull + restart on the VPS.
- [ ] Cloudflare Tunnel hostname for staging + Cloudflare Access for testers.
- [ ] E-mail domain: SPF, DKIM, DMARC.
- [ ] Backups: daily `pg_dump` off the server; restore tested once.

### Stage 5 - First SaaS test
- [ ] 3-5 people, 2 weeks. Watch requests per hour, 403/429, notification latency, spam placement, errors.
- [ ] Write the lessons into §9 and decide on MVP 1.

### Stage 6 - MVP 1 (§5.2)

### After the MVP (order flexible)
Plans and limits → Stripe test mode → OIDC provider (Keycloak) → queue between poller and notifier → Kubernetes (k3s)
as a learning exercise → Cloudflare Workers experiment → Android app as a client of the SaaS.

---

## 8. Hosting for tests (checked Oct 2026)

### 8.1 Proposal: OVHcloud VPS, range "VPS 2027", model VPS 1

- From €3.81 per month excl. VAT (OVHcloud, June 2026); IPv4 + IPv6, unlimited traffic, 99.9% SLA. Reviews list
  2 vCores, 4 GB RAM, 40 GB NVMe and a daily backup - enough for MVP 0 (web, worker, PostgreSQL, cloudflared).
- European provider with a Polish company and a data centre in Poland; choose a Polish location if VPS 1 offers it.
- Monthly billing, no hourly billing - order it when stage 2 starts.
- Weak points: support reputation; verify specs and price at checkout.

### 8.2 Alternatives considered

| Option | Notes (Oct 2026) |
| --- | --- |
| Hetzner Cloud | Good docs, hourly billing; but the cheap CX/CAX line could not be ordered in Sep-Oct 2026 and the available CPX22 costs about €20/month |
| DigitalOcean, Vultr, Linode | Good docs, hourly billing; about $18-24/month for 2 vCPU / 4 GB |
| Azure | Common in enterprises, free credits; expensive for a 24/7 server |
| PaaS (Render, Railway, Fly.io) | Less server work; pay per component (web, worker, database) |
| Cloudflare Workers / Containers | Possible, but means TypeScript (no `CinemaWatch.Core`) and lock-in; Containers need the $5 Workers Paid plan |

### 8.3 Other services (chosen in stage 4)

- E-mail provider: Resend, Brevo, Postmark, Amazon SES or Cloudflare Email Sending (beta, Workers Paid plan). Check
  free plans and .NET support when choosing.
- Registrar: Cloudflare Registrar for `.com` / `.dev` / `.app`; a Polish registrar for `.pl`.

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

---

## 10. Open questions

| Question | Options / notes |
| --- | --- |
| Umbrella domain name and TLD | `.com` / `.dev` / `.app` via Cloudflare Registrar, or `.pl` at a Polish registrar |
| Front end | Blazor (C#, shared models, maybe reuse of `CinemaWatch.ViewModels`) vs React / TypeScript |
| Development database | PostgreSQL installed on Windows vs Docker Desktop |
| Web and worker | Two containers from the start vs one process with hosted background services in MVP 0 |
| Check interval | Fixed 15 minutes in MVP 0; adaptive rules (§4.3) tuned with the stage 2 data |
| E-mail provider | See §8.3 |
| Cinema City terms | Findings after stage 1 |

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
- **`profileId`** in the profile format for sync between desktop, Android and SaaS (KB §5.3).
- **Learning items:** Kubernetes (k3s), RabbitMQ, Keycloak, Row-Level Security, monitoring (Prometheus / Grafana).

---

## 12. Glossary (SaaS terms)

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
