# XTU Profile Switcher

Small tray/window tool for Intel Extreme Tuning Utility (XTU) 7.14.x. Written and tested on an
Acer Predator Triton 500 SE (PT516-51s, i7-11800H, XTU 7.14.2.135, Windows 11).

> Unofficial. Not affiliated with or endorsed by Intel. It uses an internal, undocumented XTU
> interface and changes CPU power limits: use at your own risk, and test with a stress test first.
> No Intel files are included; you need XTU installed.

- shows whether the `XTU3SERVICE` service is running and reachable
- shows the PL1 / PL2 / time-window values the CPU really has right now (read from hardware via the service)
- works out which saved XTU profile those values correspond to (XTU does not store "active profile")
- switches profiles (only PL1, PL2, PL2 enable and the time window are changed)

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Uses `csc.exe` from .NET Framework 4.x. Intel's `XtuInterface.dll` / `XtuCommon.dll` are referenced from
the XTU install folder and loaded from there at run time; they are not copied here.

## Run

Double-click `XtuProfileSwitcher.exe`. It needs administrator rights (the XTU pipe only accepts admins).
Closing the window hides it to the tray; use the tray menu to switch profiles or exit.

## Command line (no window)

```text
XtuProfileSwitcher.exe --status [--out FILE]
XtuProfileSwitcher.exe --list   [--out FILE]
XtuProfileSwitcher.exe --apply "PL55-60W" [--out FILE]
```

Exit codes: 0 ok, 1 failed, 2 profile not found. A log is kept in
`%LOCALAPPDATA%\XtuProfileSwitcher\app.log`.

## Safety

- Only controls 47 (PL2), 48 (PL1), 49 (PL2 enable) and 66 (time window) are written.
- Values are checked before sending: PL1 15-120 W, PL1 <= PL2 <= 150 W, window 1-128 s, and the value
  must be in XTU's own supported list. Read-only or disabled controls are refused.
- Sequence matches XTU's own: DiscardChanges -> ProposeChange -> ApplyChanges, then a read-back.

## Caveats

- Talks to an internal, unsupported XTU interface; an XTU update may break it.
- The service caches "active" values and can report 0 until told to re-read hardware, so the tool calls
  `RefreshActiveValuesFromHardwareAccess` before each read.
- Profiles are read from `C:\ProgramData\Intel\Intel Extreme Tuning Utility\Profiles.xml`.
  "Default (BIOS)" is built from the service's own default values.
