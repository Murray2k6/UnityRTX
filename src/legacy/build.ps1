param([string]$UnityPath = '', [string]$LoaderPath = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$references = Join-Path $root 'lib/BepInEx5MonoLegacy32'
if ($UnityPath) {
    & (Join-Path $root 'setup-references.ps1') -UnityPath $UnityPath -Runtime BepInEx5MonoLegacy32 -LoaderPath $LoaderPath
}
. (Join-Path $root 'bepinex-tools.ps1')
Initialize-UnityRemixLoaderReferences -Runtime BepInEx5MonoLegacy32 -ReferencePath $references
$names = @('mscorlib.dll','System.dll','System.Core.dll','UnityEngine.dll','BepInEx.dll')
foreach ($name in $names) {
    if (!(Test-Path -LiteralPath (Join-Path $references $name))) { throw "Missing legacy reference $name; use -UnityPath with the Unity 4 game." }
}
$output = Join-Path $root 'bin/Release/BepInEx5MonoLegacy32/net35'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sdk = (& dotnet --list-sdks | Select-Object -Last 1)
if ($sdk -notmatch '^([^ ]+) \[(.+)\]$') { throw 'Cannot locate the C# compiler.' }
$compiler = Join-Path $Matches[2] "$($Matches[1])/Roslyn/bincore/csc.dll"
$arguments = @('/nologo','/noconfig','/nostdlib+','/langversion:latest','/runtimeMetadataVersion:v2.0.50727','/target:library','/platform:x86','/optimize+','/deterministic+',"/out:$output/UnityRemix.dll")
$arguments += @($names | ForEach-Object { '/reference:' + (Join-Path $references $_) })
$arguments += @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | ForEach-Object FullName)
$arguments += Join-Path $root 'UnityMaterialSemantics.cs'
& dotnet $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Legacy Mono compilation failed.' }
Write-Host "Built .NET 3.5 / Unity 4 / x86 plugin: $output/UnityRemix.dll"
