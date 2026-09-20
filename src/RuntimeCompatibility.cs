using System;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityRemix
{
    internal static class RuntimeCompatibility
    {
#if BEPINEX6_IL2CPP
        public const string RuntimeName = "BepInEx 6 / IL2CPP";
#elif BEPINEX6_MONO
        public const string RuntimeName = "BepInEx 6 / Mono";
#else
        public const string RuntimeName = "BepInEx 5 / Mono";
#endif
        public static bool CanReadD3D11Buffers =>
            Application.platform == RuntimePlatform.WindowsPlayer &&
            SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D11;

        public static bool Check(ManualLogSource log)
        {
            var loaderAssembly = typeof(BepInEx.Configuration.ConfigFile).Assembly;
            var loaderVersion = loaderAssembly.GetName().Version;
            string loaderBuild = loaderAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            log.LogInfo($"Detected BepInEx {loaderVersion} (build {loaderBuild ?? "unspecified"}); adapter {RuntimeName}.");
            log.LogInfo($"Runtime: {RuntimeName}; Unity {Application.unityVersion}; {IntPtr.Size * 8}-bit; graphics {SystemInfo.graphicsDeviceType}");
#if BEPINEX6_IL2CPP || BEPINEX6_MONO
            const int expectedLoaderMajor = 6;
#else
            const int expectedLoaderMajor = 5;
#endif
            if (loaderVersion == null || loaderVersion.Major != expectedLoaderMajor)
            {
                log.LogError("The installed UnityRemix build does not match this BepInEx major version. Run the package installer to select the matching plugin.");
                return false;
            }
            if (Application.platform != RuntimePlatform.WindowsPlayer)
            {
                log.LogError("This Remix renderer requires a Windows player (Win32 and Direct3D). Initialization skipped.");
                return false;
            }
            if (IntPtr.Size != 8)
            {
                log.LogError("The current Remix ray-tracing bridge requires a 64-bit game. Initialization skipped.");
                return false;
            }
            if (!CanReadD3D11Buffers)
                log.LogWarning("D3D11 mesh readback is unavailable on this graphics API. CPU-readable meshes and BakeMesh remain available. Use -force-d3d11 if the game supports it.");
            return true;
        }
    }
}
