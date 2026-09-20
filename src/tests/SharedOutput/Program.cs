using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityRemix;
using static UnityRemix.RemixAPI;

unsafe partial class Program
{
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, uint type, IntPtr software, uint flags, IntPtr levels, uint count, uint sdk, out IntPtr device, out uint level, out IntPtr context);
    [DllImport("d3d11.dll")] static extern int D3D11CreateDeviceAndSwapChain(IntPtr adapter, uint type, IntPtr software, uint flags, IntPtr levels, uint count, uint sdk, ref SwapDesc swapDesc, out IntPtr swapchain, out IntPtr device, out uint level, out IntPtr context);
    [StructLayout(LayoutKind.Sequential)] struct SwapDesc { public uint Width,Height,RateNum,RateDen,Format,Scanline,Scaling,Samples,Quality,Usage,Buffers; public IntPtr Window; public int Windowed; public uint SwapEffect,Flags; }
    [StructLayout(LayoutKind.Sequential)] struct Desc { public uint Width, Height, Mips, Array, Format, Samples, Quality, Usage, Bind, Cpu, Misc; }
    [StructLayout(LayoutKind.Sequential)] struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexture(IntPtr self, ref Desc desc, IntPtr data, out IntPtr texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int OpenShared(IntPtr self, IntPtr handle, ref Guid iid, out IntPtr texture);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint Release(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyResource(IntPtr self, IntPtr dst, IntPtr src);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Map(IntPtr self, IntPtr resource, uint sub, uint mode, uint flags, out Mapped mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void Unmap(IntPtr self, IntPtr resource, uint sub);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate remixapi_ErrorCode Enable();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate remixapi_ErrorCode Targets(IntPtr scene, IntPtr gui);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr EventAddress();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void RenderEvent(int id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong State();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate remixapi_ErrorCode Acquire(out IntPtr scene, out IntPtr gui, out uint w, out uint h);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate remixapi_ErrorCode LegacyAcquire(out IntPtr scene, out uint w, out uint h);
    static T V<T>(IntPtr obj, int slot) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));
    static T Export<T>(string name) where T:Delegate => Marshal.GetDelegateForFunctionPointer<T>(GetRemixProcAddress(name));
    static readonly string Log = Path.Combine(AppContext.BaseDirectory, "test-results.log");
    static void Check(bool ok, string what)
    {
        File.AppendAllText(Log, (ok ? "PASS: " : "FAIL: ") + what + Environment.NewLine);
        if (!ok) throw new Exception(what);
        Console.WriteLine("PASS: " + what);
    }

    static byte[] ReadPixels(IntPtr device, IntPtr context, IntPtr texture, Desc desc)
    {
        desc.Bind=0; desc.Usage=3; desc.Cpu=0x20000; desc.Misc=0;
        Check(V<CreateTexture>(device,5)(device,ref desc,IntPtr.Zero,out var staging)==0,"readback staging created");
        try
        {
            V<CopyResource>(context,47)(context,staging,texture);
            Check(V<Map>(context,14)(context,staging,0,1,0,out var mapped)==0,"shared result readable through D3D11");
            try
            {
                var pixels = new byte[desc.Width * desc.Height * 4];
                for (int y=0; y<desc.Height; y++)
                    Marshal.Copy(mapped.Data + y*(int)mapped.RowPitch,pixels,y*(int)desc.Width*4,(int)desc.Width*4);
                return pixels;
            }
            finally { V<Unmap>(context,15)(context,staging,0); }
        }
        finally { V<Release>(staging,2)(staging); }
    }

    static int AlphaPixels(byte[] pixels, bool visible)
    {
        int count=0;
        for(int i=3;i<pixels.Length;i+=4) if ((pixels[i] > 0) == visible) count++;
        return count;
    }

    static Action emergencyShutdown;
    static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { emergencyShutdown?.Invoke(); }
    }

    static void Run(string[] args)
    {
        File.WriteAllText(Log, "Shared GPU test " + DateTime.UtcNow.ToString("O") + Environment.NewLine);
        // Exercise only pipelines used by this test, without prewarming every game variant.
        Environment.SetEnvironmentVariable("RTX_ASYNC_SHADER_PREWARMING", "0");
        Environment.SetEnvironmentVariable("RTX_HIDE_SPLASH_MESSAGE", "1");
        Check(args.Length >= 1, "runtime DLL supplied");
        bool motionVectors = Array.IndexOf(args, "--motion-vectors") >= 0;
        bool raytrace = motionVectors || Array.IndexOf(args, "--raytrace") >= 0;
        Check(InitializeRemixAPI(args[0], out var api, out _) == 0, "native ABI initialization");
        IntPtr window = CreateWindowExW(0, "STATIC", "Shared output probe", 0x00CF0000, 0, 0, 640, 480, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Check(window != IntPtr.Zero, "independent hidden window");
        var swapDesc = new SwapDesc { Width=640, Height=480, Format=28, Samples=1, Usage=32, Buffers=2, Window=window, Windowed=1, SwapEffect=4 };
        Check(D3D11CreateDeviceAndSwapChain(IntPtr.Zero, 1, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, ref swapDesc, out var swapchain, out var device, out _, out var context) == 0, "D3D11 host already owns the window's swapchain");
        var startup = Marshal.GetDelegateForFunctionPointer<PFN_remixapi_Startup>(api.Startup);
        var info = new remixapi_StartupInfo { sType=remixapi_StructType.REMIXAPI_STRUCT_TYPE_STARTUP_INFO, hwnd=window, forceNoVkSwapchain=1 };
        Check(startup(ref info) == 0, "external presenter startup");
        emergencyShutdown = () => ShutdownRemix(ref api);
        var enable = Export<Enable>("remixapi_EnableUnityOutput");
        var targets = Export<Targets>("remixapi_SetUnityOutputTargets");
        var state = Export<State>("remixapi_GetUnityOutputState");
        var acquire = Export<Acquire>("remixapi_AcquireSharedD3D11Frame");
        var release = Export<Enable>("remixapi_ReleaseSharedD3D11Frame");
        var legacy = Marshal.GetDelegateForFunctionPointer<LegacyAcquire>(api.dxvk_GetSharedD3D11TextureHandle);
        var render = Marshal.GetDelegateForFunctionPointer<RenderEvent>(Export<EventAddress>("remixapi_GetUnityRenderEvent")());
        Check(enable() == 0, "shared output enabled");
        var config = Marshal.GetDelegateForFunctionPointer<PFN_remixapi_SetConfigVariable>(api.SetConfigVariable);
        Check(config("rtx.showUI", "0") == 0, "UI closed for transparent-background check");
        Check(config("rtx.enableRaytracing", raytrace ? "True" : "False") == 0, "ray-tracing test mode: " + raytrace);
        if (motionVectors)
        {
            Check(config("rtx.debugView.debugViewIdx", "880") == 0, "signed screen-space motion-vector debug view");
            Check(config("rtx.debugView.minValue", "0") == 0 && config("rtx.debugView.maxValue", "1") == 0,
                "signed motion-vector encoding is unchanged");
        }
        Check(release() != 0, "release without acquisition rejected");
        var setup = Marshal.GetDelegateForFunctionPointer<PFN_remixapi_SetupCamera>(api.SetupCamera);
        var present = Marshal.GetDelegateForFunctionPointer<PFN_remixapi_Present>(api.Present);
        var meshCreate = Marshal.GetDelegateForFunctionPointer<PFN_remixapi_CreateMesh>(api.CreateMesh);
        var draw = Marshal.GetDelegateForFunctionPointer<PFN_remixapi_DrawInstance>(api.DrawInstance);
        var vertices = stackalloc remixapi_HardcodedVertex[3];
        vertices[0] = new remixapi_HardcodedVertex {position_x=-2, position_y=-2, position_z=3, normal_z=-1, color=0xFFFFFFFF};
        vertices[1] = new remixapi_HardcodedVertex {position_x=0, position_y=2, position_z=3, normal_z=-1, color=0xFFFFFFFF};
        vertices[2] = new remixapi_HardcodedVertex {position_x=2, position_y=-2, position_z=3, normal_z=-1, color=0xFFFFFFFF};
        var indices = stackalloc uint[3] {0,1,2};
        var surface = new remixapi_MeshInfoSurfaceTriangles {vertices_values=(IntPtr)vertices,vertices_count=3,indices_values=(IntPtr)indices,indices_count=3};
        var meshInfo = new remixapi_MeshInfo {sType=remixapi_StructType.REMIXAPI_STRUCT_TYPE_MESH_INFO,hash=12345,surfaces_values=(IntPtr)(&surface),surfaces_count=1};
        Check(meshCreate(ref meshInfo, out var mesh)==0,"test triangle created");
        var instance = new remixapi_InstanceInfo {sType=remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO,mesh=mesh,transform=remixapi_Transform.Identity(),doubleSided=1};
        var param = new remixapi_CameraInfoParameterizedEXT {sType=remixapi_StructType.REMIXAPI_STRUCT_TYPE_CAMERA_INFO_PARAMETERIZED_EXT,
            forward=new remixapi_Float3D(0,0,1), up=new remixapi_Float3D(0,1,0), right=new remixapi_Float3D(1,0,0), fovYInDegrees=75,aspect=4f/3,nearPlane=.1f,farPlane=100};
        var camera = new remixapi_CameraInfo {sType=remixapi_StructType.REMIXAPI_STRUCT_TYPE_CAMERA_INFO,pNext=(IntPtr)(&param)};
        var frame = new remixapi_PresentInfo {sType=remixapi_StructType.REMIXAPI_STRUCT_TYPE_PRESENT_INFO};
        Check(acquire(out var pendingScene,out var pendingGui,out var pendingWidth,out var pendingHeight)!=0 &&
            pendingScene==IntPtr.Zero && pendingGui==IntPtr.Zero && pendingWidth==0 && pendingHeight==0,
            "first standalone acquisition reports pending with zero outputs");
        var standaloneTimer=Stopwatch.StartNew();
        while(acquire(out pendingScene,out pendingGui,out pendingWidth,out pendingHeight)!=0 && standaloneTimer.Elapsed.TotalSeconds<60)
        {
            Check(present(ref frame)==0,"standalone frame submission");
            Thread.Sleep(10);
        }
        Check(pendingScene!=IntPtr.Zero && pendingGui!=IntPtr.Zero && pendingWidth>0 && pendingHeight>0,"standalone frame ready without Unity targets");
        var textureIid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        Check(V<OpenShared>(device,28)(device,pendingGui,ref textureIid,out var importedGui)==0,"standalone UI opens on an independent D3D11 device");
        var standalonePixels=ReadPixels(device,context,importedGui,new Desc {Width=pendingWidth,Height=pendingHeight,Mips=1,Array=1,Format=87,Samples=1});
        Check(AlphaPixels(standalonePixels,false)>standalonePixels.Length/8,"standalone UI has transparent background");
        V<Release>(importedGui,2)(importedGui);
        Check(release()==0,"standalone lease released after GPU readback completion");
        var testTargets = motionVectors ? new (uint Width,uint Format)[]{(pendingWidth,28)} :
            new (uint Width,uint Format)[]{(320,28),(480,27),(320,87),(480,90)};
        foreach (var target in testTargets)
        {
            uint width=target.Width;
            var desc = new Desc {Width=width,Height=motionVectors ? pendingHeight : 240,Mips=1,Array=1,Format=target.Format,Samples=1,Bind=8|32};
            Check(V<CreateTexture>(device,5)(device,ref desc,IntPtr.Zero,out var scene)==0,"scene target created");
            Check(V<CreateTexture>(device,5)(device,ref desc,IntPtr.Zero,out var gui)==0,"UI target created");
            Check(targets(scene,IntPtr.Zero)!=0,"incomplete targets rejected");
            Check(targets(scene,gui)==0,"target pair accepted");
            ulong before=state()>>8;
            var timer=Stopwatch.StartNew();
            bool firstSubmission=true;
            while ((state()>>8)<before+6 && timer.Elapsed.TotalSeconds<60)
            {
                if(setup(ref camera)!=0 || draw(ref instance)!=0 || present(ref frame)!=0) throw new Exception("Frame submission failed");
                if (firstSubmission) { timer.Restart(); firstSubmission=false; }
                render(0);
                if((state()&4)!=0) throw new Exception("Native exchange failed");
                Thread.Sleep(5);
            }
            Check((state()>>8)>=before+6,"GPU scene/UI copies at width "+width);
            if (motionVectors)
            {
                ProbeMotion(device, context, scene, desc, setup, draw, present, render, state,
                    ref camera, ref param, ref instance, ref frame, pendingWidth, pendingHeight);
                Check(targets(IntPtr.Zero, IntPtr.Zero) == 0, "motion probe detaches targets");
                V<Release>(scene, 2)(scene); V<Release>(gui, 2)(gui);
                break;
            }
            if (raytrace)
            {
                var scenePixels = ReadPixels(device, context, scene, desc);
                int translucent = 0;
                for (int pixel = 3; pixel < scenePixels.Length; pixel += 4)
                    if (scenePixels[pixel] != 255) translucent++;
                Check(translucent == 0, "resolved scene is opaque: " + translucent + " translucent pixels");
            }
            Check(acquire(out var sharedScene,out var sharedGui,out var w,out var h)==0 && sharedScene!=IntPtr.Zero && sharedGui!=IntPtr.Zero && w==width && h==240,"completed shared-frame lease and dimensions");
            Check(legacy(out var same,out var lw,out var lh)==0 && same==sharedScene && lw==w && lh==h,"legacy shared-texture API returns same held frame");
            for(int held=0;held<12;held++) { present(ref frame); render(0); Thread.Sleep(5); }
            Check(acquire(out var heldScene,out var heldGui,out _,out _)==0 && heldScene==sharedScene && heldGui==sharedGui,"held lease remains stable while other frames render");
            Check(release()==0 && release()!=0,"frame lease releases exactly once");
            // Read back the UI texture: its background must stay transparent.
            var pixels = ReadPixels(device,context,gui,desc);
            File.WriteAllBytes(Path.Combine(AppContext.BaseDirectory, "gui-" + width + ".rgba"), pixels);
            Check(AlphaPixels(pixels,false)>width*240/2,"UI background is transparent");
            Check(config("rtx.showUI","2")==0,"open native Remix menu");
            before=state()>>8;
            timer.Restart();
            while ((state()>>8)<before+6 && timer.Elapsed.TotalSeconds<30) { present(ref frame); render(0); Thread.Sleep(5); }
            Check((state()&1)!=0 && (state()>>8)>=before+6,"native menu state and GPU output advance");
            var menuPixels=ReadPixels(device,context,gui,desc);
            Check(AlphaPixels(menuPixels,true)>100,"visible Remix menu pixels reach D3D11");
            Check(config("rtx.showUI","0")==0,"close native Remix menu");
            Check(targets(IntPtr.Zero,IntPtr.Zero)==0,"targets detach");
            V<Release>(scene,2)(scene); V<Release>(gui,2)(gui);
        }
        Check(ShutdownRemix(ref api)==0,"native shutdown");
        emergencyShutdown = null;
        render(0);
        Check(state()==0,"late native render event safely ignored after shutdown");
        V<Release>(context,2)(context); V<Release>(device,2)(device);
        V<Release>(swapchain,2)(swapchain);
        DestroyWindow(window);
        Check(true,"all shared-output integration checks completed");
    }
}
