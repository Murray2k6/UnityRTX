# Prepare references only; compilation and deployment are separate operations.
param(
    [Parameter(Mandatory=$true)][string]$UnityPath,
    [ValidateSet('Auto','BepInEx5Mono','BepInEx6Mono','BepInEx6IL2CPP','BepInEx5MonoLegacy32')][string]$Runtime = 'Auto',
    # Game root or extracted loader root containing BepInEx/core.
    [string]$LoaderPath = '',
    [switch]$PassThru
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'bepinex-tools.ps1')
$player = Get-UnityRemixPlayer -UnityPath $UnityPath
if (!$LoaderPath -and $player.Legacy -and !(Test-Path -LiteralPath (Join-Path $player.GamePath 'BepInEx/core'))) {
    $bundledLoader = Join-Path $PSScriptRoot 'artifacts/legacy-mono/loader-x86'
    if (Test-Path -LiteralPath (Join-Path $bundledLoader 'BepInEx/core/BepInEx.dll')) { $LoaderPath = $bundledLoader }
}
$download = $null
if (!$LoaderPath -and !(Test-Path -LiteralPath (Join-Path $player.GamePath 'BepInEx/core'))) {
    $selected = Get-UnityRemixLoaderRuntime $player $Runtime
    $download = Get-UnityRemixBepInEx -Runtime $selected -Architecture (Get-UnityRemixLoaderArchitecture $player)
    $LoaderPath = $download.LoaderPath
}
$layout = Get-UnityRemixLayout -UnityPath $UnityPath -LoaderPath $LoaderPath
if ($Runtime -ne 'Auto' -and $Runtime -ne $layout.Runtime) {
    throw "Requested $Runtime but detected $($layout.Runtime)."
}
$Runtime = $layout.Runtime
$required = @('UnityEngine.CoreModule.dll')
$loaderNames = Get-UnityRemixLoaderReferenceNames $Runtime
if ($Runtime -eq 'BepInEx5MonoLegacy32') {
    $required = @('mscorlib.dll','System.dll','System.Core.dll','UnityEngine.dll')
} elseif ($Runtime -eq 'BepInEx6IL2CPP') {
    $required += 'Il2Cppmscorlib.dll'
}
foreach ($name in $required) {
    if (!(Test-Path -LiteralPath (Join-Path $layout.ManagedPath $name))) {
        $hint = if ($Runtime -eq 'BepInEx6IL2CPP') { ' For IL2CPP, run fetch-bepinex.ps1 -UnityPath GAME -Install if the loader is missing, then launch the game once to generate interop assemblies.' }
            else { ' Use the original assemblies from this Unity player; do not substitute a different framework version.' }
        throw "Missing $name in $($layout.ManagedPath).$hint"
    }
}
$files = @(foreach ($name in $loaderNames) {
    Get-Item -LiteralPath (Join-Path $layout.CorePath $name) -ErrorAction Stop
})
if ($Runtime -eq 'BepInEx5MonoLegacy32') {
    # The legacy compiler uses /nostdlib and needs the player's exact CLR 2 profile.
    # UnityEngine.UI is discovered at runtime and must not become a hard reference.
    $files += @(foreach ($name in $required) { Get-Item -LiteralPath (Join-Path $layout.ManagedPath $name) })
} else {
    $files += @(Get-ChildItem -LiteralPath $layout.ManagedPath -File | Where-Object {
        $_.Name -like 'UnityEngine*.dll' -or $_.Name -eq 'Unity.Collections.dll' -or
        ($Runtime -eq 'BepInEx6IL2CPP' -and ($_.Name -eq 'Il2Cppmscorlib.dll' -or $_.Name -like 'Il2CppSystem*.dll'))
    })
}
# Validate before changing references; this also rejects incomplete/placeholder DLLs.
foreach ($file in $files) {
    try { $identity = [Reflection.AssemblyName]::GetAssemblyName($file.FullName) }
    catch { throw "Invalid reference assembly: $($file.FullName). $($_.Exception.Message)" }
    if ($Runtime -eq 'BepInEx5MonoLegacy32' -and
        (($file.Name -in @('mscorlib.dll','System.dll') -and $identity.Version.Major -ne 2) -or
         ($file.Name -eq 'System.Core.dll' -and $identity.Version.Major -notin @(2,3)))) {
        throw "Legacy Unity 4 requires the player's CLR 2 framework assemblies: $($file.FullName) is $($identity.Version)."
    }
}
$lib = Join-Path $PSScriptRoot "lib/$Runtime"
foreach ($file in $files) {
    if ($file.FullName.StartsWith($lib + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Reference sources must be separate from the selected lib directory.'
    }
}
New-Item -ItemType Directory -Path $lib -Force | Out-Null
# Only remove copied reference files inside this specific runtime directory.
Get-ChildItem -LiteralPath $lib -File -Filter '*.dll' | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }
foreach ($file in $files) { Copy-Item -LiteralPath $file.FullName -Destination $lib -Force }
Write-Host "Prepared $Runtime references in $lib"
Write-Host "Engine references: $($layout.ManagedPath)"
Write-Host "Loader references: $($layout.CorePath)"
if ($PassThru) { $layout }
