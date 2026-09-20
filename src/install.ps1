# Run from the extracted UnityRemix package. Installs one DLL selected for the game.
param([Parameter(Mandatory=$true)][string]$UnityPath, [switch]$ReplaceExistingBridge,
    [ValidateSet('Auto','BepInEx5Mono','BepInEx6Mono','BepInEx6IL2CPP','BepInEx5MonoLegacy32')][string]$Runtime = 'Auto')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'bepinex-tools.ps1')
. (Join-Path $PSScriptRoot 'native-runtime-tools.ps1')
. (Join-Path $PSScriptRoot 'legacy-runtime-tools.ps1')
$player = Get-UnityRemixPlayer $UnityPath
if ($player.Legacy) {
    $null = Get-UnityRemixLoaderRuntime $player $Runtime
    Install-UnityRemixLegacy -UnityPath $UnityPath -PackagePath $PSScriptRoot -ReplaceExistingBridge:$ReplaceExistingBridge
    return
}
$needsLoader = !(Test-Path -LiteralPath (Join-Path $player.GamePath 'BepInEx/core'))
if ($needsLoader) {
    $selected = Get-UnityRemixLoaderRuntime $player $Runtime
    # A package containing only Mono 5 should install the loader it was built for.
    if ($Runtime -eq 'Auto' -and $selected -eq 'BepInEx6Mono' -and
        !(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'runtimes/BepInEx6Mono/UnityRemix.dll')) -and
        (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'runtimes/BepInEx5Mono/UnityRemix.dll'))) { $selected = 'BepInEx5Mono' }
} else {
    $selected = (Get-UnityRemixLayout -UnityPath $UnityPath).Runtime
    if ($Runtime -ne 'Auto' -and $Runtime -ne $selected) { throw "Requested $Runtime but detected $selected." }
}
$dll = Join-Path $PSScriptRoot "runtimes/$selected/UnityRemix.dll"
if (!(Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Plugin build not found: $dll" }
# Fail before touching an existing install if plugin discovery would be ambiguous.
$existing = @(Get-ChildItem -LiteralPath (Join-Path $UnityPath 'BepInEx/plugins') -Recurse -File -Filter 'UnityRemix.dll' -ErrorAction SilentlyContinue)
if ($existing.Count -gt 1) { throw 'Multiple UnityRemix.dll installations found. Keep one before deploying.' }
Test-UnityRemixNativeBundle (Join-Path $PSScriptRoot 'native/win-x64')
if ($needsLoader) {
    $loader = Get-UnityRemixBepInEx -Runtime $selected -Architecture (Get-UnityRemixLoaderArchitecture $player)
    Install-UnityRemixBepInEx $UnityPath $loader
}
Install-UnityRemixNativeBundle -UnityPath $UnityPath -SourceDirectory (Join-Path $PSScriptRoot 'native/win-x64')
Install-UnityRemixPlugin -UnityPath $UnityPath -Runtime $selected -PluginPath $dll
