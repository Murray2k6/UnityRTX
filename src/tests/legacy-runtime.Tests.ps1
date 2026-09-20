param([Parameter(Mandatory)][string]$PackagePath, [string]$ReferenceGame = 'C:/Games/SDCWin32')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../runtime-tools.ps1')
. (Join-Path $PSScriptRoot '../native-runtime-tools.ps1')
. (Join-Path $PSScriptRoot '../legacy-runtime-tools.ps1')
$root = Join-Path $PSScriptRoot ('../artifacts/legacy-install-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$root = (Resolve-Path -LiteralPath $root).Path
$checks = 0
function Check($ok, $message) { if (!$ok) { throw $message }; $script:checks++ }
function Fails([scriptblock]$action, [string]$pattern) {
    try { & $action } catch { if ($_.Exception.Message -notlike $pattern) { throw }; $script:checks++; return }
    throw "Expected failure: $pattern"
}
$source = Get-UnityRemixPlayer $ReferenceGame
$exeName = [IO.Path]::GetFileName($source.ExePath)
$dataName = [IO.Path]::GetFileName($source.DataPath)
$managed = Join-Path $root "$dataName/Managed"
New-Item -ItemType Directory -Path $managed -Force | Out-Null
Copy-Item -LiteralPath $source.ExePath -Destination $root
foreach ($name in @('mscorlib.dll','UnityEngine.dll')) { Copy-Item -LiteralPath (Join-Path $source.ManagedPath $name) -Destination $managed }
$originalHash = (Get-FileHash -LiteralPath (Join-Path $root $exeName)).Hash
Check (Get-UnityRemixPlayer $root).Legacy 'Legacy player not detected'
Check ((Get-UnityRemixTargetFramework BepInEx5MonoLegacy32) -eq 'net35') 'Wrong framework'
Fails { Get-UnityRemixLayout $root } '*Install the matching BepInEx*'
Set-Content -LiteralPath (Join-Path $root 'd3d9.dll') -Value 'existing graphics mod'
Fails { Install-UnityRemixLegacy $root $PackagePath } '*Existing d3d9.dll*'
Check ((Get-Content -LiteralPath (Join-Path $root 'd3d9.dll')) -eq 'existing graphics mod') 'Overwrote a conflicting graphics mod'
Remove-Item -LiteralPath (Join-Path $root 'd3d9.dll')
New-Item -ItemType Directory -Path (Join-Path $root '.trex') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $root '.trex/old-dependency.dll') -Value 'previous runtime dependency'
Install-UnityRemixLegacy $root $PackagePath -ReplaceExistingBridge
Check (!(Test-Path -LiteralPath (Join-Path $root '.trex/old-dependency.dll'))) 'Old dependency mixed into new runtime'
Check (@(Get-ChildItem -LiteralPath (Join-Path $root 'BepInEx/UnityRemix/backups') -Filter old-dependency.dll -File -Recurse -Force).Count -eq 1) 'Previous complete runtime not preserved'
Check ((Get-UnityRemixLayout $root).Runtime -eq 'BepInEx5MonoLegacy32') 'Legacy loader detection failed'
Check ((Get-UnityRemixPeMachine (Join-Path $root 'version.dll')) -eq 0x14c) 'Wrong loader bitness'
Check ((Get-UnityRemixPeMachine (Join-Path $root '.trex/d3d9.dll')) -eq 0x8664) 'Wrong renderer bitness'
Check (@(Get-ChildItem -LiteralPath (Join-Path $root 'BepInEx/plugins') -Filter UnityRemix.dll -Recurse).Count -eq 1) 'Duplicate plugin'
Check ((Get-Content -LiteralPath (Join-Path $root '.trex/bridge.conf')) -contains 'exposeRemixApi = True') 'Server API not enabled'
Check ((Get-FileHash -LiteralPath (Join-Path $root $exeName)).Hash -eq $originalHash) 'Game executable changed'
Set-Content -LiteralPath (Join-Path $root 'BepInEx/config/BepInEx.cfg') -Value 'user configuration'
Set-Content -LiteralPath (Join-Path $root 'bridge.conf') -Value @('client.commandTimeout = 321','exposeRemixApi = False')
Install-UnityRemixLegacy $root $PackagePath
Check ((Get-Content -LiteralPath (Join-Path $root 'BepInEx/config/BepInEx.cfg')) -eq 'user configuration') 'Update replaced user configuration'
Check ((Get-Content -LiteralPath (Join-Path $root 'bridge.conf')) -contains 'client.commandTimeout = 321') 'Update replaced unrelated bridge settings'
Check (@(Get-ChildItem -LiteralPath (Join-Path $root 'BepInEx/UnityRemix/backups') -File -Recurse).Count -gt 0) 'Update did not back up overwritten files'
$dll = Join-Path $root 'd3d9.dll'
$stream = [IO.File]::Open((Join-Path $root $exeName), 'Open', 'ReadWrite')
$reader = New-Object IO.BinaryReader($stream)
$stream.Position = 0x3c; $offset = $reader.ReadInt32(); $stream.Position = $offset + 4
$stream.WriteByte(0x64); $stream.WriteByte(0x86); $reader.Dispose(); $stream.Dispose()
Fails { Get-UnityRemixPlayer $root } '*32-bit Unity 4*'
Write-Host "PASS: $checks legacy detection, install, conflict, architecture, and backup checks. Fixtures: $root"
