## CinemaWatch 1.2.0

- **Clickable steps.** The step indicator at the top is now navigation: click any step you have already visited
  (or the next one) to jump straight to it.
  - Going back is instant; nothing is reloaded.
  - Going forward runs the same checks and loading as *Next*, step by step, and stops at the first step that
    needs your input (for example when no screening is selected).
  - Data that has not changed - same dates, same screenings, loaded less than 5 minutes ago - is reused, so
    jumping back and forth costs no extra requests to Cinema City.
- Ticked screenings stay ticked when you change the dates (for example when widening the date range).

### Download

`CinemaWatch.exe` is a single self-contained file for Windows 10/11 x64 - no installation and no .NET runtime
needed. The `.sha256` file lets you verify the download:

```powershell
(Get-FileHash .\CinemaWatch.exe -Algorithm SHA256).Hash
```

The exe is not code-signed, so Windows SmartScreen may warn on first start (*More info* → *Run anyway*).
