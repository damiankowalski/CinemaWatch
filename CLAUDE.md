# CinemaWatch - instructions for Claude Code

Read this first in every session. It replaces the instructions of the claude.ai Project "CinemaWatch" for work in
this repository; the decisions and history live in the docs below, not in any chat.

## Context
- Owner: Damian (Warsaw). **CinemaWatch** notifies when watched seats free up for sold-out Cinema City screenings.
- Read before work: `docs/KNOWLEDGE_BASE.md` (desktop, Cinema City API), `docs/SAAS.md` (SaaS plan §7, decision log
  §9, open questions §10), `docs/SAAS_INFRA.md` (CinemaWatch on the server: containers, tunnel, Access, backup,
  runbooks). For the web app also `src/CinemaWatch.Web/README.md`.
- The shared server itself (domain, DNS, SSH, firewall, Docker, the Ansible playbook) lives in the private repository
  `damiankowalski/VPS-1` (`docs/SERVER.md`) since 12 Nov 2026; changes to it are made in that repository.
- Order of trust: repository code > KNOWLEDGE_BASE.md > SAAS.md > SAAS_INFRA.md. For live infrastructure the provider
  panels (OVHcloud, Cloudflare) are the source of truth - you cannot see them; ask.
- Desktop 1.5.0 is stable. Tracks: **SaaS** - current focus (stage 6, MVP 1: the wizard and hall map on the site as
  close to the desktop as possible); **Android** - planned (KB §2.2; first decision: .NET reusing Core vs Kotlin).
- Start every SaaS task from the first unchecked item of `docs/SAAS.md` §7 and confirm it with Damian.

## Communication
- Talk to Damian in **Polish**. Code, comments, docs, README, commit messages and release notes in **English**. UI
  texts in Polish and English (`T("polski", "English")`).
- Pragmatic, maintainable solutions; no over-engineering.
- "dyskutujemy" / "nie twórz jeszcze zmian" = discussion only: change no files.
- Before a larger feature: a short plan and the few decisions needed; build only after his confirmation.
- A step that needs his go-ahead (a commit, for example): say in a line or two that the state allows it and ask; do it
  after his yes, and only then give his next steps. Never write instructions that start "after my commit" while the
  commit has not happened.
- Damian works on Windows and on a Mac: check the shell before giving commands (PowerShell's `Select-String` and
  `curl.exe` do not exist in zsh).
- SaaS, Linux servers, Docker and cloud are new to Damian: explain briefly, one new concept at a time, and always say
  how to verify and how to undo a step. Mark commands for him as **Windows (PowerShell)** or **server (bash, SSH)**.

## How we work here (Claude Code)
- You edit the working tree directly and run builds and tests yourself. Damian reviews the diff.
- **Git: you never push, tag, merge, rebase, reset --hard or delete branches.** You may create a feature branch,
  stage and - when Damian says so - commit on it. Pushing, Squash and merge, tags and releases are Damian's (give him
  the exact commands). Never rewrite `main`.
- **GitHub (`gh`):** once Damian has pushed a branch you open its pull request (`gh pr create`, title and body in
  English) and read CI (`gh pr checks`, `gh pr view`, `gh run list`, `gh run view`). You never merge, close, release or
  change repository settings; those `gh` commands are denied in `.claude/settings.json`.
- Before you start: `git status`, `git branch --show-current`, `git log --oneline -3`; never build on assumed state.
- **No server access.** Deployments to the VPS (staging/production at damiankowalski.pl) are Damian's: write the
  steps, he runs them over SSH.
- When a file is removed or renamed, say so in your summary (`git rm` / `git mv`).

## Guardrails
- Nothing that affects Damian's PC outside this repository without his explicit confirmation (scheduled tasks,
  settings, installs, files elsewhere). The desktop app asks before creating a scheduled task - keep it that way.
- Paid steps and changes to server security (SSH, firewall, users) only after his explicit confirmation, with the
  cost and the way back stated first. Never a step that could lock him out of the server.
- Secrets (passwords, API keys, tokens, SSH keys, connection strings) never in the repository and never in your
  output; do not read user-secrets files or `.env`. A leaked secret is rotated, not just deleted.
- Cinema City API: read-only. Never automate the cart, reservations or purchases; never bypass Cloudflare Turnstile;
  respect the request budget (KB §3.6, SAAS.md §4.3, §5.2) - also in tests and local runs. Development and tests use
  the fake (`Saas:Poller:Source = Fake`); a request to the real Cinema City only when Damian asks for it.
- No personal data in the repository (the e-mail in `scripts/CinemaWatch.ps1` is a placeholder).

## Build and test (Windows, .NET 10 SDK, no Visual Studio)
- Desktop and shared logic: `dotnet run --project tests/CinemaWatch.Tests -c Release`
- Probe: `dotnet run --project tests/CinemaWatch.Probe.Tests -c Release`
- SaaS: `dotnet run --project tests/CinemaWatch.Saas.Tests -c Release` - the database and web tests need
  `CINEMAWATCH_TEST_DB` (set by Damian, see the web README); without it they are skipped: say so, never report them
  as passed.
- Desktop window: `dotnet build src/CinemaWatch.App -c Release` builds here; Damian still tests the window by hand.
- Core and ViewModels build with warnings as errors; new behaviour gets tests.
- Migrations: `dotnet ef migrations add <Name> --project src/CinemaWatch.Server --startup-project src/CinemaWatch.Web --output-dir Data/Migrations`.

## Versions and releases
- Desktop version in the root `Directory.Build.props`; tags `v*` publish the desktop exe.
- SaaS version in `src/CinemaWatch.Server` and `src/CinemaWatch.Web` csproj; tags `web-vX.Y.Z` - never `v*`.
- Features: `feature/<name>` (SaaS: `feature/saas-<name>`) → push (CI) → beta tag → Damian's tests → stable →
  pull request → Squash and merge → tag on `main` → delete branch and beta release. Small and docs-only changes go
  straight to `main` (Damian commits).
- Every desktop release: `Directory.Build.props`, `.github/RELEASE_NOTES.md`, README if behaviour changed, the KB
  (status, version history, decision log).
- End of every SaaS stage: `docs/SAAS.md` (§7, §9, §10) and `docs/SAAS_INFRA.md` (state, work log, runbooks).
  Decisions agreed with Damian go into §9 the same day - the repository is the project's memory.
