# CinemaWatch SaaS - Infrastructure (as built)

> Companion to [`SAAS.md`](SAAS.md) (plan and decisions) and [`KNOWLEDGE_BASE.md`](KNOWLEDGE_BASE.md). This file
> records what actually exists, how it was set up and why, how to verify it and how to undo it.
> The provider panels (OVHcloud, Cloudflare) are the source of truth for live settings; update this file with every
> change: the state (§1), the work log (§3) and, if needed, the runbooks (§4) and the glossary (§6).
> **No secrets here** - no passwords, recovery codes, API keys or tokens; they live in the owner's password manager.
> Everything below is public anyway (DNS data is public).
>
> Last updated: 31 October 2026 - stage 1 done (accounts, domain, DNS, DNSSEC). No server yet.

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
| Website / e-mail / server | None yet (server: stage 2, e-mail: stage 4) |
| Costs | Domain 16.69 PLN net (20.53 PLN gross) for the first year, then 58.99 PLN net (72.56 PLN gross) per year; Cloudflare Free 0 |

### 1.1 Accounts

| Account | Used for | Sign-in | Second factor |
| --- | --- | --- | --- |
| GitHub | Repository, CI; later container images (GHCR) | Password | Yes |
| Google | Sign-in to Cloudflare | Password | Yes |
| Cloudflare | DNS, DNSSEC; later Tunnel and Access | "Sign in with Google" only, no Cloudflare password | No own 2FA - accepted risk (§1.2) |
| OVHcloud | Registrar; VPS from stage 2 | Password | Yes |
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

---

## 4. Runbooks

All commands: **Windows (PowerShell)**.

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

---

## 5. Next infrastructure steps

Stage 2 of [`SAAS.md`](SAAS.md) §7: SSH key, the OVHcloud VPS (Ubuntu 24.04 LTS), basic hardening and the risk test
(R1). Each step will be added to §1 and §3 of this file.

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
