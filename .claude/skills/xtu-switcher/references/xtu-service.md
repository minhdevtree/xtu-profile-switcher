# XTU service facts (Intel Extreme Tuning Utility 7.14.2.135)

Everything here was learned by reading Intel's contract assemblies and probing the live service. It is an
internal, undocumented interface; an XTU update may change it.

## Where things are

| Thing | Path / name |
| --- | --- |
| Service | `XTU3SERVICE` (Windows service) |
| Contract DLLs | `C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Drivers\XtuInterface.dll`, `XtuCommon.dll` (namespaces `IronCity.Core`, `IronCity.Common`) |
| Service exe (version source) | `...\Drivers\XtuService.exe` |
| Saved profiles | `C:\ProgramData\Intel\Intel Extreme Tuning Utility\Profiles.xml` (`//DatabaseProfile` → `Name`, `ControlSettings/*/Id`, `Value`) |
| XTU UI settings | `...\uisettings.xml` (`HotkeyPairingOn`), hotkeys in `...\xtu.hotkey.settings` |
| XTU's own monitor logs | `...\Monitor Logs\` |

## Named pipes (WCF `NetNamedPipeBinding`, admin only)

`net.pipe://localhost/ExtremeTuningUtility/Tuning`, `.../Monitoring`, `.../ServiceInfo`, `.../Event`.

- `IXtuTuning` (Tuning): `Connect()`, `GetControl(id)` → `XtuTuningControl` (`ActiveValue`, `DefaultValue`, `SupportedValues`, `ReadOnly`, `Enabled`), `RefreshActiveValuesFromHardwareAccess()`, `ProposeChange(list, token, out delta, out requiresReboot, bool)`, `ApplyChanges(bool, int, token)`, `DiscardChanges(token, out list)`, `GetDoRestoreUserValuesOnBoot()`. Token: `IronCity.Common.SdkClientToken.Default`.
- `IXtuMonitoring` (Monitoring): `Connect(pid)`, `Start(pid)`, `GetValues()` → `Dictionary<uint, decimal>`, `GetMonitorIds()`, `Stop(pid)`, `GetPollingPeriod()`. A client registers with its own process id and must `Stop(pid)` on exit. The polling period is shared with XTU's own window, so do not change it.
- Raise WCF quotas (64 MB message size/buffer/array/string, `MaxItemsInObjectGraph = int.MaxValue`) or replies fail with "maximum message size exceeded".
- `GetTuningControlByID` returns 0 on this build; `GetControl(id).ActiveValue` after `RefreshActiveValuesFromHardwareAccess()` is correct.

To explore further without XTU's UI, load the DLLs by reflection in PowerShell (`[Reflection.Assembly]::LoadFrom(path).GetTypes()`) or decompile them. The `morluto/rea` reverse-engineering tool was used for this once. `IronCity.Common.ControlIdHelper` and `MonitorIdHelper` list the ids.

## Tuning control ids

| Id | Meaning | Used |
| --- | --- | --- |
| 47 | PL2, Turbo Boost Short Power Max (`TurboPackageTdpShort`) | written |
| 48 | PL1, Turbo Boost Power Max (`TurboPackageTdpExtended`) | written |
| 49 | PL2 enable (`TurboPackageTdpShortEnable`) | written |
| 66 | Turbo time window (`TurboExtendedTimeWindow`), seconds | written |

On the Triton 500 SE most other controls are read-only/locked by the OEM firmware; the power limits are the ones that can change. BIOS defaults: PL1 90 W, PL2 115 W, window 56 s.

## Monitor ids (`Mon` in Monitor.cs)

| Id | Meaning |
| --- | --- |
| 1000 / 1001 / 1002 | package / core / graphics power (W) |
| 1003 | VR throttle |
| 1010 | max P-core frequency |
| 7–14 | per-core temperature (core 0–7) |
| 2000+i / 3000+i / 4000+i | core i frequency / voltage / utilization |
| 2701131777 | package temperature |
| 2768240653 | graphics temperature |
| 3 / 22 / 23 | CPU (thermal) / power / current throttle indicators (0–100) |
| 4 | active cores |
| 6 | graphics frequency |
| 15 / 17 / 20 | reference clock / memory speed / uncore |
| 2768240654 / 2717909008 / 2768240652 | cache / system agent / graphics voltage |

Right after `Start(pid)` the service returns the values it measured last time; they only start moving after a moment. `MonitorService` discards those samples (the "warm-up").

## Persistence: why limits sometimes went back to default

- XTU's "Restore tuning after reboot" (on by default; `GetDoRestoreUserValuesOnBoot`) saves the user values when the **service stops** and re-applies them in `ApplicationPersistence.Initialize` when the **service starts**.
- With Windows **Fast Startup** on, "Shut down" hibernates the kernel: the service is neither stopped nor started, so nothing is saved or restored and the firmware defaults come back. "Restart" is unaffected. Fix applied on the user's machine: Fast Startup off (`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power\HiberbootEnabled=0`).
- Sleep/resume and unplugging can also let firmware reset limits; the tool re-applies after resume when auto-switch is on.
- XTU's own hotkey profile pairing uses a global low-level keyboard hook in its UI process, not `RegisterHotKey`.

## Battery and GPU readings (not from XTU)

- Battery rates: WMI `root\WMI`, `BatteryStatus` (`DischargeRate`, `ChargeRate`, mW).
- NVIDIA GPU: `nvml.dll` from the driver (temperature, power, utilization, graphics/memory clock). All optional; missing DLL means empty cells.
- CPU usage: performance counter `Processor Information\% Processor Utility` (falls back to `Processor\% Processor Time`); memory via `GlobalMemoryStatusEx`.
