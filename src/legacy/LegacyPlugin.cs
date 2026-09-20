using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityRemix.Legacy
{
    [BepInPlugin("com.Unity.remix", "Unity RTX Remix", "1.0.0")]
    public sealed class LegacyPlugin : BaseUnityPlugin
    {
        private sealed class MaterialCopy
        {
            public Material Source, Adapted;
            public MaterialMode Mode;
            public int LastSeen;
            public long SourceFingerprint;
            public bool Synchronized;
        }
        private sealed class RendererBinding
        {
            public Renderer Renderer;
            public Material[] Originals, Assigned;
        }
        private sealed class GraphicBinding
        {
            public Object Graphic;
        }
        private sealed class WaterInput
        {
            public Material Material;
            public Texture Texture;
        }

        private readonly Dictionary<long, MaterialCopy> materials = new Dictionary<long, MaterialCopy>();
        private readonly Dictionary<int, MaterialCopy> adaptedMaterials = new Dictionary<int, MaterialCopy>();
        private readonly Dictionary<int, RendererBinding> bindings = new Dictionary<int, RendererBinding>();
        private readonly Dictionary<MaterialMode, Material> templates = new Dictionary<MaterialMode, Material>();
        private readonly Dictionary<int, GraphicBinding> graphics = new Dictionary<int, GraphicBinding>();
        private readonly LegacyUiDiscovery ui = new LegacyUiDiscovery();
        private readonly LegacyLighting lighting = new LegacyLighting();
        private readonly HashSet<string> warnedShaders = new HashSet<string>();
        private readonly Queue<Texture> canvasTextures = new Queue<Texture>();
        private readonly Queue<Texture> skyTextures = new Queue<Texture>();
        private readonly Queue<WaterInput> proceduralWaterTextures = new Queue<WaterInput>();
        private readonly HashSet<Material> activeSkyMaterials = new HashSet<Material>();
        private static readonly string[] SkyTextureProperties = { "_FrontTex", "_BackTex", "_LeftTex", "_RightTex", "_UpTex", "_DownTex", "_Tex", "_MainTex", "_Cubemap", "_SkyCubemap", "_SkyTex", "_SkyTexture", "_DayTex", "_NightTex", "_StarsTex", "_CloudTex", "_CloudsTex" };
        private static readonly System.Reflection.MethodInfo GetTexturePropertyNames = typeof(Material).GetMethod("GetTexturePropertyNames", Type.EmptyTypes);
        private static readonly string[] WaterTextureProperties = UnityMaterialSemantics.WaterInputs;
        private Texture2D fallbackTexture, uiMarker, skyMarker, waterMarker;
        private IntPtr taggedMarker, taggedSky, taggedWater;
        private LegacyBridge bridge;
        private ConfigEntry<bool> convertMaterials, tagUi, fallbackSun, diagnostics;
        private ConfigEntry<float> sunIntensity;
        private ConfigEntry<int> materialLimit;
        private Renderer[] renderers = new Renderer[0];
        private bool ready, stopped, menuHeld, savedLock, savedCursor;
        private float nextScan, nextProbe, deadline;
        private int scanGeneration, sceneScans;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            convertMaterials = Config.Bind("Legacy", "ConvertWorldMaterials", true,
                "Convert world materials to fixed-function D3D9 passes for RTX Remix. UI uses a separate raster pass; skybox shaders retain their original passes.");
            tagUi = Config.Bind("Legacy", "PreserveUnityUI", true,
                "Keep Unity UI and text in a separate raster pass above the ray-traced scene. Shared game textures are never globally tagged as UI.");
            materialLimit = Config.Bind("Legacy", "MaxConvertedMaterials", 2048,
                "Maximum live material copies. Original Unity textures are shared; no duplicate texture uploads are created.");
            fallbackSun = Config.Bind("Lighting", "FallbackDirectionalLight", true,
                "Add one directional light only when the active scene has no enabled directional light. Existing game lights retain their direction, color and intensity.");
            sunIntensity = Config.Bind("Lighting", "FallbackDirectionalIntensity", 1.0f, "Intensity of the fallback directional light.");
            diagnostics = Config.Bind("Legacy", "LogSceneSummary", false, "Log material and light routing once after each scene load for troubleshooting.");
            Logger.LogInfo("Runtime: BepInEx " + typeof(BaseUnityPlugin).Assembly.GetName().Version +
                "; Unity " + Application.unityVersion + "; Mono CLR " + Environment.Version + "; " + (IntPtr.Size * 8) + "-bit.");
            Logger.LogInfo("Legacy renderer: Direct3D 9 client and separate 64-bit RTX Remix server; Unity owns the game window and input.");
            fallbackTexture = CreateTexture(0);
            uiMarker = CreateTexture(1);
            skyMarker = CreateTexture(2);
            waterMarker = CreateTexture(3);
        }

        private static Texture2D CreateTexture(int category)
        {
            var texture = new Texture2D(8, 8, TextureFormat.RGBA32, false) {
                name = "UnityRemix draw texture " + category, hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color[64];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(1, 1, 1, category == 0 ? 1 : (i % 2 == 0 ? 0.157f : 0.478f) + category * 0.1f);
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private void Start()
        {
            try
            {
                if (Application.platform != RuntimePlatform.WindowsPlayer) throw new NotSupportedException("The legacy Remix bridge requires Windows.");
                bridge = new LegacyBridge();
                deadline = Time.realtimeSinceStartup + 30;
            }
            catch (Exception error) { StopWithError(error); }
        }

        private void Update()
        {
            if (stopped || bridge == null) return;
            try
            {
                float now = Time.realtimeSinceStartup;
                if (!ready)
                {
                    if (now < nextProbe) return;
                    nextProbe = now + 1;
                    ulong version;
                    if (!bridge.QueryRenderer(out version))
                    {
                        if (now > deadline) throw new InvalidOperationException("The 64-bit Remix renderer did not become ready. Check the .trex bridge log.");
                        return;
                    }
                    Logger.LogInfo("Negotiated Remix API contract " + (version >> 48) + "." + ((version >> 16) & 0xffffffffUL) +
                        "." + (version & 0xffff) + "; native menu and Unity UI texture tagging available.");
                    ready = true;
                }
                if (now >= nextScan)
                {
                    nextScan = now + 1;
                    Rescan();
                }
                foreach (Renderer renderer in renderers) if (renderer != null) Adapt(renderer);
                UpdateGraphics();
                foreach (MaterialCopy copy in materials.Values)
                    if (copy.Source != null && copy.Adapted != null) Synchronize(copy);
                // Only our marker is tagged. A font or sprite atlas can also be used by world geometry.
                if (tagUi.Value && uiMarker != null)
                {
                    IntPtr native = uiMarker.GetNativeTexturePtr();
                    if (native != IntPtr.Zero && native != taggedMarker && bridge.TagUiTexture(native)) taggedMarker = native;
                }
                if (bridge.SupportsCategories)
                {
                    TagCategory(skyMarker, 1, ref taggedSky);
                    TagCategory(waterMarker, 2, ref taggedWater);
                    if (tagUi.Value && canvasTextures.Count > 0)
                    {
                        Texture texture = canvasTextures.Dequeue();
                        if (texture != null) bridge.TagCategory(texture.GetNativeTexturePtr(), 3);
                    }
                    if (skyTextures.Count > 0) { Texture texture = skyTextures.Dequeue(); if (texture != null) bridge.TagCategory(texture.GetNativeTexturePtr(), 1); }
                    if (proceduralWaterTextures.Count > 0)
                    {
                        WaterInput input = proceduralWaterTextures.Dequeue();
                        if (input.Texture != null && input.Material != null)
                        {
                            float ior = input.Material.HasProperty("_IOR") ? input.Material.GetFloat("_IOR") : 1.333f;
                            IntPtr native = input.Texture.GetNativeTexturePtr();
                            if (!bridge.RegisterWater(native, LegacyMaterialProperties.WaterColor(input.Material), UnityMaterialSemantics.WaterIor(ior)))
                                bridge.TagCategory(native, 4);
                        }
                    }
                }
            }
            catch (Exception error) { StopWithError(error); }
        }

        private void TagCategory(Texture texture, uint category, ref IntPtr tagged)
        {
            IntPtr native = texture.GetNativeTexturePtr();
            if (native != IntPtr.Zero && native != tagged && bridge.TagCategory(native, category)) tagged = native;
        }

        private void LateUpdate()
        {
            if (stopped || !ready) return;
            if (bridge.MenuOpen)
            {
                if (!menuHeld) { savedLock = Screen.lockCursor; savedCursor = Screen.showCursor; menuHeld = true; }
                Screen.lockCursor = false;
                Screen.showCursor = true;
            }
            else RestoreCursor();
        }

        private void Rescan()
        {
            scanGeneration++;
            lighting.Update(convertMaterials.Value && fallbackSun.Value, sunIntensity.Value);
            renderers = Object.FindObjectsOfType<Renderer>();
            activeSkyMaterials.Clear();
            if (RenderSettings.skybox != null) activeSkyMaterials.Add(RenderSettings.skybox);
            foreach (Skybox sky in Object.FindObjectsOfType<Skybox>())
                if (sky != null && sky.enabled && sky.material != null) activeSkyMaterials.Add(sky.material);
            ui.Scan(renderers);
            foreach (Renderer renderer in renderers) if (renderer != null) Adapt(renderer);
            if (tagUi.Value)
            {
                var seen = new HashSet<int>();
                bool enqueue = canvasTextures.Count == 0;
                foreach (Object graphic in ui.Graphics)
                {
                    if (graphic != null && !graphics.ContainsKey(graphic.GetInstanceID()))
                        graphics.Add(graphic.GetInstanceID(), new GraphicBinding { Graphic = graphic });
                    Texture texture = graphic == null ? null : ui.GetTexture(graphic);
                    if (enqueue && texture != null && seen.Add(texture.GetInstanceID()) && canvasTextures.Count < Math.Max(1, materialLimit.Value)) canvasTextures.Enqueue(texture);
                }
            }
            if (skyTextures.Count == 0 && bridge.SupportsCategories)
            {
                var seen = new HashSet<int>();
                foreach (Material sky in activeSkyMaterials) EnqueueSkybox(sky, seen);
                foreach (Renderer renderer in renderers)
                    if (renderer != null)
                        foreach (Material material in renderer.sharedMaterials)
                        {
                            Material original = Original(material);
                            if (IsSky(original, renderer.name))
                                EnqueueSkybox(original, seen);
                        }
            }
            UpdateGraphics();
            if (proceduralWaterTextures.Count == 0 && bridge.SupportsCategories)
            {
                var seen = new HashSet<int>();
                foreach (Renderer renderer in renderers)
                    if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy)
                        foreach (Material material in renderer.sharedMaterials)
                        {
                            Material original = Original(material);
                            if (original == null || original.shader == null || !IsProceduralWater(original)) continue;
                            foreach (string property in WaterTextureProperties)
                                if (original.HasProperty(property))
                                {
                                    Texture texture = original.GetTexture(property);
                                    if (texture != null && seen.Add(texture.GetInstanceID()) && proceduralWaterTextures.Count < Math.Max(1, materialLimit.Value))
                                        proceduralWaterTextures.Enqueue(new WaterInput { Material = original, Texture = texture });
                                }
                        }
            }
            var deadRenderers = new List<int>();
            foreach (var entry in bindings)
                if (entry.Value.Renderer == null || !entry.Value.Renderer.enabled || !entry.Value.Renderer.gameObject.activeInHierarchy) deadRenderers.Add(entry.Key);
            foreach (int id in deadRenderers) { RestoreBinding(bindings[id]); bindings.Remove(id); }
            var assigned = new HashSet<Material>();
            foreach (RendererBinding binding in bindings.Values)
                foreach (Material material in binding.Assigned) if (material != null) assigned.Add(material);
            var deadMaterials = new List<long>();
            foreach (var entry in materials)
                if (!assigned.Contains(entry.Value.Adapted) && (entry.Value.Source == null || entry.Value.LastSeen < scanGeneration - 2)) deadMaterials.Add(entry.Key);
            foreach (long id in deadMaterials)
            {
                if (materials[id].Adapted != null)
                {
                    adaptedMaterials.Remove(materials[id].Adapted.GetInstanceID());
                    Object.Destroy(materials[id].Adapted);
                }
                materials.Remove(id);
            }
            sceneScans++;
            if (diagnostics.Value && (sceneScans == 5 || sceneScans == 30))
            {
                Logger.LogInfo("Scene routing: " + renderers.Length + " renderers, " + graphics.Count + " UI graphics, " + materials.Count + " adapted materials, " + lighting.DirectionalCount + " game directional lights.");
                foreach (MaterialCopy copy in materials.Values)
                    Logger.LogInfo(copy.Mode + ": " + copy.Source.name + " [" + copy.Source.shader.name + "] -> " + copy.Adapted.shader.name + "; texture=" + (copy.Adapted.mainTexture == null ? "null" : copy.Adapted.mainTexture.name));
            }
        }

        private void EnqueueSkybox(Material sky, HashSet<int> seen)
        {
            if (sky == null) return;
            // Read scene/camera sky material slots without assuming one game's shader.
            foreach (string property in SkyTextureProperties)
                EnqueueSkyTexture(sky, property, seen);
            // Newer engines expose custom texture slots; old Unity players retain
            // the original sky shader and use the known slots above.
            if (GetTexturePropertyNames != null)
            {
                var properties = GetTexturePropertyNames.Invoke(sky, null) as string[];
                if (properties != null) foreach (string property in properties) EnqueueSkyTexture(sky, property, seen);
            }
        }

        private void EnqueueSkyTexture(Material sky, string property, HashSet<int> seen)
        {
            if (property.Length == 0 || !sky.HasProperty(property)) return;
            Texture texture = sky.GetTexture(property);
            if (texture != null && skyTextures.Count < Math.Max(1, materialLimit.Value) && seen.Add(texture.GetInstanceID())) skyTextures.Enqueue(texture);
        }

        private bool IsSky(Material material, string renderer)
        {
            return material != null && material.shader != null && (activeSkyMaterials.Contains(material) ||
                LegacyMaterialPolicy.IsSky(material.shader.name, material.name, renderer) ||
                string.Equals(material.GetTag("RenderType", false, ""), "Background", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsProceduralWater(Material material)
        {
            return LegacyMaterialPolicy.IsWater(material.shader.name, material.name) &&
                (material.shader.name.StartsWith("Ocean/", StringComparison.OrdinalIgnoreCase) || LegacyMaterialProperties.TextureProperty(material) == null);
        }

        private void UpdateGraphics()
        {
            var dead = new List<int>();
            foreach (var entry in graphics)
            {
                GraphicBinding binding = entry.Value;
                if (binding.Graphic == null) { dead.Add(entry.Key); continue; }
                if (!tagUi.Value) { dead.Add(entry.Key); continue; }
                // CanvasRenderer retains the game's shader, stencil/mask variants,
                // atlas binding and per-vertex tint. Native depth state separates UI.
            }
            foreach (int id in dead) graphics.Remove(id);
        }

        private Material Original(Material material)
        {
            MaterialCopy copy;
            return material != null && adaptedMaterials.TryGetValue(material.GetInstanceID(), out copy) && copy.Source != null ? copy.Source : material;
        }

        private MaterialCopy GetCopy(Material source, MaterialMode mode)
        {
            // A source shared between a Canvas and a world renderer needs two distinct passes.
            long id = (long)source.GetInstanceID() * 4 + (mode == MaterialMode.UI ? 1 : mode == MaterialMode.Sky ? 2 : mode == MaterialMode.Water ? 3 : 0);
            MaterialCopy copy;
            if (!materials.TryGetValue(id, out copy))
            {
                if (materials.Count >= Math.Max(1, materialLimit.Value)) return null;
                Material template = GetTemplate(mode);
                if (template == null) return null;
                copy = new MaterialCopy { Source = source, Mode = mode,
                    Adapted = new Material(source) { name = source.name + " (UnityRemix " + mode + ")", hideFlags = HideFlags.HideAndDontSave } };
                copy.Adapted.shader = template.shader;
                Synchronize(copy);
                materials.Add(id, copy);
                adaptedMaterials.Add(copy.Adapted.GetInstanceID(), copy);
            }
            if (copy.Mode != mode)
            {
                Material template = GetTemplate(mode);
                if (template != null) { copy.Adapted.shader = template.shader; copy.Mode = mode; copy.Synchronized = false; }
            }
            copy.Source = source;
            copy.LastSeen = scanGeneration;
            return copy;
        }

        private void Adapt(Renderer renderer)
        {
            if (!renderer.enabled || !renderer.gameObject.activeInHierarchy) return;
            Material[] current = renderer.sharedMaterials;
            RendererBinding binding;
            if (!bindings.TryGetValue(renderer.GetInstanceID(), out binding))
            {
                binding = new RendererBinding { Renderer = renderer, Originals = new Material[current.Length], Assigned = new Material[current.Length] };
                bindings.Add(renderer.GetInstanceID(), binding);
            }
            if (binding.Assigned.Length != current.Length)
            {
                Array.Resize(ref binding.Originals, current.Length);
                Array.Resize(ref binding.Assigned, current.Length);
            }
            bool changed = false;
            for (int index = 0; index < current.Length; index++)
            {
                Material source = current[index];
                if (source == null) { binding.Assigned[index] = null; binding.Originals[index] = null; continue; }
                if (source == binding.Assigned[index] && binding.Originals[index] != null) source = binding.Originals[index];
                source = Original(source);
                bool isUi = ui.IsText(renderer) || (source.shader != null && LegacyMaterialPolicy.IsUiShader(source.shader.name));
                bool isSky = !isUi && IsSky(source, renderer.name);
                bool isWater = !isUi && !isSky && source.shader != null && LegacyMaterialPolicy.IsWater(source.shader.name, source.name);
                if (source.shader == null || (isUi ? !tagUi.Value : !convertMaterials.Value || !LegacyMaterialPolicy.ShouldConvert(source.shader.name)) ||
                    ((isSky || isWater) && !bridge.SupportsCategories) || (isSky && !LegacyMaterialPolicy.CanConvertSky(source.shader.name)) || (isWater && IsProceduralWater(source)))
                {
                    if (current[index] == binding.Assigned[index] && binding.Originals[index] != null)
                    {
                        current[index] = binding.Originals[index];
                        changed = true;
                    }
                    binding.Assigned[index] = null;
                    binding.Originals[index] = null;
                    continue;
                }
                MaterialMode mode = isUi ? MaterialMode.UI : isSky ? MaterialMode.Sky : isWater ? MaterialMode.Water : LegacyMaterialPolicy.Classify(source.shader.name, source.GetTag("RenderType", false, ""), source.renderQueue);
                MaterialCopy copy = GetCopy(source, mode);
                if (copy == null)
                {
                    if (current[index] == binding.Assigned[index]) { current[index] = source; changed = true; }
                    binding.Assigned[index] = null;
                    binding.Originals[index] = null;
                    continue;
                }
                binding.Originals[index] = source;
                binding.Assigned[index] = copy.Adapted;
                if (current[index] != copy.Adapted)
                {
                    current[index] = copy.Adapted;
                    changed = true;
                }
            }
            if (changed) renderer.sharedMaterials = current;
        }

        private Material GetTemplate(MaterialMode mode)
        {
            Material template;
            if (templates.TryGetValue(mode, out template)) return template;
            template = new Material(LegacyMaterialPolicy.ShaderSource(mode)) { hideFlags = HideFlags.HideAndDontSave };
            if (template.shader == null || !template.shader.isSupported)
            {
                if (warnedShaders.Add(mode.ToString())) Logger.LogWarning("Legacy shader " + mode + " is unavailable; retaining original game materials.");
                Object.Destroy(template);
                templates.Add(mode, null);
                return null;
            }
            templates.Add(mode, template);
            return template;
        }

        private void Synchronize(MaterialCopy copy)
        {
            Material source = copy.Source, target = copy.Adapted;
            bool isUi = copy.Mode == MaterialMode.UI;
            long fingerprint = LegacyMaterialProperties.Fingerprint(source);
            if (!copy.Synchronized || copy.SourceFingerprint != fingerprint)
            {
                // A game may animate the assigned material directly. Do not reset it
                // every frame from an unchanged original asset.
                LegacyMaterialProperties.Copy(source, target, fallbackTexture, isUi);
                copy.SourceFingerprint = fingerprint;
                copy.Synchronized = true;
            }
            if (isUi)
            {
                target.SetTexture("_UnityRemixUiMarker", uiMarker);
                target.SetFloat("_ZTest", 8);
                target.renderQueue = Math.Max(4000, source.renderQueue);
            }
            else if (copy.Mode == MaterialMode.Sky)
            {
                target.SetTexture("_UnityRemixCategory", skyMarker);
                target.renderQueue = 1000;
            }
            else if (copy.Mode == MaterialMode.Water)
            {
                target.SetTexture("_UnityRemixCategory", waterMarker);
                target.SetFloat("_ZWrite", 0);
                target.renderQueue = Math.Max(3000, source.renderQueue);
            }
        }

        private void OnLevelWasLoaded(int level)
        {
            nextScan = 0;
            sceneScans = 0;
            taggedMarker = IntPtr.Zero;
            taggedSky = taggedWater = IntPtr.Zero;
            canvasTextures.Clear();
            skyTextures.Clear();
            proceduralWaterTextures.Clear();
            activeSkyMaterials.Clear();
        }

        private void RestoreCursor()
        {
            if (!menuHeld) return;
            Screen.lockCursor = savedLock;
            Screen.showCursor = savedCursor;
            menuHeld = false;
        }

        private void StopWithError(Exception error)
        {
            Logger.LogError("Legacy UnityRemix stopped: " + error.Message);
            Shutdown();
        }

        private void OnApplicationQuit() { Shutdown(); }
        private void OnDestroy() { Shutdown(); }
        private static void RestoreBinding(RendererBinding binding)
        {
            if (binding.Renderer == null) return;
            Material[] current = binding.Renderer.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < current.Length && i < binding.Assigned.Length; i++)
                if (current[i] == binding.Assigned[i] && binding.Originals[i] != null) { current[i] = binding.Originals[i]; changed = true; }
            if (changed) binding.Renderer.sharedMaterials = current;
        }

        private void Shutdown()
        {
            if (stopped) return;
            stopped = true;
            RestoreCursor();
            lighting.Shutdown();
            foreach (RendererBinding binding in bindings.Values) RestoreBinding(binding);
            foreach (MaterialCopy copy in materials.Values) if (copy.Adapted != null) Object.Destroy(copy.Adapted);
            foreach (Material template in templates.Values) if (template != null) Object.Destroy(template);
            materials.Clear();
            adaptedMaterials.Clear();
            bindings.Clear();
            templates.Clear();
            graphics.Clear();
            activeSkyMaterials.Clear();
            canvasTextures.Clear();
            skyTextures.Clear();
            proceduralWaterTextures.Clear();
            if (fallbackTexture != null) Object.Destroy(fallbackTexture);
            if (uiMarker != null) Object.Destroy(uiMarker);
            if (skyMarker != null) Object.Destroy(skyMarker);
            if (waterMarker != null) Object.Destroy(waterMarker);
        }
    }
}
