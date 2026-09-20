using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using BepInEx.Logging;

namespace UnityRemix
{
    // Unity owns the only game HWND, swapchain and input. Remix renders offscreen.
    public sealed class RemixWindowManager
    {
        private readonly ManualLogSource logger;
        private readonly RemixAPI.PFN_remixapi_Startup startup;
        private readonly NativeEnable enableOutput;
        private IntPtr gameWindow;
        private volatile bool ready;
        private int width = 1920, height = 1080;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate RemixAPI.remixapi_ErrorCode NativeEnable();
        private delegate bool EnumWindow(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr data);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        public IntPtr RemixWindow => gameWindow;
        public int WindowWidth => width;
        public int WindowHeight => height;
        public bool CloseRequested => gameWindow != IntPtr.Zero && !IsWindow(gameWindow);
        public bool Ready => ready;

        public RemixWindowManager(ManualLogSource log, RemixAPI.remixapi_Interface api)
        {
            logger = log;
            startup = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_Startup>(api.Startup);
            var address = RemixAPI.GetRemixProcAddress("remixapi_EnableUnityOutput");
            if (address == IntPtr.Zero)
                throw new NotSupportedException("The installed Remix runtime lacks the Unity shared-output API. Install the matching native build.");
            enableOutput = Marshal.GetDelegateForFunctionPointer<NativeEnable>(address);
        }

        // Main thread, before starting the Remix worker.
        public void SetWindowDimensions(int newWidth, int newHeight)
        {
            width = Math.Max(1, newWidth);
            height = Math.Max(1, newHeight);
            uint process = GetCurrentProcessId();
            EnumWindow find = (window, data) => {
                GetWindowThreadProcessId(window, out uint owner);
                if (owner != process || !IsWindowVisible(window)) return true;
                var name = new StringBuilder(128);
                GetClassName(window, name, name.Capacity);
                if (name.ToString() != "UnityWndClass") return true;
                gameWindow = window;
                return false;
            };
            EnumWindows(find, IntPtr.Zero);
            GC.KeepAlive(find);
            if (gameWindow == IntPtr.Zero)
                throw new InvalidOperationException("No visible Unity game window was found; leaving the game's window intact.");
            logger.LogInfo($"Using Unity's game window 0x{gameWindow.ToInt64():X} for Remix output and input.");
        }

        public bool CreateRemixWindow()
        {
            if (gameWindow == IntPtr.Zero || !IsWindow(gameWindow)) return false;
            var info = new RemixAPI.remixapi_StartupInfo {
                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_STARTUP_INFO,
                hwnd = gameWindow, forceNoVkSwapchain = 1,
                disableSrgbConversionForOutput = 0, editorModeEnabled = 0
            };
            var result = startup(ref info);
            if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS) result = enableOutput();
            ready = result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS;
            if (!ready) logger.LogError($"Remix shared-output startup failed: {result}");
            else logger.LogInfo("Remix initialized offscreen; Unity owns the single game window.");
            return ready;
        }
        public void PumpWindowsMessages() { /* Unity pumps its own window. */ }
        public bool WaitForMessages(uint milliseconds) { Thread.Sleep((int)Math.Min(milliseconds, 1000u)); return false; }
        public void DestroyRemixWindow() { ready = false; /* Never destroy Unity's HWND. */ }
    }
}
