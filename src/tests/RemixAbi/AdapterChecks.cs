using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityRemix;
using Error = UnityRemix.RemixAPI.remixapi_ErrorCode;
using Info = UnityRemix.RemixAPI.remixapi_InitializeLibraryInfo;

static class AdapterChecks
{
    public static void Check()
    {
        foreach (var target in RemixApiAdapter.Known)
        {
            int attempts = 0;
            Error Initialize(ref Info info, IntPtr output)
            {
                attempts++;
                if (info.version != target.RequestedVersion)
                {
                    Marshal.WriteIntPtr(output, 100 * IntPtr.Size, new IntPtr(42));
                    return Error.REMIXAPI_ERROR_CODE_INCOMPATIBLE_VERSION;
                }
                for (int i = 0; i < target.Slots.Length; i++) Marshal.WriteIntPtr(output, i * IntPtr.Size, new IntPtr(i + 1));
                return Error.REMIXAPI_ERROR_CODE_SUCCESS;
            }
            var result = RemixApiAdapter.Negotiate(Initialize, out var adapter, out var api);
            if (result != Error.REMIXAPI_ERROR_CODE_SUCCESS || adapter != target || attempts != Array.IndexOf(RemixApiAdapter.Known, target) + 1)
                throw new Exception("Negotiation failed: " + target.VersionLabel);
            foreach (var field in typeof(RemixAPI.remixapi_Interface).GetFields())
                if ((IntPtr)field.GetValue(api) != new IntPtr(Array.IndexOf(target.Slots, field.Name) + 1))
                    throw new Exception("Wrong normalized function slot: " + field.Name);
        }
        int failures = 0;
        Error Failed(ref Info info, IntPtr output) { failures++; return Error.REMIXAPI_ERROR_CODE_INVALID_ARGUMENTS; }
        if (RemixApiAdapter.Negotiate(Failed, out var failedAdapter, out _) != Error.REMIXAPI_ERROR_CODE_INVALID_ARGUMENTS || failures != 1 || failedAdapter != null)
            throw new Exception("A non-version error was retried or hidden.");
        Error Unknown(ref Info info, IntPtr output) => Error.REMIXAPI_ERROR_CODE_INCOMPATIBLE_VERSION;
        if (RemixApiAdapter.Negotiate(Unknown, out var unknown, out _) != Error.REMIXAPI_ERROR_CODE_INCOMPATIBLE_VERSION || unknown != null)
            throw new Exception("Unknown API was accepted.");
        foreach (int slots in new[] { 17, 23, 41 })
        {
            Error WrongLayout(ref Info info, IntPtr output)
            {
                if (info.version != RemixAPI.REMIXAPI_VERSION_MAKE(0, 6, 0)) return Error.REMIXAPI_ERROR_CODE_INCOMPATIBLE_VERSION;
                Marshal.WriteIntPtr(output, (slots - 1) * IntPtr.Size, new IntPtr(1));
                return Error.REMIXAPI_ERROR_CODE_SUCCESS;
            }
            bool rejected = false;
            try { RemixApiAdapter.Negotiate(WrongLayout, out _, out _); } catch (NotSupportedException) { rejected = true; }
            if (!rejected) throw new Exception("Incorrect 0.6 table length accepted: " + slots);
        }
        foreach (int slots in new[] { 21, 22 })
        {
            var response = RemixRuntimeProbe.Parse($"status=ok\nminor=6\nslots={slots}\nunity-output=0\nnative-menu=0\n");
            if (response.Status != "ok" || response.Minor != 6 || response.UnityOutput || response.NativeMenu)
                throw new Exception("Published upstream contract was not detected.");
        }
        foreach (string output in new[] { "", "status=ok", "status=ok\nstatus=ok", "status=ok\nminor=6\nslots=41\nunity-output=0\nnative-menu=0",
            "status=ok\nminor=7\nslots=22\nunity-output=1\nnative-menu=1", "status=ok\nminor=6\nslots=22\nunity-output=0\nnative-menu=1" })
            if (RemixRuntimeProbe.Parse(output).Status == "ok") throw new Exception("Invalid version response accepted.");
        Console.WriteLine("PASS: version negotiation, slot mapping, error propagation, old/new patch lengths, and probe parsing.");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate nuint FieldOffset([MarshalAs(UnmanagedType.LPStr)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate uint Minor();

    public static void CheckNativeFixtures(string directory, string helper)
    {
        int count = 0;
        foreach (string dll in Directory.GetFiles(directory, "remix-*.dll"))
        {
            IntPtr module = NativeLibrary.Load(Path.GetFullPath(dll));
            try
            {
                var initialize = Marshal.GetDelegateForFunctionPointer<RemixApiAdapter.Initialize>(NativeLibrary.GetExport(module, "remixapi_InitializeLibrary"));
                var offset = Marshal.GetDelegateForFunctionPointer<FieldOffset>(NativeLibrary.GetExport(module, "UnityRemix_TestFieldOffset"));
                uint minor = Marshal.GetDelegateForFunctionPointer<Minor>(NativeLibrary.GetExport(module, "UnityRemix_TestMinor"))();
                var result = RemixApiAdapter.Negotiate(initialize, out var adapter, out var api);
                if (result != Error.REMIXAPI_ERROR_CODE_SUCCESS || adapter.Minor != minor) throw new Exception("Native fixture negotiation failed: " + dll);
                foreach (var field in typeof(RemixAPI.remixapi_Interface).GetFields())
                {
                    nuint nativeOffset = offset(field.Name);
                    IntPtr expected = nativeOffset == nuint.MaxValue ? IntPtr.Zero : new IntPtr(0x10001 + (long)nativeOffset);
                    if (field.Name == "SetConfigVariable") expected = NativeLibrary.GetExport(module, "UnityRemix_TestSetConfig");
                    if ((IntPtr)field.GetValue(api) != expected) throw new Exception("Published header offset mismatch: " + dll + ": " + field.Name);
                }
                var setConfig = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_SetConfigVariable>(api.SetConfigVariable);
                if (setConfig("probe", "ok") != Error.REMIXAPI_ERROR_CODE_SUCCESS) throw new Exception("Mapped native function call failed.");
                if (RemixAPI.MissingUnityCapability(api, _ => IntPtr.Zero) == null) throw new Exception("Upstream fixture incorrectly accepted as full Unity renderer.");
                var detected = RemixRuntimeProbe.Run(helper, dll, CancellationToken.None);
                if (detected.Status != "ok" || detected.Minor != minor || detected.UnityOutput || detected.NativeMenu)
                    throw new Exception("Isolated probe failed: " + dll + ": " + detected.Description);
                count++;
            }
            finally { NativeLibrary.Free(module); }
        }
        if (count < 4) throw new Exception("Missing native fixture families.");
        Console.WriteLine($"PASS: {count} official release headers, native function-table offsets, mapped calls, capability checks and isolated version probes.");
    }
}
