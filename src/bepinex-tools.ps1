Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'runtime-tools.ps1')

function Get-UnityRemixLoaderRuntime {
    param([Parameter(Mandatory)]$Player, [string]$Runtime = 'Auto')
    $il2cpp = (Test-Path -LiteralPath (Join-Path $Player.DataPath 'il2cpp_data')) -or
        (Test-Path -LiteralPath (Join-Path $Player.GamePath 'GameAssembly.dll'))
    if ($Runtime -eq 'Auto') {
        if ($Player.Legacy) { return 'BepInEx5MonoLegacy32' }
        if ($il2cpp) { return 'BepInEx6IL2CPP' }
        return 'BepInEx6Mono'
    }
    if (($il2cpp -ne ($Runtime -eq 'BepInEx6IL2CPP')) -or ($Player.Legacy -ne ($Runtime -eq 'BepInEx5MonoLegacy32'))) {
        throw "Requested $Runtime does not match this Unity player's backend/framework."
    }
    return $Runtime
}

function Get-UnityRemixLoaderArchitecture {
    param([Parameter(Mandatory)]$Player)
    switch ($Player.Machine) { 0x14c { 'x86' }; 0x8664 { 'x64' }; default { throw 'Cannot select BepInEx: the Unity executable must identify Windows x86 or x64.' } }
}

function Get-UnityRemixBepInExAsset {
    param([Parameter(Mandatory)][string]$Runtime, [ValidateSet('x86','x64')][string]$Architecture)
    $headers = @{ 'User-Agent' = 'UnityRemix-build'; 'Accept' = 'application/vnd.github+json' }
    if ($Runtime -in @('BepInEx5Mono','BepInEx5MonoLegacy32')) {
        $endpoint = if ($Runtime -eq 'BepInEx5MonoLegacy32') { 'https://api.github.com/repos/BepInEx/BepInEx/releases/tags/v5.4.23.5' }
            else { 'https://api.github.com/repos/BepInEx/BepInEx/releases?per_page=100' }
        $releases = Invoke-RestMethod -Uri $endpoint -Headers $headers
        foreach ($release in $releases) {
            if ($release.tag_name -notmatch '^v5\.' -or $release.prerelease -or $release.draft) { continue }
            $asset = $release.assets | Where-Object name -match "^BepInEx_win_${Architecture}_5\..*\.zip$" | Select-Object -First 1
            if ($asset) {
                $digest = if ($asset.PSObject.Properties['digest'] -and $asset.digest -match '^sha256:([a-fA-F0-9]{64})$') { $Matches[1] } else { '' }
                if ($Runtime -eq 'BepInEx5MonoLegacy32') { $digest = '37651C79E40D6F909572A4F461AC25350BB3EF8FE7FBD29F1AA8791A33B84C82' }
                return [pscustomobject]@{ Name = $asset.name; Url = $asset.browser_download_url; Sha256 = $digest }
            }
        }
    } else {
        $backend = if ($Runtime -eq 'BepInEx6Mono') { 'Mono' } elseif ($Runtime -eq 'BepInEx6IL2CPP') { 'IL2CPP' } else { throw "Unknown runtime: $Runtime" }
        $page = Invoke-WebRequest -UseBasicParsing -Uri 'https://builds.bepinex.dev/projects/bepinex_be'
        $assets = @(foreach ($link in $page.Links) {
            $url = [Uri]::new([Uri]'https://builds.bepinex.dev', [string]$link.href)
            $name = [Uri]::UnescapeDataString([IO.Path]::GetFileName($url.AbsolutePath))
            if ($url.Scheme -eq 'https' -and $url.Host -eq 'builds.bepinex.dev' -and
                $name -match "^BepInEx-Unity\.${backend}-win-${Architecture}-6\.[\d.]+-be\.(\d+)\+[a-fA-F0-9]+\.zip$") {
                [pscustomobject]@{ Name = $name; Url = $url.AbsoluteUri; Sha256 = ''; Build = [int]$Matches[1] }
            }
        })
        if ($assets.Count) { return $assets | Sort-Object Build -Descending | Select-Object -First 1 }
    }
    throw "No official BepInEx download found for $Runtime / $Architecture."
}

