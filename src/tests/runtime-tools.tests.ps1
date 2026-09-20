$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../runtime-tools.ps1')
$root = Join-Path $PSScriptRoot ('../artifacts/runtime-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$root = (Resolve-Path -LiteralPath $root).Path
$script:checks = 0
function Assert-Equal($actual, $expected, $message) {
    if ($actual -ne $expected) { throw "$message : expected '$expected', got '$actual'" }
    $script:checks++
}
function Assert-Throws([scriptblock]$action, [string]$pattern) {
    try { & $action } catch {
        if ($_.Exception.Message -notlike $pattern) { throw }
        $script:checks++
        return
    }
    throw "Expected failure matching '$pattern'"
}
function New-Game([string]$name, [string]$runtime) {
    $game = Join-Path $root $name
    $core = Join-Path $game 'BepInEx/core'
    New-Item -ItemType Directory -Path $core -Force | Out-Null
    $backend = if ($runtime -eq 'BepInEx6IL2CPP') { 'il2cpp_data' } else { 'Managed' }
    New-Item -ItemType Directory -Path (Join-Path $game "Test_Data/$backend") -Force | Out-Null
    $names = switch ($runtime) {
        BepInEx5Mono { @('BepInEx.dll') }
        BepInEx6Mono { @('BepInEx.Core.dll','BepInEx.Unity.Mono.dll') }
        BepInEx6IL2CPP { @('BepInEx.Core.dll','BepInEx.Unity.IL2CPP.dll') }
    }
    foreach ($name in $names) { Set-Content -LiteralPath (Join-Path $core $name) -Value 'fixture' }
    return $game
}
$mono5 = New-Game 'Mono 5 with spaces' BepInEx5Mono
$mono6 = New-Game 'Mono 6' BepInEx6Mono
$il2cpp = New-Game 'IL2CPP' BepInEx6IL2CPP
Assert-Equal (Get-UnityRemixLayout $mono5).Runtime BepInEx5Mono 'Mono 5 detection'
Assert-Equal (Get-UnityRemixLayout $mono6).Runtime BepInEx6Mono 'Mono 6 detection'
Assert-Equal (Get-UnityRemixLayout $il2cpp).Runtime BepInEx6IL2CPP 'IL2CPP detection'
Assert-Equal (Get-UnityRemixLayout $il2cpp).ManagedPath (Join-Path $il2cpp 'BepInEx/interop') 'Use generated interop'
$plugin = Join-Path $root 'UnityRemix.dll'
Set-Content -LiteralPath $plugin -Value 'plugin-v1'
Assert-Throws { Install-UnityRemixPlugin $mono5 BepInEx6Mono $plugin } '*Cannot install*'
Assert-Throws { Install-UnityRemixPlugin $mono5 BepInEx5Mono (Join-Path $root 'missing.dll') } '*not found*'
Install-UnityRemixPlugin $mono5 BepInEx5Mono $plugin
Assert-Equal (Get-Content -LiteralPath (Join-Path $mono5 'BepInEx/plugins/UnityRemix.dll')) 'plugin-v1' 'Install selected DLL'
$nested = Join-Path $mono6 'BepInEx/plugins/UnityRemix'
New-Item -ItemType Directory -Path $nested -Force | Out-Null
Set-Content -LiteralPath (Join-Path $nested 'UnityRemix.dll') -Value 'old'
Install-UnityRemixPlugin $mono6 BepInEx6Mono $plugin
Assert-Equal (Get-Content -LiteralPath (Join-Path $nested 'UnityRemix.dll')) 'plugin-v1' 'Upgrade existing nested install'
Assert-Equal (Test-Path -LiteralPath (Join-Path $mono6 'BepInEx/plugins/UnityRemix.dll')) $false 'No duplicate root install'
Copy-Item -LiteralPath $plugin -Destination (Join-Path $mono6 'BepInEx/plugins/UnityRemix.dll')
Assert-Throws { Install-UnityRemixPlugin $mono6 BepInEx6Mono $plugin } '*Multiple UnityRemix.dll*'
Set-Content -LiteralPath (Join-Path $mono5 'BepInEx/core/BepInEx.Core.dll') -Value 'fixture'
Assert-Throws { Get-UnityRemixLayout $mono5 } '*Mixed BepInEx*'
New-Item -ItemType Directory -Path (Join-Path $mono6 'Other_Data/Managed') -Force | Out-Null
Assert-Throws { Get-UnityRemixLayout $mono6 } '*found 2*'
$wrongBackend = New-Game 'Wrong backend' BepInEx5Mono
Set-Content -LiteralPath (Join-Path $wrongBackend 'GameAssembly.dll') -Value 'fixture'
Assert-Throws { Get-UnityRemixLayout $wrongBackend } '*requires BepInEx 6 Unity IL2CPP*'
$missingInterop = New-Game 'Missing interop' BepInEx6IL2CPP
Assert-Throws { & (Join-Path $PSScriptRoot '../setup-references.ps1') -UnityPath $missingInterop } '*launch the game*'
Assert-Equal (Get-UnityRemixTargetFramework BepInEx5Mono) netstandard2.1 'Mono target framework'
Assert-Equal (Get-UnityRemixTargetFramework BepInEx6IL2CPP) net6.0 'IL2CPP target framework'
foreach ($runtime in @('BepInEx5Mono','BepInEx6Mono','BepInEx6IL2CPP')) {
    $shadowed = New-Game "Shadowed engine $runtime" $runtime
    Set-Content -LiteralPath (Join-Path $shadowed 'BepInEx/core/UnityEngine.CoreModule.dll') -Value 'reference copy from another Unity version'
    Assert-Throws { Get-UnityRemixLayout $shadowed } '*reference DLLs*shadow*'
    Assert-Throws { Install-UnityRemixPlugin $shadowed $runtime $plugin } '*reference DLLs*shadow*'
    Assert-Equal (Test-Path -LiteralPath (Join-Path $shadowed 'BepInEx/plugins/UnityRemix.dll')) $false 'Rejected loader must not install a plugin'
}
Write-Host "Passed $script:checks runtime and install checks. Fixtures: $root"
