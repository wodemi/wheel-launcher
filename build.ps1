# Build WheelLauncher.exe with the .NET Framework 4.8 csc.exe (no Visual Studio needed).
# Usage: powershell -ExecutionPolicy Bypass -File .\build.ps1

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

$cscRoot = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc     = Join-Path $cscRoot 'csc.exe'
$wpf     = Join-Path $cscRoot 'WPF'

if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

$refs = @(
    "/r:$wpf\PresentationFramework.dll",
    "/r:$wpf\PresentationCore.dll",
    "/r:$wpf\WindowsBase.dll",
    "/r:$cscRoot\System.Xaml.dll",
    "/r:$cscRoot\System.Drawing.dll",
    "/r:$cscRoot\System.Windows.Forms.dll",
    "/r:$cscRoot\System.Core.dll"
)

$out = Join-Path $here 'WheelLauncher.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ "/out:$out" "/win32icon:$here\wheel.ico" $refs (Join-Path $here 'WheelLauncher.cs')
if ($LASTEXITCODE -ne 0) { throw "build failed, exit code $LASTEXITCODE" }

$size = (Get-Item $out).Length
Write-Host "built: $out  ($size bytes)" -ForegroundColor Green
