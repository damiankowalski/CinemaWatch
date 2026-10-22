## CinemaWatch 1.1.0

- **English user interface.** The app follows the Windows display language (Polish → Polish, anything else →
  English) and can be switched at any time from the drop-down in the top-right corner - no restart needed.
  The choice is remembered and also used for background notifications.
- **Compact summary.** Screenings in the summary are collapsed by default (date, hall, counts); the arrow on
  the right expands the selected rows and seats. Results of *Check now* stay visible in both states.
- **Version in the title bar** (e.g. "CinemaWatch 1.1.0").

### Download

`CinemaWatch.exe` is a single self-contained file for Windows 10/11 x64 - no installation and no .NET runtime
needed. The `.sha256` file lets you verify the download:

```powershell
(Get-FileHash .\CinemaWatch.exe -Algorithm SHA256).Hash
```

The exe is not code-signed, so Windows SmartScreen may warn on first start (*More info* → *Run anyway*).
