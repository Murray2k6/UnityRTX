using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using UnityRemix;

TestErrorHandling.DisableCrashDialog();
try
{
if (IntPtr.Size != 8) throw new Exception("The Remix ABI requires x64.");
NativeBundleChecks.Check();
AdapterChecks.Check();
if (args.Length == 3 && args[0] == "--adapters") AdapterChecks.CheckNativeFixtures(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
if (args.Length == 3 && args[0] == "--detect")
{
    var report = RemixRuntimeProbe.Inspect(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), System.Threading.CancellationToken.None);
    foreach (string message in report.Messages) Console.WriteLine(message);
    if (report.Error != null) throw new Exception(report.Error);
    Console.WriteLine("PASS: installed-runtime version detection and selection without graphics startup.");
}
foreach (var reason in new[] { ".doorstop_version | version", "doorstop_config.ini | config", "winhttp.dll | winhttp" })
    if (!PhasmophobiaLoaderReason.Matches(reason)) throw new Exception("Known loader reason was not recognized.");
foreach (var reason in new[] { null, "", "doorstop_config.ini", "doorstop_config.ini | integrity", "account | config", "xdoorstop_config.ini | config", "doorstop_config.ini | config:other" })
    if (PhasmophobiaLoaderReason.Matches(reason)) throw new Exception("Unverified reason would be skipped: " + reason);
Console.WriteLine("PASS: loader workaround accepts only the verified file reasons.");
string verifiedGameRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "UnityRemixTestGame"));
string verifiedLoader = Path.Combine(verifiedGameRoot, "winhttp.dll");
if (!PhasmophobiaLoaderReason.Matches(verifiedLoader, verifiedGameRoot) ||
    !PhasmophobiaLoaderReason.Matches(verifiedLoader.ToUpperInvariant(), verifiedGameRoot))
    throw new Exception("Verified loader path was not recognized.");
foreach (var reason in new[] { verifiedLoader + ".other", Path.Combine(verifiedGameRoot, "other.dll"),
    Path.Combine(verifiedGameRoot, "nested", "winhttp.dll"), Path.Combine(verifiedGameRoot + "Other", "winhttp.dll") })
    if (PhasmophobiaLoaderReason.Matches(reason, verifiedGameRoot)) throw new Exception("Unverified path would be skipped: " + reason);
if (PhasmophobiaLoaderReason.Matches(verifiedLoader) || PhasmophobiaLoaderReason.Matches("winhttp.dll", ""))
    throw new Exception("Loader path was accepted without a verified game root.");
Console.WriteLine("PASS: module path workaround is restricted to the verified game root.");
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "native-layout.json")));
int checks = 0;
foreach (var entry in manifest.RootElement.GetProperty("values").EnumerateObject())
{
    string[] key = entry.Name.Split('.');
    Type type = typeof(RemixAPI).GetNestedType(key[0]) ?? throw new Exception("Missing type: " + key[0]);
    long actual = type.IsEnum ? Convert.ToInt64(Enum.Parse(type, key[1]))
        : key[1] == "size" ? Marshal.SizeOf(type)
        : Marshal.OffsetOf(type, key[1]).ToInt64();
    if (actual != entry.Value.GetInt64())
        throw new Exception($"Native ABI mismatch: {entry.Name}: managed={actual}, native={entry.Value}");
    checks++;
}
Console.WriteLine($"PASS: {checks} native ABI sizes, offsets and enum values (source {manifest.RootElement.GetProperty("commit").GetString()}).");
int shutdownCalls = 0;
RemixAPI.PFN_remixapi_Shutdown shutdownCallback = () => {
    shutdownCalls++;
    return RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS;
};
var shutdownApi = new RemixAPI.remixapi_Interface {
    Shutdown = Marshal.GetFunctionPointerForDelegate(shutdownCallback),
    Startup = new IntPtr(1)
};
if (RemixAPI.ShutdownRemix(ref shutdownApi) != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS || shutdownCalls != 1)
    throw new Exception("Native shutdown was not invoked exactly once.");
