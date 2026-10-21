## CinemaWatch 1.0.0

First public release.

- Wizard: date or date range → film and IMAX screenings → interactive hall map → summary.
- Watch whole rows or single seats (taken ones too), optional "N seats next to each other".
- Profiles you can save, reopen and check on demand.
- Background checks via Windows Task Scheduler with Windows notifications; clicking one opens the booking page.
- Scheduled tasks remove themselves after the last screening of a profile.

### Download

`CinemaWatch.exe` is a single self-contained file for Windows 10/11 x64 - no installation and no .NET runtime
needed. The `.sha256` file lets you verify the download:

```powershell
(Get-FileHash .\CinemaWatch.exe -Algorithm SHA256).Hash
```

The exe is not code-signed, so Windows SmartScreen may warn on first start (*More info* → *Run anyway*).
