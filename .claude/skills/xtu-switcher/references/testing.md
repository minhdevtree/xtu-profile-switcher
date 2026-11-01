# Build, run and test

All commands are PowerShell, run from the repo folder. The app is elevated, and on the author's machine UAC
auto-elevates for the admin account (`ConsentPromptBehaviorAdmin=0`), so `Start-Process -Verb RunAs` does not
prompt. On another machine expect a UAC prompt.

## Build

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1     # prints "Built ...\XtuProfileSwitcher.exe"
```

Needs XTU installed (the script refuses to build without the two Intel DLLs). Compiler warnings are level 4;
keep the build warning-free.

If it fails with `CS0016 ... being used by another process`, a copy is running. It is elevated, so stop it
from an elevated shell:

```powershell
Start-Process powershell -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile','-Command','Get-Process XtuProfileSwitcher -EA SilentlyContinue | Stop-Process -Force'
```

Restart it hidden in the tray: `Start-Process .\XtuProfileSwitcher.exe -Verb RunAs -ArgumentList '--tray'`
(without `--tray` the window opens).

## Command-line checks (no window; always pass `--out`, the exe has no console)

```powershell
$exe = "$PWD\XtuProfileSwitcher.exe"; $out = "$env:TEMP\xtu-cli.txt"
Start-Process $exe -Verb RunAs -Wait -ArgumentList '--status','--out',"`"$out`""; Get-Content $out
```

- `--status` / `--list`: service state, connection, active PL1/PL2/window, matching profile, restore-after-reboot, Fast Startup, every profile (`<== active`).
- `--apply "NAME"`: really changes the CPU limits. Exit 0 ok, 1 failed, 2 profile not found. Restore the user's previous profile afterwards (check `--status` first and note it; the user's usual one is `PL55-60W`).
- `--startup on|off`: creates/removes the scheduled task.
- `--log FILE.csv --seconds 15 --every 1 [--no-percore] [--no-gpu]`: headless recording; exit 2 if no duration.

## Off-screen snapshots (visual checks and README screenshots)

```powershell
Start-Process $exe -Verb RunAs -Wait -ArgumentList '--snapshot',"`"$PWD\docs\window-dark.png`"",'--theme','dark'
```

Options: `--tab profiles|monitor|settings`, `--lang vi`, `--theme dark|light`, `--offline` (draws the "can't reach XTU" state), `--dialog hotkey|profile`, `--record` (start a demo recording into `%TEMP%\xtu-preview-*.csv`), `--warmup SECONDS` (wait for live data before drawing the Monitor tab; default 7, use 40+ for a filled chart), `--click NAME` (clicks that profile row exactly as a user would, which **really applies it**: a click-to-apply self-test).
The form is drawn into a bitmap off-screen (`DrawToBitmap`); nothing appears on screen and the real screen is never captured. After monitor snapshots delete `%TEMP%\xtu-preview-*`.
README screenshots: `docs/window-dark.png`, `window-light.png`, `settings-dark.png`, `monitor-dark.png` (640x700).

## Validate a recording

```powershell
$rows = Import-Csv $csv
"rows=$($rows.Count) cols=$(($rows[0].PSObject.Properties | Measure-Object).Count)"   # 68 columns with per-core + GPU
$t = $rows | % { [datetime]$_.timestamp }; 1..($t.Count-1) | % { ($t[$_]-$t[$_-1]).TotalSeconds } | Measure-Object -Min -Max -Average
Test-Path ([IO.Path]::ChangeExtension($csv,$null).TrimEnd('.') + '.meta.json')
```

Expect: first `elapsed_s` 0, gaps ≈ the interval (1.00 s ± 0.02), `status` = `ok`, `profile` and `pl1_w` filled,
`meta.json` next to the CSV (it is `<csvname-without-extension>.meta.json`, not `<csvname>.csv.meta.json`).

## Regression checklist after a change

1. Build is clean.
2. `--status` shows connected and the right active profile.
3. If apply code changed: `--apply` a profile, `--status`, then restore the previous one; also `--apply` the already-active one (expect "Already in use").
4. If UI changed: snapshots in dark and light, `--lang vi`, `--offline`, each tab; look for clipped text, overlaps at 640 px, disabled states.
5. If recording changed: a 15 s `--log` run validated as above, plus a timed stop (`--seconds` shorter than the wait) and `Stop` path.
6. Run the missing-`Loc`-key scan from `SKILL.md`.
7. Tray/hotkeys/mouse behaviour cannot be scripted reliably: say so, and ask the user to try (hotkeys `Ctrl+Alt+F1/F2`, tray left-click, tray menu, Start/Stop recording).

## Scratch tools

Helper scripts used during development (probes, SendKeys, query tools) lived in the agent scratchpad and are not in the repo. Recreate small ones as needed; keep any new reusable tool in a clearly named folder and mention it here.
