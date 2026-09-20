using System;
using System.Runtime.InteropServices;

namespace UnityRemix.Legacy
{
    internal sealed class LegacyBridge
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint VersionQuery();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int RendererQuery(out ulong version);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int TextureTag(IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CategoryTag(IntPtr texture, uint category);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WaterMaterial(IntPtr texture, float r, float g, float b, float ior);
        private readonly WaterMaterial registerWater;
        private readonly VersionQuery menuOpen;
        private readonly RendererQuery rendererInfo;
        private readonly TextureTag tagTexture;
        private readonly CategoryTag tagCategory;

        private static Delegate Bind(IntPtr module, string name, Type type)
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero) throw new NotSupportedException("Missing legacy Remix bridge export: " + name);
            return Marshal.GetDelegateForFunctionPointer(address, type);
        }

        public LegacyBridge()
        {
            if (IntPtr.Size != 4) throw new NotSupportedException("The legacy D3D9 client requires a 32-bit Unity player.");
            IntPtr module = GetModuleHandleW("d3d9.dll");
            if (module == IntPtr.Zero) throw new NotSupportedException("Direct3D 9 is not loaded. Start this game with -force-d3d9.");
            var version = (VersionQuery)Bind(module, "UnityRemix_LegacyBridgeVersion", typeof(VersionQuery));
            if (version() != 1) throw new NotSupportedException("The legacy bridge protocol does not match this UnityRemix plugin.");
            menuOpen = (VersionQuery)Bind(module, "UnityRemix_IsNativeMenuOpen", typeof(VersionQuery));
            rendererInfo = (RendererQuery)Bind(module, "UnityRemix_GetRendererInfo", typeof(RendererQuery));
            tagTexture = (TextureTag)Bind(module, "UnityRemix_TagUiTexture", typeof(TextureTag));
            IntPtr category = GetProcAddress(module, "UnityRemix_TagTextureCategory");
            if (category != IntPtr.Zero) tagCategory = (CategoryTag)Marshal.GetDelegateForFunctionPointer(category, typeof(CategoryTag));
            IntPtr water = GetProcAddress(module, "UnityRemix_RegisterWaterMaterial");
            if (water != IntPtr.Zero) registerWater = (WaterMaterial)Marshal.GetDelegateForFunctionPointer(water, typeof(WaterMaterial));
        }

        public bool MenuOpen { get { return menuOpen() != 0; } }
        public bool QueryRenderer(out ulong version) { return rendererInfo(out version) == 0 && version != 0; }
        public bool TagUiTexture(IntPtr texture) { return texture != IntPtr.Zero && tagTexture(texture) == 0; }
        public bool SupportsCategories { get { return tagCategory != null; } }
        public bool RegisterWater(IntPtr texture, UnityEngine.Color color, float ior)
        {
            return texture != IntPtr.Zero && registerWater != null && registerWater(texture, color.r, color.g, color.b, ior) == 0;
        }
        public bool TagCategory(IntPtr texture, uint category) { return texture != IntPtr.Zero && tagCategory != null && tagCategory(texture, category) == 0; }
    }
}
