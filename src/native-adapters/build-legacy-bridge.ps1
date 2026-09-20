param([ValidateSet('x86','x64')][string]$Architecture = 'x86', [ValidateRange(1,64)][int]$Jobs = 2,
    [string]$PythonPath = '', [string]$SourcePath = (Join-Path $PSScriptRoot '../artifacts/remix-native/source/bridge'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build-tools.ps1')
$vcArch = if ($Architecture -eq 'x86') { 'x64_x86' } else { 'x64' }
$buildEnvironment = [Environment]::GetEnvironmentVariables('Process')
try {
Initialize-UnityRemixMsvc -Architecture $vcArch
$meson = Get-UnityRemixMeson -PythonPath $PythonPath -ToolsPath (Join-Path $PSScriptRoot '../artifacts/remix-native/build-tools')
Push-Location -LiteralPath $SourcePath
try {
    $buildDirectory = if ($Architecture -eq 'x86') { '_Comp32UnityRelease' } else { '_Comp64UnityRelease' }
    if (!(Test-Path -LiteralPath (Join-Path $buildDirectory 'meson-private/coredata.dat'))) {
        Invoke-UnityRemixMeson $meson @('setup', $buildDirectory, '--buildtype', 'release', '--backend', 'ninja', '-Denable_tracy=false')
    }
    $target = if ($Architecture -eq 'x86') { 'd3d9' } else { 'NvRemixBridge' }
    Invoke-UnityRemixMeson $meson @('compile', '-C', $buildDirectory, '-j', "$Jobs", $target)
} finally { Pop-Location }
} finally { Restore-UnityRemixBuildEnvironment $buildEnvironment }
