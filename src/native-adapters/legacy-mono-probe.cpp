#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <stdexcept>
#include <string>

template<typename T> T bind(HMODULE module, const char* name) {
  auto address = GetProcAddress(module, name);
  if (!address) { throw std::runtime_error(name); }
  return reinterpret_cast<T>(address);
}

int main(int argc, char** argv) {
  SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
  try {
    if (argc != 5) { throw std::runtime_error("Usage: probe mono.dll assembly-search-path plugin.dll mono-config"); }
    HMODULE mono = LoadLibraryA(argv[1]);
    if (!mono) { throw std::runtime_error("Cannot load original game Mono runtime"); }
    auto setPath = bind<void(__cdecl*)(const char*)>(mono, "mono_set_assemblies_path");
    auto config = bind<void(__cdecl*)(const char*)>(mono, "mono_config_parse");
    auto init = bind<void*(__cdecl*)(const char*, const char*)>(mono, "mono_jit_init_version");
    auto open = bind<void*(__cdecl*)(void*, const char*)>(mono, "mono_domain_assembly_open");
    auto getImage = bind<void*(__cdecl*)(void*)>(mono, "mono_assembly_get_image");
    auto getClass = bind<void*(__cdecl*)(void*, const char*, const char*)>(mono, "mono_class_from_name");
    auto parent = bind<void*(__cdecl*)(void*)>(mono, "mono_class_get_parent");
    auto method = bind<void*(__cdecl*)(void*, const char*, int)>(mono, "mono_class_get_method_from_name");
    auto invoke = bind<void*(__cdecl*)(void*, void*, void**, void**)>(mono, "mono_runtime_invoke");
    auto stringNew = bind<void*(__cdecl*)(void*, const char*)>(mono, "mono_string_new");
    auto unbox = bind<void*(__cdecl*)(void*)>(mono, "mono_object_unbox");
    auto cleanup = bind<void(__cdecl*)(void*)>(mono, "mono_jit_cleanup");
    setPath(argv[2]);
    config(argv[4]);
    void* domain = init("UnityRemix isolated legacy Mono probe", "v2.0.50727");
    if (!domain) { throw std::runtime_error("Original Mono could not initialize"); }
    void* assembly = open(domain, argv[3]);
    if (!assembly) { throw std::runtime_error("Original Mono could not load the compiled plugin"); }
    void* image = getImage(assembly);
    void* plugin = getClass(image, "UnityRemix.Legacy", "LegacyPlugin");
    if (!plugin || !parent(plugin)) { throw std::runtime_error("BepInEx/Unity base class resolution failed"); }
    void* policy = getClass(image, "UnityRemix.Legacy", "LegacyMaterialPolicy");
    void* shouldConvert = method(policy, "ShouldConvert", 1);
    if (!shouldConvert) { throw std::runtime_error("Material policy method missing"); }
    for (const char* shader : { "Diffuse", "GUI/Text Shader" }) {
      void* args[] = { stringNew(domain, shader) };
      void* exception = nullptr;
      void* result = invoke(shouldConvert, nullptr, args, &exception);
      if (exception || !result) { throw std::runtime_error("Policy execution failed in original Mono"); }
      const bool actual = *static_cast<unsigned char*>(unbox(result)) != 0;
      if (actual != (std::string(shader) == "Diffuse")) { throw std::runtime_error("Incorrect policy result"); }
    }
    cleanup(domain);
    std::puts("PASS: original 32-bit Mono loaded UnityRemix, resolved BepInEx/Unity types, and executed its material policy. No game was started.");
    return 0;
  } catch (const std::exception& error) {
    std::fprintf(stderr, "FAIL: %s\n", error.what());
    return 1;
  }
}
