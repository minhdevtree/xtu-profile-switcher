# Builds XtuProfileSwitcher.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
# Intel's contract DLLs are referenced in place from the XTU install folder and are NOT copied.
$ErrorActionPreference = 'Stop'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$xtu = 'C:\Program Files\Intel\Intel(R) Extreme Tuning Utility\Drivers'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here 'XtuProfileSwitcher.exe'

if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
foreach ($d in 'XtuInterface.dll', 'XtuCommon.dll') {
    if (-not (Test-Path (Join-Path $xtu $d))) { throw "Missing $d in $xtu (is XTU installed?)" }
}

$manifest = Join-Path $here 'app.manifest'
$source = Join-Path $here 'Program.cs'
$icon = Join-Path $here 'app.ico'
if (-not (Test-Path $icon)) { & (Join-Path $here 'make-icon.ps1') }

# app.ico is used twice: as the .exe's file icon and as an embedded resource for the window/tray icon.
& $csc /nologo /target:winexe /platform:x64 /optimize+ /warn:4 `
    "/out:$out" `
    "/win32manifest:$manifest" `
    "/win32icon:$icon" `
    "/resource:$icon,XtuProfileSwitcher.app.ico" `
    /r:System.dll /r:System.Core.dll /r:System.Xml.dll /r:System.Drawing.dll `
    /r:System.Windows.Forms.dll /r:System.ServiceModel.dll /r:System.ServiceProcess.dll `
    "/r:$xtu\XtuInterface.dll" "/r:$xtu\XtuCommon.dll" `
    $source

if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
Write-Host "Built $out"
