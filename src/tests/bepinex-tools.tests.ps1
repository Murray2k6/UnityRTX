# Offline fixtures use the official archives already fetched by fetch-bepinex.ps1 -Runtime All.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'bepinex-tools.ps1')
$root = Join-Path $repo ('artifacts/bepinex-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$checks = 0
function Check($ok, $message) { if (!$ok) { throw $message }; $script:checks++ }
function Fails([scriptblock]$action, [string]$pattern) {
    try { & $action } catch { if ($_.Exception.Message -notlike $pattern) { throw }; $script:checks++; return }
    throw "Expected failure: $pattern"
}

# A releases API array must be enumerated, and prereleases/wrong architectures ignored.
function Invoke-RestMethod {
    @(
        [pscustomobject]@{ tag_name = 'v6.0.0'; prerelease = $true; draft = $false; assets = @() },
        [pscustomobject]@{ tag_name = 'v5.4.23.5'; prerelease = $false; draft = $false; assets = @(
            [pscustomobject]@{ name = 'BepInEx_win_x86_5.4.23.5.zip'; browser_download_url = 'https://github.com/x86.zip' },
            [pscustomobject]@{ name = 'BepInEx_win_x64_5.4.23.5.zip'; browser_download_url = 'https://github.com/x64.zip'; digest = ('sha256:' + ('a' * 64)) }
        ) }
    )
}
function Invoke-WebRequest {
    [pscustomobject]@{ Links = @(
        @{ href = '/projects/bepinex_be/787/BepInEx-Unity.Mono-win-x64-6.0.0-be.787%2Babcdef.zip' },
        @{ href = '/projects/bepinex_be/788/BepInEx-Unity.Mono-win-x64-6.0.0-be.788%2Babcdef.zip' },
        @{ href = '/projects/bepinex_be/789/BepInEx-Unity.Mono-win-x86-6.0.0-be.789%2Babcdef.zip' },
        @{ href = '/projects/bepinex_be/790/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.790%2Babcdef.zip' },
        @{ href = 'https://unrelated.invalid/BepInEx-Unity.Mono-win-x64-6.0.0-be.999%2Babcdef.zip' }
    ) }
}
$stable = Get-UnityRemixBepInExAsset BepInEx5Mono x64
Check ($stable.Url -eq 'https://github.com/x64.zip' -and $stable.Sha256 -eq ('a' * 64)) 'Stable release selection failed'
Check ((Get-UnityRemixBepInExAsset BepInEx6Mono x64).Build -eq 788) 'Mono backend/architecture/version filtering failed'
Check ((Get-UnityRemixBepInExAsset BepInEx6IL2CPP x64).Build -eq 790) 'IL2CPP selection failed'
Check ((Get-UnityRemixBepInExAsset BepInEx6Mono x86).Build -eq 789) 'x86 selection failed'
Remove-Item Function:\Invoke-RestMethod,Function:\Invoke-WebRequest
function Invoke-RestMethod { throw 'Unexpected network request in offline fixture' }
function Invoke-WebRequest { throw 'Unexpected network request in offline fixture' }
$loaders = @{}
foreach ($runtime in @('BepInEx5Mono','BepInEx6Mono','BepInEx6IL2CPP','BepInEx5MonoLegacy32')) {
    $loaders[$runtime] = Get-UnityRemixBepInEx -Runtime $runtime
    Check (Test-Path -LiteralPath $loaders[$runtime].ArchivePath) "$runtime did not reuse its offline cache"
}
Fails { Test-UnityRemixLoaderFiles $loaders.BepInEx6Mono.LoaderPath BepInEx6Mono x86 } '*wrong Windows architecture*'
Fails { Test-UnityRemixLoaderFiles $loaders.BepInEx6Mono.LoaderPath BepInEx6IL2CPP x64 } '*missing BepInEx.Unity.IL2CPP.dll*'

# New-download, integrity, corruption, refresh, and archive traversal paths.
$script:fixtureArchive = $loaders.BepInEx6Mono.ArchivePath
$script:fixtureDigest = (Get-FileHash -LiteralPath $script:fixtureArchive).Hash
$originalAssetResolver = ${function:Get-UnityRemixBepInExAsset}
function Get-UnityRemixBepInExAsset {
    [pscustomobject]@{ Name = 'fixture.zip'; Url = 'https://builds.bepinex.dev/fixture.zip'; Sha256 = $script:fixtureDigest }
}
$script:downloads = 0
function Invoke-WebRequest {
    param([switch]$UseBasicParsing, $Uri, $OutFile)
    $script:downloads++
    Copy-Item -LiteralPath $script:fixtureArchive -Destination $OutFile
}
$cache = Join-Path $root 'artifacts/bepinex'
$download = Get-UnityRemixBepInEx BepInEx6Mono -CachePath $cache
$null = Get-UnityRemixBepInEx BepInEx6Mono -CachePath $cache
Check ($script:downloads -eq 1) 'Cache reuse attempted a download'
Set-Content -LiteralPath (Join-Path $download.LoaderPath 'BepInEx/core/0Harmony.dll') -Value 'changed'
Fails { Get-UnityRemixBepInEx BepInEx6Mono -CachePath $cache } '*cache changed*'
$download = Get-UnityRemixBepInEx BepInEx6Mono -CachePath $cache -Refresh
Check ($script:downloads -eq 2) 'Refresh did not fetch a clean copy'
$script:fixtureDigest = '0' * 64
Fails { Get-UnityRemixBepInEx BepInEx6Mono -CachePath (Join-Path $root 'bad-checksum') } '*checksum mismatch*'
Check (!(Test-Path -LiteralPath (Join-Path $root 'bad-checksum/BepInEx6Mono-x64.json'))) 'Failed download was cached'
$badZip = Join-Path $root 'bad-path.zip'
$zip = [IO.Compression.ZipFile]::Open($badZip, [IO.Compression.ZipArchiveMode]::Create)
try { $null = $zip.CreateEntry('../escaped.dll') } finally { $zip.Dispose() }
$script:fixtureArchive = $badZip
$script:fixtureDigest = (Get-FileHash -LiteralPath $badZip).Hash
Fails { Get-UnityRemixBepInEx BepInEx6Mono -CachePath (Join-Path $root 'bad-path') } '*Invalid path*'
Check (!(Test-Path -LiteralPath (Join-Path $root 'bad-path/BepInEx6Mono-x64.json'))) 'Unsafe archive was cached'
Set-Item Function:\Get-UnityRemixBepInExAsset -Value $originalAssetResolver
function Invoke-WebRequest { throw 'Unexpected network request in offline fixture' }

# Build reference setup uses the cached loader but does not install it in the game.
foreach ($name in @('setup-references.ps1','bepinex-tools.ps1','runtime-tools.ps1')) {
    Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $root
}
$game = Join-Path $root 'Game with spaces'
$managed = Join-Path $game 'Test_Data/Managed'
New-Item -ItemType Directory -Path $managed -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'artifacts/native-adapters/UnityRemix.Probe.exe') -Destination (Join-Path $game 'Test.exe')
Copy-Item -LiteralPath (Join-Path $repo 'lib/BepInEx6Mono/UnityEngine.CoreModule.dll') -Destination $managed
$layout = & (Join-Path $root 'setup-references.ps1') -UnityPath $game -PassThru
Check ($layout.Runtime -eq 'BepInEx6Mono') 'New Mono game did not select bleeding-edge Mono'
Check (!(Test-Path -LiteralPath (Join-Path $game 'BepInEx'))) 'Reference setup modified the game'
$player = Get-UnityRemixPlayer $game
Check ((Get-UnityRemixLoaderRuntime $player BepInEx5Mono) -eq 'BepInEx5Mono') 'Explicit Mono 5 selection failed'
Check ((Get-UnityRemixLoaderArchitecture $player) -eq 'x64') 'Player architecture detection failed'
Fails { Get-UnityRemixLoaderRuntime $player BepInEx6IL2CPP } '*does not match*'
New-Item -ItemType Directory -Path (Join-Path $player.DataPath 'il2cpp_data') | Out-Null
Check ((Get-UnityRemixLoaderRuntime $player) -eq 'BepInEx6IL2CPP') 'Missing IL2CPP loader not detected'
Fails { Get-UnityRemixLoaderRuntime $player BepInEx6Mono } '*does not match*'
Fails { & (Join-Path $root 'setup-references.ps1') -UnityPath $game -LoaderPath $loaders.BepInEx6IL2CPP.LoaderPath } '*generate interop assemblies*'
Remove-Item -LiteralPath (Join-Path $player.DataPath 'il2cpp_data')

