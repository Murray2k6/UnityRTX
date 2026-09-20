$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../native-runtime-tools.ps1')
$root = Join-Path $PSScriptRoot ('../artifacts/native-install-tests-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'build/_output'
New-Item -ItemType Directory -Path (Join-Path $source 'usd/resources') -Force | Out-Null
foreach ($name in @('d3d9.dll','dependency.dll','crashpad_handler.exe','UnityRemix.Probe.exe','usd/resources/plugInfo.json')) {
    Set-Content -LiteralPath (Join-Path $source $name) -Value "fixture-$name"
}
$package = Join-Path $root 'package'
$receipt = Join-Path $source 'unity-remix-build.json'
@{ schema = 1; rendererSha256 = (Get-FileHash -LiteralPath (Join-Path $source 'd3d9.dll')).Hash } |
    ConvertTo-Json | Set-Content -LiteralPath $receipt
New-UnityRemixNativeBundle $source $package -ProbePath (Join-Path $source 'UnityRemix.Probe.exe')
if (!(Test-Path -LiteralPath (Join-Path $package 'unity-remix-build.json'))) { throw 'Build receipt was not packaged.' }
Set-Content -LiteralPath (Join-Path $source 'd3d9.dll') -Value 'renderer changed after build'
$rejected = $false
try { New-UnityRemixNativeBundle $source (Join-Path $root 'stale') -ProbePath (Join-Path $source 'UnityRemix.Probe.exe') }
catch { if ($_.Exception.Message -notlike '*does not match its build receipt*') { throw }; $rejected = $true }
if (!$rejected -or (Test-Path -LiteralPath (Join-Path $root 'stale'))) { throw 'Stale native output was packaged.' }
if (Test-Path -LiteralPath (Join-Path $package 'd3d9.dll')) { throw 'Private runtime was not renamed.' }
$game = Join-Path $root 'Game with spaces'
New-Item -ItemType Directory -Path $game -Force | Out-Null
$rootDll = Join-Path $game 'd3d9.dll'
Set-Content -LiteralPath $rootDll -Value 'keep this other Remix version'
$originalHash = (Get-FileHash -LiteralPath $rootDll).Hash
Install-UnityRemixNativeBundle $game $package
$installed = Join-Path $game 'BepInEx/UnityRemix/native/win-x64'
Test-UnityRemixNativeBundle $installed
if (!(Test-Path -LiteralPath (Join-Path $installed 'UnityRemix.Probe.exe'))) { throw 'Version probe was not installed.' }
if ((Get-FileHash -LiteralPath $rootDll).Hash -ne $originalHash) { throw 'Installer changed game-root DLL.' }
Install-UnityRemixNativeBundle $game $package
if (@(Get-ChildItem -LiteralPath (Split-Path $installed -Parent) -Directory -Filter 'backup-*').Count -ne 1) { throw 'Old runtime was not backed up.' }
$originalNative = (Get-FileHash -LiteralPath (Join-Path $installed 'UnityRemix.Native.dll')).Hash
Set-Content -LiteralPath (Join-Path $package 'UnityRemix.Native.dll') -Value 'tampered'
$caught = $false
try { Install-UnityRemixNativeBundle $game $package } catch {
    if ($_.Exception.Message -notlike '*missing or changed*') { throw }
    $caught = $true
}
if (!$caught) { throw 'Corrupt package was installed.' }
if ((Get-FileHash -LiteralPath (Join-Path $installed 'UnityRemix.Native.dll')).Hash -ne $originalNative) { throw 'Failed install altered existing runtime.' }
if ((Get-FileHash -LiteralPath $rootDll).Hash -ne $originalHash) { throw 'Game-root DLL changed during upgrade.' }
Write-Host 'PASS: complete bundle staging, private installation, root DLL preservation, backup and failed-upgrade validation.'
