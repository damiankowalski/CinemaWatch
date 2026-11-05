# CinemaWatch SaaS - Infrastructure (as built)

> Companion to [`SAAS.md`](SAAS.md) (plan and decisions) and [`KNOWLEDGE_BASE.md`](KNOWLEDGE_BASE.md). This file
> records what actually exists, how it was set up and why, how to verify it and how to undo it.
> The provider panels (OVHcloud, Cloudflare) are the source of truth for live settings; update this file with every
> change: the state (§1), the work log (§3) and, if needed, the runbooks (§4) and the glossary (§6).
> **No secrets here** - no passwords, recovery codes, API keys or tokens; they live in the owner's password manager.
> **No server addresses either** - the server's IPv4 / IPv6 and host name stay out of this public repository (behind
> Cloudflare Tunnel the origin must stay unknown); runbooks write `<server-ip>`, the real value is in the password
> manager and the OVHcloud panel. Everything below is public anyway (DNS data is public; SSH key fingerprints are
> public by design).
>
> Last updated: 2 November 2026 - stage 2 done: VPS hardened; `CinemaWatch.Probe` runs every 10-12 minutes since
> 2 Nov 2026 (risk test R1, provisional result green; it keeps running during stage 3).

---

## 1. Current state

| Item | Value |
| --- | --- |
| Domain | `damiankowalski.pl` - umbrella domain for all future products, one subdomain per product and environment |
| Registry | NASK (operator of `.pl`) |
| Registrar | OVHcloud; holder: the owner as a private person (the `.pl` registry does not publish private persons' data in WHOIS) |
| Registered / expires | 30 Oct 2026 / 30 Oct 2027; automatic renewal every year |
| Transfer protection | On (OVHcloud default) |
| Declined at order | Hosting plan, OVHcloud DNSSEC option, Zimbra Starter mailbox |
| Nameservers | `faye.ns.cloudflare.com`, `tosana.ns.cloudflare.com` (Cloudflare); previously `dns103.ovh.net`, `ns103.ovh.net` |
| DNS provider | Cloudflare, Free plan; zone active since 31 Oct 2026 |
| DNS records | One: `TXT damiankowalski.pl "v=spf1 -all"` (the domain sends no e-mail) |
| DNSSEC | On since 31 Oct 2026: Cloudflare signs the zone; DS at the registry with key tag 2371, algorithm 13, digest type 2 |
| Cloudflare zone extras | AI crawler policy at defaults (allow); Bot Preference Sync on (robots.txt) - irrelevant until a site exists |
| Server | OVHcloud VPS-1 in Warsaw since 1 Nov 2026, hardened (§1.3); runs `CinemaWatch.Probe` (§1.4) |
| Website / e-mail | None yet (both stage 4) |
| Costs | Domain 16.69 PLN net (20.53 PLN gross) for the first year, then 58.99 PLN net (72.56 PLN gross) per year; Cloudflare Free 0; VPS about 19.20 PLN net (23.62 PLN gross) per month (cart price on 1 Nov 2026 - confirm on the first invoice) |

### 1.1 Accounts

| Account | Used for | Sign-in | Second factor |
| --- | --- | --- | --- |
| GitHub | Repository, CI; later container images (GHCR) | Password | Yes |
| Google | Sign-in to Cloudflare | Password | Yes |
| Cloudflare | DNS, DNSSEC; later Tunnel and Access | "Sign in with Google" only, no Cloudflare password | No own 2FA - accepted risk (§1.2) |
| OVHcloud | Registrar; VPS and its KVM console (since 1 Nov 2026) | Password | Yes |
| Server user `ubuntu` | Administering the VPS | SSH key only (§1.3); its password works only on the KVM console | The key file plus its passphrase |
| E-mail provider | Transactional e-mail | - | Stage 4 |

Passwords and recovery codes are in the owner's password manager. The accounts use the owner's existing e-mail
address, not an address on the new domain: losing the domain must not lock the owner out of the accounts that manage it.

### 1.2 Accepted risks

- **Cloudflare via Google only.** Whoever takes over the Google account controls the DNS of the domain (and, with
  DNSSEC on, can sign forged records validly). Mitigation: 2FA on the Google account. Setting a Cloudflare password
  via "forgot password" did not work. Cloudflare needs a password for its own 2FA and for API tokens; MVP 0 should not
  need a token (the tunnel token comes from the dashboard, deployment goes over SSH). If one is ever needed: Cloudflare
  support.
- **Personal name in the domain.** Visible in URLs and e-mail senders. Fine for learning; a product that becomes real
  gets its own domain.
- **Passwordless sudo for `ubuntu`.** Whoever gets an SSH session as `ubuntu` is root. Mitigation: key-only login and
  a passphrase on the key. A sudo password would be one more secret to manage.
- **One SSH key on one PC.** If the PC or the key file is lost, access goes through the KVM console (§4.9). The
  encrypted key file is backed up in the password manager.
- **SSH port 22 open to the internet.** Key-only login, so bots have nothing to guess; rate limiting or fail2ban is
  in the backlog ([`SAAS.md`](SAAS.md) §11).

### 1.3 Server (VPS)

| Item | Value |
| --- | --- |
| Provider / model | OVHcloud, range "VPS 2027", model VPS-1: 2 vCores, 4 GB RAM, 40 GB NVMe (38 GB usable); no swap yet |
| Location | Warsaw (WAW), Poland; OpenStack region `os-waw2` |
| Ordered / billing | 1 Nov 2026; monthly, no commitment |
| Provider backup | Automated Backup Standard (daily, last 24 h), in the order as a promotion - check the invoice |
| Addresses | One IPv4, one IPv6 - **not recorded here** (password manager, OVHcloud panel) |
| System | Ubuntu 26.04.1 LTS; kernel 7.0.0-42 after the first update; `sudo` is sudo-rs (Ubuntu 26.04 default) |
| Time | Time zone `Etc/UTC` (decision: stays UTC); clock synchronised |
| Host key (server identity) | ED25519 `SHA256:87XQpd1kTc/ZaNB2h6C3FpNUb+e6hZpTSgX2moBx+uI` - compare on the first connection from any device |
| Login | User `ubuntu`, SSH key only; password login off; root login off (§4.10) |
| Owner's SSH key | ed25519 with a passphrase on the development PC, `SHA256:OSzKjZeroCXb1qjlUzWLZBaUcrzAB0t2PnirmonCrew`; the encrypted key file is backed up in the password manager |
| `ubuntu` password | In the password manager; used only on the KVM console |
| sudo | Passwordless for `ubuntu`, granted by the cloud image (a sudoers drop-in), not by the `sudo` group |
| SSH settings | Drop-in `/etc/ssh/sshd_config.d/00-hardening.conf` (§2.2, §4.10) |
| Firewall | `ufw` active: incoming denied except 22/tcp (IPv4 + IPv6), outgoing allowed, routed disabled, logging low (§4.11) |
| Updates | `unattended-upgrades` installs security updates daily; `/etc/apt/apt.conf.d/52unattended-upgrades-local` reboots at 03:30 UTC when needed (§4.12) |
| Emergency access | KVM console in the OVHcloud panel - tested 1 Nov 2026 (§4.9) |
| Our services | `CinemaWatch.Probe` (systemd service + timer) since 2 Nov 2026 (§1.4); containers: stage 4 |

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
| Control group | The same profiles in the desktop app's Task Scheduler at home; "would notify" matched the desktop toast on 2 Nov |
| Known issue | Column `bytes` stays empty for compressed responses (.NET does not report the size after decompression); irrelevant for R1, fixed only if the probe is used again |
| Ends | Runs until the final R1 check before stage 4 ([`SAAS.md`](SAAS.md) §7); stop and remove with §4.19 |

---

## 2. How the pieces fit together

- **Registry (NASK)** runs `.pl`. For each domain it publishes who answers for it (NS records) and the fingerprint
  of the domain's signing key (DS record).
- **Registrar (OVHcloud)** is the shop and the owner's interface to the registry: payment, renewal, holder data,
  nameservers, DS records. Changes made there are forwarded to the registry.
- **DNS provider (Cloudflare)** holds the zone (the records) and answers questions about the domain, signing the
  answers. Records are edited only in Cloudflare; the OVHcloud *DNS zone* tab is no longer used.
- **Resolver** (the internet provider's, 1.1.1.1, 8.8.8.8) looks names up on behalf of devices and caches the
  answers for their TTL.

```
Device --> resolver (e.g. 1.1.1.1; caches answers for the TTL)
             |-- 1. root (.)        "for .pl ask NASK"                  + DS of .pl, signed with the root key
             |-- 2. .pl (NASK)      "for damiankowalski.pl ask faye /   + DS of damiankowalski.pl (set via OVHcloud)
             |                       tosana.ns.cloudflare.com"
             '-- 3. Cloudflare      the answer, e.g. the TXT record     + RRSIG: signature made with the zone's key
```

### 2.1 DNSSEC in short

- **Problem.** Plain DNS has no proof that an answer is genuine; a resolver accepts the first matching answer. An
  attacker can slip in a forged one (e.g. on public Wi-Fi or by cache poisoning) and send people or e-mail elsewhere.
- **Mechanism.** Cloudflare signs every answer of the zone (RRSIG) with the zone's key (DNSKEY). The `.pl` zone
  publishes a fingerprint of that key (DS), and `.pl` itself is signed by the root, whose key resolvers know in
  advance. This chain of trust works like a certificate chain in HTTPS, but for DNS answers. A validating resolver
  rejects an answer whose chain does not check out (SERVFAIL) instead of returning a possibly forged one.
- **Value here.** Integrity of every record: server addresses, future tunnel records and the e-mail records of
  stage 4 (SPF, DKIM, DMARC). Certificate authorities that validate DNSSEC (e.g. Let's Encrypt) cannot be tricked by
  forged DNS into issuing a certificate for the domain. For the website it is defence in depth - HTTPS already stops
  a fake server from showing a valid certificate. Cost: zero; Cloudflare manages the keys.
- **Not covered.** DNSSEC does not encrypt DNS queries (that is DoH / DoT), does not protect the content of a site
  (that is HTTPS) and does not help if the Cloudflare account is taken over.
- **Impact on subdomains.** None to manage: every record added in Cloudflare, including
  `cinemawatch.damiankowalski.pl` and `staging-cinemawatch.damiankowalski.pl`, is signed automatically. Users notice
  nothing.
- **Obligation.** Before disabling DNSSEC or moving the nameservers away from Cloudflare, remove the DS at the
  registrar first and wait (§4.3). The reverse order makes the domain unreachable for validating resolvers.

### 2.2 The server and SSH access

```
Development PC (Windows)                              VPS (Warsaw)
  .ssh\id_ed25519   private key (passphrase)
  .ssh\known_hosts  server's host key  --- SSH, port 22 --->  ufw: only 22/tcp in, all out
                                                              sshd: key only, no root, no passwords
                                                              ~ubuntu/.ssh/authorized_keys: public key
OVHcloud panel --> KVM console (user ubuntu + password) --->  local login; bypasses SSH and ufw
```

- **Key pair.** The private key (`id_ed25519`) is like a seal: only it can make a signature. It stays on the PC,
  locked by the passphrase. The public key (`id_ed25519.pub`) is like the seal's pattern: anyone may have it, it
  checks signatures but cannot make them. The pattern sits on the server in `authorized_keys`.
- **Login.** (1) The server shows its host key; the PC compares it with `known_hosts` - this proves it is the right
  server. (2) The server sends a random challenge. (3) The PC unlocks the private key with the passphrase and signs
  the challenge locally. (4) The server checks the signature against `authorized_keys`. Neither the private key nor
  the passphrase ever leaves the PC.
- **First connection (TOFU).** `ssh` cannot know the host key yet and asks; compare the fingerprint with §1.3, then
  it is stored in `known_hosts` and checked automatically. A later `REMOTE HOST IDENTIFICATION HAS CHANGED` means
  stop, unless the server was reinstalled.
- **Where the SSH settings live.** `sshd` reads `/etc/ssh/sshd_config` and the drop-ins in
  `/etc/ssh/sshd_config.d/` in alphabetical order, and **the first value found wins**. The cloud image ships its own
  drop-ins (which allowed passwords); our `00-hardening.conf` is read first, so it wins, and deleting it undoes the
  change. The same pattern is used for APT (`52unattended-upgrades-local`): never edit the package's own files.
- **Which window am I in?** `PS C:\…>` is Windows (PowerShell), `ubuntu@vps-…:~$` is the server (bash). A PowerShell
  command pasted into the SSH session runs on the server and fails (stage 2 step 3).

If files in `C:\Users\<user>\.ssh` are lost:

| File | What it is | If lost |
| --- | --- | --- |
| `id_ed25519.pub` | Public key | Nothing: recreate it with `ssh-keygen -y -f id_ed25519` |
| `known_hosts` | Host keys of known servers | Nothing: the next connection asks again; compare with §1.3 |
| `id_ed25519` | **Private key** | No SSH access (passwords are off): restore it from the password manager, or use the KVM console and add a new key (§4.8) |
| `id_ed25519` stolen | - | The thief also needs the passphrase. Rotate anyway: new key in, old line out of `authorized_keys` (§4.8) |

### 2.3 The probe on the server

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

---

## 4. Runbooks

Runbooks 4.1-4.6 (domain and DNS) run on **Windows (PowerShell)**. Server runbooks (4.7-4.14) mark every block as
**Windows (PowerShell)** or **server (bash, over SSH)**. `<server-ip>` = the server's IPv4 from the password manager
or the OVHcloud panel - never write it into the repository. Before a change to SSH or the firewall, keep one SSH
session open and test in a second window.

### 4.1 Check the nameservers

```powershell
Resolve-DnsName -Name damiankowalski.pl -Type NS -Server 1.1.1.1
```

Expected: two `NS` rows with `NameHost` `faye.ns.cloudflare.com` and `tosana.ns.cloudflare.com`. Resolvers cache NS
records for up to 24 hours (TTL 86400), so right after a change the old values may still appear.

### 4.2 Check DNSSEC

```powershell
Resolve-DnsName -Name damiankowalski.pl -Type DS -Server 1.1.1.1
```

Expected: one `DS` row with `KeyTag` 2371, `Algorithm` 13, `DigestType` 2. Only a `SOA` row means there is no DS at
the registry. PowerShell prints the digest as decimal bytes; to compare it with the hex *Digest* shown in Cloudflare:

```powershell
((Resolve-DnsName -Name damiankowalski.pl -Type DS -Server 1.1.1.1 | Where-Object Type -eq 'DS').Digest | ForEach-Object { $_.ToString('X2') }) -join ''
```

Expected: the same hex string as Cloudflare's *Digest* (it starts with `8D982CF7`). Also: Cloudflare *DNS → Settings
→ DNSSEC* shows "Success"; https://dnsviz.net shows the chain from `.pl` to `damiankowalski.pl` without errors.

### 4.3 Disable DNSSEC safely (also before changing the DNS provider)

The order matters; the reverse order breaks the domain for validating resolvers.

1. OVHcloud → domain → *DS records* → delete the entry (key tag 2371).
2. Wait until §4.2 shows no `DS` row, then at least one more day (the DS TTL is 86400 s = 24 h; resolvers may still
   hold the old DS).
3. Only then: Cloudflare → *DNS → Settings → Disable DNSSEC*, or move the nameservers (§4.4).

### 4.4 Move the nameservers back to OVHcloud (undo of stage 1 step 5)

1. DNSSEC first: §4.3.
2. Recreate the needed records in the OVHcloud *DNS zone* (today only `TXT "v=spf1 -all"`).
3. OVHcloud → domain → *DNS servers* → *Edit DNS* → *Use OVHcloud default DNS*.
4. Check with §4.1 (expected: `*.ovh.net`).

### 4.5 Add a DNS record (later stages)

Records are edited only in Cloudflare: *DNS → Records → Add record*. Every new record is signed automatically; nothing
to do for DNSSEC. *Proxied* (orange cloud) sends web traffic through Cloudflare; *DNS only* (grey cloud) only answers
with the address. Keep subdomains first-level (`staging-cinemawatch`, not `staging.cinemawatch`), because Cloudflare's
free certificate covers the domain and its first-level subdomains only. Record every change in §1 and §3.

### 4.6 Domain renewal

Automatic every year at OVHcloud (next: before 30 Oct 2027). Keep a valid payment method in OVHcloud and watch its
renewal e-mails. An expired domain stops working (DNS, e-mail, sites), and after the registry's grace period someone
else can register it.

### 4.7 Connect to the server

Windows (PowerShell):

```powershell
ssh ubuntu@<server-ip>
```

Expected: the passphrase prompt for `id_ed25519`, then the server prompt `ubuntu@vps-…:~$`. On the first connection
from a device (or after `known_hosts` was lost) `ssh` asks about the host key: answer `yes` only if the fingerprint
matches §1.3. `REMOTE HOST IDENTIFICATION HAS CHANGED` means stop - unless the server was reinstalled; then forget
the old key with `ssh-keygen -R <server-ip>` and update §1.3.

### 4.8 Add, replace or remove an SSH key

New key pair, Windows (PowerShell); passphrase into the password manager, the key file as an attachment too:

```powershell
ssh-keygen -t ed25519 -C "damian@$env:COMPUTERNAME" -f "$env:USERPROFILE\.ssh\id_ed25519"
```

With a working key, add another public key (Windows, PowerShell; `<other-key>` = the new key's file name):

```powershell
Get-Content "$env:USERPROFILE\.ssh\<other-key>.pub" | ssh ubuntu@<server-ip> "cat >> ~/.ssh/authorized_keys"
```

Without a working key (key lost): log in on the KVM console (§4.9) and allow passwords for a moment (server):

```bash
sudo mv /etc/ssh/sshd_config.d/00-hardening.conf /root/00-hardening.conf.off && sudo systemctl reload ssh
```

Then add the key with the server password (Windows, PowerShell):

```powershell
Get-Content "$env:USERPROFILE\.ssh\id_ed25519.pub" | ssh ubuntu@<server-ip> "mkdir -p ~/.ssh && chmod 700 ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys"
```

and switch passwords off again (server), then run the tests of §4.10:

```bash
sudo mv /root/00-hardening.conf.off /etc/ssh/sshd_config.d/00-hardening.conf && sudo sshd -t && sudo systemctl reload ssh
```

Remove an old key (server): list the keys, delete the old key's line in the editor (`Ctrl+K` deletes a line,
`Ctrl+O` saves, `Ctrl+X` exits), list again:

```bash
ssh-keygen -lf ~/.ssh/authorized_keys
nano ~/.ssh/authorized_keys
ssh-keygen -lf ~/.ssh/authorized_keys
```

A leaked or stolen key is rotated (new key in, old line out), not just deleted on the PC.

### 4.9 Emergency access: KVM console

OVHcloud panel → the VPS → KVM console. Log in as `ubuntu` with the password from the password manager (the password
works here, not over SSH). The console is a screen and keyboard attached to the VM: `ufw` and the SSH settings do not
apply. If a password with special characters is refused, suspect the console's keyboard layout. Last resorts: rescue
mode, or a reinstall and rebuild (§4.13). Tested 1 Nov 2026.

### 4.10 SSH: key-only login, no root (apply, check, undo)

Server (bash, over SSH):

```bash
sudo tee /etc/ssh/sshd_config.d/00-hardening.conf > /dev/null <<'EOF'
# Stage 2 hardening (docs/SAAS_INFRA.md): key-only login, no root login
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
PubkeyAuthentication yes
EOF
sudo sshd -t && echo "config OK"
sudo sshd -T | grep -E "^(permitrootlogin|passwordauthentication|pubkeyauthentication|kbdinteractiveauthentication) "
sudo systemctl reload ssh
```

Expected: `config OK`, then `permitrootlogin no`, `pubkeyauthentication yes`, `passwordauthentication no`,
`kbdinteractiveauthentication no`. An error from `sshd -t`: do not reload, delete the file. Keep the session open and
test in a new window, Windows (PowerShell):

```powershell
ssh ubuntu@<server-ip>
ssh -o PubkeyAuthentication=no -o PreferredAuthentications=password ubuntu@<server-ip>
ssh root@<server-ip>
```

Expected: the first logs in; the other two end with `Permission denied (publickey).` without asking for a password.

Undo (server): `sudo rm /etc/ssh/sshd_config.d/00-hardening.conf && sudo systemctl reload ssh` - back to the
delivered state (passwords allowed).

### 4.11 Firewall (`ufw`)

Server (bash, over SSH) - rules first, then enable:

```bash
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw allow 22/tcp comment 'SSH'
sudo ufw show added
sudo ufw enable
sudo ufw status verbose
```

Expected: `show added` lists `ufw allow 22/tcp comment 'SSH'` (if not, do not enable); `enable` asks
`Proceed with operation (y|n)?` → `y`; status shows `Status: active`,
`Default: deny (incoming), allow (outgoing), disabled (routed)` and `22/tcp ALLOW IN Anywhere # SSH` plus the same
for `(v6)`. Test: a new SSH session (§4.7) and `sudo apt update` (outbound). Blocked attempts in the last hour:
`sudo journalctl -k --since "1 hour ago" | grep -c "UFW BLOCK"`.

Undo: `sudo ufw disable` (also works on the KVM console). Note for stage 4: ports published by Docker bypass `ufw`;
our containers publish none.

### 4.12 Updates (manual and automatic)

Manual, server (bash, over SSH):

```bash
sudo apt update && sudo apt full-upgrade -y
[ -f /var/run/reboot-required ] && cat /var/run/reboot-required || echo "no reboot needed"
```

Dialogs: accept the defaults; for `/etc/ssh/sshd_config` keep the local version. `Pending kernel upgrade!` from
`needrestart` means a reboot is needed: check `sudo -n true && echo "sudo OK"`, then `sudo reboot`, reconnect after
1-2 minutes (§4.7) and check `uname -r; uptime`.

Automatic: `unattended-upgrades` (on by default) installs security updates daily from `apt-daily-upgrade.timer`
(early morning UTC). Our drop-in adds the reboot, server (bash, over SSH):

```bash
sudo tee /etc/apt/apt.conf.d/52unattended-upgrades-local > /dev/null <<'EOF'
// Stage 2 hardening (docs/SAAS_INFRA.md): reboot automatically when an update requires it
Unattended-Upgrade::Automatic-Reboot "true";
Unattended-Upgrade::Automatic-Reboot-Time "03:30";
EOF
apt-config dump | grep -E "Unattended-Upgrade::Automatic-Reboot|APT::Periodic::(Update-Package-Lists|Unattended-Upgrade) "
sudo unattended-upgrade --dry-run --debug 2>&1 | grep -E "Allowed origins|No packages|Packages that will be upgraded|ERROR|Traceback"
```

Expected: both `APT::Periodic` values `"1"`, `Automatic-Reboot "true"`, `Automatic-Reboot-Time "03:30"`; the allowed
origins include `-security`; no `ERROR` or `Traceback`. After an update that needs it, the reboot happens at the next
03:30 UTC. Logs: `/var/log/unattended-upgrades/`. Undo: `sudo rm /etc/apt/apt.conf.d/52unattended-upgrades-local`
(updates continue, without the automatic reboot).

### 4.13 Rebuild the server from scratch (until the Ansible playbook exists)

1. OVHcloud panel: reinstall the VPS with Ubuntu 26.04 LTS (all data on the server is lost).
2. Windows (PowerShell): `ssh-keygen -R <server-ip>` (forget the old host key).
3. First login with the password from OVHcloud (§4.7); note the new host key fingerprint.
4. Add the key (§4.8, `mkdir -p ~/.ssh …` variant) and test key login.
5. Updates and reboot (§4.12, manual part).
6. SSH hardening (§4.10), firewall (§4.11), automatic reboot (§4.12, automatic part).
7. Check the state (§4.14); update §1.3 (host key fingerprint, kernel) and the work log (§3).

About 30 minutes. In stage 4 this becomes one Ansible run ([`SAAS.md`](SAAS.md) §7).

### 4.14 Check the server state (read only)

Server (bash, over SSH):

```bash
echo "== system =="; . /etc/os-release; echo "$PRETTY_NAME"; uname -r
echo "== user =="; whoami; id; sudo -n true 2>/dev/null && echo "sudo: no password" || echo "sudo: asks for a password"
echo "== resources =="; nproc; free -h; df -h /
echo "== time =="; timedatectl | grep -E "Time zone|synchronized"
echo "== ssh =="; sudo sshd -T | grep -E "^(permitrootlogin|passwordauthentication|pubkeyauthentication|kbdinteractiveauthentication) "; ls -la ~/.ssh
echo "== firewall =="; sudo ufw status verbose
echo "== updates =="; apt list --upgradable 2>/dev/null | tail -n +2 | wc -l
echo "== host key =="; ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

Expected: the values of §1.3 - Ubuntu 26.04 LTS, `sudo: no password`, 2 CPUs, about 3.7 GiB RAM, `Etc/UTC` and
synchronised, `permitrootlogin no` / `passwordauthentication no`, `ufw` active with 22/tcp only, the host key
fingerprint of §1.3. The update count is only current after `sudo apt update`.

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

## 5. Next infrastructure steps

- During stage 3 (development on the PC, no server work): the probe keeps running; a look with §4.16 now and then.
- Start of stage 4: the final R1 check with at least 5 days of probe data (§4.18), then stop and remove the probe
  (§4.19) unless it is still useful.
- Stage 4, from the SaaS code of stage 3 ([`SAAS.md`](SAAS.md) §7): Cloudflare Access on `/admin*` (the operator's
  panel; owner's Google account only) and a Healthchecks.io account with 2FA, one check per environment (period 5 min,
  grace 10 min), its ping address only as a secret on the server (`Saas__Heartbeat__Url`).
- Stage 4: the Ansible playbook first (stage 2 hardening as code, verified by a reinstall), then swap, Docker and
  Compose, a `deploy` user, Cloudflare Tunnel and Access, e-mail records, backups, and an external heartbeat monitor
  for the poller ("dead man's switch", [`SAAS.md`](SAAS.md) §7).

Each step will be added to §1 and §3 of this file.

---

## 6. Glossary (infrastructure)

### Domains

| Term | Meaning |
| --- | --- |
| Domain name | Human-readable name such as `damiankowalski.pl`; the owner creates subdomains (`cinemawatch.damiankowalski.pl`) freely |
| TLD | Top-level domain, the last part of the name (`.pl`, `.com`) |
| Registry | Organisation that runs a TLD; for `.pl` it is NASK |
| Registrar | Company that sells domains and passes changes to the registry; here OVHcloud |
| Holder (registrant) | The legal owner of a domain |
| WHOIS | Public lookup of registration data; for `.pl` the data of private persons is not published |
| Transfer protection | Registrar lock: the domain cannot move to another registrar until the owner unlocks it |
| Renewal / expiry | A domain is rented per year; an expired domain stops working and can later be registered by someone else |

### DNS

| Term | Meaning |
| --- | --- |
| DNS | Domain Name System: the internet's directory that turns names into addresses and other data |
| Zone | All DNS records of one domain, held by its DNS provider |
| Record | One entry in the zone: name, type, value, TTL |
| Zone apex (`@`) | The domain itself, without a subdomain |
| A / AAAA | Record with an IPv4 / IPv6 address |
| CNAME | Alias: "this name is the same as that name" |
| MX | Servers that receive e-mail for the domain |
| TXT | Free text; used for SPF, DKIM, DMARC and ownership checks |
| NS | Nameservers that answer for the domain |
| SOA | Administrative record of a zone (primary server, serial number, timers) |
| CAA | Certificate authorities allowed to issue certificates for the domain |
| Authoritative nameserver | Server that holds the zone and gives the final answers; here Cloudflare's `faye` and `tosana` |
| Delegation | The parent zone (`.pl`) pointing to the nameservers of a domain |
| DNS provider | Who runs the authoritative nameservers; here Cloudflare |
| Resolver (recursive) | Server that looks names up for devices and caches the results (internet provider, 1.1.1.1, 8.8.8.8) |
| TTL | Time to live: how long resolvers may cache an answer (86400 s = 24 h) |
| Propagation | Time until the registry publishes a change and old cached answers expire |
| Proxied / DNS only | Cloudflare record modes: web traffic through Cloudflare (orange cloud) or only the DNS answer (grey cloud) |
| Cache poisoning | Attack that slips a forged answer into a resolver's cache |

### DNSSEC

| Term | Meaning |
| --- | --- |
| DNSSEC | DNS Security Extensions: signatures on DNS answers so resolvers can detect forged answers |
| DNSKEY | Public key of a zone, published in the zone |
| RRSIG | Signature over a set of records |
| KSK / ZSK | Key-signing key (flag 257) signs the zone's keys; zone-signing key (flag 256) signs the records |
| DS | Delegation signer: fingerprint (hash) of a domain's KSK, published in the parent zone (`.pl`) |
| Key tag | Short number that identifies a key (here 2371) |
| Algorithm 13 | ECDSA P-256 with SHA-256, the signing algorithm Cloudflare uses |
| Digest type 2 | SHA-256, the hash used in the DS |
| Chain of trust | root → `.pl` → `damiankowalski.pl`: each level vouches for the next with a DS |
| Trust anchor | The root key that validating resolvers know in advance |
| Validating resolver | Resolver that checks signatures; on a broken chain it answers SERVFAIL instead of a possibly forged answer |
| SERVFAIL | DNS error "server failure"; with DNSSEC it usually means the validation failed |
| NSEC3 | Signed proof that a name or record does not exist (e.g. "no DS for this domain") |
| CDS / CDNSKEY | Records in which a zone tells its parent which DS to publish (RFC 7344); Cloudflare publishes them |
| DNSViz | Web tool (dnsviz.net) that draws and checks the DNSSEC chain of a domain |
| DoH / DoT | DNS over HTTPS / TLS: encrypts DNS queries (privacy); unrelated to DNSSEC (integrity) |

### E-mail authentication

| Term | Meaning |
| --- | --- |
| SPF | TXT record listing the servers allowed to send e-mail for the domain; `v=spf1 -all` means none |
| DKIM | Signature in every e-mail; the public key is published in DNS |
| DMARC | Policy telling receivers what to do with e-mails that fail SPF / DKIM, plus reports to the owner |

### Certificates

| Term | Meaning |
| --- | --- |
| TLS certificate | Proves a server's identity for HTTPS; issued by a certificate authority |
| Certificate authority (CA) | Issuer of certificates, e.g. Let's Encrypt; checks control of the domain via DNS or HTTP first |
| Universal SSL | Free certificate from Cloudflare for the domain and its first-level subdomains |

### Accounts and security

| Term | Meaning |
| --- | --- |
| 2FA | Two-factor authentication: a second proof of identity besides the password |
| TOTP | Time-based one-time password: 6-digit codes from an authenticator app |
| Passkey / security key | Phishing-resistant second factor based on public-key cryptography (WebAuthn) |
| Recovery (backup) codes | One-time codes for when the second factor is lost; kept in the password manager |
| Social login | Signing in with another account (e.g. Google); that account's security then protects this one |
| API token | Secret key that lets scripts and tools call a provider's API; never in the repository or the chat |

### Cloudflare

| Term | Meaning |
| --- | --- |
| Cloudflare Free plan | Free DNS, DNSSEC, proxy, TLS certificates and basic protection |
| AI crawler policy / Bot Preference Sync | Zone settings that tell AI and search bots what they may do on the owner's site, written into robots.txt |
| robots.txt | File that tells crawlers what they may visit; a convention, not enforcement |

### Servers and Linux

| Term | Meaning |
| --- | --- |
| VPS | Virtual private server: a rented virtual machine with its own system and guaranteed CPU / RAM |
| KVM (virtualisation) | Full hardware virtualisation: the VM has its own kernel, so Docker and firewalls work; OpenVZ / LXC "VPS" share the host's kernel |
| Cloud image | Prepared system the provider installs; brings defaults such as the `ubuntu` user |
| cloud-init | Tool that configures a cloud image at first boot (user, keys, network, SSH settings) |
| LTS | Long-term support Ubuntu release: 5 years of standard updates (26.04: until 2031) |
| Kernel | Core of the operating system; a new kernel runs only after a reboot |
| root | Administrator account; direct login disabled here |
| `sudo` | Runs one command as root; on Ubuntu 26.04 implemented by sudo-rs |
| Drop-in file | Small extra configuration file in a `*.d/` folder that overrides defaults without editing the package's files |
| `apt` | Ubuntu's package manager: `update` refreshes the lists, `full-upgrade` installs the updates |
| `needrestart` | Reports services and kernels that need a restart after updates |
| `unattended-upgrades` | Installs security updates automatically; here also reboots at 03:30 UTC when needed |
| systemd, unit, timer | Ubuntu's service manager; a unit describes a service, a timer starts a unit on a schedule (used by the probe) |
| `oneshot` service | `Type=oneshot`: each start runs the program once to the end (the probe: one check per start) |
| `daemon-reload` | Makes systemd read new or changed unit files |
| `enable --now` | Start a unit now and after every boot |
| `DynamicUser` | systemd creates a temporary user without rights for each run; between runs its files show `nobody` as owner |
| `StateDirectory` | Data folder systemd creates for a service (`/var/lib/<name>`; with `DynamicUser` a link into the root-only `/var/lib/private`) |
| Journal, `journalctl` | systemd's log of everything the services print; `-u <unit>` shows one service, `--since today` one day |
| Symbolic link | File-system entry that points to another path |
| Glob (`*`) | Pattern the shell turns into file names before running a command - as the current user, so `sudo cat folder/*` fails when only root can read the folder; `sudo sh -c '…'` expands it as root |
| `install` | Copies a file and sets its owner and permissions in one step |
| Checksum, `sha256sum -c` | Fingerprint of a file's content; `-c` checks a copy against the recorded fingerprint |
| `scp` | Copies files over SSH (same key, passphrase and host-key check); `-r` copies a whole folder |
| Current directory, `getcwd` | The folder a shell stands in; deleting it from inside makes later commands complain until `cd ~` |
| Firewall, `ufw` | Filters network connections; `ufw` ("uncomplicated firewall") is Ubuntu's simple front end, for IPv4 and IPv6 |
| Default deny | Everything not explicitly allowed is blocked |
| Port | Number that identifies a service on a server (SSH = 22/tcp) |
| KVM console | Screen and keyboard of the VM in the provider's panel; works without SSH, firewall rules do not apply |
| Rescue mode | Starts the VPS from a separate system to repair its disk |
| Swap | Disk space used when RAM runs out; none yet, 2 GB planned in stage 4 |
| UTC | Coordinated Universal Time, without daylight saving time; the server's time zone |
| Anti-DDoS, blackholing | Protection against floods of traffic; blackholing drops all traffic to the attacked IP, so the server goes offline |

### SSH

| Term | Meaning |
| --- | --- |
| SSH | Secure Shell: encrypted remote login to a server's command line |
| Key pair | Private key plus its public key, generated together with `ssh-keygen` |
| Private key | Stays on the owner's PC (`id_ed25519`); signs the login challenge; never shared |
| Public key | May be shared (`id_ed25519.pub`); lets a server check signatures made with the private key |
| Passphrase | Password that encrypts the private key file on disk; never sent to the server |
| ed25519 | Modern, short key type; the OpenSSH default |
| `authorized_keys` | File on the server (`~/.ssh/authorized_keys`) with the public keys allowed to log in as that user |
| Host key | The server's own key pair; proves to the client that it reached the right server |
| `known_hosts` | File on the client with the host keys of servers seen before |
| Fingerprint | Short hash of a key (`SHA256:…`) for comparing keys by eye; public |
| TOFU | Trust on first use: the first connection accepts the host key after a manual check, later ones are checked automatically |
| `sshd` | The SSH server; settings in `/etc/ssh/sshd_config` and drop-ins in `sshd_config.d/` (first value wins) |
| `ssh-agent` | Optional service that keeps an unlocked key in memory so the passphrase is typed less often; not used |
| `ssh-copy-id` | Linux / macOS helper that adds a public key to a server; Windows has none (§4.8 does the same) |

### Configuration as code

| Term | Meaning |
| --- | --- |
| Infrastructure as code | Server configuration kept as versioned files instead of manual steps |
| Ansible | Brings servers to a described state over SSH, without an agent on the server; files in YAML (planned for stage 4) |
| Playbook, role | Ansible file (or reusable part) that describes the desired state |
| Inventory | List of servers Ansible manages; contains addresses, so it stays out of git here |
| Control node | Machine that runs Ansible: Linux, macOS or WSL2, not native Windows |
| Idempotency (configuration) | Running the same playbook again changes only what differs |
