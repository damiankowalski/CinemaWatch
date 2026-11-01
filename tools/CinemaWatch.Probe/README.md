# CinemaWatch.Probe

Risk test **R1** of the SaaS track ([`docs/SAAS.md`](../../docs/SAAS.md) §2, §7 stage 2): does Cloudflare in front of
Cinema City accept requests from a datacenter IP? The probe runs on the VPS, checks real profiles exactly like the
desktop's background check (KB §5.4) and logs every request. It sends **no notifications**: it only logs what it
would have e-mailed, so the result can be compared with the desktop's toasts at home.

One run checks every profile once and exits; a systemd timer repeats it every 10-12 minutes.

## Rules it follows

- Read-only endpoints only (screening details, free seats, hall layout, repertoire); never the cart.
- Each screening is fetched once per run, even if several profiles watch it; hall layouts are cached for 7 days.
- At the first HTTP 403 / 429 or Cloudflare challenge the run stops and sends nothing more (no retry storms).
- The repertoire of each watched cinema is fetched at most once per hour (tests the second host,
  `www.cinema-city.pl`, needed by the browser wizard of MVP 1).
- Honest User-Agent: `Mozilla/5.0 (X11; Linux x86_64) CinemaWatch-Probe/0.1.0 (+https://github.com/damiankowalski/CinemaWatch)`.
- Screening times are Polish time (Europe/Warsaw); the server's clock is UTC.

## Run locally (Windows)

```powershell
dotnet run --project tools\CinemaWatch.Probe -c Release -- --profiles C:\DEV\CINEMAWATCH\probe\profiles --data C:\DEV\CINEMAWATCH\probe\data
```

`--repertoire-every <minutes>` changes the repertoire interval (default 60, `0` = never). `--help` prints the usage.

## Build for the server

```powershell
.\tools\CinemaWatch.Probe\publish.ps1
```

Output: `artifacts\probe\CinemaWatch.Probe` (one self-contained Linux file, about 35 MB, no .NET needed on the
server) and `CinemaWatch.Probe.sha256` (check the copy on the server with `sha256sum -c`).
Server setup: [`deploy/`](deploy) (systemd service and timer); runbooks in [`docs/SAAS_INFRA.md`](../../docs/SAAS_INFRA.md).

## What it writes (data folder)

| File | Contents |
| --- | --- |
| `requests-yyyy-MM.csv` | One row per request (UTC month) - the data for the decision gate |
| `would-notify.log` | One line per notification the SaaS would have e-mailed |
| `state/<profile>.json` | Notification state per profile (notify only about newly freed seats) |
| `cache/seatplan-*.json` | Hall layouts (7 days) |
| `repertoire-last.txt` | Time of the last repertoire requests |
| `blocked/<run>.html` | The response that blocked a run (e.g. a Cloudflare challenge page), for diagnosis |

A summary of every run goes to standard output (on the server: the journal, `journalctl -u cinemawatch-probe`).

CSV columns: `time_utc`, `run`, `method`, `host`, `endpoint` (`presentation`, `seats-status`, `seatplan`,
`repertoire`), `target` (path and query), `status`, `duration_ms` (including the body), `bytes`, `content_type`,
`cf_mitigated` (set by Cloudflare on challenges), `cf_ray` (Cloudflare request id), `retry_after`, `error`
(network errors and timeouts).

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Checked (some screenings may have failed) |
| 1 | Configuration error: bad arguments, no readable profile, data folder not writable |
| 2 | Every upcoming screening failed (e.g. HTTP 5xx, network down) |
| 3 | Blocked: HTTP 403 / 429 or a Cloudflare challenge; the run stopped |
| 4 | Nothing to check: all screenings of all profiles are over |

## Tests

```powershell
dotnet run --project tests\CinemaWatch.Probe.Tests
```

They run with the same globalization mode as the server build (no ICU) and use the captured API responses of
`tests/CinemaWatch.Tests/Fixtures`.
