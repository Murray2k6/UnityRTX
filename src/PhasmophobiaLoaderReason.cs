using System;
using System.IO;

namespace UnityRemix
{
    internal static class PhasmophobiaLoaderReason
    {
        public static bool Matches(string reason, string gameRoot = null)
        {
            if (reason == null) return false;
            return string.Equals(reason, ".doorstop_version | version", StringComparison.Ordinal) ||
                   string.Equals(reason, "doorstop_config.ini | config", StringComparison.Ordinal) ||
                   string.Equals(reason, "winhttp.dll | winhttp", StringComparison.Ordinal) ||
                   (!string.IsNullOrEmpty(gameRoot) && Path.IsPathFullyQualified(gameRoot) &&
                    string.Equals(reason, Path.Combine(gameRoot, "winhttp.dll"), StringComparison.OrdinalIgnoreCase));
        }
    }
}