# First-time installation must preflight every conflict before copying anything.
$proxy = Join-Path $game 'winhttp.dll'
Set-Content -LiteralPath $proxy -Value 'existing proxy'
Fails { Install-UnityRemixBepInEx $game $download } '*conflicts*'
Check (!(Test-Path -LiteralPath (Join-Path $game 'BepInEx/core'))) 'Conflict partially installed a loader'
Check ((Get-Content -LiteralPath $proxy) -eq 'existing proxy') 'Existing proxy was overwritten'
Remove-Item -LiteralPath $proxy
Install-UnityRemixBepInEx $game $download
Check ((Get-UnityRemixLayout $game).Runtime -eq 'BepInEx6Mono') 'Installed loader was not detected'
Check (Test-Path -LiteralPath (Join-Path $game '.doorstop_version')) 'Hidden loader metadata was not installed'
Fails { Install-UnityRemixBepInEx $game $download } '*already exists*'

$references = Join-Path $root 'missing-loader-references'
Initialize-UnityRemixLoaderReferences BepInEx6Mono $references
Test-UnityRemixLoaderReferences $references BepInEx6Mono
Check (Test-Path -LiteralPath (Join-Path $references 'BepInEx.Unity.Mono.dll')) 'Missing loader references were not fetched'

# Exercise the extracted package's installer without a preinstalled loader.
. (Join-Path $repo 'native-runtime-tools.ps1')
foreach ($name in @('install.ps1','native-runtime-tools.ps1','legacy-runtime-tools.ps1')) {
    Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $root
}
$native = Join-Path $root 'native-source'
New-Item -ItemType Directory -Path (Join-Path $native 'usd') -Force | Out-Null
foreach ($name in @('d3d9.dll','crashpad_handler.exe','UnityRemix.Probe.exe')) { Set-Content -LiteralPath (Join-Path $native $name) -Value 'fixture' }
New-UnityRemixNativeBundle $native (Join-Path $root 'native/win-x64') -ProbePath (Join-Path $native 'UnityRemix.Probe.exe')
$plugin = Join-Path $root 'runtimes/BepInEx6Mono'
New-Item -ItemType Directory -Path $plugin -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'bin/Release/BepInEx6Mono/netstandard2.1/UnityRemix.dll') -Destination $plugin
$newGame = Join-Path $root 'New installation'
New-Item -ItemType Directory -Path (Join-Path $newGame 'Test_Data/Managed') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $game 'Test.exe') -Destination $newGame
& (Join-Path $root 'install.ps1') -UnityPath $newGame
Check ((Get-UnityRemixLayout $newGame).Runtime -eq 'BepInEx6Mono') 'Package did not install its required loader'
Check (Test-Path -LiteralPath (Join-Path $newGame 'BepInEx/plugins/UnityRemix.dll')) 'Package did not install the plugin'
Test-UnityRemixNativeBundle (Join-Path $newGame 'BepInEx/UnityRemix/native/win-x64')
Write-Host "PASS: $checks BepInEx fetch, cache, reference, and installation checks. Fixtures: $root"
