param(
    [string]$HeaderDirectory = (Join-Path $PSScriptRoot '../artifacts/upstream-api'),
    [string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/native-adapters/fixtures')
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$OutputPath = (Resolve-Path -LiteralPath $OutputPath).Path
# Compile each downloaded, official release header independently. The native
# compiler supplies field offsets; managed declarations are not the oracle.
foreach ($header in Get-ChildItem -LiteralPath $HeaderDirectory -Filter 'remix-*.h' | Sort-Object Name) {
    $text = Get-Content -LiteralPath $header.FullName -Raw
    $table = [regex]::Match($text, '(?s)typedef struct remixapi_Interface\s*\{(.*?)\} remixapi_Interface;').Groups[1].Value
    $fields = @([regex]::Matches($table, 'PFN_\w+\s+(\w+)\s*;') | ForEach-Object { $_.Groups[1].Value })
    if ($fields.Count -lt 17) { throw "No interface found: $($header.Name)" }
    $offsets = ($fields | ForEach-Object { '  if (strcmp(name, "' + $_ + '") == 0) return offsetof(remixapi_Interface, ' + $_ + ');' }) -join "`n"
    $include = $header.FullName.Replace('\', '/')
    $source = @"
#define REMIX_LIBRARY_EXPORTS
#define REMIX_WINAPI_NO_LIBRARY_LOADER
#include "$include"
#include <cstddef>
#include <cstring>
extern "C" __declspec(dllexport) size_t __cdecl UnityRemix_TestFieldOffset(const char* name) {
$offsets
  return size_t(-1);
}
extern "C" __declspec(dllexport) unsigned __cdecl UnityRemix_TestMinor() { return REMIXAPI_VERSION_MINOR; }
extern "C" __declspec(dllexport) remixapi_ErrorCode REMIXAPI_CALL UnityRemix_TestSetConfig(const char* name, const char* value) {
  return strcmp(name, "probe") == 0 && strcmp(value, "ok") == 0 ? REMIXAPI_ERROR_CODE_SUCCESS : static_cast<remixapi_ErrorCode>(3);
}
remixapi_ErrorCode REMIXAPI_CALL remixapi_InitializeLibrary(const remixapi_InitializeLibraryInfo* info, remixapi_Interface* result) {
  if (info->sType != REMIXAPI_STRUCT_TYPE_INITIALIZE_LIBRARY_INFO || info->pNext != nullptr) return static_cast<remixapi_ErrorCode>(3);
  if (REMIXAPI_VERSION_GET_MAJOR(info->version) != 0 || REMIXAPI_VERSION_GET_MINOR(info->version) != REMIXAPI_VERSION_MINOR) return REMIXAPI_ERROR_CODE_INCOMPATIBLE_VERSION;
  for (size_t offset = 0; offset < sizeof(*result); offset += sizeof(void*)) {
    uintptr_t sentinel = 0x10000 + offset + 1;
    memcpy(reinterpret_cast<char*>(result) + offset, &sentinel, sizeof(sentinel));
  }
  result->SetConfigVariable = &UnityRemix_TestSetConfig;
  return REMIXAPI_ERROR_CODE_SUCCESS;
}
"@
    $file = Join-Path $OutputPath ($header.BaseName + '.cpp')
    Set-Content -LiteralPath $file -Value $source -Encoding UTF8
    & cl.exe /nologo /LD /std:c++17 /O2 /MT /EHsc /W4 /WX $file "/Fo$OutputPath/$($header.BaseName).obj" "/Fe$OutputPath/$($header.BaseName).dll" /link /INCREMENTAL:NO
    if ($LASTEXITCODE -ne 0) { throw "Fixture build failed: $($header.Name)" }
}
Write-Host "Built native fixtures from official Remix release headers in $OutputPath"
