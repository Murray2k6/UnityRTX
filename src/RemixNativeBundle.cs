using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace UnityRemix
{
    // Windows shares dependency names across an entire process. Check before loading
    // and preload our exact files so delayed imports cannot pick a game-root DLL.
    internal static class RemixNativeBundle
    {
        private const uint SearchOwnDirectoryAndSystem32 = 0x00000100 | 0x00000800;
        private static readonly List<IntPtr> retainedModules = new List<IntPtr>();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetModuleFileNameW(IntPtr module, StringBuilder path, int size);

        internal static string Hash(string path)
        {
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
        }

        internal static Dictionary<string, string> Validate(string libraryPath)
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(libraryPath));
            string prefix = directory + Path.DirectorySeparatorChar;
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(Path.Combine(directory, "native-manifest.sha256")))
            {
                if (line.Length < 67 || line.Substring(64, 2) != "  ")
                    throw new InvalidDataException("Malformed UnityRemix native manifest.");
                string expectedHash = line.Substring(0, 64);
                foreach (char c in expectedHash)
                    if (!Uri.IsHexDigit(c)) throw new InvalidDataException("Invalid native manifest hash.");
                string name = line.Substring(66);
                string full = Path.GetFullPath(Path.Combine(directory, name));
                if (Path.IsPathRooted(name) || name.IndexOf(':') >= 0 ||
                    !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || files.ContainsKey(full))
                    throw new InvalidDataException("Invalid or duplicate native manifest path: " + name);
                if (!File.Exists(full) || !string.Equals(Hash(full), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("UnityRemix native file is missing or changed: " + name + ". Reinstall the complete package.");
                files.Add(full, expectedHash);
            }
            if (!files.ContainsKey(Path.GetFullPath(libraryPath)))
                throw new InvalidDataException("The native manifest does not contain the renderer.");
            foreach (string file in Directory.GetFiles(directory, "*.dll"))
                if (!files.ContainsKey(file)) throw new InvalidDataException("Unlisted DLL in the private renderer: " + Path.GetFileName(file));
            return files;
        }

        public static IntPtr Load(string libraryPath)
        {
            libraryPath = Path.GetFullPath(libraryPath);
            var files = Validate(libraryPath);
            var libraries = new List<string>();
            foreach (var entry in files)
            {
                string file = entry.Key;
                if (!string.Equals(Path.GetExtension(file), ".dll", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetDirectoryName(file), Path.GetDirectoryName(libraryPath), StringComparison.OrdinalIgnoreCase)) continue;
                libraries.Add(file);
                IntPtr existing = GetModuleHandleW(Path.GetFileName(file));
                if (existing == IntPtr.Zero) continue;
                var path = new StringBuilder(32768);
                if (GetModuleFileNameW(existing, path, path.Capacity) == 0 ||
                    !string.Equals(Hash(path.ToString()), entry.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A different " + Path.GetFileName(file) +
                        " is already loaded by the game or another mod. Two incompatible copies cannot safely share this process.");
            }
            libraries.Sort(StringComparer.OrdinalIgnoreCase);
            libraries.Remove(libraryPath);
            libraries.Add(libraryPath);
            IntPtr renderer = IntPtr.Zero;
            foreach (string file in libraries)
            {
                IntPtr module = LoadLibraryExW(file, IntPtr.Zero, SearchOwnDirectoryAndSystem32);
                if (module == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not load private Remix file " + file);
                // Native libraries may create background workers; keep them mapped until exit.
                retainedModules.Add(module);
                if (file == libraryPath) renderer = module;
            }
            return renderer;
        }
    }
}
