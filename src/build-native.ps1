# Build shaders before C++ dependency evaluation, then stage the resulting renderer.
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot 'artifacts/remix-native/source'),
    [string]$BuildDirectory = '_Comp64Release',
    [string]$OutputPath = '',
    [string]$PythonPath = '',
    [string]$ToolsPath = (Join-Path $PSScriptRoot 'artifacts/remix-native/build-tools'),
    [ValidateRange(1,64)][int]$Jobs = 2,
    [switch]$Reconfigure
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-tools.ps1')
$source = (Resolve-Path -LiteralPath $SourcePath).Path
if (!(Test-Path -LiteralPath (Join-Path $source 'meson.build'))) { throw "Not a Remix source checkout: $source" }
if (!$OutputPath) { $OutputPath = Join-Path $source '_output' }
$output = [IO.Path]::GetFullPath($OutputPath)
$build = if ([IO.Path]::IsPathRooted($BuildDirectory)) { $BuildDirectory } else { Join-Path $source $BuildDirectory }
$buildEnvironment = [Environment]::GetEnvironmentVariables('Process')
try {
Initialize-UnityRemixMsvc
$meson = Get-UnityRemixMeson -PythonPath $PythonPath -ToolsPath $ToolsPath
if (!$env:VULKAN_SDK) {
    $sdkRoot = Join-Path $env:SystemDrive 'VulkanSDK'
    $sdk = Get-ChildItem -LiteralPath $sdkRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+(\.\d+){1,3}$' } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if ($sdk) { $env:VULKAN_SDK = $sdk.FullName }
}
if (!$env:VULKAN_SDK -or !(Test-Path -LiteralPath (Join-Path $env:VULKAN_SDK 'Include/vulkan/vulkan.h'))) {
    throw 'Vulkan SDK not found. Install it and set VULKAN_SDK.'
}
Push-Location -LiteralPath $source
try {
    if (!(Test-Path -LiteralPath (Join-Path $build 'meson-private/coredata.dat'))) {
        Invoke-UnityRemixMeson $meson @('setup', $build, '--buildtype', 'release', '--backend', 'ninja', '-Ddownload_apics=false')
    } elseif ($Reconfigure) {
        Invoke-UnityRemixMeson $meson @('setup', '--reconfigure', $build)
    }
    Invoke-UnityRemixMeson $meson @('compile', '-C', $build, '-j', "$Jobs", 'rtx_shaders')
    Invoke-UnityRemixMeson $meson @('compile', '-C', $build, '-j', "$Jobs", 'd3d9')
    $renderer = Join-Path $build 'src/d3d9/d3d9.dll'
    if (!(Test-Path -LiteralPath $renderer -PathType Leaf)) { throw "Native build did not produce $renderer" }
    # meson install stages the matching dependencies and USD resources as well.
    Invoke-UnityRemixMeson $meson @('install', '-C', $build, '--no-rebuild', '--tags', 'output')
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $installed = Join-Path $source '_output'
    if ([IO.Path]::GetFullPath($installed) -ne $output) {
        foreach ($item in Get-ChildItem -LiteralPath $installed) { Copy-Item -LiteralPath $item.FullName -Destination $output -Recurse -Force }
    }
    Copy-Item -LiteralPath $renderer -Destination (Join-Path $output 'd3d9.dll') -Force
    [ordered]@{ schema = 1; builtUtc = [DateTime]::UtcNow.ToString('O'); rendererSha256 = (Get-FileHash -LiteralPath $renderer -Algorithm SHA256).Hash } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'unity-remix-build.json') -Encoding UTF8
    Write-Host "Built and staged native renderer: $output"
} finally { Pop-Location }
} finally { Restore-UnityRemixBuildEnvironment $buildEnvironment }
