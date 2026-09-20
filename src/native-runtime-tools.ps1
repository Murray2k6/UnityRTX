Set-StrictMode -Version Latest

function Test-UnityRemixNativeBundle {
    param([Parameter(Mandatory)][string]$Path)
    $root = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $manifest = Join-Path $root 'native-manifest.sha256'
    if (!(Test-Path -LiteralPath $manifest -PathType Leaf)) { throw "Native manifest not found: $manifest" }
    $seen = @{}
    foreach ($line in Get-Content -LiteralPath $manifest) {
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') { throw 'Malformed native manifest.' }
        $expected = $Matches[1]
        $name = $Matches[2]
        $file = [IO.Path]::GetFullPath((Join-Path $root $name))
        if ([IO.Path]::IsPathRooted($name) -or $name.Contains(':') -or
            !$file.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $seen.ContainsKey($file)) {
            throw "Invalid or duplicate native manifest path: $name"
        }
        if (!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $expected) {
            throw "Native file missing or changed: $name"
        }
        $seen[$file] = $true
    }
    if (!$seen.ContainsKey((Join-Path $root 'UnityRemix.Native.dll'))) { throw 'Native renderer is absent from manifest.' }
    if (!$seen.ContainsKey((Join-Path $root 'UnityRemix.Probe.exe'))) { throw 'Native API version probe is absent from manifest.' }
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Filter '*.dll') {
        if (!$seen.ContainsKey($file.FullName)) { throw "Unlisted native DLL: $($file.Name)" }
    }
}

function New-UnityRemixNativeBundle {
    param([Parameter(Mandatory)][string]$SourceDirectory, [Parameter(Mandatory)][string]$Destination,
        [string]$ProbePath = (Join-Path $PSScriptRoot 'artifacts/native-adapters/UnityRemix.Probe.exe'))
    $source = (Resolve-Path -LiteralPath $SourceDirectory).Path
    foreach ($required in @('d3d9.dll', 'usd', 'crashpad_handler.exe')) {
        if (!(Test-Path -LiteralPath (Join-Path $source $required))) { throw "Native runtime incomplete: missing $required" }
    }
    if (!(Test-Path -LiteralPath $ProbePath -PathType Leaf)) { throw 'Native version probe missing. Run native-adapters/build.ps1 first.' }
    $receipt = Join-Path $source 'unity-remix-build.json'
    if (Test-Path -LiteralPath $receipt) {
        $build = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
        if ($build.rendererSha256 -ne (Get-FileHash -LiteralPath (Join-Path $source 'd3d9.dll') -Algorithm SHA256).Hash) {
            throw 'Native renderer does not match its build receipt. Rebuild it before packaging.'
        }
    }
    if (Test-Path -LiteralPath $Destination) { throw "Native staging destination already exists: $Destination" }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Filter '*.dll') {
        $name = if ($file.Name -eq 'd3d9.dll') { 'UnityRemix.Native.dll' } else { $file.Name }
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $Destination $name)
    }
    Copy-Item -LiteralPath (Join-Path $source 'crashpad_handler.exe') -Destination $Destination
    Copy-Item -LiteralPath $ProbePath -Destination (Join-Path $Destination 'UnityRemix.Probe.exe')
    if (Test-Path -LiteralPath $receipt) { Copy-Item -LiteralPath $receipt -Destination $Destination }
    Copy-Item -LiteralPath (Join-Path $source 'usd') -Destination $Destination -Recurse
    # The native source distribution owns these notices, not the build directory.
    foreach ($name in @('LICENSE', 'LICENSE-MIT', 'ThirdPartyLicenses.txt')) {
        $notice = Join-Path (Split-Path $source -Parent) $name
        if (Test-Path -LiteralPath $notice -PathType Leaf) { Copy-Item -LiteralPath $notice -Destination $Destination }
    }
    $root = (Resolve-Path -LiteralPath $Destination).Path.TrimEnd('\', '/')
    $lines = @(foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse | Sort-Object FullName) {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        $name = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        "$hash  $name"
    })
    Set-Content -LiteralPath (Join-Path $root 'native-manifest.sha256') -Value $lines -Encoding ASCII
    Test-UnityRemixNativeBundle $root
}

function Install-UnityRemixNativeBundle {
    param([Parameter(Mandatory)][string]$UnityPath, [Parameter(Mandatory)][string]$SourceDirectory)
    Test-UnityRemixNativeBundle $SourceDirectory
    $game = (Resolve-Path -LiteralPath $UnityPath).Path
    $nativeRoot = [IO.Path]::GetFullPath((Join-Path $game 'BepInEx/UnityRemix/native'))
    $stage = Join-Path $nativeRoot ('staging-' + [Guid]::NewGuid().ToString('N'))
    $target = Join-Path $nativeRoot 'win-x64'
    $backup = Join-Path $nativeRoot ('backup-' + [Guid]::NewGuid().ToString('N'))
    # Verify every move remains within the explicitly selected installation directory.
    foreach ($path in @($stage, $target, $backup)) {
        if (![IO.Path]::GetFullPath($path).StartsWith($nativeRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Native installation path escaped its target directory.'
        }
    }
    foreach ($path in @($game, (Join-Path $game 'BepInEx'), (Join-Path $game 'BepInEx/UnityRemix'), $nativeRoot, $target)) {
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to replace a linked native installation directory: $path"
        }
    }
    New-Item -ItemType Directory -Path $nativeRoot -Force | Out-Null
    Copy-Item -LiteralPath $SourceDirectory -Destination $stage -Recurse
    Test-UnityRemixNativeBundle $stage
    $hadPrevious = Test-Path -LiteralPath $target
    if ($hadPrevious) { Move-Item -LiteralPath $target -Destination $backup }
    try { Move-Item -LiteralPath $stage -Destination $target }
    catch {
        if ($hadPrevious) { Move-Item -LiteralPath $backup -Destination $target }
        throw
    }
    Write-Host "Installed private Remix renderer: $target"
    if ($hadPrevious) { Write-Host "Previous private renderer preserved: $backup" }
}
