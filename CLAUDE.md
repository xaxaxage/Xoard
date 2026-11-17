# Xoard

Native Windows soundboard (WinUI 3, C# .NET 10): plays sounds "into the mic" through VB-CABLE so friends hear them in
Discord and games. Called **Xoard** (formerly Soundboard; the app migrates the old data folders and autostart entry
on first start, see `src/Xoard.Core/Shell/LegacyMigration.cs`).

## Decisions

- **Working style.** Stage by stage. After each stage: stop, show what was done and how to check it, wait for the
  user's OK before going on. Ask before big decisions (structure, new dependencies, architecture).
- **UI language: Russian.** (Unlike the user's web apps, which are in English.)
- **Virtual mic: only VB-CABLE.** No Voicemeeter, no own drivers, nothing installed system-wide.
- **No low-level keyboard hooks** (anti-cheat): hotkeys use RegisterHotKey.
- **Logo.** Flat, in the style of the user's GitHub avatar (solid yellow `#FFEF03`): a yellow audio-editor waveform
  on a near-black (`#141414`) rounded tile. Icons are drawn by a script into `src/Xoard.App/Assets/` (`Xoard.ico`
  for exe/window/tray, `Logo.png` for the window header).
- **Design (November 2026 redesign).** The user found the stock WinUI look "dry, too Windows" and wanted it in the
  family of their web apps (calorie and habit trackers), but **stricter, less soft**:
  - **Strict shape by default**: corners ~6 px, hairline borders instead of soft shadows, **sharp waveforms** (pointed
    polygons like an audio editor, not rounded bars). A "Мягкая" (soft, 18 px) shape is an option in settings.
  - **Everything on one page** — no separate Record/Settings pages. Settings are a side sheet; trim is a dialog.
  - **Three layouts, chosen in settings**: Пульт (grid + right column), Док (big tiles + floating bottom bar),
    Лента (one column, list). All show a **live waveform of the last 30 s** (replay buffer) moving in the window.
  - **Palettes** from the trackers (Гавань, Матча, Ягода, Океан, Лаванда, Графит, Ночь, Эспрессо, OLED) plus two in
    the logo's colors (Xoard, Xoard ночь) and **Авто** (Xoard by day, Xoard ночь with dark Windows). Brushes are
    changed in place (`Theme/ThemeManager.cs`), stock WinUI keys are mapped to palette brushes.
  - **Fonts**: Onest (text) + Geologica (titles, big numbers), bundled (OFL) — Cyrillic stand-ins for the trackers'
    Figtree and Bricolage Grotesque, which have no Cyrillic.
  - Humor line under the title (switch in settings), dark ink toast pill, quiet animations with an off switch.
  - Mockups: https://claude.ai/artifact/Q2XkUuqhup1KvAY6SFaxnw
- **Push-to-talk** is postponed (the user said to skip it for now).
- **Git.** Repo-local author `georg`; no AI co-author or session trailers, no model names in commits or code.
  Don't commit `app-playbook.skill`.

## Distribution and updates

- **Velopack** (NuGet `Velopack`, CLI `vpk` pinned in `dotnet-tools.json`). Pack id **`XoardApp`** — not `Xoard`,
  because Velopack owns `%LOCALAPPDATA%\<packId>` and deletes it on uninstall, while settings live in
  `%LOCALAPPDATA%\Xoard`. Installer: `XoardApp-win-Setup.exe`; link for friends:
  `https://github.com/xaxaxage/Xoard/releases/latest/download/XoardApp-win-Setup.exe`.
- Releases go to the public repo **xaxaxage/Xoard** (GitHub Releases) via `release.cmd <version>`.
- Updates: check on start and every 6 h; **download and install only when the user presses «Обновить»** (their
  choice). Own `Program.Main` runs `VelopackApp.Build().Run()` before WinUI (`DISABLE_XAML_GENERATED_MAIN`).
- The MCP server is copied on each start to a stable path, `%LOCALAPPDATA%\Xoard\mcp\Xoard.Mcp.exe`, so the
  Claude config never changes and a running server never blocks an update.
- Commit author stays `georg` (the user chose to keep it public as is).
- `XOARD_UPDATE_SOURCE=<folder|url>` points the updater at a local release folder for testing.

## Build and run

.NET 10 SDK lives in `%USERPROFILE%\.dotnet` (not in PATH): `app.cmd` builds and starts the app, `poc.cmd` runs the
console checks, `publish-mcp.cmd` publishes the MCP server to `publish\mcp\Xoard.Mcp.exe`. Tests:
`dotnet test --solution Xoard.slnx`. `XOARD_DATA_DIR` / `XOARD_PIPE` run the app on separate data and pipe for checks.
