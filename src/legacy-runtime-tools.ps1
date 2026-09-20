Set-StrictMode -Version Latest

function Test-UnityRemixLegacyPayload {
    param([Parameter(Mandatory)][string]$Path)
    $root = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    $seen = @{}
    foreach ($line in Get-Content -LiteralPath (Join-Path $root 'legacy-manifest.sha256')) {
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') { throw 'Malformed legacy manifest.' }
        $hash = $Matches[1]; $name = $Matches[2]
        $file = [IO.Path]::GetFullPath((Join-Path $root $name))
        if ([IO.Path]::IsPathRooted($name) -or $name.Contains(':') -or
            !$file.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($file)) {
            throw "Unsafe legacy manifest path: $name"
        }
        if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $hash) {
            throw "Legacy payload missing or changed: $name"
        }
        $seen[$file] = $true
    }
    foreach ($name in @('d3d9.dll','NvRemixBridge.exe','loader/winhttp.dll','loader/BepInEx/core/BepInEx.dll')) {
        if (!$seen.ContainsKey((Join-Path $root $name))) { throw "Legacy manifest missing $name" }
    }
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
        if ($file.Name -ne 'legacy-manifest.sha256' -and !$seen.ContainsKey($file.FullName)) { throw "Unlisted legacy payload: $($file.Name)" }
    }
    if ((Get-UnityRemixPeMachine (Join-Path $root 'd3d9.dll')) -ne 0x14c -or
        (Get-UnityRemixPeMachine (Join-Path $root 'loader/winhttp.dll')) -ne 0x14c -or
        (Get-UnityRemixPeMachine (Join-Path $root 'NvRemixBridge.exe')) -ne 0x8664) { throw 'Incorrect legacy bridge/loader architecture.' }
}

