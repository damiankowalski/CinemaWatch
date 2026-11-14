# CinemaWatch SaaS - Infrastructure (as built)

> Companion to [`SAAS.md`](SAAS.md) (plan and decisions) and [`KNOWLEDGE_BASE.md`](KNOWLEDGE_BASE.md). This file
> records what actually exists, how it was set up and why, how to verify it and how to undo it.
> The provider panels (OVHcloud, Cloudflare) are the source of truth for live settings; update this file with every
> change: the state (§1), the work log (§3) and, if needed, the runbooks (§4). The infrastructure glossary is in
> [VPS-1 SERVER.md] §6.
> **No secrets here** - no passwords, recovery codes, API keys or tokens; they live in the owner's password manager.
> **No server addresses either** - the server's IPv4 / IPv6 and host name stay out of this public repository (behind
> Cloudflare Tunnel the origin must stay unknown); runbooks write `<server-ip>`, the real value is in the password
> manager and the OVHcloud panel. Everything below is public anyway (DNS data is public; SSH key fingerprints are
> public by design).
>
> **Since 12 Nov 2026 (stage 7b) the shared server has its own documentation**: the domain, DNS and DNSSEC, the
> accounts, the server itself (SSH, firewall, updates, swap, Docker), the Ansible playbook and the backup platform are
> in the private repository `damiankowalski/VPS-1` ([VPS-1 SERVER.md]), which keeps this file's section numbers
> (decision [`SAAS.md`](SAAS.md) §9, 12 Nov 2026). This file keeps what belongs to CinemaWatch: its containers,
> tunnel, Access, e-mail sender, heartbeat, backup, the probe, and their runbooks. Moved sections are left as
> pointers so that existing `§` references still lead somewhere.
>
> Last updated: 12 November 2026 - stage 7b: the server's configuration (`infra/ansible`) and documentation moved to
> VPS-1. Before: 8 November 2026 - stage 4 done (4.6 skipped on purpose): CinemaWatch runs on the VPS behind
> Cloudflare Tunnel and Access since 6 Nov 2026, now 0.5.2; encrypted daily database backup to Backblaze B2 (EU
> Central), restore tested (§1.8). Stage 5 started: testers added to the Access policy and invited in the app (§3,
> §4.22); the two-week test runs since 7 Nov 2026 (§4.27); SaaS 0.6.0-beta.1 deployed 7-8 Nov. Access keeps guarding
> the whole host: the switch to model A is postponed ([`SAAS.md`](SAAS.md) §9, §11; ready runbook §4.28).

[VPS-1 SERVER.md]: https://github.com/damiankowalski/VPS-1/blob/main/docs/SERVER.md

---

## 1. Current state

| Item | Value |
| --- | --- |
| Platform | Domain `damiankowalski.pl`, registrar, DNS and DNSSEC (Cloudflare), the server (OVHcloud VPS-1, Warsaw) and its configuration: [VPS-1 SERVER.md] §1 |
| Website | `staging-cinemawatch.damiankowalski.pl`: CinemaWatch since 6 Nov 2026 (0.7.0-beta.1 with the wizard since 14 Nov), behind Cloudflare Access (§1.6) and the app's own sign-in |
| E-mail | Sending only, through Resend from `mail.damiankowalski.pl` (§1.5); no mailbox, nothing received |
| Container image | `ghcr.io/damiankowalski/cinemawatch-web`, public package; versions `0.5.0-beta.1`, `0.5.1`, `0.5.2`, `0.6.0-beta.1`, `0.7.0-beta.1` (deployed), with `sha-<commit>` tags |
| Heartbeat monitor | Healthchecks.io, checks `cinemawatch-staging` (§1.7; the alarm by e-mail tested on 7 Nov 2026) and `cinemawatch-backup` (§1.8) |
| Backups | Daily at 02:30 UTC: `pg_dump`, encrypted with `age`, to Backblaze B2 bucket `cinemawatch-backup` (EU Central), the newest 7 also on the server; restore from B2 tested on 7 Nov 2026 (§1.8) |
| Costs | Shared with the other applications on the server: [VPS-1 SERVER.md] §1 |

### 1.1 Accounts

Moved to [VPS-1 SERVER.md] §1.1 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 1.2 Accepted risks

Moved to [VPS-1 SERVER.md] §1.2 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 1.3 Server (VPS)

The server itself (model, system, SSH, firewall, updates, swap, Docker, the playbook) moved to [VPS-1 SERVER.md]
§1.3. CinemaWatch's parts on it:

