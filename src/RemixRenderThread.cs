using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace UnityRemix
{
    /// <summary>
    /// Submits one Remix frame before Unity draws the corresponding camera/UI.
    /// Managed submissions stay on Unity's main thread on both Mono and IL2CPP.
    /// </summary>
    public class RemixRenderThread
    {
        private readonly ManualLogSource logger;
        private readonly RemixWindowManager windowManager;
        private readonly RemixCameraHandler cameraHandler;
        private readonly RemixMeshConverter meshConverter;
        private readonly RemixLightConverter lightConverter;
        private readonly RemixFrameCapture frameCapture;
        
        private readonly ConfigEntry<int> configTargetFPS;
        private readonly ConfigEntry<int> configDebugLogInterval;
        private readonly ConfigEntry<bool> configEnableLights;
        private readonly ConfigEntry<bool> configUseGameGeometry;
        
        // Cached delegates
        private RemixAPI.PFN_remixapi_Present presentFunc;
        private RemixAPI.PFN_remixapi_GetVramStats getVramStats;
        private RemixAPI.PFN_remixapi_RequestVramCompaction requestVramCompaction;
        private RemixAPI.PFN_remixapi_ResetScene resetNativeScene;
        private DateTime nextMemoryLog = DateTime.MinValue;
        
        // Thread state
        private volatile bool renderThreadRunning = false;
        private volatile bool deviceReady = false;
        
        /// <summary>
        /// True once the render thread has initialized Remix's offscreen device.
        /// The main thread must not call any Remix API until this is true.
        /// </summary>
        public bool DeviceReady => deviceReady;
        public bool HasStopped => started && !renderThreadRunning;
        private bool started;
        private int mainThreadFrame;
        private long mainThreadPublishedFrame;
        private bool frameRateOwned;
        private int originalFrameRate, appliedFrameRate;
        
        // Test objects
        private IntPtr testMeshHandle = IntPtr.Zero;
        private IntPtr testLightHandle = IntPtr.Zero;
        
        // Frame state
        private volatile RemixFrameCapture.FrameState currentFrameState = new RemixFrameCapture.FrameState();
        private readonly object captureLock = new object();
        private readonly object renderLifecycleLock = new object();
        private long publishedFrame;

        // Scene resets discard every published handle before releasing native
        // resources. Wait for the current Present to finish first.
        public void ResetSceneResources(Action reset)
        {
            lock (renderLifecycleLock)
            {
                lock (captureLock) { currentFrameState = new RemixFrameCapture.FrameState(); publishedFrame++; }
                if (resetNativeScene != null)
                {
                    var result = resetNativeScene();
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                        throw new InvalidOperationException($"Native scene reset failed: {result}");
                }
                reset();
            }
        }
        
        // Scene mesh scanner (optional)
        private SceneMeshScanner sceneMeshScanner;
        
        public RemixRenderThread(
            ManualLogSource logger,
            RemixWindowManager windowManager,
            RemixCameraHandler cameraHandler,
            RemixMeshConverter meshConverter,
            RemixLightConverter lightConverter,
            RemixFrameCapture frameCapture,
            ConfigEntry<int> targetFPS,
            ConfigEntry<int> debugLogInterval,
            ConfigEntry<bool> enableLights,
            ConfigEntry<bool> useGameGeometry,
            RemixAPI.remixapi_Interface remixInterface)
        {
            this.logger = logger;
            this.windowManager = windowManager;
            this.cameraHandler = cameraHandler;
            this.meshConverter = meshConverter;
            this.lightConverter = lightConverter;
            this.frameCapture = frameCapture;
            this.configTargetFPS = targetFPS;
            this.configDebugLogInterval = debugLogInterval;
            this.configEnableLights = enableLights;
            this.configUseGameGeometry = useGameGeometry;
            
            // Cache delegate
            IntPtr resetSceneAddress = RemixAPI.GetRemixProcAddress("remixapi_ResetScene");
            if (resetSceneAddress != IntPtr.Zero)
                resetNativeScene = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_ResetScene>(resetSceneAddress);
            else
                logger.LogWarning("Native Remix runtime lacks the synchronized scene-reset extension; update the native Unity bridge for safe scene replacement.");
            if (remixInterface.GetVramStats != IntPtr.Zero)
                getVramStats = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_GetVramStats>(remixInterface.GetVramStats);
            if (remixInterface.RequestVramCompaction != IntPtr.Zero)
                requestVramCompaction = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_RequestVramCompaction>(remixInterface.RequestVramCompaction);
            if (remixInterface.Present != IntPtr.Zero)
            {
                presentFunc = Marshal.GetDelegateForFunctionPointer<RemixAPI.PFN_remixapi_Present>(
                    remixInterface.Present);
            }
        }
        
        /// <summary>
        /// Set the scene mesh scanner for drawing scanned level geometry.
        /// </summary>
        public void SetSceneMeshScanner(SceneMeshScanner scanner)
        {
            sceneMeshScanner = scanner;
        }
        
        /// <summary>
        /// Update frame state (called from main thread)
        /// </summary>
        public void UpdateFrameState(RemixFrameCapture.FrameState newState)
        {
            lock (captureLock)
            {
                currentFrameState = newState;
                publishedFrame++;
            }
        }
        
        /// <summary>
        /// Start the render thread
        /// </summary>
        public void Start()
        {
            if (renderThreadRunning)
            {
                logger.LogInfo("Render thread already running");
                return;
            }
            
            renderThreadRunning = true;
            started = true;
            try
            {
                InitializeRenderer();
                logger.LogInfo("Remix submits on Unity's main thread and completes shared output before the matching camera/UI draw.");
            }
            catch
            {
                renderThreadRunning = false;
                throw;
            }
        }

        // Called before the selected Unity camera renders, after publishing its snapshot and targets.
        public void SubmitMainThreadFrame()
        {
            if (!renderThreadRunning || !deviceReady || windowManager.CloseRequested) return;
            // Cap the whole host frame; skipping just Remix would reintroduce
            // different camera poses in the shared image and Unity UI.
            int current = UnityEngine.Application.targetFrameRate;
            if (frameRateOwned && current != appliedFrameRate) frameRateOwned = false;
            if (configTargetFPS.Value > 0)
            {
                if (!frameRateOwned) originalFrameRate = current;
                appliedFrameRate = originalFrameRate > 0 ? Math.Min(originalFrameRate, configTargetFPS.Value) : configTargetFPS.Value;
                UnityEngine.Application.targetFrameRate = appliedFrameRate;
                frameRateOwned = true;
            }
            else RestoreFrameRate();
            lock (renderLifecycleLock)
            {
                lock (captureLock)
                {
                    if (mainThreadPublishedFrame == publishedFrame) return;
                    mainThreadPublishedFrame = publishedFrame;
                }
                RenderFrame(mainThreadFrame++);
            }
        }
        
        /// <summary>
        /// Stop the render thread
        /// </summary>
        public bool Stop()
        {
            renderThreadRunning = false;
            deviceReady = false;
            RestoreFrameRate();
            windowManager.DestroyRemixWindow();
            return true;
        }
        private void RestoreFrameRate()
        {
            if (frameRateOwned && UnityEngine.Application.targetFrameRate == appliedFrameRate)
                UnityEngine.Application.targetFrameRate = originalFrameRate;
            frameRateOwned = false;
        }

        private void InitializeRenderer()
        {
            // Initialize the offscreen renderer against Unity's existing window.
            if (!windowManager.CreateRemixWindow())
            {
                throw new InvalidOperationException("Failed to initialize Remix shared output");
            }
            
            // Signal that the device is ready for API calls from other threads
            deviceReady = true;
            logger.LogInfo("Remix device ready — main thread may now call API");
            
            // Create test objects
            logger.LogInfo("Creating test triangle and light...");
            testMeshHandle = meshConverter.CreateTestTriangle();
            testLightHandle = lightConverter.CreateTestLight();
        }

        /// <summary>
        /// Render a single frame
        /// </summary>
        private void RenderFrame(int frameNum)
        {
            try
            {
                if (configUseGameGeometry.Value)
                {
                    // Resolve queued mesh handles before submitting this snapshot.
                    frameCapture?.ProcessMeshCreationBatch();
                    
                    // Render game geometry
                    RenderGameGeometry();
                    
                    // Process Unity lights
                    lightConverter.ProcessLights(frameNum);
                    
                    // Draw test light if lights disabled
                    if (!configEnableLights.Value && testLightHandle != IntPtr.Zero)
                    {
                        // DrawLightInstance would be called here
                    }
                }
                else
                {
                    // Test mode - just render triangle and light
                    cameraHandler.SetupTestCamera(
                        windowManager.WindowWidth,
                        windowManager.WindowHeight,
                        frameNum
                    );
                    
                    // Draw test objects
                    if (testMeshHandle != IntPtr.Zero)
                    {
                        meshConverter.DrawMeshInstance(
                            testMeshHandle,
                            UnityEngine.Matrix4x4.identity,
                            1
                        );
                    }
                }
                
                // Present
                if (presentFunc != null)
                {
                    var presentInfo = new RemixAPI.remixapi_PresentInfo
                    {
                        sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_PRESENT_INFO,
                        pNext = IntPtr.Zero,
                        hwndOverride = IntPtr.Zero
                    };
                    
                    var result = presentFunc(ref presentInfo);
                    if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        if (configDebugLogInterval.Value > 0 && frameNum % configDebugLogInterval.Value == 0)
                        {
                            logger.LogWarning($"Present failed: {result}");
                        }
                    }
                }
                if (getVramStats != null && DateTime.UtcNow >= nextMemoryLog)
                {
                    nextMemoryLog = DateTime.UtcNow.AddSeconds(30);
                    if (getVramStats(out var memory) == RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                    {
                        logger.LogInfo($"[RemixMemory] MiB: used={memory.totalUsedBytes / 1048576} retained={memory.poolRetainedBytes / 1048576} " +
                            $"textures={memory.usedMaterialTextureBytes / 1048576} geometry={memory.usedReplacementGeometryBytes / 1048576} " +
                            $"buffers={memory.usedBufferBytes / 1048576} acceleration={memory.usedAccelerationStructureBytes / 1048576} " +
                            $"targets={memory.usedRenderTargetBytes / 1048576} driver={memory.driverAllocatedBytes / 1048576} " +
                            $"budget={memory.driverBudgetBytes / 1048576} textureEntries={memory.forkTextureCacheCount}");
                        // Unity and Remix share the GPU. Vulkan's budget alone
                        // does not reliably reflect pressure from Unity's other
                        // graphics device. Periodically return completely empty
                        // chunks once retained space exceeds 512 MiB. Live
                        // suballocations and textures are never purged here.
                        if (requestVramCompaction != null && memory.poolRetainedBytes > 512UL * 1048576)
                        {
                            var result = requestVramCompaction();
                            if (result != RemixAPI.remixapi_ErrorCode.REMIXAPI_ERROR_CODE_SUCCESS)
                                logger.LogWarning($"Remix memory compaction request failed: {result}");
                            else
                                logger.LogInfo("[RemixMemory] Requested release of empty allocator blocks.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError($"Error in RenderFrame: {ex}");
                // A native exception can leave the device unusable. Exit cleanly
                // instead of calling it again on every frame after the failure.
                renderThreadRunning = false;
            }
        }
        
        /// <summary>
        /// Render game geometry from captured frame state
        /// </summary>
        private void RenderGameGeometry()
        {
            // Get frame state atomically
            RemixFrameCapture.FrameState state;
            lock (captureLock)
            {
                state = currentFrameState;
            }
            
            // Setup camera - CRITICAL for rendering!
            if (state.camera.valid)
            {
                var cam = state.camera;
                // Convert camera from Unity Y-up to Z-up coordinate system
                cameraHandler.SetupRemixCamera(
                    cam.position, cam.forward, cam.up, cam.right,
                    cam.fov, cam.aspect, cam.nearPlane, cam.farPlane
                );
            }
            else
            {
                // Fallback to test camera if no valid camera captured
                cameraHandler.SetupTestCamera(
                    windowManager.WindowWidth,
                    windowManager.WindowHeight,
                    state.frameCount
                );
            }
            
            // Draw static mesh instances
            uint objectPickingValue = 1;
            var claimedRendererIds = new HashSet<int>();
            var claimedStaticKeys = new HashSet<StaticGeometryKey>();
            
            foreach (var instance in state.instances)
            {
                ulong meshKey = instance.meshKey != 0 ? instance.meshKey : (ulong)(uint)instance.meshId;
                if (meshConverter.TryGetMeshHandle(meshKey, out IntPtr meshHandle))
                {
                    if (!StaticGeometryDedupe.TryClaimVisibleInstance(instance.rendererInstanceId, instance.dedupeKey, claimedRendererIds, claimedStaticKeys))
                        continue;

                    meshConverter.DrawMeshInstance(meshHandle, instance.localToWorld, objectPickingValue);
                    objectPickingValue++;
                }
            }
            
            // Draw scanned scene mesh instances
            // Always call GetInstances during streaming to drain the queue
            if (sceneMeshScanner != null && (sceneMeshScanner.HasData || sceneMeshScanner.IsStreaming))
            {
                var scannedInstances = sceneMeshScanner.GetInstances();
                if (scannedInstances != null)
                {
                    var drawFunc = meshConverter.GetDrawInstanceFunc();
                    if (drawFunc != null)
                    {
                        foreach (var instance in scannedInstances)
                        {
                            if (instance.MeshHandle == IntPtr.Zero)
                                continue;
                            
                            if (frameCapture != null && frameCapture.IsLayerDisabled(instance.Layer))
                                continue;

                            if (!StaticGeometryDedupe.TryClaimVisibleInstance(instance.RendererInstanceId, instance.DedupeKey, claimedRendererIds, claimedStaticKeys))
                                continue;
                            
                            var instanceInfo = new RemixAPI.remixapi_InstanceInfo
                            {
                                sType = RemixAPI.remixapi_StructType.REMIXAPI_STRUCT_TYPE_INSTANCE_INFO,
                                pNext = IntPtr.Zero,
                                categoryFlags = 0,
                                mesh = instance.MeshHandle,
                                transform = instance.Transform,
                                doubleSided = 1
                            };
                            
                            drawFunc(ref instanceInfo);
                            objectPickingValue++;
                        }
                    }
                }
            }
            
            // Draw skinned meshes
            RenderSkinnedMeshes(state, objectPickingValue);
        }
        
        /// <summary>
        /// Render skinned meshes from frame state
        /// </summary>
        private void RenderSkinnedMeshes(RemixFrameCapture.FrameState state, uint startObjectPickingValue)
        {
            if (state.skinned == null || state.skinned.Count == 0)
            {
                meshConverter.CleanupStaleSkinnedMeshes(new HashSet<ulong>());
                return;
            }
            
            HashSet<ulong> updatedMeshes = new HashSet<ulong>();
            uint objectPickingValue = startObjectPickingValue;
            
            foreach (var skinned in state.skinned)
            {
                try
                {
                    if (skinned.skinningData != null && skinned.boneTransforms != null)
                    {
                        // GPU skinning path: create mesh once with bone weights, draw with bone transforms each frame
                        IntPtr meshHandle;
                        if (!skinned.skinningData.meshCreated ||
                            !meshConverter.TryGetSkinnedMeshHandle(skinned.remixMeshHash, out meshHandle) ||
                            meshHandle == IntPtr.Zero)
                        {
                            meshHandle = meshConverter.CreateSkinnedMeshWithBones(
                                skinned.remixMeshHash,
                                skinned.vertices,
                                skinned.normals,
                                skinned.uvs,
                                skinned.triangles,
                                skinned.skinningData.blendWeights,
                                skinned.skinningData.blendIndices,
                                skinned.skinningData.bonesPerVertex,
                                skinned.materialId,
                                skinned.colors
                            );
                            
                            if (meshHandle == IntPtr.Zero)
                                continue;
                            
                            meshConverter.UpdateSkinnedMeshHandle(skinned.remixMeshHash, meshHandle);
                            skinned.skinningData.meshCreated = true;
                        }
                        
                        updatedMeshes.Add(skinned.remixMeshHash);
                        meshConverter.DrawSkinnedInstance(meshHandle, skinned.localToWorld, skinned.boneTransforms, objectPickingValue);
                        objectPickingValue++;
                    }
                    else
                    {
                        // BakeMesh fallback: recreate mesh each frame with new vertex data
                        IntPtr meshHandle = meshConverter.CreateRemixMeshFromData(
                            skinned.remixMeshHash,
                            skinned.vertices,
                            skinned.normals,
                            skinned.uvs,
                            skinned.triangles,
                            state.frameCount,
                            skinned.materialId,
                            skinned.colors
                        );
                        
                        if (meshHandle == IntPtr.Zero)
                            continue;
                        
                        meshConverter.UpdateSkinnedMeshHandle(skinned.remixMeshHash, meshHandle);
                        updatedMeshes.Add(skinned.remixMeshHash);
                        meshConverter.DrawMeshInstance(meshHandle, skinned.localToWorld, objectPickingValue, skinned.particle);
                        objectPickingValue++;
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Unable to render skinned mesh 0x{skinned.remixMeshHash:X16}", ex);
                }
            }
            
            // Cleanup stale meshes periodically
            if (state.frameCount % 60 == 0)
            {
                meshConverter.CleanupStaleSkinnedMeshes(updatedMeshes);
            }
        }
    }
}
