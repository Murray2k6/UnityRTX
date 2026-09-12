using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    public enum SingleWindowMethod
    {
        Embedded = 0,
        Copy = 1
    }

    /// <summary>
    /// Manages Unity in-engine rendering suppression and API-agnostic framebuffer presentation
    /// for Single Window mode.
    /// </summary>
    public class RemixFramebufferPresenter : MonoBehaviour
    {
        private ManualLogSource logger;
        private RemixWindowManager windowManager;
        private ConfigEntry<bool> configSingleWindow;
        private ConfigEntry<SingleWindowMethod> configSingleWindowMethod;
        private ConfigEntry<bool> configDisableInEngineRendering;

        // Tracking camera states for in-engine rendering suppression
        private readonly Dictionary<Camera, int> originalCullingMasks = new Dictionary<Camera, int>();
        private readonly Dictionary<Camera, CameraClearFlags> originalClearFlags = new Dictionary<Camera, CameraClearFlags>();
        private bool inEngineRenderingSuppressed = false;

        // Framebuffer copy mode state
        private Texture2D copyTexture;
        private byte[] pixelBuffer;
        private int lastWidth = 0;
        private int lastHeight = 0;

        public void Initialize(
            ManualLogSource logger,
            RemixWindowManager windowManager,
            ConfigEntry<bool> singleWindow,
            ConfigEntry<SingleWindowMethod> singleWindowMethod,
            ConfigEntry<bool> disableInEngineRendering)
        {
            this.logger = logger;
            this.windowManager = windowManager;
            this.configSingleWindow = singleWindow;
            this.configSingleWindowMethod = singleWindowMethod;
            this.configDisableInEngineRendering = disableInEngineRendering;

            logger?.LogInfo($"[RemixFramebufferPresenter] Initialized (SingleWindow: {singleWindow.Value}, Method: {singleWindowMethod.Value}, SuppressInEngine: {disableInEngineRendering.Value})");
        }

        void LateUpdate()
        {
            if (configSingleWindow == null) return;

            bool shouldSuppress = configSingleWindow.Value && configDisableInEngineRendering.Value;

            if (shouldSuppress != inEngineRenderingSuppressed)
            {
                if (shouldSuppress)
                {
                    ApplyInEngineRenderingSuppression();
                }
                else
                {
                    RestoreInEngineRendering();
                }
            }

            // Sync window bounds if embedded
            if (configSingleWindow.Value && configSingleWindowMethod.Value == SingleWindowMethod.Embedded && windowManager != null)
            {
                windowManager.SyncWindowBounds();
            }

            // Handle Alt+X detection for Remix ImGui mouse unlock
            bool altPressed = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (altPressed && Input.GetKeyDown(KeyCode.X))
            {
                RemixWindowManager.ToggleRemixUI();
                logger?.LogInfo($"[RemixFramebufferPresenter] Alt+X toggled, RemixUIOpen: {RemixWindowManager.IsRemixUIOpen}");
            }
        }

        /// <summary>
        /// Suppresses Unity's 3D scene rendering so the engine does not perform duplicate
        /// rasterization passes while RTX Remix is rendering the path-traced scene.
        /// Player movement, physics, animations, and scripts continue running untouched.
        /// </summary>
        public void ApplyInEngineRenderingSuppression()
        {
            var cameras = Camera.allCameras;
            int suppressedCount = 0;

            foreach (var cam in cameras)
            {
                if (cam == null) continue;

                // Don't suppress UI-only cameras (e.g. HUD camera) if any exist
                string camName = cam.name.ToLowerInvariant();
                bool isHudCamera = camName.Contains("hud") || camName.Contains("ui");
                if (isHudCamera && configSingleWindowMethod.Value == SingleWindowMethod.Copy)
                {
                    continue;
                }

                if (!originalCullingMasks.ContainsKey(cam))
                {
                    originalCullingMasks[cam] = cam.cullingMask;
                    originalClearFlags[cam] = cam.clearFlags;
                }

                // Set cullingMask to 0 so Unity skips all scene geometry, shadow passes, and lighting
                cam.cullingMask = 0;
                cam.clearFlags = CameraClearFlags.Nothing;
                suppressedCount++;
            }

            inEngineRenderingSuppressed = true;
            logger?.LogInfo($"[RemixFramebufferPresenter] In-engine 3D rendering suppressed on {suppressedCount} cameras.");
        }

        /// <summary>
        /// Restores original camera culling masks and clear flags.
        /// </summary>
        public void RestoreInEngineRendering()
        {
            foreach (var kvp in originalCullingMasks)
            {
                var cam = kvp.Key;
                if (cam != null)
                {
                    cam.cullingMask = kvp.Value;
                    if (originalClearFlags.TryGetValue(cam, out var flags))
                    {
                        cam.clearFlags = flags;
                    }
                }
            }

            originalCullingMasks.Clear();
            originalClearFlags.Clear();
            inEngineRenderingSuppressed = false;
            logger?.LogInfo("[RemixFramebufferPresenter] Restored in-engine camera rendering.");
        }

        /// <summary>
        /// Framebuffer Copy mode: Blits the captured Remix framebuffer directly onto the screen.
        /// Engine-level and API-agnostic (works on DirectX 11, DirectX 12, Vulkan, OpenGL).
        /// </summary>
        void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            if (configSingleWindow != null && configSingleWindow.Value &&
                configSingleWindowMethod != null && configSingleWindowMethod.Value == SingleWindowMethod.Copy &&
                windowManager != null)
            {
                int width = Screen.width > 0 ? Screen.width : 1920;
                int height = Screen.height > 0 ? Screen.height : 1080;

                if (copyTexture == null || lastWidth != width || lastHeight != height)
                {
                    if (copyTexture != null) Destroy(copyTexture);
                    copyTexture = new Texture2D(width, height, TextureFormat.BGRA32, false);
                    pixelBuffer = new byte[width * height * 4];
                    lastWidth = width;
                    lastHeight = height;
                }

                if (windowManager.CaptureRemixFramebuffer(pixelBuffer, width, height))
                {
                    copyTexture.LoadRawTextureData(pixelBuffer);
                    copyTexture.Apply(false, false);
                    Graphics.Blit(copyTexture, dest);
                    return;
                }
            }

            Graphics.Blit(src, dest);
        }

        void OnDestroy()
        {
            RestoreInEngineRendering();
            if (copyTexture != null)
            {
                Destroy(copyTexture);
                copyTexture = null;
            }
        }
    }
}