| Item | Value |
| --- | --- |
| App folder | `/opt/cinemawatch` (root, `0750`): `compose.yaml` (copied from the deployed tag), `.env` (root, `600`; §4.23), `backups/` (root, `0700`; a `pg_dump` before every deployment, the newest 10; `daily/` the newest 7 encrypted daily backups) |
| Backup job | `/usr/local/sbin/cinemawatch-backup` (from `infra/backup/backup.sh` in the clone `~/CinemaWatch`, installed by VPS-1's playbook), `cinemawatch-backup.service` + `.timer` (02:30 UTC, up to 5 min random delay, `Persistent`); config `/etc/cinemawatch/backup.env` (root, `600`); log: `journalctl -u cinemawatch-backup.service` (§1.8, §4.25) |
| Our services | Since 6 Nov 2026 the containers of `infra/compose/compose.yaml`, project `cinemawatch`: `postgres` (18), `web` (0.6.0-beta.1 since 7-8 Nov), `cloudflared` (2026.10.3); deployed with `deploy.sh` (§4.24). Memory on 7 Nov: web 222 MiB, postgres 38 MiB, cloudflared 16 MiB. `CinemaWatch.Probe` ran 2-6 Nov 2026 and went with the reinstall (§1.4) |

### 1.4 CinemaWatch.Probe (stage 2, risk test R1)

| Item | Value |
| --- | --- |
| Version / source | 0.1.0 from `tools/CinemaWatch.Probe` ([README](../tools/CinemaWatch.Probe/README.md)); built on the development PC with `publish.ps1`: one self-contained `linux-x64` file, about 35 MB, no .NET on the server |
| Program | `/opt/cinemawatch-probe/CinemaWatch.Probe` (root, 0755) |
| Profiles | `/etc/cinemawatch-probe/*.cinemawatch.json` (root, 0644): two profiles from the desktop app, six IMAX screenings in Sadyba between 21 Nov and 18 Dec 2026 |
| Data | `/var/lib/cinemawatch-probe` - a link into `/var/lib/private`, readable with `sudo` only: `requests-yyyy-MM.csv`, `would-notify.log`, `state/`, `cache/`, `repertoire-last.txt`, `blocked/` (only after a block) |
| Units | `/etc/systemd/system/cinemawatch-probe.service` (oneshot, temporary user `DynamicUser`, sandboxed, `MemoryMax=300M`) and `cinemawatch-probe.timer` (1 min after the timer starts, then every 10 min plus up to 2 min random delay); timer enabled since 2 Nov 2026, so it also starts after every reboot |
| Output | The journal: `journalctl -u cinemawatch-probe.service` |
| Load | About 12 requests per run (2 per screening; hall layout cached for 7 days) plus the repertoire once an hour: about 1,600 requests a day. One run: about 2.5 s, 1.2 s CPU, memory peak 75 MB |
| Control group | The desktop app at home; "would notify" matched the desktop toast on 2 Nov. In fact only the Diuna profile (5 of the 6 screenings) ran in the home Task Scheduler, and only while the PC was on (found in the final check, §3) |
| Known issue | Column `bytes` stays empty for compressed responses (.NET does not report the size after decompression); irrelevant for R1, fixed only if the probe is used again |
| Result | R1 green on 5 Nov 2026 (§3, stage 4 step 1) |
| Ended | 6 Nov 2026: removed with the whole system by the reinstall of 4.3; the data of 2-5 Nov is on the PC (`C:\DEV\CINEMAWATCH\probe\gate`). §2.3 and runbooks §4.15-4.19 stay as the record of how it ran |

### 1.5 E-mail (Resend)

| Item | Value |
| --- | --- |
| Provider / plan | Resend, Free (3,000 e-mails a month, at most 100 a day, one domain); SMTP `smtp.resend.com`, port 587, STARTTLS, user `resend`, password = an API key |
| Sending domain | `mail.damiankowalski.pl` (only for mail; the tunnel's names cannot carry other records), region Ireland (`eu-west-1`), status Verified (6 Nov 2026) |
| Sender | `CinemaWatch <cinemawatch@mail.damiankowalski.pl>`; replies go nowhere (no mailbox) |
| DKIM | `TXT resend._domainkey.mail.damiankowalski.pl` = `p=MIGfMA0GCSqG…pBywIDAQAB` (public key; full value in Resend and Cloudflare) |
| Return path (SPF) | `CNAME send.mail` → `send.forge.rmta.net` and `CNAME rsend.mail` → `rsend-euw1.forge.rmta.net`, both DNS only (a proxied record would break them) |
| DMARC | `TXT _dmarc.damiankowalski.pl` = `v=DMARC1; p=reject;` since 6 Nov 2026 (first `p=none` until the test passed); applies to the apex and every subdomain; no reports (no mailbox to receive them) |
| Apex SPF | `v=spf1 -all` unchanged: the apex sends nothing; Resend's e-mails use `send.mail…` as their return path |
| Test (6 Nov 2026) | One e-mail over SMTP from the PC with a temporary key: inbox (not spam) in Gmail; SPF pass (IP 54.240.3.11), DKIM pass with `mail.damiankowalski.pl`, DMARC pass. The test key was deleted |
| API keys | `staging-server` (6 Nov 2026): sending access, domain `mail.damiankowalski.pl` only; only in the server's `.env` (`MAIL_PASSWORD`). The test key of 4.2 was deleted |

### 1.6 Cloudflare Zero Trust: tunnel and Access

| Item | Value |
| --- | --- |
| Organization | Team domain `damiankowalski.cloudflareaccess.com` (public: the login page testers see); plan Zero Trust Free, $0, 50 seats (one per person who signs in through Access) |
| Login methods | Cloudflare (default, added with the organization) and One-time PIN (code by e-mail from `noreply@notify.cloudflare.com`, valid 10 minutes); both applications accept the PIN only, with Instant Auth |
| Tunnel | `staging-cinemawatch`, remotely managed (configured in the dashboard); Healthy since 6 Nov 2026: one connector (`cloudflared` 2026.10.3 in a container), 4 QUIC connections to Warsaw. Its token is a secret, only in the server's `.env` (`TUNNEL_TOKEN`) |
| Route (published application) | `staging-cinemawatch.damiankowalski.pl` → `http://web:8080` (the app container in the compose network); DNS: `CNAME staging-cinemawatch` → `<tunnel id>.cfargotunnel.com`, Proxied. The CNAME's tunnel id must equal the tunnel's own id (*Tunnel details*): it pointed at a deleted tunnel until 6 Nov 2026 (error 1033) |
| Access application 1 | `CinemaWatch staging`: the whole host; policy `Owner and testers` (Allow, Include: e-mails - the owner and the invited testers, 7 Nov 2026); session 1 week |
| Access policies | Reusable objects of their own (*Access controls → Policies*), attached to applications; Cloudflare accepts two policies with the same name. `Owner and testers` exists once again since a duplicate was removed on 7 Nov 2026 (§3) |
| Access application 2 | `CinemaWatch staging admin`: path `admin` (covers `/admin` and everything below it); policy `Owner only`; session 24 hours. The more specific application wins and inherits nothing, so testers never pass `/admin` |
| Checked (6 Nov 2026) | Before Access: HTTP 530 from Cloudflare (`-WAW`, tunnel without a connector); with Access: HTTP 302 to the team domain's login page (§4.21) |

### 1.7 Healthchecks.io (heartbeat)

| Item | Value |
| --- | --- |
| Plan | Hobbyist, $0 (20 checks) |
| Check | `cinemawatch-staging` (created again on 7 Nov 2026, the first one deleted - its ping address had shown in a screenshot): simple schedule, period 5 minutes, grace 10 minutes (an alert after about 15 minutes without a ping; the nightly reboot at 03:30 UTC takes 1-2 minutes). The e-mail integration must be switched on **per check** (the "@" in the Integrations column dark, not grey). Alarm tested on 7 Nov 2026: `web` stopped → "DOWN" e-mail, started → "UP" e-mail |
| Ping address | `https://hc-ping.com/…` - a secret (whoever has it can pretend the server is alive); only in the server's `.env` (`SAAS_HEARTBEAT_URL`); the app never logs it, and it never goes into a screenshot |

### 1.8 Backups (Backblaze B2)

| Item | Value |
| --- | --- |
| Account | Backblaze, region **EU Central** (fixed at sign-up), free up to 10 GB (we use kilobytes) |
| Bucket | `cinemawatch-backup`, private, server-side encryption on, endpoint `s3.eu-central-003.backblazeb2.com`; lifecycle rule: files under `daily/` hidden after 30 days, deleted 1 day later |
| Server's key | `staging-server-backup`: bucket `cinemawatch-backup` only, **Write Only** - the server can add backups but never read or delete them; only in `/etc/cinemawatch/backup.env` |
| Encryption | `age`; the public key (`age1…`) in `backup.env`, the private key (`AGE-SECRET-KEY-1…`) only in the password manager ("CinemaWatch backup age key") |
| Schedule | Daily 02:30 UTC (+ up to 5 min), the newest 7 copies also in `/opt/cinemawatch/backups/daily`; files `daily/cinemawatch-<UTC time>.dump.age` |
| Monitoring | Healthchecks check `cinemawatch-backup`: cron `30 2 * * *` UTC, grace 2 hours, e-mail on; the script pings start, success (file and size) or failure (the reason) |
| First backup | 7 Nov 2026 12:51 UTC, 56 KB, uploaded in 3 seconds |
| Restore test | 7 Nov 2026 from B2 with a temporary read-only key (deleted afterwards): every one of the 16 tables restored, counts equal to the live database (except rows added since), same last migration. Next: every 3 months and after a schema change (§4.26) |

---

## 2. How the pieces fit together

Domain and DNS, DNSSEC and SSH access: [VPS-1 SERVER.md] §2. CinemaWatch's parts: §2.3 (the probe), §2.4 (the way
in to the app).

### 2.1 DNSSEC in short

Moved to [VPS-1 SERVER.md] §2.1 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 2.2 The server and SSH access

Moved to [VPS-1 SERVER.md] §2.2 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 2.3 The probe on the server (2-6 Nov 2026)

```
cinemawatch-probe.timer (every 10-12 min, also after a reboot)
  '-> cinemawatch-probe.service (oneshot; temporary user without rights; sandbox)
        '-> /opt/cinemawatch-probe/CinemaWatch.Probe
              reads   /etc/cinemawatch-probe/*.cinemawatch.json
              writes  /var/lib/cinemawatch-probe/  (CSV, state, cache; root-only folder)
              prints  to the journal
              '-> HTTPS out (ufw allows outgoing) -> Cloudflare (WAW) -> Cinema City, read-only
```

- **Why a temporary user.** `DynamicUser=yes` makes systemd create a user without any rights for each run. It can
  write only to its own data folder; it cannot use sudo, read home folders or change the system. `ubuntu` has
  passwordless sudo, so a bug in parsing data from the internet would otherwise mean root. Between runs the files
  show `nobody` as their owner.
- **Why the data folder needs sudo.** systemd keeps the data of temporary users in `/var/lib/private` (root only)
  and links `/var/lib/cinemawatch-probe` to it.
- **sudo and `*`.** The shell expands `*` into file names before the command runs, as `ubuntu`, who cannot look
  into that folder: `sudo cat /var/lib/cinemawatch-probe/requests-*.csv` fails with "No such file". Use the full
  name or let a root shell expand it: `sudo sh -c 'cat /var/lib/cinemawatch-probe/requests-*.csv'`.
- **Nothing opened.** The probe only connects out; `ufw`, SSH and the users are unchanged.

### 2.4 The way in to the app (from stage 4)

```
Browser --HTTPS--> Cloudflare (WAW)
                     |-- Access: is the visitor signed in? if not -> login page of damiankowalski.cloudflareaccess.com
                     |           (e-mail -> one-time PIN); /admin: owner only, the rest: owner and testers
                     '-- tunnel staging-cinemawatch <--outbound connection-- cloudflared (container on the VPS)
                                                                               '-> http://web:8080 (app container)
                                                                                   '-> its own sign-in (accounts by invitation)
```

- **Two gates.** Access decides who may see anything at all (e-mail + PIN); the app's own sign-in decides which
  account it is. A tester must be on the Access list before the app's invitation link can be opened (§4.22).
- **No open web ports.** `cloudflared` connects out to Cloudflare; the VPS's address appears nowhere (the DNS answer is
  Cloudflare's own addresses). `ufw` keeps SSH as the only way in.
- **HTTPS ends at Cloudflare.** The tunnel carries the request to `http://web:8080`; the app trusts the tunnel's
  `X-Forwarded-Proto` (`Saas:TrustForwardedHeaders`), so it knows the visitor used HTTPS.
- **E-mail goes out separately**: the app → Resend over SMTP (port 587, outbound) → recipients; nothing comes back in.

---

## 3. Work log

### Stage 0 - documents (Oct 2026)

- `docs/SAAS.md` added; KB §2.3 links to it (commit `5884d15`).

### Stage 1 - accounts, domain, DNS (30-31 Oct 2026)

1. **Second factors.** 2FA confirmed on GitHub and on the Google account; recovery codes in the password manager.
2. **Cloudflare account** (Free plan) created with "Sign in with Google". Such an account has no password; the
   "forgot password" route to set one did not work, so Cloudflare's own 2FA could not be enabled (§1.2).
3. **Domain choice.** Personal name as the umbrella: neutral, lasting, fits any product. Compared in the OVHcloud
   shop (net, first year → renewal): `.pl` 16.69 → 58.99 PLN; `.eu` 35.49 → 40.69 PLN; `.com` already taken;
   promotional TLDs rejected for their renewals (e.g. `.store` 4.27 → 242.29 PLN, `.cloud` 8.41 → 92.69 PLN).
   Chosen: `.pl` at OVHcloud - natural for Polish users, moderate renewal compared with other Polish registrars, and
   one account fewer because OVHcloud is also the planned VPS provider. Cloudflare Registrar does not sell `.pl`.
4. **OVHcloud account** with 2FA; domain ordered for 1 year. Removed from the cart: the *DNSSEC* option (it signs on
   OVHcloud's nameservers and would have broken the move to Cloudflare; DNSSEC was enabled later on Cloudflare) and
   the *Zimbra Starter* mailbox (e-mail comes in stage 4); no hosting plan. Holder: private person. Result:
   registered 30 Oct 2026, technical status active, transfer protection on, DNSSEC off.
5. **DNS moved to Cloudflare.**
   1. Cloudflare → *Add a domain* → "We can't register this TLD" → *Connect* (Cloudflare provides DNS only; the
      registrar stays OVHcloud). AI crawler policy left at defaults; DNS records imported automatically; plan *Free*.
   2. The imported OVHcloud default records were deleted: `A @` and `A www` → `213.186.33.5` (OVHcloud welcome
      page), `CNAME ftp`, `TXT "1|www.damiankowalski.pl"` and `TXT www "3|welcome"` (OVHcloud markers). Kept:
      `TXT "v=spf1 -all"`.
   3. OVHcloud → domain → *DNS servers* → *Edit DNS* → **Use my own DNS** → `faye.ns.cloudflare.com`,
      `tosana.ns.cloudflare.com`. Not "Use OVHcloud DNS and my own DNS": mixed nameservers give inconsistent
      answers and Cloudflare would not activate the zone.
   4. Cloudflare showed "Invalid nameservers - waiting for your registrar" for a few hours (normal: the registry
      publishes changes with a delay), then the zone became active on 31 Oct 2026. Checked with §4.1.
6. **DNSSEC.**
   1. Cloudflare → *DNS → Settings → DNSSEC → Enable DNSSEC*. The dialog shows the DS record, digest, digest type,
      algorithm, public key, key tag and flags; status *Pending*.
   2. OVHcloud → *DS records* → *Add*: key tag `2371`, flag `257` (KSK), algorithm `13` (ECDSAP256SHA256) and the
      **public key** from Cloudflare. OVHcloud asks for the public key, not the digest, and computes the DS itself.
   3. DNSViz during propagation reported errors "CDS / CDNSKEY RRset must be signed with a key that is represented
      in both the current DNSKEY and the current DS RRset" and warnings "NSEC3 proving non-existence of DS" - expected
      while the DS is not yet in `.pl`. The NSEC3 warning "salt should be empty" concerns the `.pl` zone itself (NASK).
   4. Cloudflare: "Success! damiankowalski.pl is protected with DNSSEC". The DS seen through 1.1.1.1 has key tag
      2371 and a digest matching Cloudflare's (§4.2).
7. **Cinema City terms (R2)** read; findings and rules in [`SAAS.md`](SAAS.md) §9.

### Stage 2 - server and hardening (1 Nov 2026)

1. **SSH key.** ed25519 with a passphrase generated on the development PC (`ssh-keygen`); passphrase in the password
   manager. Other files in `.ssh` are unrelated and not offered automatically (non-default names).
2. **Ordering.** The OVHcloud configurator first offered VPS-1 only in Canada (Beauharnois) and in Europe only VPS-2
   (France). Canada was rejected before paying (R1 not representative, EU hosting, location fixed); alternatives were
   compared ([`SAAS.md`](SAAS.md) §8.2). Later the same day VPS-1 in Warsaw could be ordered: monthly, Ubuntu 26.04,
   Automated Backup Standard as a promotion. Delivered with user `ubuntu`, password login on, root login with a key
   allowed, firewall inactive, no swap.
3. **First login** with the password from the OVHcloud delivery; host key fingerprint noted. The public key was added
   to `~/.ssh/authorized_keys` from PowerShell (Windows has no `ssh-copy-id`, §4.8) and key login tested. Lesson: the
   PowerShell command was first pasted into the SSH session and ran on the server (`Get-Content: command not found`,
   then an SSH prompt from the server to itself; answered `no`, nothing changed) - check the prompt before pasting.
4. **State check** (§4.14); the host key fingerprint seen from the server side matched the first connection.
5. **Updates.** `apt update` + `apt full-upgrade` (the image was from August); new kernel 7.0.0-42. `needrestart`
   reported "Pending kernel upgrade", so the server was rebooted; `sudo` (sudo-rs, an alternatives warning during the
   upgrade) checked before and after the reboot.
6. **SSH hardening.** Drop-in `00-hardening.conf` (§4.10), checked with `sshd -t` and `sshd -T`, reloaded, tested in
   a second window: key login works, password and root are refused.
7. **Firewall.** `ufw` defaults plus 22/tcp, enabled, tested: a new SSH session and `apt update` (outbound) work.
8. **Automatic updates.** `unattended-upgrades` was already on; automatic reboot at 03:30 UTC added (§4.12); dry run
   OK.
9. **Way back.** Encrypted private key file backed up in the password manager; KVM console login as `ubuntu` tested.
10. **Decisions** recorded in [`SAAS.md`](SAAS.md) §9: Warsaw, Ubuntu 26.04, user `ubuntu`, UTC, `ufw`, automatic
    reboot, addresses out of the repository, Ansible in stage 4, no fail2ban.

### Stage 2 - the probe and the risk test R1 (1-2 Nov 2026)

11. **Probe written** on `feature/saas-probe` (design and decisions: [`SAAS.md`](SAAS.md) §7, §9). The repository
    came as a zip built on Linux: Windows then listed dozens of files as modified. Only the line ends differed (LF
    from Linux, CRLF in the Windows copy; Git first compares file sizes); `git add -A` showed the 21 real changes.
    Later zips come with CRLF.
12. **Smoke test at home** (`dotnet run` on Windows): `exit 0`, all requests HTTP 200.
13. **CI:** both jobs green. Annotations: actions `@v4` run on the retired Node 20 → `actions/checkout@v7` and
    `actions/setup-dotnet@v6` (also in `release.yml`); `ubuntu-latest` moves to Ubuntu 26 → the Linux job pinned to
    `ubuntu-26.04` (the server's release). `.gitattributes`: `*.service` and `*.timer` always with LF - a CR from a
    Windows checkout would become part of a value in the unit.
14. **Deployment** (§4.15): `publish.ps1` → one upload folder → `scp -r` to `/tmp` → `sha256sum -c` OK, `file`
    shows no CRLF → `install` into `/opt`, `/etc/cinemawatch-probe` and `/etc/systemd/system` → `daemon-reload`,
    `systemd-analyze verify` OK.
15. **First run**, started by hand (`systemctl start`) at 15:44 UTC: 14 requests, all HTTP 200, no `cf-mitigated`,
    `cf-ray` ending in `-WAW` (Cloudflare Warsaw), both hosts (`tickets.` and `www.cinema-city.pl`); Polish time
    correct on the UTC server; 2.5 s, 75 MB. "Would notify" for 21 Nov (row 6: 14, 21) matched the desktop toast.
    Lesson: `sudo cat …/requests-*.csv` failed - the shell expands `*` as `ubuntu` before sudo runs (§2.3).
16. **Timer enabled** (`systemctl enable --now`): runs every 10-12 minutes; the first 3 runs (one by hand) made 38
    requests, all HTTP 200, no challenges. Lesson: deleting the folder the shell stands in (`rm -rf
    /tmp/cinemawatch-probe-upload` from inside it) makes later commands print `getcwd() failed` - harmless, `cd ~`.
17. **Provisional gate** (2 Nov): green. The probe keeps running during stage 3; the full check with at least
    5 days of data comes before the first deployment in stage 4 ([`SAAS.md`](SAAS.md) §9).

### Stage 4 - final R1 check (5 Nov 2026)

1. **Data copied** with §4.18 (CSV of requests, `would-notify.log`) plus the desktop's background log at home
   (`%LOCALAPPDATA%\CinemaWatch\Logs\cinemawatch.log`); nothing changed on the server, `/tmp` cleaned afterwards.
2. **Server (probe):** 2 Nov 15:44 - 5 Nov 19:21 UTC (75.6 h), 410 runs, gaps 10-12 minutes without a miss (apart
   from the switch from the run by hand to the timer on 2 Nov), 12-13 requests per run, median 61 requests an hour
   (at most 74). 4,997 requests: `tickets.cinema-city.pl` 4,920, `www.cinema-city.pl` 77 (repertoire); 4,995 HTTP 200
   and 2 HTTP 500 (2 Nov 19:34 and 3 Nov 10:56 UTC, both screening 1765592, both after about 5 s: 0.04%); no 403, no
   429, no `cf-mitigated`, no `Retry-After`, no network error; every answer `application/json`; every `cf-ray` ends in
   `-WAW`. Duration median 118 ms (details) and 66 ms (free seats), 95th percentile 544 / 352 ms, 25 answers over 2 s,
   the longest 13.4 s (the client's timeout is 30 s). "Would notify": only the first run (2 Nov, row 6: 14, 21).
3. **Home (control group):** only the Diuna profile ran in the background (every 5 minutes) and only while the PC was
   on: 2 Nov 16:44 - 22:59, 3 Nov 14:54 - 23:54 and 4 Nov 15:54 - 5 Nov 00:04 (Polish time), about 23 h of
   overlap. One HTTP 500 there too (3 Nov 22:24 UTC, screening of 18 Dec 16:30). No newly freed watched seats on either
   side during the overlap, so no notifications to compare.
4. **Result: green**, decided on 3.1 days instead of the planned 5 ([`SAAS.md`](SAAS.md) §9): no sign of a block, 5xx far
   under 1% and also seen from home. Limit as before: about 1,600 requests a day is a desktop user's load, not the SaaS
   load. The probe keeps running until the reinstall (4.3).

### Stage 4 - container image and accounts (4.1, 4.2; 6 Nov 2026)

1. **4.1 merged** (#8, `main` `ee72c6e`): CI green including the new `container` job (image built, started from
   `infra/compose/compose.yaml` with PostgreSQL, checked). Tag `web-v0.5.0-beta.1` published the image to GHCR as
   `0.5.0-beta.1` and `sha-75f16cb`; the package was made public once (package settings → visibility).
2. **E-mail provider: Resend** ([`SAAS.md`](SAAS.md) §9): account with a password from the password manager (no
   social login) and TOTP; domain `mail.damiankowalski.pl` added in region Ireland. Resend's page lists the DNS
   records read-only - they are copied into Cloudflare (*Add record*), not typed in Resend. "Auto configure" was not
   used: it would give Resend access to the whole zone.
3. **Mail DNS** in Cloudflare: DKIM TXT, two CNAMEs set to *DNS only* (Cloudflare proposes *Proxied* for CNAMEs),
   DMARC `p=none` on the apex; checked with §4.20; Resend: Verified.
4. **Delivery test** from the PC with a temporary sending key (`Send-MailMessage`, the key typed into a credential
   prompt, never saved): Gmail inbox, SPF / DKIM / DMARC pass; key deleted. Then DMARC tightened to `p=reject`.
5. **Zero Trust organization** `damiankowalski` on plan Free; Cloudflare requires a payment card even for the free plan.
6. **Tunnel and route.** First attempt: the route's subdomain got the tunnel's name (`cinemawatch-staging…`), so
   `staging-cinemawatch…` answered NXDOMAIN. Route deleted and added again with the right name. Lessons: a name that
   was just looked up as "does not exist" stays cached as such (by 1.1.1.1, Windows and Chrome) for up to the SOA's
   30 minutes - ask the zone's own server (`-Server faye.ns.cloudflare.com`) or bypass DNS with
   `curl.exe --resolve` (§4.21). Result: HTTP 530 from Cloudflare (tunnel known, no connector yet).
7. **One-time PIN** added as a login method; **two Access applications** (whole host; path `admin`); checked: HTTP 302
   to the Access login page (§4.21).
8. **Healthchecks.io**: account with 2FA, check `cinemawatch-staging` (5 / 10 minutes), e-mail alerts; the ping
   address stays unused until 4.4.

### Stage 4 - Ansible and the reinstall (4.3; 6 Nov 2026)

1. **Branch `feature/saas-ansible`.** The first commit landed on the local `main` (the `git switch -c` step was
   missed); the push then failed with `src refspec … does not match any`, so nothing reached GitHub. Fixed locally:
   `git switch -c feature/saas-ansible` at that commit, `git branch -f main origin/main`. Lesson: check
   `git branch --show-current` before committing.
2. **CI job `ansible` failed** with "Ansible CLI (2.21.5) and python module (2.20.1) versions do not match": the GitHub
   runner brings its own Ansible (pipx) earlier on `PATH`. The job now puts `/usr/bin` first and prints both versions.
3. **Reinstall** from the OVHcloud panel (Ubuntu 26.04 LTS, the public key pasted into the form; same IP addresses).
   The automated backup of the same evening was visible beforehand as the way back.
4. **First login** with the key; Ubuntu demanded a password change at once (OVHcloud's start password is expired):
   start password from the e-mail, new password from the password manager - now the KVM console password. New host
   key `SHA256:cf/CvkJV…`, the same seen from both sides; passwordless sudo works.
5. **Ansible** from Ubuntu's packages (`ansible`, `git`, `python3-debian`), clone of the branch, dry run: `ok=25
   changed=15 failed=0 skipped=10` (Docker's installation skipped, as expected). Real run, a reboot for the new kernel
   (7.0.0-28 → 7.0.0-43), second run `ok=30 changed=0 failed=0`.
6. **SSH tests** in a new window: key login works, password and root refused. A first attempt went to a wrong address
   (`Connection timed out`) - hence the `$vps` tip in §4.7.
7. **Checks** (§4.13 step 9): all as expected; `qemu-guest-agent` already active in OVHcloud's image; `ufw` routed now
   `deny` because Docker switched on forwarding.
8. **The probe is gone** with the old system; its data of 2-5 Nov is on the PC.

### Stage 4 - the first deployment (4.4; 6-7 Nov 2026)

1. **Branch `feature/saas-deploy`** with `infra/compose/deploy.sh`, CI green (with `shellcheck`); the server's clone
   switched to the branch.
2. **`.env`** (§4.23): a Resend key `staging-server` (sending only, `mail.` only), the tunnel token (the dashboard shows
   the install command cut off; copied whole with its copy button, cut down to the token with `sed` on the server),
   the ping address, a database password from `openssl rand -hex 32` (also in the password manager); `600 root:root`,
   every value set.
3. **Deployment of 0.5.1** at 22:38 UTC: three images pulled, containers started, `/healthz ok`; the tunnel connected
   at once (4 QUIC connections, Warsaw, all pre-checks pass); the heartbeat check went up; the owner's invitation
   arrived in the Gmail inbox from `cinemawatch@mail.damiankowalski.pl`.
4. **Error 1033** behind Access although the tunnel was Healthy: the `staging-cinemawatch` CNAME still pointed at the
   tunnel deleted on 6 Nov (wrong route name), not at `staging-cinemawatch`'s own id. Fixed by editing the record's
   target; check added to §4.21. Lesson: a deleted tunnel leaves its DNS record behind.
5. **Log entries checked:** two `Failed executing DbCommand` on `__EFMigrationsHistory` = EF Core asking an empty
   database for its migration history before creating it (first start only); `Error: libgssapi_krb5.so.2 …` = Npgsql
   probing Kerberos (GSS) encryption, absent from the chiseled image - silenced in 0.5.2 (`GSS Encryption Mode=Disable`
   in the compose file, checked by the container test).
6. **Tests:** password set through the invitation; test e-mail from the server in the inbox with SPF, DKIM
   (`mail.damiankowalski.pl`) and DMARC pass; all app cookies Secure (`X-Forwarded-Proto` from the tunnel works);
   `/admin` behind its own Access application, panel as expected; profile import and "Check now" from the server's IP
   (HTTP 200, notification e-mail); `docker stats`: about 280 MiB for the three containers.
7. **Heartbeat alarm:** `web` stopped, no e-mail - the check's e-mail integration was off (grey "@"). A screenshot of
   the check also showed its ping address, so a new check was created with e-mail on, its address put into `.env`,
   the same version deployed again (the first pre-deployment `pg_dump`), the old check deleted. Second test: "DOWN"
   e-mail after stopping `web`, "UP" after starting it.
8. **`cloudflared`**: the dashboard notes a newer release (2026.10.7); 2026.10.3 stays until 2026.10.7 is two weeks old
   (13 Nov 2026), then a compose update (§4.24).

### Stage 4 - backups off the server (4.5; 7 Nov 2026)

1. **Branch `feature/saas-backup`.** CI failed in `shellcheck`: `infra/backup/*.sh` did not exist in the commit.
   The Visual Studio part of `.gitignore` has `Backup*/`, and Git on Windows ignores case (`core.ignorecase`), so
   `git add -A` silently skipped `infra/backup/`. Fixed with `!infra/backup/` after that rule. Lesson:
   `git check-ignore -v <file>` tells which rule hides a file.
2. **Backblaze**: account in EU Central with two-step verification; bucket `cinemawatch-backup` (private, encrypted,
   lifecycle 30 + 1 days on `daily/`).
3. **Server**: the clone on the branch, `ansible-playbook site.yml --tags backup` (age, rclone, script, timer);
   `age-keygen` printed the key pair once - saved in the password manager, nothing on disk; a write-only B2 key;
   Healthchecks check `cinemawatch-backup` (cron, e-mail on); `/etc/cinemawatch/backup.env`, five values set.
4. **First backup** by hand: `saved … (56K)`, `uploaded …`, `done`; the timer's next run 02:33 UTC. The write-only key
   was enough for the upload (`--no-check-dest`). rclone's notice about a missing `rclone.conf` is gone since
   `--config ""` (config in memory only).
5. **Restore test from B2** with a temporary read-only key: downloaded, decrypted with the private key typed in,
   restored into `cinemawatch_restore_test`, 16 tables compared (only `check_log` higher live: 6 requests after the
   backup), same last migration, `PASS`; the scratch database dropped, the temporary key deleted.

---

### Stage 5 - testers and the Access policy (7 Nov 2026)

1. **Testers invited in the app** (`/admin/users`) before their e-mail addresses were in the Access policy
   `Owner and testers`. The invitation e-mails arrived, but the link led to the Access login page and no PIN came:
   Access sends a one-time PIN only to addresses its policy allows, and the page looks the same either way (it does not
   reveal who is allowed). The order in §4.22 (Access first, then the app) had been skipped.
2. **Fixed** by adding the testers' addresses to *Include → Emails* of `Owner and testers`. While editing, a second
   policy named `Owner and testers` appeared, not used by any application; deleted. Cloudflare keeps policies as
   separate, reusable objects and does not require unique names, so a duplicate is easy to miss on the application's
   page.
3. **Lessons:** edit the existing policy (the duplicate most likely came from adding a policy on the application's
   page instead of editing the existing one); afterwards check under
   *Access controls → Policies* that each name exists once and which application uses it; a tester who gets no PIN
   is first looked up in the policy. §4.22 now says so. Granting access in two places (policy and invitation) is the
   reason for the access-model question of stage 5.2 ([`SAAS.md`](SAAS.md) §10).
4. **Decisions** (7 Nov 2026, [`SAAS.md`](SAAS.md) §9): access model A (Access only on `/admin` after SaaS 0.6.0), no
   public account request form, the owner deletes accounts in `/admin`, `cloudflared` 2026.10.7 with 0.6.0 (on or
   after 13 Nov). The two-week test started the same day with the testers already invited; daily and weekly checks in
   §4.27.
5. **SaaS 0.6.0 built** (7 Nov 2026, branch `feature/saas-accounts`): the owner deletes accounts in `/admin/users`;
   public pages hardened for model A (password help queued and limited, the sign-in answer the same and padded,
   `/admin` only in lower case, `/healthz` only inside the server, `robots.txt` and `noindex`, refused forms a plain
   400); the privacy notice names Backblaze B2. No database migration. Beta on staging first; the stable tag on or
   after 13 Nov 2026 with `cloudflared` 2026.10.7; then §4.28.
6. **0.6.0-beta.1 deployed** (7-8 Nov 2026, §4.24) and checked by the owner in the browser: account deletion, password
   help, the sign-in message. **The switch to model A (§4.28) postponed** on 8 Nov 2026: Access keeps guarding the whole
   host, testers are still added as in §4.22. While preparing it: the rate limiting rules of the Free plan may only use
   the *URI Path* field (host, method and functions such as `lower()` need higher plans); §4.28 step 2 corrected.

### Stage 7b - the server's configuration moves to VPS-1 (11-12 Nov 2026)

The server is shared with a second application (Finanse), so its configuration and documentation moved out of this
repository into the private repository `damiankowalski/VPS-1` ([`SAAS.md`](SAAS.md) §9, 12 Nov 2026). The work log of
the move is in [VPS-1 SERVER.md] §3 (stage 7b).

1. **Move**: `infra/ansible` at commit `28f6347` → VPS-1 `ansible/`, the same behaviour; the domain, DNS, server and
   platform parts of this file → VPS-1 `docs/SERVER.md` with the same section numbers.
2. **Switch on the server** (11 Nov 2026): the playbook runs from the clone `~/VPS-1`; the second run `changed=0`.
   CinemaWatch's containers kept running. VPS-1's playbook still installs `infra/backup/backup.sh` from the clone
   `~/CinemaWatch`, so that file stays here.
3. **Clean-up here** (12 Nov 2026): `infra/ansible` and the CI job `ansible` removed; the moved sections of this file
   replaced by pointers. Nothing changed in `infra/compose`, `infra/backup` or on the server.

### Stage 6 - the wizard on staging (6.4.6; 13-14 Nov 2026)

1. **Sped up** (13 Nov 2026, [`SAAS.md`](SAAS.md) §9): no separate stable `web-v0.6.0`; `cloudflared` 2026.10.7 went
   into 0.7.0 (two weeks after its release); no tester days; the stage 5 test no longer gates anything. Stage 7 planned:
   the same site moves to `cinemawatch.damiankowalski.pl` (§4.30); Access stays in front of the whole site.
2. **Beta** (§4.29): commit `7a1d963` pushed, CI green - three jobs since the `ansible` job moved to VPS-1 (the runbook
   said four; corrected). Tag `web-v0.7.0-beta.1`, image built, `deploy.sh 0.7.0-beta.1` (migration `AddWizard` at
   start-up).
3. **Owner's check** (14 Nov 2026): the wizard on `staging-cinemawatch.damiankowalski.pl` works. Next: the stable
   `web-v0.7.0` (§4.29 step 6), then §4.30.

---

## 4. Runbooks

Runbooks 4.1-4.14 and 4.20 (domain, DNS, the server itself) moved to [VPS-1 SERVER.md] §4. The runbooks below mark
every block as **Windows (PowerShell)** or **server (bash, over SSH)**. `<server-ip>` = the server's IPv4 from the password manager
or the OVHcloud panel - never write it into the repository. Before a change to SSH or the firewall, keep one SSH
session open and test in a second window.

### 4.1 Check the nameservers

Moved to [VPS-1 SERVER.md] §4.1 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 4.2 Check DNSSEC

Moved to [VPS-1 SERVER.md] §4.2 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 4.3 Disable DNSSEC safely (also before changing the DNS provider)

Moved to [VPS-1 SERVER.md] §4.3 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 4.4 Move the nameservers back to OVHcloud (undo of stage 1 step 5)

Moved to [VPS-1 SERVER.md] §4.4 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 4.5 Add a DNS record (later stages)

Moved to [VPS-1 SERVER.md] §4.5 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 4.6 Domain renewal

Moved to [VPS-1 SERVER.md] §4.6 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 4.7 Connect to the server

Moved to [VPS-1 SERVER.md] §4.7 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 4.8 Add, replace or remove an SSH key

Moved to [VPS-1 SERVER.md] §4.8 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 4.9 Emergency access: KVM console

Moved to [VPS-1 SERVER.md] §4.9 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 4.10 SSH: key-only login, no root (apply, check, undo)

Moved to [VPS-1 SERVER.md] §4.10 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 4.11 Firewall (`ufw`)

Moved to [VPS-1 SERVER.md] §4.11 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 4.12 Updates (manual and automatic)

Moved to [VPS-1 SERVER.md] §4.12 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 4.13 Rebuild the server from scratch (Ansible)

Moved to [VPS-1 SERVER.md] §4.13 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 4.14 Check the server state (read only)

Moved to [VPS-1 SERVER.md] §4.14 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 4.15 Deploy or update the probe

Windows (PowerShell), in the repository folder - build, then gather everything to upload in one folder (the
profiles come from `C:\DEV\CINEMAWATCH\probe\profiles`, the same files as the desktop control group):

```powershell
cd C:\DEV\CINEMAWATCH\repo\CinemaWatch
.\tools\CinemaWatch.Probe\publish.ps1
$up = 'C:\DEV\CINEMAWATCH\probe\upload'
Remove-Item $up -Recurse -ErrorAction SilentlyContinue
New-Item -ItemType Directory "$up\profiles" -Force | Out-Null
Copy-Item .\artifacts\probe\CinemaWatch.Probe, .\artifacts\probe\CinemaWatch.Probe.sha256, .\tools\CinemaWatch.Probe\deploy\cinemawatch-probe.service, .\tools\CinemaWatch.Probe\deploy\cinemawatch-probe.timer $up
Copy-Item C:\DEV\CINEMAWATCH\probe\profiles\*.cinemawatch.json "$up\profiles"
scp -r $up ubuntu@<server-ip>:/tmp/cinemawatch-probe-upload
```

Expected: `publish.ps1` ends with the file size (about 35 MB) and `SHA-256: …`; `scp` asks for the key's passphrase
and prints one `100%` line per file. If `/tmp/cinemawatch-probe-upload` already exists on the server, delete it
first, otherwise `scp` nests an `upload` folder inside it. If PowerShell refuses to run scripts:
`powershell -ExecutionPolicy Bypass -File .\tools\CinemaWatch.Probe\publish.ps1`.

Server (bash, over SSH) - check the copy, install, check the units:

```bash
cd /tmp/cinemawatch-probe-upload
sha256sum -c CinemaWatch.Probe.sha256
file cinemawatch-probe.service cinemawatch-probe.timer
sudo install -d -m 0755 /opt/cinemawatch-probe /etc/cinemawatch-probe
sudo install -m 0755 CinemaWatch.Probe /opt/cinemawatch-probe/
sudo install -m 0644 profiles/*.cinemawatch.json /etc/cinemawatch-probe/
sudo install -m 0644 cinemawatch-probe.service cinemawatch-probe.timer /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemd-analyze verify /etc/systemd/system/cinemawatch-probe.service /etc/systemd/system/cinemawatch-probe.timer && echo "units OK"
/opt/cinemawatch-probe/CinemaWatch.Probe --help | head -1
cd ~ && rm -rf /tmp/cinemawatch-probe-upload
```

Expected: `CinemaWatch.Probe: OK`; both units `ASCII text` without `with CRLF line terminators` (otherwise stop);
`units OK`; the version line. An update of the program alone is picked up by the next run - no restart needed.
First deployment only: one run by hand, then the timer:

```bash
sudo systemctl start cinemawatch-probe.service; echo "exit=$?"
sudo journalctl -u cinemawatch-probe.service --no-pager -n 30
sudo systemctl enable --now cinemawatch-probe.timer
systemctl list-timers cinemawatch-probe.timer --no-pager
```

Expected: `exit=0`; the journal ends with `Done …: N request(s) (200 xN) … exit 0` and `Finished`; the timer shows
`NEXT` about a minute ahead. Exit codes: 1 configuration, 2 every screening failed, 3 blocked (do not retry; the
response is in `blocked/`), 4 nothing to check (all screenings over).

### 4.16 Check the probe (daily)

Server (bash, over SSH):

```bash
systemctl list-timers cinemawatch-probe.timer --no-pager
sudo journalctl -u cinemawatch-probe.service --since today --no-pager | grep -cE "Done in"
sudo journalctl -u cinemawatch-probe.service --since today --no-pager | grep -E "BLOCKED|WOULD NOTIFY|ERROR|NOTHING TO CHECK|, exit [1-4]$"
sudo sh -c 'cat /var/lib/cinemawatch-probe/requests-*.csv' | awk -F, -v d="$(date -u +%F)" '$1 ~ "^"d {n++; s[$7==""?"error":$7]++; if ($11!="") c++} END {printf "Requests on %s (UTC): %d;", d, n; for (k in s) printf " %s x%d", k, s[k]; printf "; Cloudflare challenges: %d\n", c+0}'
```

Expected: fresh `LAST` / `NEXT`; about 5-6 runs per hour since midnight UTC (about 130 a day); only `WOULD NOTIFY`
lines (compare them with the desktop toasts); `200 xN` and `Cloudflare challenges: 0`. `BLOCKED`, `ERROR`,
`exit 1-4`, any status other than 200 or a challenge count above 0 need a look. "Today" is the UTC day.

### 4.17 Change the watched profiles

Keep the server's profiles identical to the desktop control group. Upload the changed profiles like in §4.15
(Windows), then on the server (bash, over SSH):

```bash
ls /etc/cinemawatch-probe/
sudo rm "/etc/cinemawatch-probe/<old profile>.cinemawatch.json"
sudo install -m 0644 /tmp/cinemawatch-probe-upload/profiles/*.cinemawatch.json /etc/cinemawatch-probe/
ls /etc/cinemawatch-probe/
```

The next run reads the new set. The state of a removed profile stays in `state/` (harmless).

### 4.18 Copy the probe data to the PC (for the gate)

Server (bash, over SSH) - copy the files to `/tmp` and hand them to `ubuntu`:

```bash
sudo sh -c 'cp /var/lib/cinemawatch-probe/requests-*.csv /var/lib/cinemawatch-probe/would-notify.log /tmp/ && chown ubuntu: /tmp/requests-*.csv /tmp/would-notify.log'
```

Windows (PowerShell) - download and summarise:

```powershell
New-Item -ItemType Directory C:\DEV\CINEMAWATCH\probe\gate -Force | Out-Null
scp "ubuntu@<server-ip>:/tmp/requests-*.csv" "ubuntu@<server-ip>:/tmp/would-notify.log" C:\DEV\CINEMAWATCH\probe\gate\
$rows = Get-ChildItem C:\DEV\CINEMAWATCH\probe\gate\requests-*.csv | ForEach-Object { Import-Csv $_ }
$rows | Group-Object status | Select-Object Name, Count
$rows | Where-Object { $_.cf_mitigated -or $_.status -ne '200' } | Format-Table time_utc, endpoint, status, cf_mitigated, error
```

Expected: one group `200` with all requests, nothing in the second table. Afterwards on the server:
`rm /tmp/requests-*.csv /tmp/would-notify.log`.

### 4.19 Stop or remove the probe

Server (bash, over SSH). Stop the runs and keep the data:

```bash
sudo systemctl disable --now cinemawatch-probe.timer
```

Remove everything (the server returns to the state after the hardening):

```bash
sudo systemctl disable --now cinemawatch-probe.timer
sudo rm /etc/systemd/system/cinemawatch-probe.service /etc/systemd/system/cinemawatch-probe.timer
sudo systemctl daemon-reload
sudo rm -rf /opt/cinemawatch-probe /etc/cinemawatch-probe /var/lib/private/cinemawatch-probe /var/lib/cinemawatch-probe
```

---

### 4.20 Check the e-mail DNS records

Moved to [VPS-1 SERVER.md] §4.20 (the shared server's documentation, stage 7b, 12 Nov 2026).

### 4.21 Check the tunnel and Access from outside

Windows (PowerShell). `--resolve` sends the request to a Cloudflare address directly, so a stale DNS cache on the PC
does not matter (use an address from `Resolve-DnsName staging-cinemawatch.damiankowalski.pl -Server 1.1.1.1`):

```powershell
curl.exe -sI https://staging-cinemawatch.damiankowalski.pl --resolve staging-cinemawatch.damiankowalski.pl:443:188.114.96.11
foreach ($p in '/', '/admin') {
  $loc = curl.exe -sI "https://staging-cinemawatch.damiankowalski.pl$p" --resolve staging-cinemawatch.damiankowalski.pl:443:188.114.96.11 | Select-String '^Location:'
  if ("$loc" -match 'kid=([0-9a-f]{12})') { "$p -> app $($Matches[1])" } else { "$p -> no Access redirect" }
}
```

After any change to the tunnel or its route, compare in Cloudflare the **Tunnel ID** (*Networking → Tunnels → the
tunnel → Tunnel details*) with the target of the `staging-cinemawatch` record (*DNS → Records*:
`<tunnel id>.cfargotunnel.com`). Different ids give error 1033 although the tunnel is Healthy (6 Nov 2026).

Expected: `HTTP/1.1 302` with `Location: https://damiankowalski.cloudflareaccess.com/cdn-cgi/access/login/…`; two
different application ids for `/` and `/admin` (the same id means the admin application lost its path). Anything
other than 302 without a sign-in means Access does not protect the host: stop and check *Access controls →
Applications*. After signing in (browser): until 4.4 Cloudflare's error 1033 (HTTP 530: tunnel without a connector),
afterwards the app's sign-in page. With model A (after §4.28) `/` answers the app itself: see the checks there.

From SaaS 0.6.0 on, also the paths that must never reach the owner's pages or the health check (same `--resolve`;
a GET that prints only the headers, because the app answers `HEAD` for these paths differently):

```powershell
foreach ($p in '/Admin', '/ADMIN', '/%61dmin', '/healthz', '/robots.txt') {
  $head = curl.exe -s -D - -o NUL "https://staging-cinemawatch.damiankowalski.pl$p" --resolve staging-cinemawatch.damiankowalski.pl:443:188.114.96.11
  "$p -> $(($head | Select-Object -First 1).Trim())  $(($head | Select-String '^Location:') -replace '\?.*','')"
}
```

Expected while the whole host is behind Access: every line `302` to the Access login. With model A: `/Admin`,
`/ADMIN`, `/%61dmin` either `302` to the Access login (the admin application caught it) or `404` (the app refused it),
`/healthz` `404`, `/robots.txt` `200`. A `200` for any spelling of `/admin` or for `/healthz`: stop, put the Access
application of the whole host back (§4.28, way back) and report it.

### 4.22 Add a tester

Order matters: Access first, then the app. Valid while the whole host is behind Access (the access model may change
in stage 5.2, [`SAAS.md`](SAAS.md) §10).

1. Cloudflare → Zero Trust → *Access controls → Policies* → the **existing** policy `Owner and testers` → add the
   tester's e-mail to *Include → Emails* → save. Do not add a new policy on the application's page: that most likely
   created the duplicate of 7 Nov 2026 (§3). Never add testers to `Owner only`.
2. Check: *Access controls → Policies* lists `Owner and testers` once, used by `CinemaWatch staging`, with the new
   address in it.
3. The app → `/admin/users` → invite the same address (the invitation e-mail goes through Resend).
4. The tester opens the link: Access asks for their e-mail and sends a PIN (from `noreply@notify.cloudflare.com`, valid
   10 minutes; check spam), then the app's set-password page opens. **No PIN** = the address is not in the policy
   (Access shows the same page either way): go back to step 1 and compare the spelling.
5. Remove a tester in the reverse order: delete the account in the app (from SaaS 0.6.0: `/admin/users` → *Usuń
   konto* → *Tak, usuń konto*; the person gets an e-mail; before 0.6.0 the tester deletes it on their account page),
   then the e-mail from the policy.

With model A (after §4.28) only steps 3-4 remain: invite in the app, the person sets a password; removing = deleting
the account in `/admin/users`. Access then guards only `/admin`, for the owner.

---

### 4.23 Create the `.env` (first deployment)

The server keeps every secret of the app in one file, `/opt/cinemawatch/.env` (root only, mode 600). Secrets are
copied from the panels straight into the editor on the server - never into the chat, a file on the PC or the
repository. Each one can be shown again or replaced in its panel, except the database password (keep it in the
password manager).

1. Resend → **API Keys → Create API key**: name `staging-server`, permission **Sending access**, domain
   `mail.damiankowalski.pl`. The key is shown once; copy it in step 5.
2. Cloudflare → Zero Trust → **Networking → Tunnels** → `staging-cinemawatch`: the install command (tab *Docker*) is cut
   off on the screen, but its copy button copies all of it, token included. In step 5 paste the whole command after
   `TUNNEL_TOKEN=`, then cut it down to the token on the server (step 6). Run none of the commands; afterwards copy
   something harmless and remove the entry from the Windows clipboard history (Win+V) if it is on.
3. Healthchecks.io → check `cinemawatch-staging` → its ping URL (`https://hc-ping.com/…`), copied in step 5. Make sure
   the check's e-mail integration is on (the "@" in the Integrations column dark), or a down server stays silent.
4. Server (bash, over SSH) - the template and a database password:

```bash
sudo install -m 600 -o root -g root ~/CinemaWatch/infra/compose/env.example /opt/cinemawatch/.env
openssl rand -hex 32
```

   Save the printed 64 characters in the password manager ("CinemaWatch staging database").
5. Server - fill in the file: `sudo nano /opt/cinemawatch/.env`. Paste (right click in the Windows terminal) after the
   `=` of: `POSTGRES_PASSWORD` (step 4), `SAAS_OWNER_EMAIL` (the owner's Gmail, the same as in Access),
   `SAAS_HEARTBEAT_URL` (step 3), `MAIL_PASSWORD` (step 1), `TUNNEL_TOKEN` (step 2). No spaces, no quotes.
   `CINEMAWATCH_VERSION` is set by `deploy.sh`. Save: `Ctrl+O`, `Enter`; exit: `Ctrl+X`.
6. Server - cut the pasted install command down to the tunnel token, then check without showing a secret:

```bash
sudo sed -i 's/^TUNNEL_TOKEN=.*--token[[:space:]]*/TUNNEL_TOKEN=/' /opt/cinemawatch/.env
sudo grep -c '^TUNNEL_TOKEN=eyJ[A-Za-z0-9+/=_-]*$' /opt/cinemawatch/.env
sudo stat -c '%a %U:%G' /opt/cinemawatch/.env
sudo awk -F= '/^[A-Z_]+=/ { print $1, ($2 == "" ? "EMPTY" : "set") }' /opt/cinemawatch/.env
```

   Expected: `1` (one well-formed token line); `600 root:root`; every line `set` (none `EMPTY`).

Changing a value later: edit the file the same way, then `sudo bash ~/CinemaWatch/infra/compose/deploy.sh <running
version>` (the containers read `.env` only when they are created). A leaked secret is replaced in its panel first.

### 4.24 Deploy, roll back, look around

Server (bash, over SSH). Deploy a version that has its tag `web-vX.Y.Z` on GitHub (and its image in GHCR):

```bash
cd ~/CinemaWatch && git pull
sudo bash ~/CinemaWatch/infra/compose/deploy.sh 0.5.1
```

What it does (`infra/compose/deploy.sh`): checks `.env`, the version and the tag; `pg_dump` of the running database
to `/opt/cinemawatch/backups` (the newest 10 stay); copies `infra/compose/compose.yaml` **of the tag** to
`/opt/cinemawatch/compose.yaml`; writes the version into `.env`; `docker compose pull`, `up -d`; waits up to 2 minutes
for `/healthz` = `ok` and checks that `postgres`, `web` and `cloudflared` run. Expected end: `== Done: 0.5.1` and the
three containers. On an error it prints the log of the failing container and stops; nothing is rolled back by
itself. Git runs as the clone's owner (`runuser`), so the clone never gets root-owned files.

Roll back: the same command with the previous version (the script prints it). If the new version's database
migration is the problem, restore the dump taken before it first:

```bash
cw() { sudo docker compose -f /opt/cinemawatch/compose.yaml "$@"; }
sudo ls -lt /opt/cinemawatch/backups
cw stop web
sudo cat /opt/cinemawatch/backups/<file>.dump | cw exec -T postgres pg_restore -U cinemawatch -d cinemawatch --clean --if-exists
sudo bash ~/CinemaWatch/infra/compose/deploy.sh <previous version>
```

Look around (the `cw` helper above saves typing; `/opt/cinemawatch` itself is root only):

```bash
cw ps
cw logs --tail 100 web
cw logs -f cloudflared
sudo docker stats --no-stream
```

### 4.25 Backups off the server (setup)

Once; afterwards the timer runs it every day. Design ([`SAAS.md`](SAAS.md) §9): `pg_dump` → encrypted with `age` for a
public key (the server can encrypt, not decrypt) → Backblaze B2 in EU Central with a **write-only** key (the server can
add backups, never delete them; the bucket's lifecycle rule removes them after 30 days) → the newest 7 encrypted
copies also stay in `/opt/cinemawatch/backups/daily` → Healthchecks.io check `cinemawatch-backup`. `.env` is not backed
up (secrets; recreate it with §4.23).

1. Backblaze (browser) - an account with the region **EU Central** (chosen at sign-up, cannot be changed later), the
   owner's existing e-mail, a password from the password manager, two-step verification (authenticator app).
2. Backblaze - **Buckets → Create a Bucket**: a globally unique name such as `cinemawatch-backup-<6 random letters>`,
   files **Private**, default encryption on, Object Lock off. The bucket's endpoint must read
   `s3.eu-central-003.backblazeb2.com` (or another `eu-central` one).
3. Backblaze - the bucket → **Lifecycle Settings → custom rules**: file path `daily/`, hide after **30** days, delete
   after **1** day.
4. Server (bash, over SSH) - VPS-1's playbook installs `age`, `rclone`, the script (from this clone) and the timer
   ([VPS-1 SERVER.md] §4.13):

```bash
cd ~/CinemaWatch && git pull
cd ~/VPS-1 && git pull
cd ~/VPS-1/ansible && ansible-playbook site.yml --tags backup
```

5. Server - the age key pair. `age-keygen` without `-o` only prints it; nothing is written to disk:

```bash
age-keygen
```

   Save all three lines (`# created`, `# public key: age1…`, `AGE-SECRET-KEY-1…`) in the password manager as
   "CinemaWatch backup age key", then `clear`. Without the private key no backup can ever be read.
6. Backblaze - **Application Keys → Add a New Application Key**: name `staging-server-backup`, bucket = the backup
   bucket, type of access **Write Only**. keyID and applicationKey are shown once: paste them in step 8.
7. Healthchecks.io - a check `cinemawatch-backup`: schedule **Cron** `30 2 * * *`, time zone UTC, grace **2 hours**;
   the e-mail integration on (dark "@"). Its ping address goes into step 8, never into a screenshot.
8. Server - the config:

```bash
sudo install -m 600 -o root -g root ~/CinemaWatch/infra/backup/backup.env.example /etc/cinemawatch/backup.env
sudo nano /etc/cinemawatch/backup.env
sudo awk -F= '/^[A-Z0-9_]+=/ { print $1, ($2 == "" ? "EMPTY" : "set") }' /etc/cinemawatch/backup.env
```

   Fill in `B2_KEY_ID`, `B2_KEY` (step 6), `B2_BUCKET` (step 2), `AGE_RECIPIENT` (the `age1…` public key of step 5),
   `HEARTBEAT_URL` (step 7). Expected: five lines `set`.
9. Server - the first backup by hand, then the timer:

```bash
sudo systemctl start cinemawatch-backup.service
sudo journalctl -u cinemawatch-backup.service -n 20 --no-pager
systemctl list-timers cinemawatch-backup.timer --no-pager
```

   Expected: `saved cinemawatch-….dump.age`, `uploaded to b2:…/daily/…`, `done`; the timer's NEXT at 02:30 UTC (plus up
   to 5 minutes). Also: the file in the bucket (Backblaze → the bucket → Browse files → `daily/`); the Healthchecks
   check up. An `ERROR` line names the reason; the check gets it as a failure message too.

Daily check: nothing to do - a missing or failed backup e-mails the owner through Healthchecks.

### 4.26 Restore test, and restoring for real

**Restore test** - after the setup, then every 3 months and after a change of the database schema:

1. Backblaze - a temporary key `restore-test`: the backup bucket, access **Read Only**.
2. Server (bash, over SSH):

```bash
sudo bash ~/CinemaWatch/infra/backup/restore-test.sh --b2
```

   It asks for the temporary keyID and applicationKey and for the age private key (password manager; nothing is
   shown or stored), downloads the newest backup from B2, restores it into the scratch database
   `cinemawatch_restore_test`, prints the rows of every table next to the live database and drops the scratch
   database. Expected: the table list and `PASS`. Without `--b2` it uses the newest local copy.
3. Backblaze - delete the key `restore-test`.

**Restoring for real** (the database is lost or broken; everything written after the backup is lost):

1. Server gone: rebuild it (§4.13), recreate `.env` (§4.23; the database password from the password manager) and
   deploy the version that matches the backup (`deploy.sh`, §4.24) - it starts with an empty database.
2. Bring the backup to the server: the newest local copy in `/opt/cinemawatch/backups/daily`, or download it from
   B2 (web UI, then `scp` to `/tmp`).
3. Server - stop the app, load the backup over the database, start the app:

```bash
cw() { sudo docker compose -f /opt/cinemawatch/compose.yaml "$@"; }
cw stop web
read -rsp "age private key: " k; echo
sudo cat <backup file> | age --decrypt --identity <(printf '%s\n' "$k") \
  | cw exec -T postgres pg_restore -U cinemawatch -d cinemawatch --clean --if-exists --no-owner
unset k
cw start web
```

   Then sign in and check the profiles; the heartbeat check goes up again.

### 4.27 Stage 5: daily and weekly check

While the test runs (from 7 Nov 2026, about two weeks). Write down anything unusual with the date, the tester and
the mail provider; the notes become the lessons of stage 5.7 ([`SAAS.md`](SAAS.md) §7).

**Daily, about 5 minutes (browser):**

1. `/admin`: the poller runs, is not paused and has no block in force; requests in the last 24 hours within the budget
   (120 per hour); answers HTTP 200 (a rare 5xx is Cinema City's, as in R1); no new warning among the events.
2. Healthchecks.io: `cinemawatch-staging` and `cinemawatch-backup` both up; no "DOWN" or failure e-mail in the inbox.
3. Testers' reports: notification e-mails in the inbox or in spam (with the mail provider: Gmail, Outlook, WP, Onet,
   Interia, o2…), missing or late notifications, errors on the site.
4. Latency: for a screening watched both by the desktop app at home and by the SaaS (the same profile imported),
   note the time of the desktop notification and of the e-mail. Expected: within one interval (about 12 minutes).

**Weekly, about 10 minutes - server (bash, over SSH):**

```bash
cw() { sudo docker compose -f /opt/cinemawatch/compose.yaml "$@"; }
cw exec -T postgres psql -U cinemawatch -d cinemawatch -c "
select date_trunc('day', at)::date as day, count(*) as requests,
       count(*) filter (where status = 200) as ok,
       count(*) filter (where status in (403, 429) or cf_mitigated is not null) as blocked,
       count(*) filter (where content_type is not null and content_type not like 'application/json%') as not_json,
       count(*) filter (where status is null) as no_answer,
       max(duration_ms) as slowest_ms
from check_log where at > now() - interval '7 days' group by 1 order by 1;"
cw exec -T postgres psql -U cinemawatch -d cinemawatch -c "
select max(n) as busiest_hour from (select count(*) as n from check_log
where at > now() - interval '7 days' group by date_trunc('hour', at)) h;"
```

Expected: `blocked` and `not_json` 0 every day (anything else: stop and look at `/admin` and §4.24 logs);
`busiest_hour` at most 120. Then:

```bash
cw logs --since 168h web 2>&1 | grep -ciE 'fail|error|exception'
sudo docker stats --no-stream
df -h /
```

Expected: few error lines (read them: `cw logs --since 168h web 2>&1 | grep -iE 'fail|error|exception' | tail -20`),
about 300 MiB for the three containers, the disk far from full. Nothing here changes anything on the server.

### 4.28 Switch to access model A (stage 5.2) - not done, kept for later

**Postponed on 8 Nov 2026** ([`SAAS.md`](SAAS.md) §9, §11): nothing below has been done; Access guards the whole host.
Access stops guarding the whole host and keeps guarding `/admin` (owner only); the app's own sign-in guards the rest
([`SAAS.md`](SAAS.md) §9). **Only after** SaaS 0.6.0 (stable) is deployed (§4.24) and its pages work on staging. Each
step says how to check it; the way back is at the end. Nothing here touches the server.

1. **Before - Windows (PowerShell):** run §4.21 and note the application id (`kid=…`) for `/admin`. Expected: two
   different ids for `/` and `/admin`.
2. **Rate limiting rule - Cloudflare dashboard:** the domain `damiankowalski.pl` → *Security → Security rules* (older
   layout: *WAF → Rate limiting rules*) → *Create rule* → *Rate limiting rule*. Name `CinemaWatch account forms`. The
   Free plan allows only the *URI Path* field (no host, no method, no functions): condition *URI Path* *starts with*
   `/Account/` **or** *URI Path* *starts with* `/account/`. Counted per IP (the only choice on Free): **10** requests
   per **10 seconds** (all methods, so room for normal clicking); action *Block*; duration 10 seconds (fixed on Free).
   Deploy. The rule covers the whole zone, but only CinemaWatch has `/Account/` paths; other spellings of the path
   escape it, and the app's own limits stay the real protection. Undo: switch the rule off or delete it.
3. **Remove the host-wide Access application:** Zero Trust → *Access controls → Applications* → `CinemaWatch staging`
   (the whole host, **not** `CinemaWatch staging admin`) → *Delete*. Keep the policy `Owner and testers` for a week
   (the way back needs it), then delete it under *Access controls → Policies*: the testers' addresses then leave
   Cloudflare.
4. **Check - Windows (PowerShell):** run §4.21 again (both blocks). Expected: `/` → `302` with `Location:` to
   `/Account/Login?ReturnUrl=%2F` (the app, not Access); `/admin` → `302` to the Access login with the id noted in step
   1; the extra paths as described there. Then the rate limit (the posts carry no form token, so the app answers 400
   and no account is touched):

   ```powershell
   1..12 | ForEach-Object { curl.exe -s -o NUL -w "%{http_code} " -d "x=1" "https://staging-cinemawatch.damiankowalski.pl/Account/Login" --resolve staging-cinemawatch.damiankowalski.pl:443:188.114.96.11 }
   ```

   Expected: a few `400`, then `429` (blocked for 10 seconds). Only `400`: the rule does not match - check its
   expression. Finally sign in as the owner in a browser: the app's sign-in, then `/admin` asks for the Access PIN.
5. **Testers:** tell them the PIN step is gone; they sign in with the app's password only. Optionally free their Access
   seats: Zero Trust → *Users* → select them → *Remove users*.
6. **Docs:** §1.6 (Access applications and policies), §2.4 (one gate for the site, two for `/admin`), §3 work log;
   [`SAAS.md`](SAAS.md) §7 5.2 done.

**Way back** (anything unexpected in step 4, e.g. a `200` for a spelling of `/admin`): Zero Trust → *Access controls →
Applications* → *Add an application* → *Self-hosted* → name `CinemaWatch staging`, domain
`staging-cinemawatch.damiankowalski.pl` with no path, session 1 week, policy: the existing `Owner and testers` → save;
§4.21 shows `302` to Access for `/` again within a minute. The rate limiting rule can stay.

### 4.29 SaaS 0.7.0-beta.1: the wizard on staging (stage 6.4.6)

Sped up on 13 Nov 2026 ([`SAAS.md`](SAAS.md) §9): no separate `web-v0.6.0` before it (`main` is already merged into
the branch, and `cloudflared` 2026.10.7 is in `compose.yaml` on the branch); the owner checks the beta on the day it
is deployed and the stable `web-v0.7.0` follows when nothing blocks; no tester days. The project files already say
`0.7.0`; the tag adds the beta suffix.

1. **Windows (PowerShell)**, in the repository: push the branch (its last commit holds the `cloudflared` bump and
   these documents), wait for green CI.

   ```powershell
   git switch feature/saas-wizard
   git status
   git push
   ```

   Check: `git status` says the branch is ahead of `origin` by one commit before the push; then GitHub *Actions*, the
   run for the branch's last commit - all three jobs green (`build`, `linux`, `container`; the `ansible` job went to
   VPS-1 on 12 Nov 2026).
2. **Windows (PowerShell)**: the beta tag on the branch's last commit; `web-image.yml` builds, tests and publishes
   `ghcr.io/damiankowalski/cinemawatch-web:0.7.0-beta.1`.

   ```powershell
   git tag web-v0.7.0-beta.1
   git push origin web-v0.7.0-beta.1
   ```

   Check: *Actions → Web image* green; the package lists `0.7.0-beta.1`. A tag pushed by mistake:
   `git push origin :refs/tags/web-v0.7.0-beta.1` and `git tag -d web-v0.7.0-beta.1` (the image stays unused).
3. **Server (bash, SSH)**: deploy (the dump comes first; the migration `AddWizard` runs at start-up).

   ```bash
   cd ~/CinemaWatch && git fetch --tags && git pull
   sudo bash ~/CinemaWatch/infra/compose/deploy.sh 0.7.0-beta.1
   ```

   Expected end: `== Done: 0.7.0-beta.1` and three containers. Way back: §4.24 with `0.6.0-beta.1` - restore the
   dump taken by this deployment first (the migration added columns and settings that 0.6.0 does not know). Then in
   the dashboard *Tunnels → staging-cinemawatch*: the connector reports version 2026.10.7 ([`SAAS.md`](SAAS.md) 5.5).
4. **Wizard budget down for the start:** `/admin` → settings → *budżet kreatora (na godzinę)* `120 → 60` → save. The
   panel records the change (events). Raise it again when the request log is calm.
5. **Owner's check (same day):** *Nowy profil* with one city and one day, two screenings, the hall map, step 4,
   *Zapisz profil*; *Edytuj* on the list, change a seat, *Zapisz zmiany*. In `/admin`: the wizard's requests in their
   own line (`(kreator)`), no answer other than HTTP 200, the hour within both budgets. These are real requests to
   Cinema City, made by hand by the owner - nothing automated.
6. **Stable** (when nothing in step 5 blocks; smaller errors are fixed later on the live site): pull request
   `feature/saas-wizard` → `main`, *Squash and merge*, tag `web-v0.7.0` on `main`, §4.24 with `0.7.0`; delete the
   branch. The testers may be told that profiles can now be made on the site (the e-mail is the owner's). Docs:
   [`SAAS.md`](SAAS.md) §7, §9, §10; here §1 (state), §3 (work log). Next: §4.30.

### 4.30 Move the site to `cinemawatch.damiankowalski.pl` (stage 7)

The same instance gets the final address ([`SAAS.md`](SAAS.md) §7 stage 7, §9 13 Nov 2026): nothing new on the
server, no new tunnel, database or backup. **Order matters:** Access covers the new host *before* the tunnel serves
it, so the site is never reachable without the PIN. Do it after the stable `web-v0.7.0` (§4.29).

1. **Cloudflare (browser), Access:** Zero Trust → *Access controls → Applications* → `CinemaWatch staging` (the whole
   host) → *Edit* → add a second public hostname `cinemawatch.damiankowalski.pl` with no path → save. The same in the
   application for `/admin`: a second hostname `cinemawatch.damiankowalski.pl`, path `admin` → save. The policies stay
   as they are. Optionally rename the applications (e.g. `CinemaWatch`, `CinemaWatch admin`); the name is a label only.
2. **Cloudflare (browser), tunnel:** Zero Trust → *Networks → Tunnels* → `staging-cinemawatch` → *Published
   application routes* → *Add*: subdomain `cinemawatch`, domain `damiankowalski.pl`, service `HTTP` → `web:8080` →
   save. Cloudflare creates the DNS record itself. Check: DNS → *Records*: `CNAME cinemawatch` →
   `<tunnel id>.cfargotunnel.com`, Proxied, the same tunnel id as `staging-cinemawatch` (lesson of error 1033, §1.6).
   The tunnel's name stays (renaming is cosmetic and not needed).
3. **Windows (PowerShell), check from outside** (as §4.21, with the new host): both must redirect to the Access login.

   ```powershell
   curl.exe -sI https://cinemawatch.damiankowalski.pl/ | Select-String '^HTTP|^Location:'
   curl.exe -sI https://cinemawatch.damiankowalski.pl/admin | Select-String '^HTTP|^Location:'
   ```

   Expected: `302` and a `Location:` at `damiankowalski.cloudflareaccess.com`. A `200` or the app's own page: stop
   and delete the route from step 2 at once (the way back below), then check step 1.
4. **Server (bash, SSH):** the app's public address (links in e-mails, Secure cookies) in `.env`, then a deployment
   of the running version so `web` restarts with it (a dump comes first, as always).

   ```bash
   sudo cp /opt/cinemawatch/.env /opt/cinemawatch/.env.before-stage7
   sudo sed -i 's|^SAAS_BASE_URL=.*|SAAS_BASE_URL=https://cinemawatch.damiankowalski.pl|' /opt/cinemawatch/.env
   sudo grep '^SAAS_BASE_URL=' /opt/cinemawatch/.env
   sudo bash ~/CinemaWatch/infra/compose/deploy.sh 0.7.0
   ```

   Expected: the `grep` prints the new address; `== Done: 0.7.0` and three containers.
5. **Owner's check (browser):** open `https://cinemawatch.damiankowalski.pl`, the PIN, the app's sign-in (everybody
   signs in again once: the cookies belonged to the old host name), the profile list, *Sprawdź teraz* on one profile,
   `/admin`. Send yourself an invitation or the password help e-mail: its link starts with the new address.
6. **Testers:** tell them the new address (the e-mail is the owner's). The old address keeps working for a week, so
   links in older e-mails still open.
7. **After a week, remove the old address** (Cloudflare, browser): the route `staging-cinemawatch.damiankowalski.pl`
   in the tunnel (step 2's list) → delete; DNS → the `CNAME staging-cinemawatch` record, if it is still there →
   delete; in both Access applications remove the old hostname. Check: `curl.exe -sI
   https://staging-cinemawatch.damiankowalski.pl/` no longer reaches the app (a Cloudflare error or no DNS answer).
8. **Docs:** here §1, §1.6, §2.4, §3 (work log) and the runbooks naming the old host (§4.21, §4.28);
   `infra/compose/env.example` (`SAAS_BASE_URL`); [`SAAS.md`](SAAS.md) §7 stage 7, the header.

**Way back** (any step before 7): step 4 undone with `sudo cp /opt/cinemawatch/.env.before-stage7
/opt/cinemawatch/.env` and `deploy.sh 0.7.0` again; the route from step 2 deleted in the tunnel (the DNS record
goes with it or is deleted by hand); the extra hostnames removed from the Access applications. The old address was
never touched, so it works throughout. Delete `.env.before-stage7` once the move is done (it holds secrets).

## 5. Next infrastructure steps

Stage 5 plan and order: [`SAAS.md`](SAAS.md) §7.

- Stage 6.4.6: SaaS 0.7.0-beta.1 (the wizard, `cloudflared` 2026.10.7) on staging, the owner's check the same day,
  then the stable 0.7.0 (§4.29). No separate stable 0.6.0 (13 Nov 2026).
- Stage 7: the same site moves to `cinemawatch.damiankowalski.pl` (§4.30); the old address goes a week later.
- Stage 5 ([`SAAS.md`](SAAS.md) §7): the testers keep using the site, but the test no longer gates anything
  (13 Nov 2026). The switch to model A (§4.28) is postponed.
- Stage 5.8 (an inventory of everything that needs updates and a runbook for it) belongs to the server now:
  [VPS-1 SERVER.md] §5.
- Restore test again in February 2027 (§4.26).
- 4.6 (deployment from GitHub Actions) was skipped; it stays in the backlog ([`SAAS.md`](SAAS.md) §11).
- `infra/backup/backup.sh` still says in its header that `infra/ansible` installs it; fix the comment together with
  the next real change of the script (any change to it is copied to the server by VPS-1's playbook).

Each CinemaWatch step will be added to §1 and §3 of this file; server steps go to [VPS-1 SERVER.md].

---

## 6. Glossary (infrastructure)

Moved to [VPS-1 SERVER.md] §6 (the shared server's documentation, stage 7b, 12 Nov 2026).
