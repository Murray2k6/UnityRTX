using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Automatically detects and classifies scene Cameras and Canvases into
    /// World Cameras (3D scene to suppress in-engine) and UI Cameras (HUD, menus,
    /// viewmodels, and overlays to keep active and render on top).
    /// </summary>
    public class RemixUIDetector
    {
        private readonly ManualLogSource logger;
        private readonly ConfigEntry<bool> configAutoDetectUI;
        private readonly ConfigEntry<string> configUICameraNames;
        private readonly ConfigEntry<string> configCameraName;

        private readonly List<Camera> uiCameras = new List<Camera>();
        private readonly List<Camera> worldCameras = new List<Camera>();
        private readonly Dictionary<Canvas, RenderMode> originalCanvasRenderModes = new Dictionary<Canvas, RenderMode>();
        private readonly Dictionary<Canvas, Camera> originalCanvasCameras = new Dictionary<Canvas, Camera>();

        // Common UI keywords in camera names across Unity games
        private static readonly string[] UIKeywords = new string[]
        {
            "ui", "hud", "canvas", "menu", "gui", "overlay", "interface",
            "virtual", "crosshair", "reticle", "cursor", "viewmodel", "gun",
            "weapon", "text", "subtitles", "scoreboard", "minimap", "radar",
            "dialogue", "chat", "fps", "debug"
        };

        public IReadOnlyList<Camera> UICameras => uiCameras;
        public IReadOnlyList<Camera> WorldCameras => worldCameras;

        public RemixUIDetector(
            ManualLogSource logger,
            ConfigEntry<bool> autoDetectUI,
            ConfigEntry<string> uiCameraNames,
            ConfigEntry<string> cameraName)
        {
            this.logger = logger;
            this.configAutoDetectUI = autoDetectUI;
            this.configUICameraNames = uiCameraNames;
            this.configCameraName = cameraName;
        }

        /// <summary>
        /// Scans all active cameras in the scene and categorizes them into World and UI cameras.
        /// </summary>
        public void Refresh(Camera preferredWorldCamera = null)
        {
            uiCameras.Clear();
            worldCameras.Clear();

            var allCameras = Camera.allCameras;
            if (allCameras == null || allCameras.Length == 0)
            {
                allCameras = UnityEngine.Object.FindObjectsOfType<Camera>();
            }

            // User manual override camera names
            var manualUINames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(configUICameraNames?.Value))
            {
                var parts = configUICameraNames.Value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    manualUINames.Add(p.Trim());
                }
            }

            // Find known canvases and their worldCameras
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            var canvasCameras = new HashSet<Camera>();
            foreach (var canvas in canvases)
            {
                if (canvas != null && canvas.renderMode == RenderMode.ScreenSpaceCamera && canvas.worldCamera != null)
                {
                    canvasCameras.Add(canvas.worldCamera);
                }
            }

            int uiLayer = LayerMask.NameToLayer("UI");
            int uiLayerBit = uiLayer >= 0 ? (1 << uiLayer) : 0x20;

            // Determine primary 3D world camera
            Camera primaryWorld = preferredWorldCamera ?? Camera.main;
            if (primaryWorld == null && !string.IsNullOrEmpty(configCameraName?.Value))
            {
                primaryWorld = allCameras.FirstOrDefault(c =>
                    c != null && c.name.Equals(configCameraName.Value, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var cam in allCameras)
            {
                if (cam == null) continue;

                string camName = cam.name ?? "";

                // 1. Manual override from config takes highest priority
                if (manualUINames.Contains(camName))
                {
                    uiCameras.Add(cam);
                    logger?.LogInfo($"[RemixUIDetector] Camera '{camName}' classified as UI (manual override)");
                    continue;
                }

                // If camera is explicitly the configured 3D main camera, classify as World
                if (cam == primaryWorld)
                {
                    worldCameras.Add(cam);
                    logger?.LogInfo($"[RemixUIDetector] Camera '{camName}' classified as World (primary 3D camera)");
                    continue;
                }

                if (configAutoDetectUI != null && !configAutoDetectUI.Value)
                {
                    // Auto-detect disabled: all non-manual cameras treated as World
                    worldCameras.Add(cam);
                    continue;
                }

                // 2. Name-based heuristics
                string lowerName = camName.ToLowerInvariant();
                bool nameMatch = UIKeywords.Any(k => lowerName.Contains(k));

                // 3. Canvas association
                bool isCanvasCam = canvasCameras.Contains(cam);

                // 4. Culling mask heuristics: does it render the UI layer or avoid layer 0 (Default)?
                bool rendersUILayer = (cam.cullingMask & uiLayerBit) != 0;
                bool avoidsDefaultLayer = (cam.cullingMask & 1) == 0; // Layer 0 = Default

                // Count set bits in cullingMask
                int bitCount = CountBits((uint)cam.cullingMask);
                bool hasRestrictedLayers = bitCount <= 3 && avoidsDefaultLayer;

                // 5. ClearFlags heuristics: overlay cameras commonly use Depth or Nothing
                bool isOverlayClear = cam.clearFlags == CameraClearFlags.Depth || cam.clearFlags == CameraClearFlags.Nothing;
                bool higherDepth = primaryWorld != null && cam.depth > primaryWorld.depth;

                // Decision logic
                if (nameMatch || isCanvasCam || (rendersUILayer && avoidsDefaultLayer) || (isOverlayClear && higherDepth && hasRestrictedLayers))
                {
                    uiCameras.Add(cam);
                    logger?.LogInfo($"[RemixUIDetector] Camera '{camName}' classified as UI (name:{nameMatch}, canvas:{isCanvasCam}, uiLayer:{rendersUILayer}, overlayClear:{isOverlayClear}, depth:{cam.depth})");
                }
                else
                {
                    worldCameras.Add(cam);
                    logger?.LogInfo($"[RemixUIDetector] Camera '{camName}' classified as World (cullingMask:0x{cam.cullingMask:X}, depth:{cam.depth})");
                }
            }

            // Order UI cameras ascending by depth so they render in natural sequence
            uiCameras.Sort((a, b) => a.depth.CompareTo(b.depth));
            logger?.LogInfo($"[RemixUIDetector] Scan complete: {worldCameras.Count} World Cameras, {uiCameras.Count} UI Cameras detected.");
        }

        /// <summary>
        /// Routes ScreenSpaceOverlay Canvases to render through the primary UI camera in ScreenSpaceCamera mode
        /// so their contents can be captured into a transparent UI texture.
        /// </summary>
        public void RouteOverlayCanvasesToCamera(Camera uiCamera)
        {
            if (uiCamera == null) return;

            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            foreach (var canvas in canvases)
            {
                if (canvas == null) continue;

                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    if (!originalCanvasRenderModes.ContainsKey(canvas))
                    {
                        originalCanvasRenderModes[canvas] = canvas.renderMode;
                        originalCanvasCameras[canvas] = canvas.worldCamera;
                    }

                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = uiCamera;
                    canvas.planeDistance = 1.0f;
                }
            }
        }

        /// <summary>
        /// Restores original Canvas render modes and world cameras.
        /// </summary>
        public void RestoreCanvases()
        {
            foreach (var kvp in originalCanvasRenderModes)
            {
                var canvas = kvp.Key;
                if (canvas != null)
                {
                    canvas.renderMode = kvp.Value;
                    if (originalCanvasCameras.TryGetValue(canvas, out var cam))
                    {
                        canvas.worldCamera = cam;
                    }
                }
            }

            originalCanvasRenderModes.Clear();
            originalCanvasCameras.Clear();
        }

        private static int CountBits(uint v)
        {
            v = v - ((v >> 1) & 0x55555555);
            v = (v & 0x33333333) + ((v >> 2) & 0x33333333);
            return (int)((((v + (v >> 4)) & 0x0F0F0F0F) * 0x01010101) >> 24);
        }
    }
}
