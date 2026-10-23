## CinemaWatch 1.3.0

- **Profile tabs.** Several profiles can be open at once, each in its own tab above the steps - with its own
  wizard, selection and messages.
  - *Open profiles…* accepts several files at once; a profile that is already open just gets focus.
  - At start-up the tabs are restored from the profiles folder. Profiles whose screenings are all over are skipped.
    A tab loads its data when you first open it.
  - A dot on the tab marks unsaved changes; closing such a tab (or the app) asks first.
- **Check all.** Next to *Check this profile*, *Check all (N)* checks every open profile, one after another.
  A screening watched in several profiles is fetched only once. Each tab shows a badge with the number of free
  watched seats, and opening a tab later shows the results on its cards.
- Fixed: the city, cinema and technology fields in step 1 could show up empty (they are still fixed to
  Warsaw / Cinema City Sadyba / IMAX - choosing them comes with the all-IMAX version). Switching tabs also can no
  longer reset the chosen film or the active screening map.
- Pre-release builds: tags such as `v1.4.0-beta.1` are published as pre-releases (never "Latest"),
  and CI also runs on `feature/*` branches.

### Download

`CinemaWatch.exe` is a single self-contained file for Windows 10/11 x64 - no installation and no .NET runtime
needed. The `.sha256` file lets you verify the download:

```powershell
(Get-FileHash .\CinemaWatch.exe -Algorithm SHA256).Hash
```

The exe is not code-signed, so Windows SmartScreen may warn on first start (*More info* → *Run anyway*).
