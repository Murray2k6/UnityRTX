using System;
using UnityEngine;
using UnityEngine.Rendering;
#if BEPINEX6_IL2CPP
using Il2CppInterop.Runtime.Attributes;
#endif

namespace UnityRemix
{
    // Only lifecycle callbacks cross into IL2CPP; the runtime stays managed.
    public sealed class RemixPersistentBehaviour : MonoBehaviour
    {
        private UnityRemixPlugin plugin;
        private Camera.CameraCallback beforeCamera;
#if BEPINEX6_IL2CPP
        private Il2CppSystem.Action<ScriptableRenderContext, Camera> beforeSrpCamera;
#else
        private Action<ScriptableRenderContext, Camera> beforeSrpCamera;
#endif
#if BEPINEX6_IL2CPP
        public RemixPersistentBehaviour(IntPtr pointer) : base(pointer) { }
        [HideFromIl2Cpp]
#endif
        public void Initialize(UnityRemixPlugin runtime)
        {
            plugin = runtime;
            DontDestroyOnLoad(gameObject);
        }

        // IL2CPP's Load callback can run before the engine is ready for rendering.
        // Start both backends on the first normal Unity Start callback.
        private void Start()
        {
            if (plugin == null) return;
            try
            {
                plugin.Start();
#if BEPINEX6_IL2CPP
                beforeCamera = (Action<Camera>)BeforeCamera;
                beforeSrpCamera = (Action<ScriptableRenderContext, Camera>)BeforeSrpCamera;
#else
                beforeCamera = BeforeCamera;
                beforeSrpCamera = BeforeSrpCamera;
#endif
                Camera.onPreCull += beforeCamera;
                RenderPipelineManager.beginCameraRendering += beforeSrpCamera;
            }
            catch (Exception ex)
            {
                UnityRemixPlugin.LogSource.LogError($"Unity RTX Remix initialization failed: {ex}");
                Shutdown();
            }
        }

        private void Update() => plugin?.Update();
        // Capture after every game's LateUpdate camera/animation controllers.
        // Both built-in and scriptable pipelines stay on Unity's main thread.
#if BEPINEX6_IL2CPP
        [HideFromIl2Cpp]
#endif
        private void BeforeCamera(Camera camera)
        {
            try { plugin?.CaptureBeforeCamera(camera); }
            catch (Exception ex)
            {
                UnityRemixPlugin.LogSource.LogError($"Unity frame capture failed: {ex}");
                Shutdown();
            }
        }
#if BEPINEX6_IL2CPP
        [HideFromIl2Cpp]
#endif
        private void BeforeSrpCamera(ScriptableRenderContext context, Camera camera) => BeforeCamera(camera);
        private void OnApplicationQuit() => Shutdown();
        private void OnDestroy() => Shutdown();

#if BEPINEX6_IL2CPP
        [HideFromIl2Cpp]
#endif
        public void Shutdown()
        {
            if (beforeCamera != null) Camera.onPreCull -= beforeCamera;
            if (beforeSrpCamera != null) RenderPipelineManager.beginCameraRendering -= beforeSrpCamera;
            beforeCamera = null;
            beforeSrpCamera = null;
            var runtime = plugin;
            plugin = null;
            runtime?.Shutdown();
        }
    }
}
