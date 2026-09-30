# Build I6Pull as a library plus the reflection harness, then run the harness. Windows only.
# Does not open the CAN adapter.
$ErrorActionPreference = "Stop"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path ([IO.Path]::GetTempPath()) "i6pull-safety"
New-Item -ItemType Directory -Force $out | Out-Null
& $csc /nologo /target:library /platform:anycpu /out:"$out\I6Pull.lib.dll" "$root\src\I6Pull.cs"
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& $csc /nologo /out:"$out\SafetyGates.exe" "$PSScriptRoot\SafetyGates.cs"
if ($LASTEXITCODE) { exit $LASTEXITCODE }
& "$out\SafetyGates.exe" "$out\I6Pull.lib.dll"
exit $LASTEXITCODE
