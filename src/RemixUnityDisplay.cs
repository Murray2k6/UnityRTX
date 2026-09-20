using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_UI
using UnityEngine.UI;
#endif

namespace UnityRemix
{
    // Keep Unity UI and event systems intact. Scene below, transparent Remix UI above.
    internal sealed class RemixUnityDisplay : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate RemixAPI.remixapi_ErrorCode SetTargets(IntPtr scene, IntPtr gui);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr GetRenderEvent();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong GetState();
        private readonly ManualLogSource logger;
        private readonly SetTargets setTargets;
        private readonly GetState getState;
        private readonly CommandBuffer copy;
        private readonly RemixCameraAntialiasing cameraAntialiasing;
        private readonly RemixCompositionOrder compositionOrder = new RemixCompositionOrder();
        private RenderTexture scene, gui;
        private bool disposed, cursorHeld, savedCursorVisible, loggedReady;
        private CursorLockMode savedCursorLock;
        private ulong firstFrame;
#if UNITY_UI
        private Canvas sceneCanvas, guiCanvas;
        private RawImage sceneImage, guiImage;
#endif
        private static T Load<T>(string name) where T : Delegate
        {
            var address = RemixAPI.GetRemixProcAddress(name);
            if (address == IntPtr.Zero) throw new NotSupportedException($"Missing native shared-output function: {name}");
            return Marshal.GetDelegateForFunctionPointer<T>(address);
        }
        public RemixUnityDisplay(ManualLogSource log)
        {
#if !UNITY_UI
            throw new NotSupportedException("Single-window compositing requires the game's Unity UI module.");
#else
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                throw new NotSupportedException("Shared Unity output currently requires Direct3D11.");
            logger = log;
            cameraAntialiasing = new RemixCameraAntialiasing(log);
            setTargets = Load<SetTargets>("remixapi_SetUnityOutputTargets");
            getState = Load<GetState>("remixapi_GetUnityOutputState");
            var renderEvent = Load<GetRenderEvent>("remixapi_GetUnityRenderEvent")();
            copy = new CommandBuffer { name = "UnityRemix shared GPU output" };
            copy.IssuePluginEvent(renderEvent, 0);
            try
            {
                sceneCanvas = CreateCanvas("UnityRemix_Scene", RenderMode.ScreenSpaceCamera, 0, out sceneImage);
                guiCanvas = CreateCanvas("UnityRemix_UI", RenderMode.ScreenSpaceOverlay, short.MaxValue, out guiImage);
            }
            catch { Dispose(); throw; }
#endif
        }
#if UNITY_UI
        private static Canvas CreateCanvas(string name, RenderMode mode, int order, out RawImage image)
        {
            var root = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.hideFlags = HideFlags.HideAndDontSave;
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = mode;
            canvas.sortingOrder = order;
            canvas.enabled = false;
            var surface = new GameObject("Output");
            surface.transform.SetParent(root.transform, false);
            image = surface.AddComponent<RawImage>();
            image.raycastTarget = false;
            // Shared D3D11 output starts at the top-left; Unity's UI UVs start at the bottom-left.
            image.uvRect = new Rect(0, 1, 1, -1);
            var rect = image.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return canvas;
        }
#endif
        private RenderTexture Allocate(string name, int width, int height)
        {
            var result = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) {
                name = name, hideFlags = HideFlags.HideAndDontSave, antiAliasing = 1,
                useMipMap = false, autoGenerateMips = false
            };
            if (!result.Create())
            {
                UnityEngine.Object.Destroy(result);
                throw new InvalidOperationException("Could not create Unity shared-output render texture.");
            }
            var previous = RenderTexture.active;
            try { RenderTexture.active = result; GL.Clear(false, true, Color.clear); }
            finally { RenderTexture.active = previous; }
            return result;
        }
        public void Update(Camera camera)
        {
            if (disposed) return;
#if UNITY_UI
            ulong state = getState();
            if ((state & 4) != 0) throw new InvalidOperationException("Native shared-output exchange failed; see the Remix log.");
            int width = Math.Max(1, Screen.width), height = Math.Max(1, Screen.height);
            if (scene == null || scene.width != width || scene.height != height)
            {
                sceneCanvas.enabled = guiCanvas.enabled = false;
                RenderTexture nextScene = null, nextGui = null;
                try
                {
                    nextScene = Allocate("UnityRemix shared scene", width, height);
                    nextGui = Allocate("UnityRemix shared UI", width, height);
                    var result = setTargets(nextScene.GetNativeTexturePtr(), nextGui.GetNativeTexturePtr());
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                        throw new InvalidOperationException($"Native output rejected Unity's texture targets: {result}");
                }
                catch
                {
                    if (nextScene != null) UnityEngine.Object.Destroy(nextScene);
                    if (nextGui != null) UnityEngine.Object.Destroy(nextGui);
                    throw;
                }
                if (scene != null) UnityEngine.Object.Destroy(scene);
                if (gui != null) UnityEngine.Object.Destroy(gui);
                sceneImage.texture = scene = nextScene;
                guiImage.texture = gui = nextGui;
                firstFrame = state >> 8;
                logger.LogInfo($"Unity shared scene/UI targets: {width}x{height}");
            }
            bool outputReady = (state >> 8) > firstFrame;
            if (camera != null && camera.isActiveAndEnabled)
            {
                sceneCanvas.worldCamera = camera;
                sceneCanvas.planeDistance = camera.nearClipPlane + 0.01f;
                sceneCanvas.enabled = outputReady;
            }
            else sceneCanvas.enabled = false;
            cameraAntialiasing.Update(camera, sceneCanvas.enabled);
            compositionOrder.Update(camera, sceneCanvas, guiCanvas);
            guiCanvas.enabled = outputReady;
            if (outputReady && !loggedReady)
            {
                logger.LogInfo("Shared GPU output is live in Unity's window; game UI remains on Unity canvases.");
                loggedReady = true;
            }
            if ((state & 1) != 0)
            {
                if (!cursorHeld)
                {
                    savedCursorLock = Cursor.lockState;
                    savedCursorVisible = Cursor.visible;
                    cursorHeld = true;
                }
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else RestoreCursor();
#endif
        }
        // Enqueue the GPU copy after publishing/submitting this Unity frame.
        public void CopyLatestFrame()
        {
            if (!disposed) Graphics.ExecuteCommandBuffer(copy);
        }
        private void RestoreCursor()
        {
            if (!cursorHeld) return;
            Cursor.lockState = savedCursorLock;
            Cursor.visible = savedCursorVisible;
            cursorHeld = false;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            setTargets?.Invoke(IntPtr.Zero, IntPtr.Zero);
            copy?.Release();
            cameraAntialiasing?.Dispose();
            compositionOrder.Dispose();
            RestoreCursor();
#if UNITY_UI
            if (sceneCanvas != null) UnityEngine.Object.Destroy(sceneCanvas.gameObject);
            if (guiCanvas != null) UnityEngine.Object.Destroy(guiCanvas.gameObject);
#endif
            if (scene != null) UnityEngine.Object.Destroy(scene);
            if (gui != null) UnityEngine.Object.Destroy(gui);
        }
    }
}
