param([string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/legacy-mono/probes'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build-tools.ps1')
$buildEnvironment = [Environment]::GetEnvironmentVariables('Process')
try {
Initialize-UnityRemixMsvc -Architecture x64_x86
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$OutputPath = (Resolve-Path -LiteralPath $OutputPath).Path
& cl.exe /nologo /std:c++17 /O2 /MT /EHsc /W4 /WX (Join-Path $PSScriptRoot 'legacy-graphics-probe.cpp') "/Fo$OutputPath/graphics-probe.obj" "/Fe$OutputPath/UnityRemix.LegacyProbe.exe" /link user32.lib d3dcompiler.lib /INCREMENTAL:NO
if ($LASTEXITCODE -ne 0) { throw 'Legacy graphics probe build failed.' }
& cl.exe /nologo /std:c++17 /O2 /MT /EHsc /W4 /WX (Join-Path $PSScriptRoot 'legacy-mono-probe.cpp') "/Fo$OutputPath/mono-probe.obj" "/Fe$OutputPath/UnityRemix.LegacyMonoProbe.exe" /link /INCREMENTAL:NO
if ($LASTEXITCODE -ne 0) { throw 'Legacy Mono probe build failed.' }
} finally { Restore-UnityRemixBuildEnvironment $buildEnvironment }
