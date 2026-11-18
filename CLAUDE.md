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
  - **Everything on one page** — no separate Record/Settings pages. Clips open in an editor overlay
    (`Views/ClipEditor`).
  - **Settings: a centered window with numbered sections on the left** (01 Звук … 09 Claude Desktop), content on the
    right, cable/update status at the bottom left (`Views/SettingsSheet`). The user picked it over a single long
    page. Few-option settings are segments (L+R / L / R, Поверх / Заново), group labels are mono caps.
  - **Three layouts, chosen in settings**: Пульт (grid + right column), Док (big tiles + floating bottom bar),
    Лента (one column, list). All show a **live waveform of the last 30 s** (replay buffer) moving in the window,
    as three lanes — Discord / Игра / Голос (`Controls/LiveTracksView`).
  - **Palettes** from the trackers (Гавань, Матча, Ягода, Океан, Лаванда, Графит, Ночь, Эспрессо, OLED) plus two in
    the logo's colors (Xoard, Xoard ночь) and **Авто** (Xoard by day, Xoard ночь with dark Windows). Brushes are
    changed in place (`Theme/ThemeManager.cs`), stock WinUI keys are mapped to palette brushes. **Yellow Xoard is
    the default**; configs saved with "auto" before config version 2 are moved to it once.
  - **Fonts**: Onest (text) + Geologica (titles, big numbers), bundled (OFL) — Cyrillic stand-ins for the trackers'
    Figtree and Bricolage Grotesque, which have no Cyrillic. JetBrains Mono (`XFontMono`) for numbers: times,
    hotkeys, percentages, rulers.
  - **Technical, but not to the max** (feedback round, Nov 2026): mono digits, tick marks, the live strip as an
    audio-editor view (peak + RMS layers, scrolled every frame). Not a full DAW look.
  - Dark ink toast pill, quiet animations with an off switch. **No humor anywhere** — the user asked to remove it.
  - Mockups: https://claude.ai/artifact/Q2XkUuqhup1KvAY6SFaxnw
- **Volume 0–200 %**, 100 % = unity (default for all three). Curve: cube up to 100 %, square above (200 % ≈ +12 dB);
  a −1 dBFS limiter keeps it from clipping.
- **Voice is processed in Xoard (Nov 2026).** Discord must use the «Студия» input profile (its noise suppression,
  echo cancellation and auto-gain kill sounds), which leaves the voice raw and quiet. So the mic path does it
  itself, before mixing with sounds: RNNoise 0.2 (native `rnnoise.dll` in `src/Xoard.Core/Native`), auto-level
  to −18 dBFS speech at 100 % (`VoiceProcessor`), its own −1 dBFS limiter after the slider, and a **pause gate** (the user heard breathing and keyboard): RNNoise's
  speech probability, opening only when ≥2 of 5 frames are ≥ 0.85 (measured: speech stays above for dozens of frames,
  clicks and breaths are 1–2 frame spikes), 30 ms look-ahead so word starts survive, 200 ms hold, 80 ms release.
  Three switches in settings (Убирать шум, Глушить паузы, Выравнивать громкость), all on. Voice delay 50 ms. The DLL is built **locally with portable Zig** by `Native/build-rnnoise.cmd` — not GitHub
  Actions: the user's `gh` login has no `workflow` scope (they declined it), so never add workflow files.
- **Recording and replay are multi-track (Nov 2026, the user's request).** Three tracks on one timeline
  (`Recording/TrackTimeline`, 16-bit): **Discord**, **Игра** = everything else the PC plays except Discord and
  Xoard (the user: "which program is the game — that's your job", so no guessing: every app with an audio session
  gets its own process-loopback capture, `AppAudioCaptures`; Xoard's own tree, its ancestors such as Explorer, and
  apps whose ancestor is already captured are skipped), and **Голос** = the processed voice as friends hear it
  (`IVoiceTap` from the mic path; silence when muted). **Saving = one sound** (1.4.2, after the user called
  1.4's folder of track WAVs + mix checkboxes inconvenient): each strip's label is a button that opens the editor
  with only that track; the main «Сохранить» opens it with all tracks together; the editor has a segment
  «Всё вместе · Discord · Игра · Голос». No `Дорожки` folder, no app names in the editor (the user disliked
  «Игра (steam, Xoard…)»). Hotkey/MCP save all tracks together (MCP `source`: system = Discord + Игра,
  mic = Голос). Live strips are decimated to ≤1 point per 3 px, grouped by absolute bucket index (three full-res
  strips stuttered).
- **Clips go through an editor.** Recording and «Сохранить» (replay) from the window open `ClipEditor` with the
  clip in memory: «Сохранить выделенное», «Сохранить целиком», «Выбросить», «Сбросить выделение». No file until the
  user saves. Hotkeys (in a game) and MCP still save straight to a file. Trim uses the same editor.
- **Sounds folder can be reassigned** in settings (move the files or just switch). `AppConfig.SoundsFolder`, null
  means the default `Документы\Xoard\Sounds`.
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
