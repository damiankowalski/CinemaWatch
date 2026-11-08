## CinemaWatch 1.5.0

- **Multi-select on the hall map** - fewer clicks for blocks of seats, no modifier keys:
  - **Painting:** press on a seat and drag. The first seat decides: if it was not watched, every seat you pass over
    is selected; if it was, they are all cleared. Passing over a seat again changes nothing.
  - **Rectangle:** press on an empty spot and drag. The seats whose centre lies inside are all selected - or all
    cleared when every one of them already was (the same rule as a row number).
  - The seats change while you drag; releasing the button keeps the result. **Esc** (or switching to another window
    during the drag) restores the selection from before the press.
  - Painting and the rectangle skip wheelchair spaces; those are selected by a single click only.
  - A click on a seat and a click on a row number work as before.
- The selection rules now live in `CinemaWatch.Core` with a shared case file (`tests/Fixtures/seat-selection-cases.json`),
  so the planned web version of the map behaves exactly the same.
- No change to the profile format: profiles saved by 1.5.0 open in 1.4.0 and older.

### Download

`CinemaWatch.exe` is a single self-contained file for Windows 10/11 x64 - no installation and no .NET runtime
needed. The `.sha256` file lets you verify the download:

```powershell
(Get-FileHash .\CinemaWatch.exe -Algorithm SHA256).Hash
```

The exe is not code-signed, so Windows SmartScreen may warn on first start (*More info* → *Run anyway*).
