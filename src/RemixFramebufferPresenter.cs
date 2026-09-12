using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    /// Coordinates Single-Window mode presentation and in-engine rendering suppression.
    /// Uses RemixUIDetector to automatically preserve and render UI/HUD cameras and Canvases on top
    /// of the RTX Remix ray-traced viewport.
    /// </summary>
    public class RemixFramebufferPresenter : MonoBehaviour
    {
        private ManualLogSource logger;
        private RemixWindowManager windowManager;
        private RemixCameraHandler cameraHandler;
        private RemixUIDetector uiDetector;
        private RemixUIOverlay uiOverlay;

        // Config entries
        private ConfigEntry<bool> configSingleWindow;
        private ConfigEntry<SingleWindowMethod> configSingleWindowMethod;
        private ConfigEntry<bool> configDisableInEngineRendering;
        private ConfigEntry<bool> configAutoDetectUI;
        private ConfigEntry<string> configUICameraNames;
        private ConfigEntry<bool> configSingleWindowUIOverlay;

        // Tracking suppressed world cameras
        private readonly Dictionary<Camera, int> originalCullingMasks = new Dictionary<Camera, int>();
        private readonly Dictionary<Camera, CameraClearFlags> originalClearFlags = new Dictionary<Camera, CameraClearFlags>();
        private bool inEngineRenderingSuppressed = false;

        // Copy mode blitter reference
        private RemixCameraBlitter currentCameraBlitter;
        private Coroutine overlayCoroutine;

        public RemixUIDetector UIDetector => uiDetector;

        public void Initialize(
            ManualLogSource logger,
            RemixWindowManager windowManager,
            RemixCameraHandler cameraHandler,
            ConfigEntry<bool> singleWindow,
            ConfigEntry<SingleWindowMethod> singleWindowMethod,
            ConfigEntry<bool> disableInEngineRendering,
            ConfigEntry<bool> autoDetectUI,
            ConfigEntry<string> uiCameraNames,
            ConfigEntry<bool> singleWindowUIOverlay)
        {
            this.logger = logger;
            this.windowManager = windowManager;
            this.cameraHandler = cameraHandler;
            this.configSingleWindow = singleWindow;
            this.configSingleWindowMethod = singleWindowMethod;
            this.configDisableInEngineRendering = disableInEngineRendering;
            this.configAutoDetectUI = autoDetectUI;
            this.configUICameraNames = uiCameraNames;
            this.configSingleWindowUIOverlay = singleWindowUIOverlay;

            uiDetector = new RemixUIDetector(
                logger,
                autoDetectUI,
                uiCameraNames,
                null
            );

            logger?.LogInfo($"[RemixFramebufferPresenter] Initialized (SingleWindow: {singleWindow.Value}, Method: {singleWindowMethod.Value}, SuppressInEngine: {disableInEngineRendering.Value}, AutoDetectUI: {autoDetectUI.Value})");
        }

        void OnEnable()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDisable()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
            Cleanup();
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            if (configSingleWindow != null && configSingleWindow.Value)
            {
                // Re-evaluate suppression and UI detection on scene change
                StartCoroutine(DeferredSceneSetup());
            }
        }

        private IEnumerator DeferredSceneSetup()
        {
            yield return null; // Wait one frame for game to instantiate all scene cameras and canvases
            if (configSingleWindow != null && configSingleWindow.Value && configDisableInEngineRendering.Value)
            {
                ApplyInEngineRenderingSuppression();
            }
        }

        void LateUpdate()
        {
            if (configSingleWindow == null) return;

            bool isSingle = configSingleWindow.Value;
            bool shouldSuppress = isSingle && configDisableInEngineRendering.Value;

            if (shouldSuppress != inEngineRenderingSuppressed)
            {
                if (shouldSuppress)
                    ApplyInEngineRenderingSuppression();
                else
                    RestoreInEngineRendering();
            }

            // Sync embedded window bounds
            if (isSingle && configSingleWindowMethod.Value == SingleWindowMethod.Embedded && windowManager != null)
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
        /// Suppresses Unity's 3D scene rasterization passes on World Cameras while keeping UI/HUD cameras active.
        /// </summary>
        public void ApplyInEngineRenderingSuppression()
        {
            if (uiDetector == null) return;

            Camera worldCam = cameraHandler?.CurrentCamera ?? Camera.main;
            uiDetector.Refresh(worldCam);

            // Suppress 3D World Cameras
            int suppressedCount = 0;
            foreach (var cam in uiDetector.WorldCameras)
            {
                if (cam == null) continue;

                if (!originalCullingMasks.ContainsKey(cam))
                {
                    originalCullingMasks[cam] = cam.cullingMask;
                    originalClearFlags[cam] = cam.clearFlags;
                }

                cam.cullingMask = 0;
                cam.clearFlags = CameraClearFlags.Nothing;
                suppressedCount++;
            }

            logger?.LogInfo($"[RemixFramebufferPresenter] In-engine 3D rendering suppressed on {suppressedCount} World Cameras.");

            // Setup UI Presentation depending on single-window method
            if (configSingleWindowMethod.Value == SingleWindowMethod.Embedded)
            {
                SetupEmbeddedUIOverlay();
            }
            else if (configSingleWindowMethod.Value == SingleWindowMethod.Copy)
            {
                SetupCopyModeBlitter(worldCam);
            }

            inEngineRenderingSuppressed = true;
        }

        private void SetupEmbeddedUIOverlay()
        {
            if (configSingleWindowUIOverlay == null || !configSingleWindowUIOverlay.Value)
                return;

            IntPtr gameWnd = windowManager != null && windowManager.GameWindow != IntPtr.Zero 
                ? windowManager.GameWindow 
                : RemixWindowManager.FindGameWindow();

            if (uiOverlay == null && gameWnd != IntPtr.Zero)
            {
                uiOverlay = new RemixUIOverlay(logger, gameWnd);
                if (!uiOverlay.Initialize())
                {
                    uiOverlay = null;
                    return;
                }
            }

            if (uiOverlay != null && uiDetector.UICameras.Count > 0)
            {
                uiOverlay.ConfigureUICameras(uiDetector.UICameras);
                uiDetector.RouteOverlayCanvasesToCamera(uiDetector.UICameras[0]);

                if (overlayCoroutine == null)
                {
                    overlayCoroutine = StartCoroutine(EndOfFrameOverlayPump());
                }
            }
        }

        private IEnumerator EndOfFrameOverlayPump()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                if (uiOverlay != null && configSingleWindow != null && configSingleWindow.Value &&
                    configSingleWindowMethod.Value == SingleWindowMethod.Embedded)
                {
                    uiOverlay.UpdateOverlay();
                }
            }
        }

        private void SetupCopyModeBlitter(Camera worldCam)
        {
            if (worldCam == null) worldCam = Camera.main;
            if (worldCam == null) return;

            currentCameraBlitter = worldCam.GetComponent<RemixCameraBlitter>();
            if (currentCameraBlitter == null)
            {
                currentCameraBlitter = worldCam.gameObject.AddComponent<RemixCameraBlitter>();
            }

            currentCameraBlitter.Initialize(windowManager, logger);
            currentCameraBlitter.SetBlitEnabled(true);
        }

        /// <summary>
        /// Restores original camera culling masks and clear flags.
        /// </summary>
        public void RestoreInEngineRendering()
        {
            if (overlayCoroutine != null)
            {
                StopCoroutine(overlayCoroutine);
                overlayCoroutine = null;
            }

            if (uiOverlay != null)
            {
                uiOverlay.RestoreUICameras();
                uiOverlay.Destroy();
                uiOverlay = null;
            }

            uiDetector?.RestoreCanvases();

            if (currentCameraBlitter != null)
            {
                currentCameraBlitter.SetBlitEnabled(false);
            }

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

        private void Cleanup()
        {
            RestoreInEngineRendering();
        }

        void OnDestroy()
        {
            Cleanup();
        }
    }
}
