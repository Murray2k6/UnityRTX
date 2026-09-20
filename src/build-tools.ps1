Set-StrictMode -Version Latest

function Restore-UnityRemixBuildEnvironment {
    param([Parameter(Mandatory)][Collections.IDictionary]$Snapshot)
    foreach ($name in [Environment]::GetEnvironmentVariables('Process').Keys) {
        if (!$Snapshot.Contains($name)) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    }
    foreach ($name in $Snapshot.Keys) { [Environment]::SetEnvironmentVariable($name, $Snapshot[$name], 'Process') }
}

function Initialize-UnityRemixMsvc {
    param([ValidateSet('x64','x64_x86')][string]$Architecture = 'x64')
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio Installer/vswhere.exe is required to locate the C++ toolchain.' }
    $installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or !$installation) { throw 'Install the Visual Studio C++ x86/x64 build tools.' }
    $vcvars = Join-Path ($installation | Select-Object -First 1) 'VC/Auxiliary/Build/vcvarsall.bat'
    $compilerEnvironment = & $env:ComSpec /d /c "call `"$vcvars`" $Architecture >nul && set"
    if ($LASTEXITCODE -ne 0) { throw "MSVC environment initialization failed for $Architecture." }
    foreach ($line in $compilerEnvironment) {
        if ($line -match '^([^=]+)=(.*)$') { [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process') }
    }
}

function Get-UnityRemixMeson {
    param([string]$PythonPath = '', [string]$ToolsPath = '')
    if ($ToolsPath -and (Test-Path -LiteralPath $ToolsPath -PathType Container)) {
        $tools = (Resolve-Path -LiteralPath $ToolsPath).Path
        $env:PYTHONPATH = (@($tools, $env:PYTHONPATH) | Where-Object { $_ }) -join [IO.Path]::PathSeparator
        $env:PATH = (@((Join-Path $tools 'Scripts'), (Join-Path $tools 'bin'), (Join-Path $tools 'ninja/data/bin'), $env:PATH)) -join [IO.Path]::PathSeparator
    }
    if (!$PythonPath) {
        $meson = Get-Command meson -CommandType Application -ErrorAction SilentlyContinue
        if ($meson) { return [pscustomobject]@{ File = $meson.Source; Prefix = @() } }
        $python = Get-Command python -CommandType Application -ErrorAction SilentlyContinue
        if ($python -and $python.Source -notlike '*WindowsApps*') { $PythonPath = $python.Source }
        else {
            $launcher = Get-Command py -CommandType Application -ErrorAction SilentlyContinue
            if ($launcher) {
                try {
                    $discovered = & $launcher.Source -3 -c 'import sys; print(sys.executable)' 2>$null
                    if ($LASTEXITCODE -eq 0) { $PythonPath = $discovered | Select-Object -Last 1 }
                } catch { $PythonPath = '' }
            }
        }
    }
    if (!$PythonPath -or !(Test-Path -LiteralPath $PythonPath -PathType Leaf)) {
        throw 'Python/Meson not found. Install Python and Meson, or pass -PythonPath to an existing Python executable.'
    }
    $PythonPath = (Resolve-Path -LiteralPath $PythonPath).Path
    $hasMeson = $false
    try { & $PythonPath -c 'import mesonbuild' 2>$null; $hasMeson = $LASTEXITCODE -eq 0 } catch { $hasMeson = $false }
    if (!$hasMeson) { throw "Meson is not available to $PythonPath. Install meson or set -ToolsPath to its module directory." }
    $env:PATH = (Split-Path $PythonPath -Parent) + [IO.Path]::PathSeparator + $env:PATH
    [pscustomobject]@{ File = $PythonPath; Prefix = @('-m','mesonbuild.mesonmain') }
}

function Invoke-UnityRemixMeson {
    param([Parameter(Mandatory)]$Command, [Parameter(Mandatory)][string[]]$Arguments)
    & $Command.File @($Command.Prefix) @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Meson failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}
