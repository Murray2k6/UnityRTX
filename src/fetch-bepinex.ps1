param(
    [ValidateSet('Auto','All','BepInEx5Mono','BepInEx6Mono','BepInEx6IL2CPP','BepInEx5MonoLegacy32')][string]$Runtime = 'Auto',
    [string]$UnityPath = '', [ValidateSet('x86','x64')][string]$Architecture = 'x64',
    [switch]$Refresh, [switch]$Install
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'bepinex-tools.ps1')
if ($Install -and (!$UnityPath -or $Runtime -eq 'All')) { throw '-Install requires one -UnityPath.' }
if ($UnityPath) {
    if ($Runtime -eq 'All') { throw 'Select one runtime for -UnityPath.' }
    $player = Get-UnityRemixPlayer $UnityPath
    if ($Runtime -eq 'Auto' -and (Test-Path -LiteralPath (Join-Path $player.GamePath 'BepInEx/core'))) {
        $Runtime = (Get-UnityRemixLayout $UnityPath).Runtime
    }
    $Runtime = Get-UnityRemixLoaderRuntime $player $Runtime
    $Architecture = Get-UnityRemixLoaderArchitecture $player
} elseif ($Runtime -eq 'Auto') { $Runtime = 'All' }
$runtimes = if ($Runtime -eq 'All') { @('BepInEx5Mono','BepInEx6Mono','BepInEx6IL2CPP','BepInEx5MonoLegacy32') } else { @($Runtime) }
foreach ($selected in $runtimes) {
    $loader = Get-UnityRemixBepInEx -Runtime $selected -Architecture $Architecture -Refresh:$Refresh
    if ($Install) { Install-UnityRemixBepInEx $UnityPath $loader }
    Write-Host "$selected references: $($loader.LoaderPath)"
}
