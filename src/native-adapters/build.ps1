param([string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/native-adapters'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build-tools.ps1')
$buildEnvironment = [Environment]::GetEnvironmentVariables('Process')
try {
Initialize-UnityRemixMsvc
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$OutputPath = (Resolve-Path -LiteralPath $OutputPath).Path
& cl.exe /nologo /std:c++17 /O2 /MT /EHsc /W4 /WX (Join-Path $PSScriptRoot 'probe.cpp') "/Fo$OutputPath/probe.obj" "/Fe$OutputPath/UnityRemix.Probe.exe" /link /INCREMENTAL:NO
if ($LASTEXITCODE -ne 0) { throw 'Native API probe build failed.' }
} finally { Restore-UnityRemixBuildEnvironment $buildEnvironment }
