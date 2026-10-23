## CinemaWatch 1.4.0 beta 1

> **Pre-release.** For testing the new cinemas - the stable version is still 1.3.0.
> Profiles saved by this beta can still be opened and checked by version 1.3.0.

- **All Cinema City IMAX cinemas in Poland:** Katowice (Punkt 44), Kraków (Zakopianka), Łódź (Manufaktura),
  Poznań (Plaza), Warszawa (Sadyba) and Wrocław (Wroclavia).
  - Step 1 now has a city drop-down (each city has one IMAX hall) with *All cities* at the end.
    The last choice is remembered for new profiles.
  - Date range: up to 31 days for one city, up to 14 days for all cities (six repertoire requests per day).
  - Step 2 groups screenings by day and, within a day, by city.
  - The city is shown on the screening tabs, in the summary and in Windows notifications.
- Hall maps: row numbers now stand in two straight columns at the edges of the hall, like on the Cinema City
  website - neater in halls with rows of different widths (e.g. Kraków, Wrocław).
- Wording: screenings are filtered as "IMAX without dubbing" (Polish films in IMAX have no subtitles).

### Download

`CinemaWatch.exe` is a single self-contained file for Windows 10/11 x64 - no installation and no .NET runtime
needed. The `.sha256` file lets you verify the download:

```powershell
(Get-FileHash .\CinemaWatch.exe -Algorithm SHA256).Hash
```

The exe is not code-signed, so Windows SmartScreen may warn on first start (*More info* → *Run anyway*).
