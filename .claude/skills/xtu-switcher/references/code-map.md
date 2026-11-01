# Code map (method level)

Read this when you need to know *which method does what, who calls it, and what it assumes* without opening
the source. `SKILL.md` has the architecture and the recipes; this file is the index under it. Names are exact,
line numbers are left out on purpose (they rot). If a name here is not found, grep for it: the file has moved on.

Conventions that apply everywhere: namespace `XtuProfileSwitcher`, everything `internal`, C# 5, anonymous
`delegate` instead of lambdas, UI sizes through `Ui.S(n)`, text through `Loc`, colours through `Theme`.

## Threads and locks (the rules that are easy to break)

| Thread | What runs there | Rule |
| --- | --- | --- |
| UI thread | everything in `MainForm`, all controls, `Osd`, dialogs | Never block it on the XTU pipe for long: `XtuClient` has 5 s open / 15 s send+receive timeouts |
| `monitor` sampler thread | `MonitorService.Loop` only | Owns its own `XtuMonitorClient`, `XtuClient` (tuning, for PL1/PL2) and `BatteryReader`. Never touches controls |
| thread-pool | `SystemEvents.PowerModeChanged` handler, the "show window" `EventWaitHandle` callback, `MonitorService.RecordingEnded` | Always `BeginInvoke` back to the UI thread, and check `IsHandleCreated` first (the form may be gone) |

`MonitorService.gate` guards `latest`, `history`, `rec`, `viewers`, `thread`. `Profiles` and `IncludeGpu` are
`volatile` fields written from the UI thread and read from the sampler. `XtuClient` instances are not
thread-safe and are never shared: `MainForm.client` is UI-only, the sampler has its own.

## Program.cs