foreach (var field in typeof(RemixAPI.remixapi_Interface).GetFields(BindingFlags.Public | BindingFlags.Instance))
    if ((IntPtr)field.GetValue(shutdownApi) != IntPtr.Zero)
        throw new Exception("Shutdown left an accessible native function: " + field.Name);
if (RemixAPI.ShutdownRemix(ref shutdownApi) != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_INVALID_ARGUMENTS || shutdownCalls != 1)
    throw new Exception("Repeated shutdown invoked a stale native function.");
GC.KeepAlive(shutdownCallback);
Console.WriteLine("PASS: shutdown invalidates native callbacks and rejects repeated cleanup.");
if ((args.Length == 2 || args.Length == 3) && args[0] == "--runtime")
{
    var result = RemixAPI.InitializeRemixAPI(Path.GetFullPath(args[1]), out var api, out var module);
    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
        throw new Exception("Native runtime initialization failed: " + result);
    try
    {
        foreach (var field in typeof(RemixAPI.remixapi_Interface).GetFields(BindingFlags.Public | BindingFlags.Instance))
            if ((IntPtr)field.GetValue(api) == IntPtr.Zero)
                throw new Exception("Missing runtime function: " + field.Name);
        Console.WriteLine("PASS: native API initialization and all interface function pointers.");
        if (RemixRuntimeSelection.IsBundled(args[1]))
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(args[1]));
            int loaded = 0;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            foreach (System.Diagnostics.ProcessModule dependency in process.Modules)
            {
                string expected = Path.Combine(directory, dependency.ModuleName);
                if (!File.Exists(expected)) continue;
                if (!string.Equals(dependency.FileName, expected, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Dependency escaped private runtime directory: " + dependency.FileName);
                loaded++;
            }
            Console.WriteLine($"PASS: {loaded} native modules loaded from the private bundle.");
        }
        if (args.Length == 3 && args[2] == "--startup") GraphicsStartup.Check(api);
    }
    // Once Startup has been attempted, native workers may still reference this
    // module after Shutdown. Match the plugin's process-lifetime module ownership.
    finally { if (args.Length != 3 || args[2] != "--startup") NativeLibrary.Free(module); }
}
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL: " + error);
    Environment.ExitCode = 1;
}

static class TestErrorHandling
{
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint flags);
    public static void DisableCrashDialog() => SetErrorMode(0x0001 | 0x0002 | 0x8000);
}

static class GraphicsStartup
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint extendedStyle, string className,
        string title, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    public static void Check(RemixAPI.remixapi_Interface api)
    {
        // An independent, hidden test window keeps game code out of this probe.
        IntPtr window = CreateWindowExW(0, "STATIC", "UnityRTX graphics startup probe",
            0x00CF0000, 0, 0, 640, 480, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        bool started = false;
        try
        {
            var startup = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_Startup>(api.Startup);
            var info = new RemixAPI.remixapi_StartupInfo {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_STARTUP_INFO,
                hwnd = window
            };
            Console.WriteLine("Starting native graphics device with an independent test window...");
            var result = startup(ref info);
            if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                throw new Exception("Graphics Startup failed: " + result);
            started = true;
            Console.WriteLine("PASS: native graphics device and swapchain Startup.");
            IntPtr resetAddress = RemixAPI.GetRemixProcAddress("remixapi_ResetScene");
            if (resetAddress == IntPtr.Zero) throw new Exception("Missing native scene-reset extension.");
            var reset = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_ResetScene>(resetAddress);
            if (reset() != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                throw new Exception("Native scene reset failed.");
            Console.WriteLine("PASS: native scene reset queued before shutdown.");
        }
        finally
        {
            if (started)
            {
                var result = RemixAPI.ShutdownRemix(ref api);
                if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    throw new Exception("Graphics Shutdown failed: " + result);
                Console.WriteLine("PASS: native graphics Shutdown.");
            }
            DestroyWindow(window);
        }
    }
}
