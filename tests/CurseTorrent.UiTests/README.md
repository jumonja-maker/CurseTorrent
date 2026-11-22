# CurseTorrent Windows UI regression checks

Run on Windows with the .NET 10 Windows Desktop runtime:

```powershell
dotnet run --project tests/CurseTorrent.UiTests/CurseTorrent.UiTests.csproj --configuration Release -- artifacts/ui-tests
```

The project can be cross-compiled on Linux using `dotnet build -m:1`, but WPF execution requires Windows. The runner loads the exact production application resources and constructs the real windows, without starting the production App, torrent engine, network traffic, or real user profile. It uses synthetic `ITorrentService` snapshots and records the commands sent by actual UI bindings.

Unlike the original audit harness, every regression condition is an assertion. Failed checks cause a nonzero process exit. Stdout contains `UI_TEST` JSON records, a `UI_TEST_SUMMARY`, and screenshot filenames. The output directory contains `ui-regression-results.json` and eleven PNGs.

The checks cover numeric file sorting and updates, all four priority labels and command values, selection/focus preservation during refresh, actual horizontal page commands, initial monitor work-area bounds, minimum-width columns, fully visible rows in both table viewports at the minimum window size, sidebar visibility, restoration of the standard header after resizing, modal existing-file consent, five title shapes (including 20,000 unbroken characters and many newlines), long error text, and action buttons in short windows. The final palette check calls the production palette switch against current system colors and verifies that existing windows update and return to dark colors.

The palette check does not toggle the Windows high-contrast setting or test Narrator/NVDA. The runner reports its actual DPI and work area; it does not simulate a multi-monitor DPI transition. These remain manual validation scenarios. PNGs render the full WPF Window visual; native Windows title bars/borders are not rendered and may leave transparent outer pixels. The images contain synthetic transfer values and do not demonstrate network performance.
