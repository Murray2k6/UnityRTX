param([string]$LegacyGame = 'C:/Games/SDCWin32')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$root = Join-Path $repo ('artifacts/reference-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'setup-references.ps1'),(Join-Path $repo 'runtime-tools.ps1'),(Join-Path $repo 'bepinex-tools.ps1') -Destination $root
New-Item -ItemType Directory -Path (Join-Path $root 'legacy') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $root 'legacy/build.ps1') -Value "throw 'Reference setup must not invoke a compiler.'"
. (Join-Path $root 'runtime-tools.ps1')
$checks = 0
function Check($ok, $message) { if (!$ok) { throw $message }; $script:checks++ }
function Fails([scriptblock]$action, [string]$pattern) {
    try { & $action } catch { if ($_.Exception.Message -notlike $pattern) { throw }; $script:checks++; return }
    throw "Expected failure: $pattern"
}
function Hashes([string]$path) {
    return (@(Get-ChildItem -LiteralPath $path -File | Sort-Object Name | ForEach-Object {
        $_.Name + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash
    }) -join '|')
}
$setup = Join-Path $root 'setup-references.ps1'
$games = @{}
foreach ($runtime in @('BepInEx5Mono','BepInEx6Mono','BepInEx6IL2CPP','BepInEx5MonoLegacy32')) {
    $source = Join-Path $repo "lib/$runtime"
    $game = Join-Path $root "games/$runtime with spaces"
    $games[$runtime] = $game
    $core = Join-Path $game 'BepInEx/core'
    $managed = Join-Path $game 'Test_Data/Managed'
    if ($runtime -eq 'BepInEx5MonoLegacy32') {
        $player = Get-UnityRemixPlayer $LegacyGame
        $managed = Join-Path $game ((Split-Path $player.DataPath -Leaf) + '/Managed')
    } elseif ($runtime -eq 'BepInEx6IL2CPP') {
        New-Item -ItemType Directory -Path (Join-Path $game 'Test_Data/il2cpp_data') -Force | Out-Null
        $managed = Join-Path $game 'BepInEx/interop'
    }
    New-Item -ItemType Directory -Path $core,$managed -Force | Out-Null
    if ($runtime -eq 'BepInEx5MonoLegacy32') { Copy-Item -LiteralPath $player.ExePath -Destination $game }
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Filter '*.dll') {
        if ($file.Name -like 'BepInEx*.dll' -or $file.Name -eq '0Harmony.dll' -or $file.Name -eq 'Il2CppInterop.Runtime.dll') {
            Copy-Item -LiteralPath $file.FullName -Destination $core
        } else { Copy-Item -LiteralPath $file.FullName -Destination $managed }
    }
    $destination = Join-Path $root "lib/$runtime"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $destination 'old-reference.dll') -Value 'stale reference'
    Set-Content -LiteralPath (Join-Path $destination 'notes.txt') -Value 'preserve this'
    & $setup -UnityPath $game
    Check (!(Test-Path -LiteralPath (Join-Path $destination 'old-reference.dll'))) "$runtime retained a stale DLL"
    Check ((Get-Content -LiteralPath (Join-Path $destination 'notes.txt')) -eq 'preserve this') "$runtime lost unrelated files"
    foreach ($file in Get-ChildItem -LiteralPath $destination -File -Filter '*.dll') {
        Check ((Get-FileHash -LiteralPath $file.FullName).Hash -eq (Get-FileHash -LiteralPath (Join-Path $source $file.Name)).Hash) "$runtime reference differs: $($file.Name)"
    }
    $before = Hashes $destination
    $requiredName = if ($runtime -eq 'BepInEx5MonoLegacy32') { 'System.Core.dll' } else { 'UnityEngine.CoreModule.dll' }
    $requiredPath = Join-Path $managed $requiredName
    Remove-Item -LiteralPath $requiredPath
    Fails { & $setup -UnityPath $game } "*Missing $requiredName*"
    Check ((Hashes $destination) -eq $before) "$runtime changed references after missing input"
    Copy-Item -LiteralPath (Join-Path $source $requiredName) -Destination $requiredPath
    Set-Content -LiteralPath $requiredPath -Value 'corrupt managed assembly'
    Fails { & $setup -UnityPath $game } '*Invalid reference assembly*'
    Check ((Hashes $destination) -eq $before) "$runtime changed references after invalid input"
    Copy-Item -LiteralPath (Join-Path $source $requiredName) -Destination $requiredPath
}
Check (!(Test-Path -LiteralPath (Join-Path $root 'bin'))) 'Reference setup started a build'
$legacy = $games.BepInEx5MonoLegacy32
$legacyLib = Join-Path $root 'lib/BepInEx5MonoLegacy32'
Check (@(Get-ChildItem -LiteralPath $legacyLib -File -Filter '*.dll').Count -eq 5) 'Legacy references must contain only the five required assemblies'
Check (!(Test-Path -LiteralPath (Join-Path $legacyLib 'UnityEngine.UI.dll'))) 'Legacy UI must remain optional'
Fails { & $setup -UnityPath $legacy -Runtime BepInEx6Mono } '*Requested BepInEx6Mono but detected BepInEx5MonoLegacy32*'
$external = Join-Path $root 'external loader'
New-Item -ItemType Directory -Path (Join-Path $external 'BepInEx') -Force | Out-Null
Move-Item -LiteralPath (Join-Path $legacy 'BepInEx/core') -Destination (Join-Path $external 'BepInEx/core')
& $setup -UnityPath $legacy -LoaderPath $external
Check (!(Test-Path -LiteralPath (Join-Path $legacy 'BepInEx/core'))) 'Preparing external references installed a loader'
$fallback = Join-Path $root 'artifacts/legacy-mono/loader-x86'
New-Item -ItemType Directory -Path $fallback -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $external 'BepInEx') -Destination $fallback -Recurse
& $setup -UnityPath $legacy
Check (!(Test-Path -LiteralPath (Join-Path $legacy 'BepInEx/core'))) 'Bundled reference fallback installed a loader'
# An alternate loader is valid for preparing a different Mono variant from the
# same engine; it must not accidentally select the installed loader instead.
& $setup -UnityPath $games.BepInEx5Mono -LoaderPath $games.BepInEx6Mono -Runtime BepInEx6Mono
Check ((Get-FileHash -LiteralPath (Join-Path $root 'lib/BepInEx6Mono/BepInEx.Unity.Mono.dll')).Hash -eq
    (Get-FileHash -LiteralPath (Join-Path $repo 'lib/BepInEx6Mono/BepInEx.Unity.Mono.dll')).Hash) 'External Mono 6 references not selected'
Write-Host "PASS: $checks reference setup checks across all four builds. Fixtures: $root"
