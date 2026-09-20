#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <array>
#include <cstdint>
#include <cstdio>
#include <string>

struct InitializeInfo { int type; void* next; uint64_t version; };
using Initialize = int (__stdcall*)(const InitializeInfo*, void*);

// No Startup or graphics calls. This process keeps unselected native dependencies
// out of the Unity process and lets the plugin contain faulty DLL initialization.
int wmain(int count, wchar_t** arguments) {
  SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
  if (count != 2) { std::puts("status=arguments"); return 2; }
  std::wstring path(arguments[1]);
  for (auto& c : path) { if (c == L'/') { c = L'\\'; } }
  HMODULE runtime = LoadLibraryExW(path.c_str(), nullptr,
    LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
  if (!runtime) { std::printf("status=load-failed\nwin32=%lu\n", GetLastError()); return 2; }
  auto initialize = reinterpret_cast<Initialize>(GetProcAddress(runtime, "remixapi_InitializeLibrary"));
  if (!initialize) { std::puts("status=no-api"); return 2; }
  for (unsigned minor : { 1000u, 6u, 5u, 4u, 2u }) {
    std::array<void*, 512> table{};
    InitializeInfo info{ 1, nullptr, uint64_t(minor) << 16 };
    int result = initialize(&info, table.data());
    if (result == 8) { continue; }
    if (result != 0) { std::printf("status=initialize-failed\nerror=%d\n", result); return 2; }
    size_t slots = 0;
    for (size_t i = 0; i < table.size(); i++) { if (table[i]) { slots = i + 1; } }
    const bool knownSize = minor == 1000 ? slots == 41 : minor == 6 ? slots == 21 || slots == 22
      : minor == 5 ? slots == 21 : minor == 4 ? slots == 19 || slots == 21 : slots == 17;
    if (!knownSize) {
      std::printf("status=unknown-layout\nminor=%u\nslots=%zu\n", minor, slots); return 2;
    }
    bool output = true;
    for (const char* name : { "remixapi_EnableUnityOutput", "remixapi_SetUnityOutputTargets",
      "remixapi_GetUnityOutputState", "remixapi_GetUnityRenderEvent", "remixapi_ResetScene" }) {
      output = output && GetProcAddress(runtime, name) != nullptr;
    }
    bool menu = output;
    for (const char* name : { "remixapi_imgui_RegisterDrawCallback", "remixapi_imgui_Begin", "remixapi_imgui_End" }) {
      menu = menu && GetProcAddress(runtime, name) != nullptr;
    }
    std::printf("status=ok\nminor=%u\nslots=%zu\nunity-output=%u\nnative-menu=%u\n", minor, slots, unsigned(output), unsigned(menu));
    // Exit without invoking native Shutdown: a graphics device was never started.
    return 0;
  }
  std::puts("status=unknown-version");
  return 2;
}