function New-UnityRemixLegacyPayload {
    param([Parameter(Mandatory)][string]$Destination)
    if (Test-Path -LiteralPath $Destination) { throw 'Legacy staging directory already exists.' }
    $source = Join-Path $PSScriptRoot 'artifacts/remix-native/source/bridge'
    $archive = Join-Path $PSScriptRoot 'artifacts/legacy-mono/BepInEx_win_x86_5.4.23.5.zip'
    if (!(Test-Path -LiteralPath $archive)) {
        . (Join-Path $PSScriptRoot 'bepinex-tools.ps1')
        $archive = (Get-UnityRemixBepInEx -Runtime BepInEx5MonoLegacy32 -Architecture x86).ArchivePath
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '37651C79E40D6F909572A4F461AC25350BB3EF8FE7FBD29F1AA8791A33B84C82') {
        throw 'BepInEx 5.4.23.5 x86 archive does not match its official release digest.'
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    # Extract the verified archive instead of trusting a previously expanded copy.
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $Destination 'loader')
    Copy-Item -LiteralPath (Join-Path $source '_Comp32UnityRelease/src/client/d3d9.dll') -Destination $Destination
    Copy-Item -LiteralPath (Join-Path $source '_Comp64UnityRelease/src/server/NvRemixBridge.exe') -Destination $Destination
    $root = (Resolve-Path -LiteralPath $Destination).Path
    $lines = @(foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse | Sort-Object FullName) {
        (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash + '  ' + $file.FullName.Substring($root.Length + 1).Replace('\','/')
    })
    Set-Content -LiteralPath (Join-Path $root 'legacy-manifest.sha256') -Value $lines -Encoding ASCII
    Test-UnityRemixLegacyPayload $root
}

function Install-UnityRemixLegacy {
    param([Parameter(Mandatory)][string]$UnityPath, [Parameter(Mandatory)][string]$PackagePath,
        [switch]$ReplaceExistingBridge)
    $player = Get-UnityRemixPlayer $UnityPath
    if (!$player.Legacy) { throw 'This installation requires a 32-bit Unity 4 Mono player.' }
    $game = $player.GamePath
    $payload = Join-Path $PackagePath 'legacy-runtime'
    $native = Join-Path $PackagePath 'native/win-x64'
    $plugin = Join-Path $PackagePath 'runtimes/BepInEx5MonoLegacy32/UnityRemix.dll'
    Test-UnityRemixLegacyPayload $payload
    Test-UnityRemixNativeBundle $native
    if ((Get-UnityRemixPeMachine $plugin) -ne 0x14c -or
        [Reflection.AssemblyName]::GetAssemblyName($plugin).Name -ne 'UnityRemix') { throw 'Invalid legacy plugin.' }
    $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $player.ExePath })
    if ($running.Count) { throw 'Close the game before installing the legacy bridge.' }
    if (Test-Path -LiteralPath (Join-Path $game 'BepInEx/core/BepInEx.Core.dll')) { throw 'Remove the incompatible BepInEx 6 installation first.' }
    $existing = @(Get-ChildItem -LiteralPath (Join-Path $game 'BepInEx/plugins') -Recurse -File -Filter UnityRemix.dll -ErrorAction SilentlyContinue)
    if ($existing.Count -gt 1) { throw 'Multiple UnityRemix.dll installations found.' }
    $previousManifest = Join-Path $game 'BepInEx/UnityRemix/legacy-installed.sha256'
    if (!(Test-Path -LiteralPath $previousManifest)) {
        foreach ($name in @('d3d9.dll','version.dll','winhttp.dll','.trex','doorstop_config.ini','BepInEx/core')) {
            if ($ReplaceExistingBridge -and $name -in @('d3d9.dll','.trex')) { continue }
            if (Test-Path -LiteralPath (Join-Path $game $name)) { throw "Existing $name needs review before installing a different loader/bridge." }
        }
    }
    $stage = Join-Path ([IO.Path]::GetTempPath()) ('UnityRemix-legacy-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $stage '.trex') -Force | Out-Null
    Copy-Item -Path (Join-Path $native '*') -Destination (Join-Path $stage '.trex') -Recurse
    Copy-Item -LiteralPath (Join-Path $native 'UnityRemix.Native.dll') -Destination (Join-Path $stage '.trex/d3d9.dll')
    # The native manifest describes the source bundle; installation gets its own manifest.
    Remove-Item -LiteralPath (Join-Path $stage '.trex/UnityRemix.Native.dll'),(Join-Path $stage '.trex/native-manifest.sha256'),(Join-Path $stage '.trex/UnityRemix.Probe.exe')
    Copy-Item -LiteralPath (Join-Path $payload 'NvRemixBridge.exe') -Destination (Join-Path $stage '.trex')
    Copy-Item -LiteralPath (Join-Path $payload 'd3d9.dll') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $payload 'loader/BepInEx') -Destination $stage -Recurse
    Copy-Item -LiteralPath (Join-Path $payload 'loader/winhttp.dll') -Destination (Join-Path $stage 'version.dll')
    Copy-Item -LiteralPath (Join-Path $payload 'loader/doorstop_config.ini') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $payload 'loader/.doorstop_version') -Destination $stage -Force
    $pluginRelative = if ($existing.Count) { $existing[0].FullName.Substring($game.Length + 1) } else { 'BepInEx/plugins/UnityRemix.dll' }
    New-Item -ItemType Directory -Path (Split-Path (Join-Path $stage $pluginRelative) -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $plugin -Destination (Join-Path $stage $pluginRelative)
    New-Item -ItemType Directory -Path (Join-Path $stage 'BepInEx/config') -Force | Out-Null
    if (!(Test-Path -LiteralPath (Join-Path $game 'BepInEx/config/BepInEx.cfg'))) {
        Set-Content -LiteralPath (Join-Path $stage 'BepInEx/config/BepInEx.cfg') -Encoding ASCII -Value @(
            '[Preloader.Entrypoint]','Assembly = UnityEngine.dll','Type = MonoBehaviour','Method = .cctor','',
            '[Chainloader]','HideManagerGameObject = true','', '[Logging.Console]','Enabled = false')
    }
    foreach ($relative in @('bridge.conf','.trex/bridge.conf')) {
        $config = Join-Path $game $relative
        $lines = @(if (Test-Path -LiteralPath $config) { Get-Content -LiteralPath $config | Where-Object { $_ -notmatch '^\s*(exposeRemixApi|client.hookMessagePump)\s*=' } })
        Set-Content -LiteralPath (Join-Path $stage $relative) -Encoding ASCII -Value ($lines + @('exposeRemixApi = True','client.hookMessagePump = True'))
    }
    $exeName = [IO.Path]::GetFileName($player.ExePath)
    if ($exeName -match '[%\r\n"]') { throw 'Unsupported executable name for the launcher.' }
    Set-Content -LiteralPath (Join-Path $stage 'Start-UnityRemix.cmd') -Encoding ASCII -Value @('@echo off','cd /d "%~dp0"',('start "" "' + $exeName + '" -force-d3d9 -vrmode none'))
    $backup = Join-Path $game ('BepInEx/UnityRemix/backups/' + [Guid]::NewGuid().ToString('N'))
    $files = @(Get-ChildItem -LiteralPath $stage -File -Recurse -Force)
    foreach ($file in $files) {
        $target = [IO.Path]::GetFullPath((Join-Path $game $file.FullName.Substring($stage.Length + 1)))
        if (!$target.StartsWith($game + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Installation escaped the game directory.' }
        $ancestor = $target
        while ($ancestor -and $ancestor.Length -ge $game.Length) {
            if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked installation path: $ancestor" }
            $ancestor = Split-Path $ancestor -Parent
        }
    }
    $installed = New-Object 'Collections.Generic.List[string]'
    $runtimeTarget = Join-Path $game '.trex'
    $runtimeBackup = Join-Path $backup '.trex'
    $failedRuntime = Join-Path $backup 'failed-runtime'
    foreach ($path in @($runtimeTarget,$runtimeBackup,$failedRuntime)) {
        if (![IO.Path]::GetFullPath($path).StartsWith($game + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime move escaped the game directory.' }
    }
    $movedRuntime = $false
    try {
        # Move the complete previous renderer aside, including dependencies that
        # are absent from the new bundle, so versions are never mixed.
        if (Test-Path -LiteralPath $runtimeTarget) {
            New-Item -ItemType Directory -Path $backup -Force | Out-Null
            Move-Item -LiteralPath $runtimeTarget -Destination $runtimeBackup
            $movedRuntime = $true
        }
        foreach ($file in $files) {
            $relative = $file.FullName.Substring($stage.Length + 1)
            $target = Join-Path $game $relative
            if (Test-Path -LiteralPath $target) {
                $saved = Join-Path $backup $relative
                New-Item -ItemType Directory -Path (Split-Path $saved -Parent) -Force | Out-Null
                Copy-Item -LiteralPath $target -Destination $saved
            }
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            $installed.Add($relative)
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
            if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Installation verification failed: $relative" }
        }
        $lines = @(foreach ($relative in $installed) { (Get-FileHash -LiteralPath (Join-Path $game $relative)).Hash + '  ' + $relative.Replace('\','/') })
        New-Item -ItemType Directory -Path (Split-Path $previousManifest -Parent) -Force | Out-Null
        Set-Content -LiteralPath $previousManifest -Value $lines -Encoding ASCII
    } catch {
        foreach ($relative in $installed) {
            if ($movedRuntime -and $relative.StartsWith('.trex\',[StringComparison]::OrdinalIgnoreCase)) { continue }
            $saved = Join-Path $backup $relative
            if (Test-Path -LiteralPath $saved) { Copy-Item -LiteralPath $saved -Destination (Join-Path $game $relative) -Force }
            else { Remove-Item -LiteralPath (Join-Path $game $relative) -Force -ErrorAction SilentlyContinue }
        }
        if ($movedRuntime) {
            if (Test-Path -LiteralPath $runtimeTarget) { Move-Item -LiteralPath $runtimeTarget -Destination $failedRuntime }
            Move-Item -LiteralPath $runtimeBackup -Destination $runtimeTarget
        }
        throw
    }
    Write-Host "Installed Unity 4 Mono x86 support: $game"
    if (Test-Path -LiteralPath $backup) { Write-Host "Previous files preserved: $backup" }
    Write-Host 'Game was not launched. Start-UnityRemix.cmd selects Direct3D 9 and non-VR mode.'
}
