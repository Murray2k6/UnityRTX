using System;
using System.Runtime.InteropServices;

namespace UnityRemix
{
    // Published contracts, not Remix product release numbers. Patch versions are
    // not discoverable through InitializeLibrary: the runtime checks the minor ABI.
    internal sealed class RemixApiAdapter
    {
        public readonly uint Minor;
        public readonly string Name;
        public readonly string[] Slots;
        public bool IsUnityBridge => Minor == 1000;
        public ulong RequestedVersion => RemixAPI.REMIXAPI_VERSION_MAKE(0, Minor, 0);
        public string VersionLabel => "0." + Minor + ".x";

        private RemixApiAdapter(uint minor, string name, string[] slots)
        {
            Minor = minor;
            Name = name;
            Slots = slots;
        }

        private static string[] UpstreamSlots(int count)
        {
            var slots = new[] { "Shutdown", "CreateMaterial", "DestroyMaterial", "CreateMesh", "DestroyMesh",
                "SetupCamera", "DrawInstance", "CreateLight", "DestroyLight", "DrawLightInstance", "SetConfigVariable",
                "dxvk_CreateD3D9", "dxvk_RegisterD3D9Device", "dxvk_GetExternalSwapchain", "dxvk_GetVkImage",
                "dxvk_CopyRenderingOutput", "dxvk_SetDefaultOutput", "pick_RequestObjectPicking", "pick_HighlightObjects",
                "Startup", "Present", "SetCameraMediumMaterial" };
            Array.Resize(ref slots, count);
            return slots;
        }

        private static string[] BridgeSlots()
        {
            var fields = typeof(RemixAPI.remixapi_Interface).GetFields();
            Array.Sort(fields, (a, b) => Marshal.OffsetOf<RemixAPI.remixapi_Interface>(a.Name).ToInt64()
                .CompareTo(Marshal.OffsetOf<RemixAPI.remixapi_Interface>(b.Name).ToInt64()));
            var result = new string[fields.Length];
            for (int i = 0; i < fields.Length; i++) result[i] = fields[i].Name;
            return result;
        }

        internal static readonly RemixApiAdapter[] Known = {
            new RemixApiAdapter(1000, "Unity bridge", BridgeSlots()),
            new RemixApiAdapter(6, "NVIDIA upstream", UpstreamSlots(22)),
            new RemixApiAdapter(5, "NVIDIA upstream", UpstreamSlots(21)),
            new RemixApiAdapter(4, "NVIDIA upstream", UpstreamSlots(21)),
            new RemixApiAdapter(2, "NVIDIA upstream", UpstreamSlots(17))
        };

        // The published append-only table may be shorter in an earlier patch.
        // Reject oversized tables (including old forks reusing upstream version IDs).
        internal bool AcceptsSlotCount(int count) => Minor == 4 ? count == 19 || count == 21
            : Minor == 6 ? count == 21 || count == 22 : count == Slots.Length;

        internal RemixAPI.remixapi_Interface Adapt(IntPtr[] pointers)
        {
            if (!AcceptsSlotCount(pointers.Length))
                throw new NotSupportedException("Unexpected interface size for Remix API " + VersionLabel);
            object result = new RemixAPI.remixapi_Interface();
            for (int i = 0; i < pointers.Length; i++)
                typeof(RemixAPI.remixapi_Interface).GetField(Slots[i]).SetValue(result, pointers[i]);
            return (RemixAPI.remixapi_Interface)result;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate RemixAPI.remixapi_ErrorCode Initialize(ref RemixAPI.remixapi_InitializeLibraryInfo info, IntPtr output);

        internal static RemixAPI.remixapi_ErrorCode Negotiate(Initialize initialize,
            out RemixApiAdapter adapter, out RemixAPI.remixapi_Interface api)
        {
            adapter = null;
            api = default;
            // Native initialization has no output-size argument. Avoid writing a
            // runtime-sized table directly into a managed struct of a different ABI.
            const int capacity = 512;
            IntPtr output = Marshal.AllocHGlobal(capacity * IntPtr.Size);
            try
            {
                foreach (var candidate in Known)
                {
                    for (int i = 0; i < capacity; i++) Marshal.WriteIntPtr(output, i * IntPtr.Size, IntPtr.Zero);
                    var info = new RemixAPI.remixapi_InitializeLibraryInfo {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INITIALIZE_LIBRARY_INFO,
                        version = candidate.RequestedVersion
                    };
                    var result = initialize(ref info, output);
                    if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_INCOMPATIBLE_VERSION) continue;
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS) return result;
                    int count = 0;
                    for (int i = 0; i < capacity; i++)
                        if (Marshal.ReadIntPtr(output, i * IntPtr.Size) != IntPtr.Zero) count = i + 1;
                    var pointers = new IntPtr[count];
                    Marshal.Copy(output, pointers, 0, count);
                    api = candidate.Adapt(pointers);
                    adapter = candidate;
                    return result;
                }
                return RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_INCOMPATIBLE_VERSION;
            }
            finally { Marshal.FreeHGlobal(output); }
        }
    }
}
