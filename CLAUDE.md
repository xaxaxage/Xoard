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
- **Push-to-talk** is postponed (the user said to skip it for now).
- **Git.** Repo-local author `georg`; no AI co-author or session trailers, no model names in commits or code.
  Don't commit `app-playbook.skill`.

## Build and run

.NET 10 SDK lives in `%USERPROFILE%\.dotnet` (not in PATH): `app.cmd` builds and starts the app, `poc.cmd` runs the
console checks, `publish-mcp.cmd` publishes the MCP server to `publish\mcp\Xoard.Mcp.exe`. Tests:
`dotnet test --solution Xoard.slnx`. `XOARD_DATA_DIR` / `XOARD_PIPE` run the app on separate data and pipe for checks.