| Name | What it does and what to know |
| --- | --- |
| `Paths` | Constants for XTU folders, the service name, the two pipe URIs and the task name. `DataDir` creates `%LOCALAPPDATA%\XtuProfileSwitcher` on first use. `LogFile` = `app.log`, `SettingsFile` = `settings.ini` |
| `Ids`, `Limits` | The four writable control ids (47 PL2, 48 PL1, 49 PL2 enable, 66 window) and the hard ranges: PL1 15-120, PL2 up to 150 and not below PL1, window 1-128 s |
| `Profile` | `Name, Pl1, Pl2, Pl2Enable, Window, Builtin, Custom`. `Matches(Snapshot)` compares with 0.01 tolerance and requires `Connected`. `Describe()` is the one-line text used by the CLI |
| `Snapshot` | Data only: service state, `Connected`, `Error`, active values, default values, `RestoreOnBoot`, `FastStartup`. Not the same thing as `Ui.Snapshot` (the off-screen render method). `Model.Read` fills it |
| `Log.Write(msg)` | Appends a timestamped line to `app.log`; swallows every error. Safe from any thread |
| `Hotkey` | `Mods` uses the Win32 bits `Alt=1, Ctrl=2, Shift=4`, plus `NoRepeat=0x4000` passed to `RegisterHotKey`. `Parse("Ctrl+Alt+F2")` needs Ctrl or Alt and a valid `Keys` name as the last token, else `null`. `Canonical()` is upper-case, for comparing. `XtuCombos()` returns XTU's own hotkeys (empty unless `HotkeyPairingOn` is `True` in `uisettings.xml`; the binary `xtu.hotkey.settings` is decoded as Latin-1 and scanned with a regex) |
| `AppSettings` | `Load()` / `Save()` for `settings.ini`. First run creates three default hotkeys and saves. Hotkeys are a case-insensitive dictionary keyed by profile name. Names starting with `@` are reserved for tags. `Save()` always writes `@auto`, `@lang`, `@rec.*`; it writes `@theme` only when not `auto`, and `@ac`/`@battery` only when set. Numbers are written with the invariant culture |
| `StartupTask` | Wraps `schtasks.exe` (`/Query`, `/Create /XML`, `/Delete`). The task XML: logon trigger with `PT15S` delay, `HighestAvailable` (so no UAC at logon), `--tray` argument, `IgnoreNew`, runs on battery. `Enable` writes a temp XML in UTF-16 and deletes it |
| `XtuClient` | The tuning pipe. `Connect()` builds the binding with 64 MB quotas and raises `MaxItemsInObjectGraph` per operation; `Close()` aborts (never throws). `RefreshFromHardware()`, `Active(id)`, `Control(id)`, `RestoreOnBoot()` are thin calls. `Apply(Profile)` is the only writer, see below. `Validate(Profile)` is also used by `ProfileDialog` |
| `Model` | `LoadProfiles(defaults, customs)`: "Default (BIOS)" first (from the service's default values, or 90/115/56 when offline), then `Profiles.xml`, then customs whose names are not already taken. `LoadXtuProfiles` keeps only profiles that have all four ids. `Read(client)` never throws: on failure it closes the client and returns `Connected=false` with `Error`. `ActiveProfile` returns the first match or `null` |
| `Program.Main` | Registers `AssemblyResolve` (loads Intel DLLs from `Paths.XtuDir`) and calls `Run`. `Run` is `NoInlining` so no Intel type is touched before the resolver exists |
| `Program.Run` | Parses the flags, loads settings, sets `Loc.Lang`, `Theme`, `Ui` scale, then picks one path: CLI (`--status/--list/--apply/--startup`) → `RunCli`; `--log` → `RunLog`; `--snapshot` → off-screen render; otherwise the GUI with the single-instance mutex |
| `RunCli`, `RunLog` | Build a text result, log it, write it to `--out` if given. Exit codes: 0 ok, 1 failed, 2 profile not found or no duration |

`XtuClient.Apply(Profile)` in order: `Validate` → `RefreshFromHardware` → if the active values already equal the
profile return `msg.already` (the service answers `DidNotAttempt` otherwise) → refuse read-only/disabled PL1/PL2 →
`RequireSupported` (value must be in the control's `SupportedValues`) → `DiscardChanges` → `ProposeChange`
(enable, PL2, PL1, window; refuses on any error code or `requiresReboot`) → `ApplyChanges(false, 1, token)` →
refresh and read back → return `msg.applied`. Every failure path calls `DiscardChanges` and throws
`InvalidOperationException` with a localized message; the caller decides how to show it.

## MainForm.cs

State flags: `busy` (an apply is running; a second request is dropped silently), `suppress` (programmatic UI
update in progress, change handlers must return early), `inited`, `reallyExit` (true only from the tray "Quit"),
`startHidden`, `monRetained` (this form holds a `monitor.Retain()`), `comboKey` (memo of what the two profile
drop-downs were built from), `lastLine` (previous power line status), `statusY`.

| Method | Role |
| --- | --- |
| constructor | Creates controls, wires events, sets fonts/colours, window 640x700 logical (min 600x560), tray icon, three timers, power event, "show" signal wait. Calls `ApplyTexts()` and `Relayout()` last |
| `Init()` | One-time start: reads the startup task state, first `RefreshAll()`, applies the profile for the current power source, starts the 5 s timer. Called from `Load`, or from `SetVisibleCore` when started with `--tray` (a never-shown form gets no `Load`) |
| `BuildMonitorPage`, `BuildSettingsPage` | Create and wire the controls of those tabs (the Profiles tab is built in the constructor) |
| `ApplyTexts()` | Sets every language-dependent text and refills the drop-downs. Runs at start and on language change |
| `ShowPage()` | Shows/hides control groups per tab (`Page` = `tabs.SelectedIndex`: 0 Profiles, 1 Monitor, 2 Settings). A new control must be added to the right list here |
| `Relayout()` | Positions everything with absolute rectangles (margin 28, top bar 48 high at y 8, readout 134 high at y 62, tab strip 40 high). Runs on every size change |
| `RefreshAll()` | The one refresh: `Model.Read`, reload profiles, update top bar, readout, table rows, facts line, combos, tray menu and tooltip. Safe to call often |
| `RebuildTray(active)` | New context menu each time (old one disposed): one check item per profile, recording toggle, open, quit. Tooltip is cut to 63 chars (Windows limit) |
| `ApplyProfile(p, notify, reason)` | Guarded by `busy`. Connects if needed, `client.Apply`, refresh, status line, and an `Osd` notice when `notify`. On error it closes the client, shows the error, refreshes. `reason` is non-null for automatic switches |
| `ApplyByName(name)` | From a hotkey; unknown name shows an error notice |
| `RegisterHotkeys` / `UnregisterHotkeys` | Re-register all from `settings.Hotkeys` (ids 1..n in `registered`); failures are reported together. Called on handle creation, after editing a hotkey or deleting a profile. `WndProc` maps `WM_HOTKEY` (0x0312) back to a profile name |
| `EditHotkey(row)` | Dialog → parse → reject duplicates and XTU's combos → save → re-register |
| `AddProfile`, `DeleteProfile(row)` | Customs only. Delete also removes its hotkey and clears it from the AC/battery choices |
| `FillCombos`, `FillCombo`, `OnAutoChanged` | The two "when plugged in / on battery" lists. `OnAutoChanged` saves and applies immediately only when the change concerns the power source in use |
| `OnPowerModeChanged` | Hops to the UI thread; on resume starts `resumeTimer` (4 s) which forces `CheckPowerSource(true)` |
| `CheckPowerSource(force)` | Compares the power line with `lastLine`; if changed (or forced) and auto-switch is on, applies the mapped profile (or reports a missing one). Also updates the readout's power text. Called from the 5 s timer, power events, `Init`, `OnAutoChanged` |
| `UpdateMonitorSubscription()` | Retain/Release of the sampler: wanted only when the Monitor tab is the visible page. Starts/stops `monTimer` (1 s) |
| `UpdateMonitorUi()` | Reads `monitor.Latest` and `monitor.History(300)`, grows the time axis (60 s → 120 s → 300 s), feeds the three `SeriesRow`s, builds the details line (clock, hottest core, GPU, throttling) |
| `ToggleRecording`, `StopRecording`, `OnRecordingEnded`, `UpdateRecordingUi`, `OpenLogFolder` | Recording controls. Files go to `Documents\XtuProfileSwitcher\logs\xtu-log-<date>_<time>.csv`. A timed recording ends on the sampler thread and comes back through `RecordingEnded` → `BeginInvoke(OnRecordingEnded)` |
| `StartService()` | Starts `XTU3SERVICE` and waits up to 20 s |
| `OnStartupChanged()` | Creates/removes the scheduled task; on failure shows the error and puts the switch back |
| `Say(text, error)` | Status line at the bottom of the window, and the same text in `app.log` |
| `OnFormClosing` | Hides to tray unless `reallyExit` (and shows a hint notice); on real exit stops timers, unsubscribes the power event, finishes any recording (`monitor.Dispose`), closes the pipe |
| `Resize` handler | Minimizing hides the window (it lives in the tray, not the taskbar) |
| preview members | `SimulateOffline`, `PreviewTab`, `PreviewClick`, `PreviewRecord(Path)`, `PreviewUpdate()`. Used only by `--snapshot`; with `preview=true` the tray icon is hidden, hotkeys are not registered, the power event is not subscribed and timers do not start |

Timer cadence: `timer` 5 s (full `RefreshAll` when visible, every 3rd tick when hidden; `CheckPowerSource(false)`
every tick), `monTimer` 1 s, `resumeTimer` 4 s one-shot.

## Monitor.cs

- `Mon`: monitor ids and `Cores = 8`. `PkgTemp`, `GfxVolt`, `GfxTemp`, `CacheVolt`, `SaVolt` are large uint ids.
- `Sample`: one reading. `NaN` or empty means "not available". `X(id)` returns `NaN` when the id is missing. `Warmup` samples are neither shown nor recorded. `Status` is `ok` or `xtu_unavailable`.
- `LogColumns.Build(perCore, gpu)`: the single definition of the CSV schema, in this order: 10 context columns, 17 XTU/CPU columns, 4 throttle columns, then 32 per-core (8 x temp/freq/volt/util), then 5 GPU. That is 68 with both options on. The header, each row and the `.meta.json` column list all come from this list.
- `Nvml`: lazy one-time init, device 0, temperature (sensor 0), power (mW), utilization, graphics clock (type 0) and memory clock (type 2). Any exception turns it off for the session.
- `BatteryReader.Read()`: WMI `root\WMI` `BatteryStatus`, at most every 2 s; rates are mW, converted to W. `NaN` on desktops.
- `XtuMonitorClient`: opens `.../ExtremeTuningUtility/Monitoring` on first `GetValues()`, registers with `Connect(pid)` + `Start(pid)`, sets `JustOpened` so the loop can run its warm-up, `Reset()` drops the channel after an error, `Dispose()` calls `Stop(pid)`.
- `Recorder`: opens the file with `FileMode.CreateNew` (throws if it exists) and `FileShare.Read`, UTF-8 without BOM, writes the header and a first `.meta.json` immediately. `Offer(sample)` writes a row when `sample.Time >= nextDue - 200 ms` and returns false when the requested duration is over (the clock starts at the first row). Every row is flushed. `Close()` rewrites the meta with the end time. Meta path is the CSV path without extension plus `.meta.json`.
- `MonitorService` public surface: `Profiles`, `IncludeGpu`, `RecordingEnded`, `LastPath`, `LastRows`, `Latest`, `Recording`, `RecRows`, `RecPath`, `RecStarted`, `History(seconds)`, `Retain()`, `Release()`, `StartRecording(path, everySeconds, minutes, perCore, gpu)`, `StopRecording(out rows)`, `Dispose()`. It keeps at most 900 samples.
- `MonitorService.Loop` per iteration: pick the period (1000 ms with a viewer, else the recorder's interval, minimum 1000) → XTU `GetValues()` → warm-up logic → every 5 s re-read PL1/PL2 and the active profile name through its own `XtuClient` → CPU counter, memory, power source, battery, optional NVML → under `gate`: store in `latest`/`history` and `rec.Offer` → fire `RecordingEnded` if the recorder finished → sleep in 50 ms steps to the next fixed slot (`nextAt += periodMs`; if more than one period behind it resets instead of bursting). The thread exits by itself when there is no viewer and no recorder, and `EnsureThread` starts a new one later.

## MonitorUi.cs, Tables.cs

- `SeriesRow` (a `Flat` control): set `Title, Unit, Current, Values[], Lo/Hi` (or `AutoHi`), `Ref` and `RefLabel` for the dashed reference line (PL1 on the power row), `Capacity` (samples across the plot), `SpanLabel`, `NowLabel`, `Hue`. Call `Invalidate()` after changing data.
- `Row` is the table's data record (copy of a profile plus `Active`, `Custom`, `Key` = hotkey text, `Tag` = the `Profile`).
- `ProfileTable`: header 30 px, rows 40 px, a "..." column 40 px wide. Events: `Activated` (left click or Enter on a row = apply) and `MenuRequested` (the dots or right click). `SetRows` keeps keyboard focus on the same profile name. Keyboard: Up/Down/Home/End move the focus row, Enter or Space applies it, Apps or Shift+F10 opens the row menu; the mouse wheel scrolls one row per notch. `RowPoint` and `TestClick` exist for the `--snapshot --click` self-test.
- `TabStrip`: `Items`, `SelectedIndex` (setting it raises `SelectedChanged` only when the value really changes), Left/Right arrows change the tab.

## Ui.cs

- `Theme.Init("auto"|"light"|"dark")` fills the static colours (`Ground, Raised, Line, Text, Sub, Ember, EmberDim, Track, Ok, Bad, Heat, InkBg, InkFg`) and `Dark`. "auto" reads `AppsUseLightTheme` from the registry once at start (no live switching). Fonts: `Body`, `Strong`, `Figure` use "Segoe UI Variable Text/Display" when installed, else "Segoe UI", and are cached.
- `Ui`: `InitScale()` (never below 1.0), `S(int|float)`, `Round(rect, radius)`, `Text(g, s, font, colour, rect, align)` (TextRenderer, single line, ellipsis), `Width`, `Dot`, `TitleBar(handle)` (dark title bar), `Snapshot(form, file, size[, warmupMs, beforeDraw])` (off-screen `DrawToBitmap`).
- `AppIcon.Load(size)`: from the embedded resource `XtuProfileSwitcher.app.ico`; falls back to the system icon.
- `Menus.Create()` / `Menus.Item(text, handler)`: themed context menu and item. Use these for any new menu.
- `Flat`: base for custom controls (double-buffered, themed ground, `FocusRing`). Derived: `Btn` (`BtnKind` Primary/Secondary/Quiet, height 34), `Switch` (`Checked`, `CheckedChanged`; `Set(bool)` changes it without raising the event), `Dropdown` (`Items`, `SelectedIndex`, `SelectionChanged`), `TopBar` (status text and colour, the EN·VI toggle raises `LanguageChosen`), `Readout` (the PL1/PL2 strip: set the public fields, then `Invalidate()`), `ProfileTable`, `TabStrip`, `SeriesRow`.
- `Osd.Flash(title, detail, error)`: one notice at a time (a new one closes the old), borderless, top-most, no activation, click-through, 2.6 s, bottom-centre of the primary screen. It is a normal top-most window, so it does not show over exclusive-fullscreen games.
- `DialogBase` (themed dialog with `Caption`, `Field`, `Button` helpers), `HotkeyDialog` (`Result`: null = unchanged, "" = cleared, otherwise the combo text), `ProfileDialog` (`Result` is a validated `Profile`). The profile dialog parses PL1, PL2 and the window as **integers** only, so a decimal such as 37.5 W cannot be created in the UI, although `settings.ini` and `Profile` accept decimals. Names: 1-40 characters, must not start with `@`, no tab or newline, unique ignoring case.

## Loc.cs

About 130 keys, each `A("key", "English", "Tiếng Việt")`. `Loc.T(key)` returns the raw key when it is missing (a visible bug, not an exception). `Loc.F(key, args)` is `string.Format` with the invariant culture, placeholders `{0}`. `Loc.Lang` is `"en"` or `"vi"` and is set once at start from `--lang` or `settings.ini`, then by the top-bar switch (`SetLanguage` → `ApplyTexts`, `RefreshAll`, `UpdateMonitorUi`). Keys are grouped by prefix: `status.`, `ro.` (readout), `tab.`, `mon.`, `rec.`, `auto.`, `su.`, `msg.`, `err.`, `val.`, `dlg.`, `tray.`, `power.`, `reason.`.

## Things that look wrong but are intended

- `Snapshot` (data) and `Ui.Snapshot` (render to PNG) share a word. They are unrelated.
- The Monitor tab shows nothing until about 4-7 s after it opens (warm-up).
- `ProfileTable` activates on click; there is no separate "Apply" button.
- Minimizing hides the window; there is no taskbar entry once it is hidden.
- A hotkey pressed while an apply is running does nothing (`busy`), by design.
- `--snapshot --click NAME` changes the real CPU limits.
- The manifest is `requireAdministrator`, so launching the exe always asks Windows to elevate; whether a UAC prompt appears depends on the machine's UAC setting. A script started from a normal shell cannot read or kill the running (elevated) copy.
