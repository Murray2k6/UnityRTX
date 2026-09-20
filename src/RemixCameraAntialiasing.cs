using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    internal sealed class RemixCameraAntialiasing : IDisposable
    {
        private static readonly string[] Types = {
            "UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, Unity.RenderPipelines.Universal.Runtime",
            "UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, Unity.RenderPipelines.HighDefinition.Runtime",
            "UnityEngine.Rendering.PostProcessing.PostProcessLayer, Unity.Postprocessing.Runtime"
        };
        private readonly ManualLogSource logger;
        private readonly List<TemporalAntialiasingOverride> overrides = new List<TemporalAntialiasingOverride>();
        private readonly HashSet<string> failures = new HashSet<string>();
        private Camera camera;
        private float nextDiscovery;

        public RemixCameraAntialiasing(ManualLogSource logger) { this.logger = logger; }

        public void Update(Camera next, bool compositing)
        {
            if (!compositing) next = null;
            if (camera != next)
            {
                Restore();
                camera = next;
                nextDiscovery = 0;
            }
            if (camera == null) return;
            if (overrides.Count == 0 && Time.unscaledTime >= nextDiscovery)
            {
                nextDiscovery = Time.unscaledTime + 2;
                Discover();
            }
            foreach (var entry in overrides)
            {
                try
                {
                    if (entry.Update())
                        logger.LogInfo($"[Compositor] Suppressed Unity temporal AA on '{camera.name}' while displaying Remix's resolved image.");
                }
                catch (Exception error) { WarnOnce(error); }
            }
        }

        private void Discover()
        {
            foreach (string qualifiedName in Types)
            {
                try
                {
                    // Optional pipeline assemblies stay optional on both Mono and
                    // IL2CPP. A typed generic getter returns the proper interop wrapper.
                    Type type = Type.GetType(qualifiedName, false);
                    if (type == null) continue;
                    MethodInfo getter = null;
                    foreach (var method in typeof(Component).GetMethods())
                        if (method.Name == "GetComponent" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0)
                        { getter = method; break; }
                    if (getter == null) continue;
                    object component = getter.MakeGenericMethod(type).Invoke(camera, null);
                    if (component == null) continue;
                    string member = type.Name == "PostProcessLayer" ? "antialiasingMode" : "antialiasing";
                    var entry = new TemporalAntialiasingOverride(component, member);
                    if (!entry.Supported) continue;
                    logger.LogInfo($"[Compositor] Camera '{camera.name}': {type.Name}.{member}={entry.Current}.");
                    overrides.Add(entry);
                    LogMotionBlur(component, type);
                }
                catch (System.IO.FileNotFoundException) { }
                catch (Exception error) { WarnOnce(error); }
            }
        }

        private void LogMotionBlur(object cameraData, Type cameraDataType)
        {
            if (cameraDataType.Name != "UniversalAdditionalCameraData" &&
                cameraDataType.Name != "HDAdditionalCameraData") return;
            try
            {
                string pipeline = cameraDataType.Name == "UniversalAdditionalCameraData" ? "Universal" : "HighDefinition";
                string assembly = pipeline == "Universal" ? "Unity.RenderPipelines.Universal.Runtime" : "Unity.RenderPipelines.HighDefinition.Runtime";
                Type blurType = Type.GetType($"UnityEngine.Rendering.{pipeline}.MotionBlur, {assembly}", false);
                Type managerType = Type.GetType("UnityEngine.Rendering.VolumeManager, Unity.RenderPipelines.Core.Runtime", false);
                if (blurType == null || managerType == null) return;
                object stack = cameraDataType.GetProperty("volumeStack")?.GetValue(cameraData);
                string source = "camera";
                if (stack == null)
                {
                    object manager = managerType.GetProperty("instance")?.GetValue(null);
                    stack = managerType.GetProperty("stack")?.GetValue(manager);
                    source = "last evaluated global";
                }
                if (stack == null) return;
                MethodInfo getter = null;
                foreach (var method in stack.GetType().GetMethods())
                    if (method.Name == "GetComponent" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0)
                    { getter = method; break; }
                object blur = getter?.MakeGenericMethod(blurType).Invoke(stack, null);
                if (blur == null) return;
                object intensity = blurType.GetProperty("intensity")?.GetValue(blur) ?? blurType.GetField("intensity")?.GetValue(blur);
                object value = intensity?.GetType().GetProperty("value")?.GetValue(intensity);
                object active = blurType.GetMethod("IsActive", Type.EmptyTypes)?.Invoke(blur, null);
                object post = cameraDataType.GetProperty("renderPostProcessing")?.GetValue(cameraData);
                logger.LogInfo($"[Compositor] Camera '{camera.name}': postProcessing={post ?? "unknown"}; {source} MotionBlur active={active ?? "unknown"}, intensity={value ?? "unknown"}.");
            }
            catch (Exception error) { WarnOnce(error); }
        }

        private void WarnOnce(Exception error)
        {
            string message = (error.InnerException ?? error).Message;
            if (failures.Add(message)) logger.LogWarning("[Compositor] Camera AA integration: " + message);
        }

        private void Restore()
        {
            foreach (var entry in overrides)
                try { entry.Restore(); } catch (Exception error) { WarnOnce(error); }
            overrides.Clear();
        }
        public void Dispose() { Restore(); camera = null; }
    }
}
