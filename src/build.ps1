# Build one runtime, or all prepared runtime reference sets.
param(
    [string]$UnityPath = '',
    [string]$LoaderPath = '',
    [ValidateSet('Auto','All','BepInEx5Mono','BepInEx6Mono','BepInEx6IL2CPP','BepInEx5MonoLegacy32')][string]$Runtime = 'Auto',
    [switch]$Deploy,
    [switch]$Package,
    [string]$NativeRuntimePath = (Join-Path $PSScriptRoot 'artifacts/remix-native/source/_output'),
    [switch]$SkipNativeBuild,
    [string]$PythonPath = '',
    [ValidateRange(1,64)][int]$Jobs = 2
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'bepinex-tools.ps1')
. (Join-Path $PSScriptRoot 'native-runtime-tools.ps1')
. (Join-Path $PSScriptRoot 'legacy-runtime-tools.ps1')
if ($Deploy -and !$UnityPath) { throw '-Deploy requires -UnityPath.' }
if ($LoaderPath -and !$UnityPath) { throw '-LoaderPath requires -UnityPath.' }
if ($Runtime -eq 'All' -and ($Deploy -or $UnityPath)) { throw 'Use -Runtime All with prepared references and no -Deploy/-UnityPath.' }
if ($UnityPath) {
    $layout = & (Join-Path $PSScriptRoot 'setup-references.ps1') -UnityPath $UnityPath -Runtime $Runtime -LoaderPath $LoaderPath -PassThru
    $Runtime = $layout.Runtime
    if ($Deploy -and (Test-Path -LiteralPath (Join-Path $UnityPath 'BepInEx/core')) -and
        (Get-UnityRemixLayout $UnityPath).Runtime -ne $Runtime) { throw 'The selected reference loader does not match the installed game loader.' }
} elseif ($Runtime -eq 'Auto') { $Runtime = 'All' }
$runtimes = if ($Runtime -eq 'All') { @('BepInEx5Mono','BepInEx6Mono','BepInEx6IL2CPP','BepInEx5MonoLegacy32') } else { @($Runtime) }
foreach ($selected in $runtimes) {
    $references = Join-Path $PSScriptRoot "lib/$selected"
    Initialize-UnityRemixLoaderReferences $selected $references
    $engineNames = if ($selected -eq 'BepInEx5MonoLegacy32') { @('mscorlib.dll','System.dll','System.Core.dll','UnityEngine.dll') }
        elseif ($selected -eq 'BepInEx6IL2CPP') { @('UnityEngine.CoreModule.dll','Il2Cppmscorlib.dll') }
        else { @('UnityEngine.CoreModule.dll') }
    foreach ($name in $engineNames) {
        if (!(Test-Path -LiteralPath (Join-Path $references $name))) { throw "Missing $selected game reference $name. Run setup-references.ps1 -UnityPath GAME -Runtime $selected." }
    }
}
if ($Package -or $Deploy) {
    if (!$SkipNativeBuild) {
        & (Join-Path $PSScriptRoot 'build-native.ps1') -SourcePath (Split-Path ([IO.Path]::GetFullPath($NativeRuntimePath)) -Parent) -OutputPath $NativeRuntimePath -PythonPath $PythonPath -Jobs $Jobs
        if ($runtimes -contains 'BepInEx5MonoLegacy32') {
            & (Join-Path $PSScriptRoot 'native-adapters/build-legacy-bridge.ps1') -Architecture x86 -PythonPath $PythonPath -Jobs $Jobs
            & (Join-Path $PSScriptRoot 'native-adapters/build-legacy-bridge.ps1') -Architecture x64 -PythonPath $PythonPath -Jobs $Jobs
        }
    }
    & (Join-Path $PSScriptRoot 'native-adapters/build.ps1')
}
$gitHash = 'unknown'
$gitCommand = Get-Command git -ErrorAction SilentlyContinue
if ($gitCommand) {
    try {
        $candidate = & git -C $PSScriptRoot rev-parse --short HEAD 2>$null
        if ($LASTEXITCODE -eq 0 -and $candidate -match '^[0-9a-f]+$') { $gitHash = $candidate }
    } catch { $gitHash = 'unknown' }
}
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'BuildInfo.cs') -Value "namespace UnityRemix { public static class BuildInfo { public const string GitHash = `"$gitHash`"; } }"
foreach ($selected in $runtimes) {
    if ($selected -eq 'BepInEx5MonoLegacy32') { & (Join-Path $PSScriptRoot 'legacy/build.ps1') }
    else {
        & dotnet build (Join-Path $PSScriptRoot 'UltrakillRemix.csproj') --configuration Release "-p:RemixRuntime=$selected"
        if ($LASTEXITCODE -ne 0) { throw "Build failed for $selected." }
    }
    $framework = Get-UnityRemixTargetFramework $selected
    $dll = Join-Path $PSScriptRoot "bin/Release/$selected/$framework/UnityRemix.dll"
    if ($Deploy) {
        if ($selected -eq 'BepInEx5MonoLegacy32') {
            $legacyStage = Join-Path $PSScriptRoot ('artifacts/legacy-deploy-' + [Guid]::NewGuid().ToString('N'))
            $pluginStage = Join-Path $legacyStage "runtimes/$selected"
            New-Item -ItemType Directory -Path $pluginStage -Force | Out-Null
            Copy-Item -LiteralPath $dll -Destination $pluginStage
            New-UnityRemixNativeBundle -SourceDirectory $NativeRuntimePath -Destination (Join-Path $legacyStage 'native/win-x64')
            New-UnityRemixLegacyPayload -Destination (Join-Path $legacyStage 'legacy-runtime')
            Install-UnityRemixLegacy -UnityPath $UnityPath -PackagePath $legacyStage
            continue
        }
        $nativeStage = Join-Path $PSScriptRoot ('artifacts/deploy-native-' + [Guid]::NewGuid().ToString('N'))
        New-UnityRemixNativeBundle -SourceDirectory $NativeRuntimePath -Destination $nativeStage
        if (!(Test-Path -LiteralPath (Join-Path $UnityPath 'BepInEx/core'))) {
            $loader = [pscustomobject]@{ LoaderPath = (Split-Path (Split-Path $layout.CorePath -Parent) -Parent); Runtime = $selected; Name = $selected }
            Install-UnityRemixBepInEx $UnityPath $loader
        }
        Install-UnityRemixNativeBundle -UnityPath $UnityPath -SourceDirectory $nativeStage
        Install-UnityRemixPlugin -UnityPath $UnityPath -Runtime $selected -PluginPath $dll
    }
    Write-Host "Built $dll"
}
if ($Package) {
    # Stage only builds from this invocation; never silently package stale variants.
    $packageRoot = Join-Path $PSScriptRoot ('artifacts/package-' + [Guid]::NewGuid().ToString('N'))
    foreach ($selected in $runtimes) {
        $framework = Get-UnityRemixTargetFramework $selected
        $destination = Join-Path $packageRoot "runtimes/$selected"
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "bin/Release/$selected/$framework/UnityRemix.dll") -Destination $destination
    }
    New-UnityRemixNativeBundle -SourceDirectory $NativeRuntimePath -Destination (Join-Path $packageRoot 'native/win-x64')
    if ($runtimes -contains 'BepInEx5MonoLegacy32') { New-UnityRemixLegacyPayload -Destination (Join-Path $packageRoot 'legacy-runtime') }
    foreach ($file in @('install.ps1','fetch-bepinex.ps1','bepinex-tools.ps1','runtime-tools.ps1','native-runtime-tools.ps1','legacy-runtime-tools.ps1','COMPATIBILITY.md')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $packageRoot
    }
    foreach ($folder in @('native-patches', 'profiles')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $folder) -Destination $packageRoot -Recurse
    }
    $archive = Join-Path $PSScriptRoot 'artifacts/UnityRemix.zip'
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $archive -Force
    Write-Host "Package: $archive"
}
