using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnityRemix
{
    // File/process work only. Never call Unity or attach this worker to IL2CPP.
    // Loading an unselected DLL in Unity would contaminate its dependency set.
    internal static class RemixRuntimeProbe
    {
        public const string FileName = "UnityRemix.Probe.exe";

        internal sealed class Result
        {
            public string Status;
            public uint Minor;
            public int Slots;
            public bool UnityOutput;
            public bool NativeMenu;
            public string Description => Status == "ok"
                ? "API 0." + Minor + ".x (" + Slots + " functions); Unity texture sharing " +
                  (UnityOutput ? "available" : "unavailable") + "; native menu inside Unity " + (NativeMenu ? "available" : "unavailable")
                : "API detection " + Status;
        }

        internal sealed class Report
        {
            public string LibraryPath;
            public string Error;
            public readonly List<string> Messages = new List<string>();
        }

        internal static Result Parse(string output)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var reader = new StringReader(output))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int separator = line.IndexOf('=');
                    if (separator < 1) continue;
                    string key = line.Substring(0, separator);
                    if (key != "status" && key != "minor" && key != "slots" && key != "unity-output" && key != "native-menu") continue;
                    if (fields.ContainsKey(key)) return new Result { Status = "invalid-response" };
                    fields.Add(key, line.Substring(separator + 1));
                }
            }
            if (!fields.TryGetValue("status", out var status)) return new Result { Status = "no-response" };
            if (status != "ok") return new Result { Status = status };
            if (!fields.TryGetValue("minor", out var minorText) || !uint.TryParse(minorText, out var minor) ||
                !fields.TryGetValue("slots", out var slotsText) || !int.TryParse(slotsText, out var slots) ||
                !fields.TryGetValue("unity-output", out var outputText) || (outputText != "0" && outputText != "1") ||
                !fields.TryGetValue("native-menu", out var menuText) || (menuText != "0" && menuText != "1"))
                return new Result { Status = "invalid-response" };
            var adapter = Array.Find(RemixApiAdapter.Known, item => item.Minor == minor);
            if (adapter == null || !adapter.AcceptsSlotCount(slots) ||
                (menuText == "1" && outputText != "1")) return new Result { Status = "unknown-layout" };
            return new Result { Status = status, Minor = minor, Slots = slots, UnityOutput = outputText == "1", NativeMenu = menuText == "1" };
        }

        internal static Result Run(string helperPath, string libraryPath, CancellationToken cancellation, int timeoutMilliseconds = 15000)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!File.Exists(libraryPath)) return new Result { Status = "file-not-found" };
            if (!File.Exists(helperPath)) return new Result { Status = "helper-not-installed" };
            libraryPath = Path.GetFullPath(libraryPath);
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo {
                    FileName = Path.GetFullPath(helperPath),
                    Arguments = "\"" + libraryPath + "\"",
                    WorkingDirectory = Path.GetDirectoryName(helperPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                if (!process.Start()) return new Result { Status = "could-not-start-helper" };
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> errors = process.StandardError.ReadToEndAsync();
                var elapsed = Stopwatch.StartNew();
                while (!process.WaitForExit(100))
                {
                    if (cancellation.IsCancellationRequested || elapsed.ElapsedMilliseconds >= timeoutMilliseconds)
                    {
                        // This process is our own helper; never stop the game or an unrelated process.
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        process.WaitForExit(1000);
                        return new Result { Status = cancellation.IsCancellationRequested ? "cancelled" : "timed-out" };
                    }
                }
                if (!Task.WaitAll(new Task[] { output, errors }, 1000)) return new Result { Status = "incomplete-response" };
                var result = Parse(output.Result);
                if (process.ExitCode != 0 && result.Status == "ok") result.Status = "helper-failed";
                return result;
            }
        }

        internal static Report Inspect(string gameRoot, string bepinexRoot, CancellationToken cancellation)
        {
            var report = new Report();
            try
            {
                report.LibraryPath = RemixRuntimeSelection.Select(gameRoot, bepinexRoot);
                bool bundled = RemixRuntimeSelection.IsBundled(report.LibraryPath);
                string helper = Path.Combine(Path.GetDirectoryName(report.LibraryPath), FileName);
                if (bundled)
                {
                    var files = RemixNativeBundle.Validate(report.LibraryPath);
                    if (File.Exists(helper) && !files.ContainsKey(helper))
                        throw new InvalidDataException("The version probe is not listed in the native manifest. Reinstall the complete package.");
                }
                cancellation.ThrowIfCancellationRequested();
                string rootDll = Path.GetFullPath(Path.Combine(gameRoot, "d3d9.dll"));
                if (bundled && File.Exists(rootDll))
                {
                    var root = Run(helper, rootDll, cancellation);
                    report.Messages.Add("Game-root Remix: " + root.Description + ".");
                    if (root.Status == "ok" && (!root.UnityOutput || !root.NativeMenu))
                        report.Messages.Add("Selecting the packaged Unity bridge to preserve texture sharing and the native Remix menu.");
                }
                cancellation.ThrowIfCancellationRequested();
                var selected = Run(helper, report.LibraryPath, cancellation);
                report.Messages.Add("Selected " + (bundled ? "private" : "game-root") + " Remix: " + selected.Description + ".");
                if (selected.Status != "ok" && selected.Status != "helper-not-installed")
                    throw new NotSupportedException("Selected Remix renderer " + selected.Description + ". Reinstall the complete UnityRemix package.");
                if (selected.Status == "ok" && (!selected.UnityOutput || !selected.NativeMenu))
                    throw new NotSupportedException("Detected " + selected.Description + ". The complete UnityRemix package includes the renderer needed for Unity textures and the native Remix menu.");
                string version = FileVersionInfo.GetVersionInfo(report.LibraryPath).FileVersion;
                if (!string.IsNullOrEmpty(version)) report.Messages.Add("Native DLL file version: " + version + ".");
            }
            catch (OperationCanceledException) { report.Error = "Remix version detection cancelled."; }
            catch (Exception error) { report.Error = error.Message; }
            return report;
        }
    }
}