function Get-UnityRemixLoaderReferenceNames {
    param([Parameter(Mandatory)][string]$Runtime)
    switch ($Runtime) {
        BepInEx5Mono { @('BepInEx.dll','0Harmony.dll') }
        BepInEx5MonoLegacy32 { @('BepInEx.dll') }
        BepInEx6Mono { @('BepInEx.Core.dll','BepInEx.Unity.Common.dll','BepInEx.Unity.Mono.dll','0Harmony.dll') }
        BepInEx6IL2CPP { @('BepInEx.Core.dll','BepInEx.Unity.Common.dll','BepInEx.Unity.IL2CPP.dll','Il2CppInterop.Runtime.dll','0Harmony.dll') }
        default { throw "Unknown runtime: $Runtime" }
    }
}

function Test-UnityRemixLoaderReferences {
    param([Parameter(Mandatory)][string]$CorePath, [Parameter(Mandatory)][string]$Runtime)
    foreach ($name in Get-UnityRemixLoaderReferenceNames $Runtime) {
        $file = Join-Path $CorePath $name
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Loader archive is missing $name." }
        $identity = [Reflection.AssemblyName]::GetAssemblyName($file)
        if ($name -in @('BepInEx.dll','BepInEx.Core.dll')) {
            $major = if ($Runtime -like 'BepInEx5*') { 5 } else { 6 }
            if ($identity.Version.Major -ne $major) { throw 'Loader archive has the wrong BepInEx major version.' }
        }
    }
}

function Test-UnityRemixLoaderFiles {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Runtime, [string]$Architecture)
    Test-UnityRemixLoaderReferences (Join-Path $Path 'BepInEx/core') $Runtime
    $machine = Get-UnityRemixPeMachine (Join-Path $Path 'winhttp.dll')
    if (($Architecture -eq 'x86' -and $machine -ne 0x14c) -or ($Architecture -eq 'x64' -and $machine -ne 0x8664)) {
        throw 'Loader archive has the wrong Windows architecture.'
    }
}

function Get-UnityRemixBepInEx {
    param([Parameter(Mandatory)][ValidateSet('BepInEx5Mono','BepInEx6Mono','BepInEx6IL2CPP','BepInEx5MonoLegacy32')][string]$Runtime,
        [ValidateSet('x86','x64')][string]$Architecture = 'x64',
        [string]$CachePath = (Join-Path $PSScriptRoot 'artifacts/bepinex'), [switch]$Refresh)
    if ($Runtime -eq 'BepInEx5MonoLegacy32') { $Architecture = 'x86' }
    $cache = [IO.Path]::GetFullPath($CachePath).TrimEnd('\','/')
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $key = "$Runtime-$Architecture"
    $index = Join-Path $cache "$key.json"
    if (!$Refresh -and (Test-Path -LiteralPath $index)) {
        $record = Get-Content -LiteralPath $index -Raw | ConvertFrom-Json
        if ($record.directory -notmatch ('^' + [regex]::Escape($key) + '-[a-f0-9]{32}$') -or !@($record.files).Count) { throw 'Invalid BepInEx cache record.' }
        $root = Join-Path $cache $record.directory
        foreach ($entry in $record.files) {
            $file = [IO.Path]::GetFullPath((Join-Path $root $entry.path))
            if ([IO.Path]::IsPathRooted($entry.path) -or $entry.path.Contains(':') -or
                !$file.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) -or
                (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw 'BepInEx cache changed; use -Refresh to fetch a clean copy.' }
        }
        Test-UnityRemixLoaderFiles (Join-Path $root 'loader') $Runtime $Architecture
        return [pscustomobject]@{ LoaderPath = (Join-Path $root 'loader'); ArchivePath = (Join-Path $root 'loader.zip'); Runtime = $Runtime; Architecture = $Architecture; Name = $record.name }
    }
    $asset = Get-UnityRemixBepInExAsset $Runtime $Architecture
    $uri = [Uri]$asset.Url
    if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('github.com','builds.bepinex.dev')) { throw 'Unexpected BepInEx download origin.' }
    $directory = "$key-$([Guid]::NewGuid().ToString('N'))"
    $root = Join-Path $cache $directory
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    $archive = Join-Path $root 'loader.zip'
    Write-Host "Fetching $($asset.Name)"
    Invoke-WebRequest -UseBasicParsing -Uri $asset.Url -OutFile $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($asset.Sha256 -and $hash -ne $asset.Sha256) { throw 'BepInEx archive checksum mismatch.' }
    $loader = Join-Path $root 'loader'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach ($entry in $zip.Entries) {
            $destination = [IO.Path]::GetFullPath((Join-Path $loader $entry.FullName))
            if ([IO.Path]::IsPathRooted($entry.FullName) -or $entry.FullName.Contains(':') -or
                !$destination.StartsWith($loader + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid path in BepInEx archive.' }
        }
    } finally { $zip.Dispose() }
    Expand-Archive -LiteralPath $archive -DestinationPath $loader
    Test-UnityRemixLoaderFiles $loader $Runtime $Architecture
    $files = @(foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force) {
        [ordered]@{ path = $file.FullName.Substring($root.Length + 1); sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash }
    })
    [ordered]@{ name = $asset.Name; url = $asset.Url; directory = $directory; files = $files } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $index -Encoding UTF8
    [pscustomobject]@{ LoaderPath = $loader; ArchivePath = $archive; Runtime = $Runtime; Architecture = $Architecture; Name = $asset.Name }
}

