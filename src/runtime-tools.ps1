Set-StrictMode -Version Latest

function Get-UnityRemixPeMachine {
    param([Parameter(Mandatory)][string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    $reader = New-Object IO.BinaryReader($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "Invalid PE file: $Path" }
        $stream.Position = 0x3c
        $offset = $reader.ReadInt32()
        if ($offset -lt 64 -or $offset -gt $stream.Length - 6) { throw "Invalid PE header: $Path" }
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x4550) { throw "Invalid PE signature: $Path" }
        return $reader.ReadUInt16()
    } finally { $reader.Dispose(); $stream.Dispose() }
}

function Get-UnityRemixPlayer {
    param([Parameter(Mandatory)][string]$UnityPath)
    $game = (Resolve-Path -LiteralPath $UnityPath -ErrorAction Stop).Path
    $data = @(Get-ChildItem -LiteralPath $game -Directory -Filter '*_Data' | Where-Object {
        (Test-Path -LiteralPath (Join-Path $_.FullName 'Managed')) -or
        (Test-Path -LiteralPath (Join-Path $_.FullName 'il2cpp_data'))
    })
    if ($data.Count -ne 1) { throw "Expected one Unity *_Data folder in '$game'; found $($data.Count)." }
    $exe = Join-Path $game ($data[0].Name.Substring(0, $data[0].Name.Length - 5) + '.exe')
    $machine = if (Test-Path -LiteralPath $exe) { Get-UnityRemixPeMachine $exe } else { 0 }
    $managed = Join-Path $data[0].FullName 'Managed'
    $monoCore = Join-Path $managed 'mscorlib.dll'
    $oldMono = (Test-Path -LiteralPath $monoCore) -and
        ([Reflection.AssemblyName]::GetAssemblyName($monoCore).Version.Major -eq 2)
    $unityVersion = if (Test-Path -LiteralPath $exe) { (Get-Item -LiteralPath $exe).VersionInfo.FileVersion } else { '' }
    $legacy = $oldMono -and !(Test-Path -LiteralPath (Join-Path $managed 'UnityEngine.CoreModule.dll'))
    if ($legacy -and ($machine -ne 0x14c -or $unityVersion -notmatch '^4\.')) {
        throw 'The legacy adapter currently requires a 32-bit Unity 4 Windows player.'
    }
    [pscustomobject]@{ GamePath = $game; DataPath = $data[0].FullName; ManagedPath = $managed;
        ExePath = $exe; Machine = $machine; Legacy = [bool]$legacy; UnityVersion = $unityVersion }
}

function Get-UnityRemixLayout {
    param([Parameter(Mandatory)][string]$UnityPath, [string]$LoaderPath = '')
    $game = (Resolve-Path -LiteralPath $UnityPath -ErrorAction Stop).Path
    $data = @(Get-ChildItem -LiteralPath $game -Directory -Filter '*_Data' | Where-Object {
        (Test-Path -LiteralPath (Join-Path $_.FullName 'Managed')) -or
        (Test-Path -LiteralPath (Join-Path $_.FullName 'il2cpp_data'))
    })
    if ($data.Count -ne 1) {
        throw "Expected one Unity *_Data folder in '$game'; found $($data.Count). Select the game root containing its executable."
    }
    $player = Get-UnityRemixPlayer $game
    $loader = if ($LoaderPath) { (Resolve-Path -LiteralPath $LoaderPath -ErrorAction Stop).Path } else { $game }
    $core = Join-Path $loader 'BepInEx/core'
    if (!(Test-Path -LiteralPath $core)) { throw "BepInEx/core not found in '$loader'. Install the matching BepInEx loader first, or select an extracted loader with -LoaderPath." }
    $il2cpp = (Test-Path -LiteralPath (Join-Path $data[0].FullName 'il2cpp_data')) -or
              (Test-Path -LiteralPath (Join-Path $game 'GameAssembly.dll'))
    $v5 = Test-Path -LiteralPath (Join-Path $core 'BepInEx.dll')
    $v6 = Test-Path -LiteralPath (Join-Path $core 'BepInEx.Core.dll')
    if ($v5 -and $v6) { throw 'Mixed BepInEx 5/6 core assemblies detected. Use a clean installation of one loader.' }
    # Doorstop searches core before Managed; engine reference copies can replace
    # the player's own Unity types before BepInEx or this plugin starts.
    $engineCopies = @(Get-ChildItem -LiteralPath $core -File -Filter 'UnityEngine*.dll')
    if ($engineCopies.Count) {
        throw 'Unity engine reference DLLs were found in BepInEx/core. They can shadow the game assemblies and crash Unity before plugin startup. Restore a clean matching BepInEx loader; keep build references in lib, not in the game loader.'
    }
    if ($il2cpp) {
        if (!$v6 -or !(Test-Path -LiteralPath (Join-Path $core 'BepInEx.Unity.IL2CPP.dll'))) {
            throw 'This game uses IL2CPP and requires BepInEx 6 Unity IL2CPP.'
        }
        $runtime = 'BepInEx6IL2CPP'
        $managed = Join-Path $game 'BepInEx/interop'
    } else {
        $managed = Join-Path $data[0].FullName 'Managed'
        if ($player.Legacy -and !$v5) { throw 'Legacy Unity 4 requires BepInEx 5 Mono x86.' }
        if ($v5) { $runtime = if ($player.Legacy) { 'BepInEx5MonoLegacy32' } else { 'BepInEx5Mono' } }
        elseif ($v6 -and (Test-Path -LiteralPath (Join-Path $core 'BepInEx.Unity.Mono.dll'))) { $runtime = 'BepInEx6Mono' }
        else { throw 'No supported BepInEx Unity Mono loader found.' }
    }
    [pscustomobject]@{ GamePath = $game; Runtime = $runtime; CorePath = $core; ManagedPath = $managed }
}

function Get-UnityRemixTargetFramework {
    param([Parameter(Mandatory)][string]$Runtime)
    if ($Runtime -eq 'BepInEx5MonoLegacy32') { 'net35' }
    elseif ($Runtime -eq 'BepInEx6IL2CPP') { 'net6.0' } else { 'netstandard2.1' }
}

function Install-UnityRemixPlugin {
    param([Parameter(Mandatory)][string]$UnityPath, [Parameter(Mandatory)][string]$Runtime,
          [Parameter(Mandatory)][string]$PluginPath)
    $layout = Get-UnityRemixLayout -UnityPath $UnityPath
    if ($layout.Runtime -ne $Runtime) { throw "Cannot install $Runtime into a $($layout.Runtime) game." }
    if (!(Test-Path -LiteralPath $PluginPath -PathType Leaf)) { throw "Plugin build not found: $PluginPath" }
    $plugins = Join-Path $layout.GamePath 'BepInEx/plugins'
    New-Item -ItemType Directory -Path $plugins -Force | Out-Null
    # Update a pre-existing install in place, but never create a second copy.
    $existing = @(Get-ChildItem -LiteralPath $plugins -Recurse -File -Filter 'UnityRemix.dll')
    if ($existing.Count -gt 1) { throw 'Multiple UnityRemix.dll installations found. Keep one before deploying.' }
    $destination = if ($existing.Count -eq 1) { $existing[0].FullName } else { Join-Path $plugins 'UnityRemix.dll' }
    Copy-Item -LiteralPath $PluginPath -Destination $destination -Force
    Write-Host "Installed $Runtime plugin: $destination"
}
