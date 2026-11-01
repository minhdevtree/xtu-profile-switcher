---
name: xtu-switcher
description: Working knowledge of the XTU Profile Switcher codebase (a C# 5 / .NET Framework WinForms tray tool that switches Intel XTU power-limit profiles PL1/PL2 through the XTU service's WCF named pipe, monitors CPU temp/power/usage and records them to CSV). Use it before reading or changing anything in this repo, when adding a feature (profile control, setting, tab, CSV column, translation), when debugging XTU connection, hotkey, auto-switch, tray or recording problems, or when building, running and checking the app. Saves re-deriving the architecture, the XTU protocol facts and the gotchas below.
---

# XTU Profile Switcher: codebase guide

Tool for an Acer Predator Triton 500 SE (i7-11800H, XTU 7.14.2.135, Windows 11) but nothing is hard-wired to
that machine except the 8-core constant (`Mon.Cores`) and the sample profile hotkeys. Public repo:
`minhdevtree/xtu-profile-switcher`. User-facing docs are in `README.md`; this file is for people changing code.

Detail files (read only when needed):
- `references/xtu-service.md`: XTU pipes, contracts, control and monitor ids, how persistence works, why values reset.
- `references/testing.md`: build, run elevated, stop the elevated process, off-screen snapshots, CLI checks, CSV validation, what cannot be tested by script.

## Hard constraints (check before you write code)

- **C# 5, .NET Framework 4.8, built with the OS's `csc.exe`** (`build.ps1`). No `?.`, `$""`, `nameof`, `out var`, expression-bodied members, `async` is fine but unused. Anonymous `delegate` style is used everywhere.
- **No NuGet, no project file.** `build.ps1` compiles every `*.cs` in the folder. A new file is picked up automatically.
- **Intel DLLs are never copied.** `XtuInterface.dll` and `XtuCommon.dll` are referenced from `C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Drivers` and loaded at run time by `Program.ResolveIntel` (AssemblyResolve). Do not commit Intel files; do not touch the types outside `Program.Run`/`Main` before the resolver is registered (`Run` is `NoInlining` for this reason).
- **Needs administrator** (`app.manifest` is `requireAdministrator`): the XTU pipes only accept admins.
- **Only four tuning controls may be written**: PL2 (47), PL1 (48), PL2 enable (49), time window (66). `Limits` and `XtuClient.Validate` enforce ranges. Do not widen this without the user asking; it is a deliberate safety boundary.
- **All UI text goes through `Loc`** (English default, Vietnamese). All colours through `Theme`. No hard-coded strings or colours in controls.
- Output of `winexe` has no console: CLI results are only visible through `--out FILE`.
- Source files are mixed LF/CRLF (git warns "LF will be replaced by CRLF"); ignore it, do not "fix" it in a feature commit.

## Repo map

| File | What lives there |
| --- | --- |
| `Program.cs` | `Paths`, `Ids` (control ids), `Limits`, `Profile`, `Snapshot`, `Log`, `Hotkey`, `AppSettings` (settings.ini), `StartupTask` (scheduled task), `XtuClient` (tuning pipe: read, validate, apply), `Model` (read snapshot, load profiles, match active), `Program.Main/Run/RunLog/RunCli` (all command-line options) |
| `MainForm.cs` | The window, tray icon, three tabs (Profiles, Monitor, Settings), hotkey registration, auto-switch by power source, recording UI |
| `Monitor.cs` | `Mon` (monitor ids), `Sample`, `Col`/`LogColumns` (CSV schema), `Nvml`, `BatteryReader`, `SystemMemory`, `XtuMonitorClient` (monitoring pipe), `Recorder` (CSV + `.meta.json`), `MonitorService` (single sampler thread, history, recording) |
| `MonitorUi.cs` | `SeriesRow`: one metric row (value left, time-series line right, optional dashed reference such as PL1) |
| `Tables.cs` | `ProfileTable` (click a row = apply; "..." or right-click = menu), `TabStrip` |
| `Ui.cs` | `Theme` (light/dark colours, fonts), `Ui` helpers (`S()` DPI scale, `Text`, `Round`, `Snapshot`), `AppIcon`, `Menus`, `Flat` base control, `Btn`, `Switch`, `Dropdown`, `TopBar` (status + EN/VI), `Readout` (the PL1/PL2 power strip), `Osd` (toast), `DialogBase`, `HotkeyDialog`, `ProfileDialog` |
| `Loc.cs` | `Loc.T(key)` / `Loc.F(key, args)`; every key has `{English, Vietnamese}`; a missing key shows the raw key (that is how the tray tooltip once read "tray.tip") |
| `build.ps1`, `make-icon.ps1`, `app.manifest`, `app.ico` | Build, icon generator, manifest |
| `docs/*.png` | README screenshots, generated with `--snapshot` (see `references/testing.md`) |

## How it works

**Reading state.** `Model.Read(client)` builds a `Snapshot`: service state (`ServiceController` on `XTU3SERVICE`), Fast Startup flag (registry `HiberbootEnabled`), then over the tuning pipe `RefreshActiveValuesFromHardwareAccess()` followed by `GetControl(id)` for the four controls (`ActiveValue`, `DefaultValue`) and `GetDoRestoreUserValuesOnBoot()`. Always refresh first and read `GetControl(...).ActiveValue`: `GetTuningControlByID` returns 0 on this build and the service caches stale values.

**Profiles.** `Model.LoadProfiles`: "Default (BIOS)" (built from the control default values) + profiles parsed from `C:\ProgramData\Intel\Intel Extreme Tuning Utility\Profiles.xml` (`//DatabaseProfile`, only those having all four control ids) + profiles created in this tool (`@custom` lines). A name used by XTU wins over a custom one. XTU does not store an "active profile", so `Model.ActiveProfile` finds the first profile whose PL1/PL2/enable/window equal the hardware values (`Profile.Matches`, tolerance 0.01).

**Applying.** `XtuClient.Apply`: validate, refresh, return "already in use" if equal, check controls not read-only and the value is in `SupportedValues`, then `DiscardChanges` → `ProposeChange` (enable, PL2, PL1, window) → `ApplyChanges(false, 1, token)` → refresh and read back. Any failure discards pending changes and throws; callers show the message. `MainForm.ApplyProfile` wraps it with the `busy` guard, status line, optional toast (`Osd`).

**Triggers for applying:** click a row, tray menu, global hotkey (`RegisterHotKey`, `WM_HOTKEY` in `WndProc`; needs Ctrl or Alt), `--apply NAME`, auto-switch. Auto-switch (`CheckPowerSource`) applies the AC or battery profile when the power line status changes (power event plus the 5 s timer as backup), forced a few seconds after resume from sleep (`resumeTimer`, 4 s). A manual change stays until the power source changes again.

**Timers (MainForm).** `timer` 5 s (full refresh; every 3rd tick while hidden; power check always), `monTimer` 1 s (Monitor tab redraw), `resumeTimer`. The refresh also sets `monitor.Profiles` so recorded rows carry the matching profile name.

**Tray/lifetime.** Single instance (mutex `XtuProfileSwitcher.Singleton`; a second launch signals `XtuProfileSwitcher.Show` and exits). Closing the window hides it (`reallyExit` false) and flashes a hint; real exit is the tray "Quit". `--tray` starts hidden: `SetVisibleCore` creates the handle and calls `Init()` because `Load` does not fire for a never-shown form. Left click on the tray icon opens the window.

**Monitoring and recording.** `MonitorService` owns one background thread. It runs only while someone `Retain()`s it (the Monitor tab is visible) or a `Recorder` exists. Each loop: XTU `GetValues()` (monitoring pipe, ~1 Hz source), warm-up skip (right after connect XTU returns the previous run's values; samples are discarded until a probe id moves or 4 s pass), every 5 s re-read PL1/PL2/profile via its own `XtuClient`, Windows CPU counter (`Processor Information\% Processor Utility`), memory, power source, battery (WMI), NVML GPU if `IncludeGpu`. Fixed schedule (`nextAt += periodMs`), so rows are 1.00 s apart, no drift. `Recorder.Offer` writes a row when `s.Time >= nextDue - 200 ms`, flushes each row, ends when `endAt` is reached (the clock starts at the first row). Output: `Documents\XtuProfileSwitcher\logs\xtu-log-<date>_<time>.csv` and `<same name>.meta.json` (column list with units and descriptions, machine info). `FileShare.Read`, UTF-8 without BOM.

**Settings file** `%LOCALAPPDATA%\XtuProfileSwitcher\settings.ini`, tab-separated lines: `<profile>\t<combo>` (hotkey), `@auto`, `@ac`, `@battery`, `@lang`, `@theme`, `@custom\tname\tPL1\tPL2\twindow`, `@rec.every`, `@rec.minutes`, `@rec.percore`, `@rec.gpu`. Unknown lines are ignored. Log: `app.log` in the same folder. "Start with Windows" is a scheduled task `XtuProfileSwitcher` (logon + 15 s, highest privileges, `--tray`).

## Recipes

**Add a UI string.** Add `A("key", "English", "Tiếng Việt");` in `Loc.cs`, use `Loc.T`/`Loc.F` (`{0}` placeholders). Before finishing, scan for keys used in code but missing from `Loc.cs`:
```powershell
$used=@{}; Get-ChildItem *.cs | ? Name -ne 'Loc.cs' | % { [regex]::Matches((Get-Content $_.FullName -Raw),'Loc\.[TF]\(\s*"([^"]+)"') | % { $used[$_.Groups[1].Value]=1 } }
$def=@{}; [regex]::Matches((Get-Content Loc.cs -Raw),'A\("([^"]+)"') | % { $def[$_.Groups[1].Value]=1 }
$used.Keys | ? { -not $def.ContainsKey($_) }
```
Texts that depend on the language are set in `MainForm.ApplyTexts()` (re-run on language change); dynamic ones in `RefreshAll`/`UpdateMonitorUi`.

**Add a CSV column.** In `Monitor.cs`: add the id to `Mon` if it is an XTU monitor id, then `c.Add(Xm("name", "unit", "description", Mon.Id, false))` in `LogColumns.Build` (or a `Col` with a getter for non-XTU data, add the field to `Sample` and fill it in `MonitorService.Loop`). The header, the rows and the `.meta.json` come from the same list, so nothing else changes. Update the column table in `README.md`. Keep names `snake_case` with the unit suffix; missing value = empty cell.

**Add a setting.** Field in `AppSettings`, parse in `Load`, write in `Save` (`@name` tag), control in `BuildSettingsPage`, text in `ApplyTexts`, state restore in `FillCombos`/`Init`, `settings.Save()` on change (guard with `suppress` while populating).

**Add a tab.** Add the label key and strip entry (`TabStrip`), build the controls in a `BuildXPage()`, show/hide them in `ShowPage()`, position them in `Relayout()`; if it needs live data, tie it to `UpdateMonitorSubscription()` so sampling stops when it is hidden. Extend `--tab` handling in `Program.Run` and `PreviewTab` for snapshots.

**Add a command-line option.** Parse it in the loop at the top of `Program.Run`; headless operations return an exit code (0 ok, 1 failed, 2 not found/bad usage) and write text to `--out`. Document it in `README.md`.

**Control another XTU setting.** Do not do it by default (see Hard constraints). If the user asks: find the id (`references/xtu-service.md`), read the control with `Control(id)` to see `ReadOnly`, `Enabled`, `SupportedValues`, add a validated path next to `Apply`, and test with `--status` before and after on the real machine.

## UI conventions (keep new UI consistent)

Flat surfaces, 1 px hairlines, no cards, pills or gradients. Colour carries data: Ember for watts, `Heat` for temperature, `Ok`/`Bad` only for state. Everything is custom-drawn on `Flat` (double-buffered, own hover/focus/disabled states); always size with `Ui.S(n)` (DPI) and draw text with `Ui.Text`. Disabled controls fade towards `Ground` (see `Switch`, `Dropdown`). Click on a profile row applies it; secondary actions live in the "..." menu. Default window 640x700 logical px (min 600x560). Dark/light follow the Windows app theme unless `@theme` is set.

## Gotchas (each cost time once)

- A running copy is elevated, so a normal shell cannot overwrite or kill the exe: the build fails with "being used by another process". Stop it from an elevated PowerShell first (see `references/testing.md`).
- PowerShell aliases bite in helper names (`Kill`, `Cli`, ...): use `RunCli`, `StopApp`.
- `ProfileTable` members named `Right`/`Move` hid inherited members: they are `RightEdge`/`MoveFocus`. Avoid names that collide with `Control` members.
- The XTU service answers `DidNotAttempt` when applying values already active: the app pre-checks and reports "already in use".
- WCF default quotas are too small for XTU's replies: both clients raise `MaxReceivedMessageSize`, `ReaderQuotas` and `MaxItemsInObjectGraph`. Copy that block for any new channel.
- XTU's own hotkeys use a global keyboard hook, not `RegisterHotKey`: reusing XTU's combos makes both programs react. `Hotkey.XtuCombos()` reads XTU's settings so the dialog can refuse them.
- Windows Fast Startup makes "Shut down" skip the XTU service stop/start, so restore-after-reboot never runs and limits return to BIOS defaults; the user disabled it (`HiberbootEnabled=0`). `Snapshot.FastStartup` shows the state.
- Open question, unresolved: PL once fell back to 90/115 W overnight without a reboot. Auto-switch and the resume re-apply mitigate it. A possible feature: re-apply when the matching profile disappears while auto-switch is on.
- Sampling the NVIDIA GPU (NVML) can keep a sleeping dGPU awake on battery; that is why the GPU columns are optional.
- XTU data refreshes about once per second; sampling faster only repeats values.
- Snapshots (`--snapshot`) draw the form off-screen with `DrawToBitmap`. Never use a real screen capture: it can grab the user's other windows.

## Verified vs not verified (as of the last commit)

Verified on the real machine: reading state, applying profiles from CLI and by clicking, hotkeys, auto-switch on plug/unplug, restore after restart, tray menu and left-click open, 8 s and 15 s CSV recordings (1.00 s spacing, correct columns, `.meta.json`), Monitor tab rendering (off-screen).
Not verified by script: clicking Start/Stop recording in the real window, tray Start/Stop recording, timed auto-stop in the UI, the battery-only GPU wake-up effect, HiDPI above 100 % scaling, other laptop models or XTU versions.

## Working agreements with this user

Replies in Vietnamese unless asked otherwise (the app itself defaults to English). Commit locally with the trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`; push to `origin/main` when asked (or when the user said to push after finishing). Use the one-off credential helper if plain `git push` prompts: `git -c credential.helper= -c "credential.helper=!gh auth git-credential" push`. PowerShell prints git's progress on stderr as a red error; check the exit state and `git status -sb` instead.