function Install-UnityRemixBepInEx {
    param([Parameter(Mandatory)][string]$UnityPath, [Parameter(Mandatory)]$Loader)
    $player = Get-UnityRemixPlayer $UnityPath
    $runtime = Get-UnityRemixLoaderRuntime $player $Loader.Runtime
    $architecture = Get-UnityRemixLoaderArchitecture $player
    Test-UnityRemixLoaderFiles $Loader.LoaderPath $runtime $architecture
    if (Test-Path -LiteralPath (Join-Path $player.GamePath 'BepInEx/core')) { throw 'An installed BepInEx loader already exists; it will not be replaced.' }
    if (@(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $player.ExePath }).Count) { throw 'Close the game before installing BepInEx.' }
    $files = @(Get-ChildItem -LiteralPath $Loader.LoaderPath -File -Recurse -Force)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($Loader.LoaderPath.Length + 1)
        $target = [IO.Path]::GetFullPath((Join-Path $player.GamePath $relative))
        if (!$target.StartsWith($player.GamePath.TrimEnd('\','/') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Loader path escaped the game directory.' }
        if (Test-Path -LiteralPath $target) { throw "Existing file conflicts with BepInEx installation: $relative" }
        $ancestor = Split-Path $target -Parent
        while ($ancestor -and $ancestor.Length -ge $player.GamePath.Length) {
            if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Linked installation path: $ancestor" }
            $ancestor = Split-Path $ancestor -Parent
        }
    }
    foreach ($file in $files) {
        $target = Join-Path $player.GamePath ($file.FullName.Substring($Loader.LoaderPath.Length + 1))
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    Write-Host "Installed $($Loader.Name) into $($player.GamePath)."
}

# Missing loader references can be downloaded; Unity/interop references must
# still be supplied by a matching game through setup-references.ps1.
function Initialize-UnityRemixLoaderReferences {
    param([Parameter(Mandatory)][string]$Runtime, [Parameter(Mandatory)][string]$ReferencePath)
    $names = @(Get-UnityRemixLoaderReferenceNames $Runtime)
    $missing = @($names | Where-Object { !(Test-Path -LiteralPath (Join-Path $ReferencePath $_) -PathType Leaf) })
    if (!$missing.Count) {
        Test-UnityRemixLoaderReferences $ReferencePath $Runtime
        return
    }
    $loader = Get-UnityRemixBepInEx -Runtime $Runtime
    New-Item -ItemType Directory -Path $ReferencePath -Force | Out-Null
    # Copy the complete loader reference set together to avoid mixing versions.
    foreach ($name in $names) {
        Copy-Item -LiteralPath (Join-Path $loader.LoaderPath "BepInEx/core/$name") -Destination $ReferencePath -Force
    }
    Write-Host "Prepared $Runtime loader references from $($loader.Name)."
}
