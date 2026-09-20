using System;

using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Shared managed runtime - orchestrates all Remix components
    /// Hosted by the Mono or IL2CPP BepInEx entry point.
    /// </summary>
    public sealed class UnityRemixPlugin
    {
        public const string PluginGUID = "com.Unity.remix";
        public const string PluginName = "Unity RTX Remix";
        public const string PluginVersion = "1.0.0";

        // Configuration entries
        private ConfigEntry<string> configCameraName;
        private ConfigEntry<string> configCameraTag;
        private ConfigEntry<bool> configListCameras;
        private ConfigEntry<bool> configUseGameGeometry;
        private ConfigEntry<bool> configUseDistanceCulling;
        private ConfigEntry<float> configMaxRenderDistance;
        private ConfigEntry<bool> configUseVisibilityCulling;
        private ConfigEntry<int> configRendererCacheDuration;
        private ConfigEntry<int> configDebugLogInterval;
        private ConfigEntry<bool> configEnableLights;
        private ConfigEntry<float> configLightIntensityMultiplier;
        private ConfigEntry<int> configTargetFPS;

        // Debug Toggles
        private ConfigEntry<bool> configCaptureStaticMeshes;
        private ConfigEntry<bool> configCaptureSkinnedMeshes;
        private ConfigEntry<bool> configHardwareSkinning;
        private ConfigEntry<bool> configCaptureTextures;
        private ConfigEntry<bool> configCaptureMaterials;
        private ConfigEntry<bool> configVerboseTextureLogging;
        private ConfigEntry<int> configMaxTextureDimension;
        
        // Scene mesh scanner settings
        private ConfigEntry<bool> configEnableSceneScan;
        private ConfigEntry<bool> configSceneScanActiveOnly;
        private ConfigEntry<string> configDisabledLayers;
        private ConfigEntry<bool> configPersistDisabledRenderers;
        
        public static ManualLogSource LogSource { get; private set; }
        private RemixAPI.remixapi_Interface remixInterface;
        private IntPtr remixDll = IntPtr.Zero;
        private bool remixInitialized = false;
        private bool deviceRegistered = false;
        
        // COMPONENTS - All functionality delegated to these
        private RemixWindowManager windowManager;
        private RemixCameraHandler cameraHandler;
        private RemixLightConverter lightConverter;
        private RemixMaterialManager materialManager;
        private RemixMeshConverter meshConverter;
        private RemixFrameCapture frameCapture;
        private RemixRenderThread renderThread;
        private TextureCategoryManager textureCategoryManager;
        private SceneMeshScanner sceneMeshScanner;
        private RemixSettingsUI settingsUI;
        private RemixDebugHUD debugHUD;
        
        private int frameCount = 0;
        private bool stopped;
        private bool started;
        private System.Threading.Tasks.Task<RemixRuntimeProbe.Report> runtimeDetection;
        private readonly System.Threading.CancellationTokenSource detectionCancellation = new System.Threading.CancellationTokenSource();
        private bool sceneResourcesDirty = true;
        private readonly System.Collections.Generic.List<UnityEngine.SceneManagement.Scene> pendingAdditiveScenes =
            new System.Collections.Generic.List<UnityEngine.SceneManagement.Scene>();
        private readonly ConfigFile Config;
#if BEPINEX6_IL2CPP
        private UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode> sceneLoadedHandler;
        private UnityEngine.Events.UnityAction<UnityEngine.SceneManagement.Scene> sceneUnloadedHandler;
#endif

        public UnityRemixPlugin(ConfigFile config, ManualLogSource logger)
        {
            Config = config;
            LogSource = logger;
        }
        
        // Shared lock for all Remix API calls to prevent deadlocks
        private static readonly object remixApiLock = new object();
        
        public void Start()
        {
            if (started || stopped) return;
            if (!RuntimeCompatibility.Check(LogSource)) { stopped = true; return; }
            started = true;
            LogSource.LogInfo($"Plugin {PluginName} v{PluginVersion} is loading!");
            
            // Initialize configuration
            InitializeConfig();
            
            // Retain the converted delegate so IL2CPP can unsubscribe the same instance.
#if BEPINEX6_IL2CPP
            sceneLoadedHandler = (Action<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>)OnSceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += sceneLoadedHandler;
            sceneUnloadedHandler = (Action<UnityEngine.SceneManagement.Scene>)OnSceneUnloaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += sceneUnloadedHandler;
#else
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += OnSceneUnloaded;
#endif
            // Load Remix API
            try
            {
                LogSource.LogInfo("Detecting installed Remix API versions...");
                string gameRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
                string bepinexRoot = BepInEx.Paths.BepInExRootPath;
                var cancellation = detectionCancellation.Token;
                runtimeDetection = System.Threading.Tasks.Task.Run(() => RemixRuntimeProbe.Inspect(gameRoot, bepinexRoot, cancellation));
            }
            catch (Exception ex)
            {
                LogSource.LogError($"Failed to load Remix interface: {ex}");
                Shutdown();
            }
        }
        
        private void InitializeConfig()
        {
            // Camera Settings
            configCameraName = Config.Bind("Camera", "CameraName", "",
                "Specific camera name to use for RTX Remix rendering. Leave empty to use auto-detection.");
            
            configCameraTag = Config.Bind("Camera", "CameraTag", "MainCamera",
                "Camera tag to search for if CameraName is not set.");
            
            configListCameras = Config.Bind("Camera", "ListCamerasOnSceneLoad", true,
                "Log all available cameras when a scene loads to help identify the correct camera name.");
            
            // Rendering Settings
            configUseGameGeometry = Config.Bind("Rendering", "EnableGameGeometry", true,
                "Enable rendering of game geometry through RTX Remix.");
            
            configUseDistanceCulling = Config.Bind("Rendering", "EnableDistanceCulling", false,
                "Enable distance-based culling of objects.");
            
            configMaxRenderDistance = Config.Bind("Rendering", "MaxRenderDistance", 500f,
                new ConfigDescription("Maximum render distance in Unity units.",
                    new AcceptableValueRange<float>(10f, 10000f)));
            
            configUseVisibilityCulling = Config.Bind("Rendering", "UseVisibilityCulling", false,
                "Use Unity's renderer.isVisible check to filter out invisible renderers. May cause visual issues in some games - disable if you see missing geometry.");
            
            configRendererCacheDuration = Config.Bind("Performance", "RendererCacheDuration", 300,
                new ConfigDescription("Number of frames to cache renderer list before refreshing.",
                    new AcceptableValueRange<int>(60, 3600)));
            
            configDebugLogInterval = Config.Bind("Debug", "DetailedLogInterval", 0,
                new ConfigDescription("Number of frames between detailed diagnostic logs (skinned dumps, prune reasons, mesh failures). 0 = disabled.",
                    new AcceptableValueRange<int>(0, 10800)));
            
            // Lighting Settings
            configEnableLights = Config.Bind("Lighting", "EnableLights", true,
                "Convert Unity lights to RTX Remix lights.");
            
            configLightIntensityMultiplier = Config.Bind("Lighting", "IntensityMultiplier", 1.0f,
                new ConfigDescription("Global multiplier for all light intensities.",
                    new AcceptableValueRange<float>(0.01f, 100f)));
            
            // Performance Settings
            configTargetFPS = Config.Bind("Performance", "TargetFPS", 0,
                new ConfigDescription("Target FPS for the Remix render thread. Set to 0 for uncapped.",
                    new AcceptableValueRange<int>(0, 500)));

            // Debug Toggles
            configCaptureStaticMeshes = Config.Bind("Debug", "CaptureStaticMeshes", true,
                "Enable capturing and rendering of static meshes.");
            
            configCaptureSkinnedMeshes = Config.Bind("Debug", "CaptureSkinnedMeshes", true,
                "Enable capturing and rendering of skinned meshes.");
            
            configHardwareSkinning = Config.Bind("Performance", "HardwareSkinning", false,
                "Use GPU hardware skinning for animated meshes. When off, uses CPU BakeMesh fallback.");
            
            configCaptureTextures = Config.Bind("Debug", "CaptureTextures", true,
                "Enable texture capturing and uploading.");
            
            configCaptureMaterials = Config.Bind("Debug", "CaptureMaterials", true,
                "Enable material capturing and creation.");
            
            configVerboseTextureLogging = Config.Bind("Debug", "VerboseTextureLogging", false,
                "Log detailed texture/material diagnostics (hashes, shader props, emission details). Disable to reduce log spam.");
            
            // Scene scan settings
            configEnableSceneScan = Config.Bind("SceneScan", "EnableSceneScan", true,
                "Enable runtime scene scanning to find all static level geometry (including inactive objects). No external bake tool needed.");

            configSceneScanActiveOnly = Config.Bind("SceneScan", "ActiveRenderersOnly", false,
                "Only preload meshes from active renderers. Disabled renderers remain hidden regardless of this setting; scanning inactive objects makes their geometry ready when the game enables them.");

            configPersistDisabledRenderers = Config.Bind("Rendering", "PersistDisabledRenderers", false,
                "Keep drawing static meshes after their renderer is deactivated by the game. Enable for games that temporarily deactivate visible geometry (e.g. ULTRAKILL CyberGrind).");

            configDisabledLayers = Config.Bind("Rendering", "DisabledLayers", "",
                "Comma-separated list of Unity layer indices to disable (e.g. '8,13,21'). Managed by the in-game UI.");
            
            LogSource.LogInfo("Configuration loaded:");
            LogSource.LogInfo($"  Camera Name: '{configCameraName.Value}' (empty = auto-detect)");
            LogSource.LogInfo($"  Camera Tag: '{configCameraTag.Value}'");
            LogSource.LogInfo($"  Game Geometry: {configUseGameGeometry.Value}");
            LogSource.LogInfo($"  Target FPS: {(configTargetFPS.Value == 0 ? "Uncapped" : configTargetFPS.Value.ToString())}");
        }
        
        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            LogSource.LogInfo($"Scene loaded: {scene.name}, mode: {mode}");
            if (mode == UnityEngine.SceneManagement.LoadSceneMode.Additive && !sceneResourcesDirty)
                pendingAdditiveScenes.Add(scene);
            else
                sceneResourcesDirty = true;
            cameraHandler?.ResetTracking();
            frameCapture?.RefreshRendererTracking();
        }

        private void OnSceneUnloaded(UnityEngine.SceneManagement.Scene scene)
        {
            sceneResourcesDirty = true;
        }

        private void RefreshSceneResources()
        {
            if (!deviceRegistered) return;
            if (!sceneResourcesDirty)
            {
                // Additive loads retain the existing scene and its allocations.
                if (configEnableSceneScan.Value)
                    foreach (var added in pendingAdditiveScenes)
                        if (added.IsValid() && added.isLoaded) sceneMeshScanner.OnSceneLoaded(added);
                pendingAdditiveScenes.Clear();
                return;
            }
            sceneResourcesDirty = false;
            pendingAdditiveScenes.Clear();
            // Coalesce load/unload callbacks at the next Unity update. Discard
            // queued frames, then release in mesh -> material -> texture order.
            renderThread.ResetSceneResources(() =>
            {
                frameCapture.InvalidateCaches();
                frameCapture.Cleanup();
                sceneMeshScanner.ClearData();
                meshConverter.Cleanup();
                materialManager.Cleanup();
                lightConverter.ClearCache();
            });
            cameraHandler?.ResetTracking();
            
            // List cameras if enabled
            if (configListCameras.Value && cameraHandler != null)
            {
                cameraHandler.ListAvailableCameras();
            }
            
            // Rebuild the union of loaded scenes, including additive scenes.
            if (configEnableSceneScan.Value)
            {
                for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (scene.IsValid() && scene.isLoaded) sceneMeshScanner.OnSceneLoaded(scene);
                }
            }
            
            // Refresh light cache on scene load
            lightConverter?.RefreshLightCache();
            
            // Refresh camera snapshots for UI
            cameraHandler?.RefreshCameraSnapshots();
            
        }
        
        private void LoadRemixInterface(string dllPath)
        {
            LogSource.LogInfo("Loading Remix API interface...");
            
            LogSource.LogInfo($"Looking for Remix DLL at: {dllPath}");
            
            if (!System.IO.File.Exists(dllPath))
            {
                LogSource.LogError($"Remix DLL not found at {dllPath}");
                LogSource.LogInfo("Install the complete UnityRemix package, including its private native renderer.");
                Shutdown();
                return;
            }
            
            // Load API
            RemixAPI.remixapi_ErrorCode result;
            try
            {
                result = RemixAPI.InitializeRemixAPI(dllPath, out remixInterface, out remixDll);
            }
            catch (Exception exception)
            {
                LogSource.LogError("Could not load the UnityRemix native renderer: " + exception.Message);
                Shutdown();
                return;
            }
            if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
            {
                LogSource.LogError($"Failed to load Remix API: {result}");
                if (result == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_INCOMPATIBLE_VERSION)
                    LogSource.LogError("No matching API contract was found (known families: 0.2, 0.4, 0.5, 0.6 and Unity bridge 0.1000). Install the complete UnityRemix package.");
                Shutdown();
                return;
            }
            
            LogSource.LogInfo($"Remix API loaded successfully ({(RemixRuntimeSelection.IsBundled(dllPath) ? "private renderer" : "legacy game-root renderer")}).");
            LogSource.LogInfo($"Remix bindings selected automatically: {RemixAPI.DetectedAdapter.Name} API {RemixAPI.DetectedAdapter.VersionLabel}; native Remix menu and Unity texture sharing available.");
            LogSource.LogInfo($"Interface pointers - CreateMesh: {remixInterface.CreateMesh}, DrawInstance: {remixInterface.DrawInstance}");
            
            remixInitialized = true;
            
            // Initialize components
            InitializeComponents();
            
            // Device registration is advanced from Update/UpdateFromPersistent so it survives
            // plugin recreation during scene transitions.
        }
        
        private void InitializeComponents()
        {
            LogSource.LogInfo("Initializing components...");
            configMaxTextureDimension = Config.Bind("Performance", "MaxTextureDimension", 2048,
                new ConfigDescription("Maximum dimension of textures copied into Remix. Limits the extra GPU/CPU memory needed alongside Unity. 0 keeps the original size. Changing this requires a scene reload.",
                    new AcceptableValueRange<int>(0, 16384)));
            LogSource.LogInfo($"Remix texture capture maximum dimension: {configMaxTextureDimension.Value} (0 = original size).");
            
            // Create all components with dependencies
            textureCategoryManager = new TextureCategoryManager();
            
            windowManager = new RemixWindowManager(LogSource, remixInterface);
            
            cameraHandler = new RemixCameraHandler(
                LogSource,
                configCameraName,
                configCameraTag,
                configListCameras,
                remixInterface
            );
            
            lightConverter = new RemixLightConverter(
                LogSource,
                configEnableLights,
                configLightIntensityMultiplier,
                configDebugLogInterval,
                remixInterface,
                remixApiLock
            );
            
            materialManager = new RemixMaterialManager(
                LogSource,
                textureCategoryManager,
                configCaptureTextures,
                configCaptureMaterials,
                configVerboseTextureLogging,
                remixInterface,
                remixApiLock,
                configMaxTextureDimension
            );
            
            meshConverter = new RemixMeshConverter(
                LogSource,
                materialManager,
                configDebugLogInterval,
                remixInterface,
                remixApiLock
            );
            
            frameCapture = new RemixFrameCapture(
                LogSource,
                cameraHandler,
                meshConverter,
                materialManager,
                configUseDistanceCulling,
                configMaxRenderDistance,
                configUseVisibilityCulling,
                configRendererCacheDuration,
                configDebugLogInterval,
                configCaptureStaticMeshes,
                configCaptureSkinnedMeshes,
                configHardwareSkinning,
                configPersistDisabledRenderers
            );
            frameCapture.LoadDisabledLayersString(configDisabledLayers.Value);
            
            renderThread = new RemixRenderThread(
                LogSource,
                windowManager,
                cameraHandler,
                meshConverter,
                lightConverter,
                frameCapture,
                configTargetFPS,
                configDebugLogInterval,
                configEnableLights,
                configUseGameGeometry,
                remixInterface
            );
            
            sceneMeshScanner = new SceneMeshScanner(
                LogSource,
                meshConverter,
                materialManager,
                remixApiLock,
                configSceneScanActiveOnly.Value
            );
            
            // Give render thread access to scene mesh scanner
            renderThread.SetSceneMeshScanner(sceneMeshScanner);
            
            // Initialize ImGui overlay
            if (RemixImGui.Initialize(LogSource))
            {
                settingsUI = new RemixSettingsUI(LogSource, this);
                RemixImGui.RegisterDrawCallback(new RemixImGui.DrawCallback(settingsUI.Draw));
                LogSource.LogInfo("ImGui settings overlay registered");

                // Register always-on debug HUD overlay
                debugHUD = new RemixDebugHUD(LogSource, this);
                if (RemixImGui.RegisterOverlayCallback(new RemixImGui.DrawCallback(debugHUD.Draw)))
                    LogSource.LogInfo("Debug HUD overlay registered (toggle with F3)");
                else
                    LogSource.LogWarning("Remix build does not support overlay callbacks — debug HUD unavailable");
            }
            
            LogSource.LogInfo("All components initialized");
        }
        
        private bool renderThreadStarted = false;
        private RemixUnityDisplay unityDisplay;

        private void AdvanceDeviceRegistration()
        {
            if (!remixInitialized || deviceRegistered)
                return;

            // Phase 1: start the render thread (once)
            if (!renderThreadStarted)
            {
                LogSource.LogInfo("Device registration: starting render thread...");
                try
                {
                    InitializeRemixDevice();
                    renderThreadStarted = true;
                }
                catch (Exception ex)
                {
                    LogSource.LogError($"Failed while starting render thread: {ex}");
                }
                return;
            }
            
            // Phase 2: wait for the render thread to confirm device is ready
            if (renderThread != null && renderThread.DeviceReady)
            {
                deviceRegistered = true;
                LogSource.LogInfo("Device registered — render thread confirmed ready");
            }
        }
        
        private void InitializeRemixDevice()
        {
            // Set window dimensions
            int width = Screen.width > 0 ? Screen.width : 1920;
            int height = Screen.height > 0 ? Screen.height : 1080;
            
            if (windowManager != null)
            {
                windowManager.SetWindowDimensions(width, height);
            }
            
            // Start render thread — it will create the window and D3D9 device.
            // deviceRegistered stays false until the render thread confirms DeviceReady.
            LogSource.LogInfo("Starting render thread...");
            renderThread?.Start();
        }
        
        public void Update()
        {
            if (stopped || !started) return;
            if (runtimeDetection != null)
            {
                if (!runtimeDetection.IsCompleted) return;
                var completed = runtimeDetection;
                runtimeDetection = null;
                try
                {
                    var report = completed.GetAwaiter().GetResult();
                    foreach (string message in report.Messages) LogSource.LogInfo(message);
                    if (report.Error != null) throw new InvalidOperationException(report.Error);
                    // All Unity/native renderer initialization stays on the Unity thread.
                    LoadRemixInterface(report.LibraryPath);
                }
                catch (Exception error)
                {
                    LogSource.LogError("Remix initialization stopped: " + error.Message);
                    Shutdown();
                }
                if (stopped) return;
            }
            if (renderThread != null && (renderThread.HasStopped || windowManager.CloseRequested))
            {
                Shutdown();
                return;
            }
            frameCount++;

            AdvanceDeviceRegistration();
            RefreshSceneResources();
            // Rescan for async-loaded meshes (Addressables, etc.)
            if (configEnableSceneScan.Value)
                sceneMeshScanner?.Update(Time.unscaledDeltaTime);

            // Visibility filtering for scanned instances is done in UpdateFromPersistent()
            // after the camera has been resolved by CaptureStaticMeshes
        }
        
        private int lastCapturedUnityFrame = -1;
        private Camera lastCaptureCamera;
        public void CaptureBeforeCamera(Camera camera)
        {
            if (stopped || !remixInitialized)
                return;

            // Reflection, shadow, UI and stacked cameras must not advance Remix
            // history independently of the selected world camera.
            if (camera == null || camera != cameraHandler?.GetPreferredCamera() ||
                lastCapturedUnityFrame == Time.frameCount) return;

            AdvanceDeviceRegistration();
            if (!deviceRegistered)
                return;
            lastCapturedUnityFrame = Time.frameCount;
            if (lastCaptureCamera != camera)
            {
                lastCaptureCamera = camera;
                var expectedView = Matrix4x4.Scale(new Vector3(1, 1, -1)) * camera.transform.worldToLocalMatrix;
                var expectedProjection = Matrix4x4.Perspective(camera.fieldOfView, camera.aspect, camera.nearClipPlane, camera.farClipPlane);
                float viewDifference = 0, projectionDifference = 0;
                var actualView = camera.worldToCameraMatrix;
                var actualProjection = camera.projectionMatrix;
                for (int row = 0; row < 4; row++)
                    for (int column = 0; column < 4; column++)
                    {
                        viewDifference = Math.Max(viewDifference, Math.Abs(actualView[row, column] - expectedView[row, column]));
                        projectionDifference = Math.Max(projectionDifference, Math.Abs(actualProjection[row, column] - expectedProjection[row, column]));
                    }
                LogSource.LogInfo($"[Camera capture] Before-render camera '{camera.name}' ({camera.GetInstanceID()}), Unity frame {Time.frameCount}; view difference={viewDifference:F6}, projection difference={projectionDifference:F6}.");
            }
            RefreshSceneResources();
            
            if (configDebugLogInterval.Value > 0 && frameCount % configDebugLogInterval.Value == 1 && LogSource != null)
            {
                LogSource.LogInfo($"UpdateFromPersistent: frame={frameCount}, initialized={remixInitialized}, deviceReg={deviceRegistered}");
            }
            
            frameCount++;
            
            if (configUseGameGeometry.Value && frameCapture != null && renderThread != null)
            {
                var nextState = new RemixFrameCapture.FrameState();
                nextState.frameCount = frameCount;
                
                // Refresh light cache every frame to capture transient lights (explosions, muzzle flashes)
                if (configEnableLights.Value && lightConverter != null)
                {
                    lightConverter.RefreshLightCache();
                }
                
                // Refresh camera snapshot for the UI
                if (frameCount % configRendererCacheDuration.Value == 0 && cameraHandler != null)
                {
                    cameraHandler.RefreshCameraSnapshots();
                }
                
                // Capture static meshes and camera
                frameCapture.CaptureStaticMeshes(nextState, frameCount);
                
                // Capture skinned meshes
                frameCapture.CaptureSkinnedMeshes(nextState, frameCount);
                frameCapture.CaptureParticles(nextState);
                
                // Update scene scan visibility with the camera position resolved by CaptureStaticMeshes
                if (sceneMeshScanner != null)
                {
                    Vector3 camPos = nextState.camera.valid ? nextState.camera.position : Vector3.zero;
                    sceneMeshScanner.UpdateVisibility(
                        camPos,
                        configUseDistanceCulling.Value,
                        configMaxRenderDistance.Value,
                        configUseVisibilityCulling.Value
                    );
                }

                // Send to render thread (mesh creation moved to render thread to avoid deadlocks)
                renderThread.UpdateFrameState(nextState);
            }
            else
            {
                renderThread?.UpdateFrameState(new RemixFrameCapture.FrameState { frameCount = frameCount });
            }

            // Update debug HUD snapshot after all frame data is captured
            debugHUD?.UpdateSnapshot();
            try
            {
                if (unityDisplay == null) unityDisplay = new RemixUnityDisplay(LogSource);
                unityDisplay.Update(cameraHandler?.CurrentCamera);
                renderThread?.SubmitMainThreadFrame();
                unityDisplay.CopyLatestFrame();
            }
            catch (Exception ex)
            {
                LogSource.LogError($"Unity shared output failed: {ex}");
                Shutdown();
            }
        }
        
        public void Shutdown()
        {
            if (stopped) return;
            stopped = true;
            detectionCancellation.Cancel();
#if BEPINEX6_IL2CPP
            if (sceneLoadedHandler != null)
                UnityEngine.SceneManagement.SceneManager.sceneLoaded -= sceneLoadedHandler;
            sceneLoadedHandler = null;
            if (sceneUnloadedHandler != null)
                UnityEngine.SceneManagement.SceneManager.sceneUnloaded -= sceneUnloadedHandler;
            sceneUnloadedHandler = null;
#else
            if (started)
            {
                UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
                UnityEngine.SceneManagement.SceneManager.sceneUnloaded -= OnSceneUnloaded;
            }
#endif
            CleanupRemix();
        }
        private void CleanupRemix()
        {
            if (!remixInitialized) return;
            
            LogSource.LogInfo("Cleaning up Remix...");
            unityDisplay?.Dispose();
            unityDisplay = null;
            
            // Stop render thread
            if (renderThread != null && !renderThread.Stop())
            {
                LogSource.LogError("Remix worker is still stopping; native resources remain loaded to avoid freeing active callbacks.");
                return;
            }
            deviceRegistered = false;

            RemixImGui.UnregisterDrawCallback();
            RemixImGui.UnregisterOverlayCallback();
            
            // Cleanup all components
            sceneMeshScanner?.ClearData();
            meshConverter?.Cleanup();
            materialManager?.Cleanup();
            frameCapture?.Cleanup();
            lightConverter?.ClearCache();
            
            // Shutdown Remix API
            var shutdownStatus = RemixAPI.ShutdownRemix(ref remixInterface);
            if (shutdownStatus != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                LogSource.LogWarning($"Remix shutdown returned {shutdownStatus}; native module remains mapped until process exit.");
            remixDll = IntPtr.Zero;
            remixInitialized = false;
            
            LogSource.LogInfo("Remix cleanup complete");
        }

        #region Config Accessors (for ImGui settings UI)

        public bool GetConfigBool(string key)
        {
            switch (key)
            {
                case "EnableGameGeometry": return configUseGameGeometry.Value;
                case "EnableDistanceCulling": return configUseDistanceCulling.Value;
                case "UseVisibilityCulling": return configUseVisibilityCulling.Value;
                case "EnableLights": return configEnableLights.Value;
                case "CaptureStaticMeshes": return configCaptureStaticMeshes.Value;
                case "CaptureSkinnedMeshes": return configCaptureSkinnedMeshes.Value;
                case "HardwareSkinning": return configHardwareSkinning.Value;
                case "CaptureTextures": return configCaptureTextures.Value;
                case "CaptureMaterials": return configCaptureMaterials.Value;
                case "EnableSceneScan": return configEnableSceneScan.Value;
                case "ActiveRenderersOnly": return configSceneScanActiveOnly.Value;
                case "PersistDisabledRenderers": return configPersistDisabledRenderers.Value;
                default: return false;
            }
        }

        public float GetConfigFloat(string key)
        {
            switch (key)
            {
                case "MaxRenderDistance": return configMaxRenderDistance.Value;
                case "IntensityMultiplier": return configLightIntensityMultiplier.Value;
                default: return 0f;
            }
        }

        public string GetConfigString(string key)
        {
            switch (key)
            {
                case "CameraName": return configCameraName.Value;
                case "DisabledLayers": return configDisabledLayers.Value;
                default: return "";
            }
        }

        public void SetConfig(string key, string value)
        {
            switch (key)
            {
                case "CameraName": configCameraName.Value = value; break;
                case "DisabledLayers": configDisabledLayers.Value = value; break;
            }
        }

        public int GetConfigInt(string key)
        {
            switch (key)
            {
                case "TargetFPS": return configTargetFPS.Value;
                default: return 0;
            }
        }

        public void SetConfig(string key, bool value)
        {
            switch (key)
            {
                case "EnableGameGeometry": configUseGameGeometry.Value = value; break;
                case "EnableDistanceCulling": configUseDistanceCulling.Value = value; break;
                case "UseVisibilityCulling": configUseVisibilityCulling.Value = value; break;
                case "EnableLights": configEnableLights.Value = value; break;
                case "CaptureStaticMeshes": configCaptureStaticMeshes.Value = value; break;
                case "CaptureSkinnedMeshes": configCaptureSkinnedMeshes.Value = value; break;
                case "HardwareSkinning": configHardwareSkinning.Value = value; break;
                case "CaptureTextures": configCaptureTextures.Value = value; break;
                case "CaptureMaterials": configCaptureMaterials.Value = value; break;
                case "EnableSceneScan": configEnableSceneScan.Value = value; break;
                case "ActiveRenderersOnly": configSceneScanActiveOnly.Value = value; break;
                case "PersistDisabledRenderers": configPersistDisabledRenderers.Value = value; break;
            }
        }

        public void SetConfig(string key, float value)
        {
            switch (key)
            {
                case "MaxRenderDistance": configMaxRenderDistance.Value = value; break;
                case "IntensityMultiplier": configLightIntensityMultiplier.Value = value; break;
            }
        }

        public void SetConfig(string key, int value)
        {
            switch (key)
            {
                case "TargetFPS": configTargetFPS.Value = value; break;
            }
        }

        public void SaveConfig() => Config.Save();

        public RemixCameraHandler CameraHandler => cameraHandler;

        public RemixFrameCapture FrameCapture => frameCapture;

        public SceneMeshScanner SceneMeshScanner => sceneMeshScanner;

        public RemixMeshConverter MeshConverter => meshConverter;

        public RemixMaterialManager MaterialManager => materialManager;

        public RemixLightConverter LightConverter => lightConverter;

        #endregion
    }
}
