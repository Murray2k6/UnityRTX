// A minimal engine model for material ownership tests, not a ShaderLab compiler.
namespace UnityEngine
{
    public class Object
    {
        private static int nextId;
        private readonly int id = ++nextId;
        public static readonly List<Object> World = new();
        public string name = "";
        public bool Destroyed;
        public HideFlags hideFlags;
        public Object() { World.Add(this); }
        public int GetInstanceID() => id;
        public static void Destroy(Object value) { if (value is not null) value.Destroyed = true; }
        public static void DontDestroyOnLoad(Object value) { }
        public static T[] FindObjectsOfType<T>() where T : Object => World.OfType<T>().Where(x => !x.Destroyed).ToArray();
        public static Object[] FindObjectsOfType(Type type) => World.Where(x => type.IsInstanceOfType(x) && !x.Destroyed).ToArray();
        public static bool operator ==(Object a, Object b) => ReferenceEquals(a, b) || ((a is null || a.Destroyed) && (b is null || b.Destroyed));
        public static bool operator !=(Object a, Object b) => !(a == b);
        public override bool Equals(object other) => ReferenceEquals(this, other);
        public override int GetHashCode() => id;
    }
    public enum HideFlags { HideAndDontSave }
    public enum RuntimePlatform { WindowsPlayer }
    public record struct Color(float R, float G, float B, float A) { public static Color white => new(1,1,1,1); }
    public record struct Vector2(float X, float Y);
    public class GameObject : Object
    {
        public bool activeInHierarchy = true;
        public GameObject() { }
        public GameObject(string n) { name = n; }
        public T AddComponent<T>() where T : Component, new() { return new T { gameObject = this }; }
    }
    public class Component : Object
    {
        public GameObject gameObject = new();
        public List<Component> Components = new();
        public T GetComponent<T>() where T : Component => Components.OfType<T>().FirstOrDefault();
        public T[] GetComponentsInParent<T>(bool includeInactive) where T : Component => Components.OfType<T>().ToArray();
    }
    public class MonoBehaviour : Component { }
    public class TextMesh : Component { }
    public enum LightType { Directional, Point }
    public enum LightRenderMode { ForceVertex }
    public class Transform { public Quaternion rotation; }
    public struct Quaternion { public static Quaternion Euler(float x, float y, float z) => new(); }
    public class Light : Component { public bool enabled = true; public LightType type; public LightRenderMode renderMode; public float intensity = 1; public Color color = Color.white; public Transform transform = new(); }
    public class Skybox : Component { public bool enabled = true; public Material material; }
    public static class RenderSettings { public static Material skybox; }
    public static class Mathf { public static float Max(float a, float b) => Math.Max(a,b); }
    public class Shader : Object { public bool isSupported = true; }
    public class Texture : Object { public int width = 32, height = 32; public IntPtr GetNativeTexturePtr() => (IntPtr)GetInstanceID(); }
    public class Cubemap : Texture { }
    public enum TextureFormat { RGBA32 }
    public class Texture2D : Texture
    {
        public Texture2D() { }
        public Texture2D(int w, int h, TextureFormat format, bool mipmaps) { width = w; height = h; }
        public Color[] Pixels;
        public void SetPixels(Color[] pixels) { Pixels = pixels; }
        public void Apply(bool update, bool unreadable) { }
    }
    public class Material : Object
    {
        public Shader shader;
        public Texture mainTexture;
        public Color color = Color.white;
        public Vector2 mainTextureScale = new(1,1), mainTextureOffset;
        public int renderQueue = 2000;
        public string RenderType = "Opaque";
        public HashSet<string> Properties = new() { "_MainTex", "_Color", "_Cutoff" };
        private Dictionary<string,float> floats = new() { ["_Cutoff"] = .5f };
        private Dictionary<string,Texture> textures = new();
        private Dictionary<string,Color> colors = new();
        private Dictionary<string,Vector2> scales = new(), offsets = new();
        public Material(string source) { shader = new Shader { name = System.Text.RegularExpressions.Regex.Match(source, "Shader \\\"([^\\\"]+)").Groups[1].Value }; }
        public Material(Material source) { shader = source.shader; mainTexture = source.mainTexture; color = source.color; Properties = new(source.Properties); floats = new(source.floats); textures = new(source.textures); colors = new(source.colors); scales = new(source.scales); offsets = new(source.offsets); }
        public bool HasProperty(string key) => Properties.Contains(key);
        public float GetFloat(string key) => floats.GetValueOrDefault(key);
        public void SetFloat(string key, float value) { floats[key] = value; }
        public Texture GetTexture(string key) => key == "_MainTex" ? mainTexture : textures.GetValueOrDefault(key);
        public void SetTexture(string key, Texture value) { if (key == "_MainTex") mainTexture = value; else textures[key] = value; }
        public Color GetColor(string key) => key == "_Color" ? color : colors.GetValueOrDefault(key, Color.white);
        public void SetColor(string key, Color value) { if (key == "_Color") color = value; else colors[key] = value; }
        public Vector2 GetTextureScale(string key) => key == "_MainTex" ? mainTextureScale : scales.GetValueOrDefault(key, new(1,1));
        public Vector2 GetTextureOffset(string key) => key == "_MainTex" ? mainTextureOffset : offsets.GetValueOrDefault(key);
        public void SetTextureScale(string key, Vector2 value) { scales[key] = value; }
        public void SetTextureOffset(string key, Vector2 value) { offsets[key] = value; }
        public string GetTag(string key, bool fallback, string defaultValue) => RenderType;
    }
    public class Renderer : Component
    {
        public bool enabled = true;
        private Material[] values = Array.Empty<Material>();
        public Material[] sharedMaterials { get => values.ToArray(); set => values = value.ToArray(); }
    }
    public class GUITexture : Object { public Texture texture; }
    public class Font : Object { public Material material; }
    public static class Resources { public static Object[] FindObjectsOfTypeAll(Type type) => Object.World.Where(x => type.IsInstanceOfType(x) && !x.Destroyed).ToArray(); }
    public static class Screen { public static bool lockCursor, showCursor; }
    public static class Time { public static float realtimeSinceStartup; }
    public static class Application { public static string unityVersion = "4.6.6"; public static RuntimePlatform platform = RuntimePlatform.WindowsPlayer; }
}
namespace UnityEngine.UI { public class Graphic : UnityEngine.Component { public UnityEngine.Texture mainTexture { get; set; } public UnityEngine.Material material { get; set; } } }
namespace BepInEx.Configuration
{
    public class ConfigEntry<T> { public T Value; public ConfigEntry(T value) { Value = value; } }
    public class ConfigFile
    {
        public readonly Dictionary<string,object> Entries = new();
        public ConfigEntry<T> Bind<T>(string section, string key, T value, string description) { var entry = new ConfigEntry<T>(value); Entries[key] = entry; return entry; }
    }
}
namespace BepInEx
{
    [AttributeUsage(AttributeTargets.Class)] public class BepInPlugin : Attribute { public BepInPlugin(string id, string name, string version) { } }
    public class Log { public readonly List<object> Errors = new(); public void LogInfo(object value) { } public void LogWarning(object value) { } public void LogError(object value) { Errors.Add(value); } }
    public class BaseUnityPlugin : UnityEngine.MonoBehaviour { public Configuration.ConfigFile Config = new(); public Log Logger = new(); }
}
namespace UnityRemix.Legacy
{
    internal class LegacyBridge
    {
        public static bool Open;
        public static readonly List<IntPtr> Tagged = new();
        public static readonly Dictionary<uint,List<IntPtr>> Categories = new();
        public static readonly Dictionary<IntPtr,(UnityEngine.Color Color,float Ior)> WaterMaterials = new();
        public bool MenuOpen => Open;
        public bool SupportsCategories => true;
        public bool RegisterWater(IntPtr texture, UnityEngine.Color color, float ior) { WaterMaterials[texture] = (color,ior); return TagCategory(texture, 4); }
        public bool QueryRenderer(out ulong version) { version = 1000UL << 16; return true; }
        public bool TagUiTexture(IntPtr texture) { Tagged.Add(texture); return true; }
        public bool TagCategory(IntPtr texture, uint category) { if(!Categories.ContainsKey(category)) Categories[category] = new(); Categories[category].Add(texture); return true; }
    }
}
