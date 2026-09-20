using System;
using System.IO;

namespace UnityRemix
{
    internal static class RemixRuntimeSelection
    {
        public const string LibraryName = "UnityRemix.Native.dll";

        public static string Select(string gameRoot, string bepinexRoot)
        {
            string bundled = Path.Combine(bepinexRoot, "UnityRemix", "native", "win-x64", LibraryName);
            // A damaged private installation must not silently load a different renderer.
            if (Directory.Exists(Path.GetDirectoryName(bundled))) return Path.GetFullPath(bundled);
            return Path.GetFullPath(Path.Combine(gameRoot, "d3d9.dll"));
        }

        public static bool IsBundled(string path) =>
            string.Equals(Path.GetFileName(path), LibraryName, StringComparison.OrdinalIgnoreCase);
    }
}
