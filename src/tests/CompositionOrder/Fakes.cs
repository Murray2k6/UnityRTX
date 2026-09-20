namespace UnityEngine
{
    public class Object
    {
        public static readonly List<Object> All = new();
        private static int nextId;
        private readonly int id = ++nextId;
        public Object() { All.Add(this); }
        public int GetInstanceID() => id;
        public static T[] FindObjectsOfType<T>() where T : Object => All.OfType<T>().ToArray();
    }
    public class GameObject { public bool activeInHierarchy = true; public int layer; }
    public class Camera : Object { public int cullingMask = -1; }
    public class Renderer : Object
    {
        public bool enabled = true;
        public GameObject gameObject = new();
        public int sortingLayerID, sortingOrder;
    }
    public enum RenderMode { WorldSpace, ScreenSpaceCamera, ScreenSpaceOverlay }
    public class Canvas : Object
    {
        public bool enabled = true, isRootCanvas = true, overrideSorting;
        public int sortingLayerID, sortingOrder;
        public RenderMode renderMode;
    }
    public static class Time { public static float unscaledTime; }
    public class SortingLayer
    {
        public int id, value;
        public static SortingLayer[] layers = { new() { id = 0, value = 0 }, new() { id = 9, value = 100 } };
        public static int GetLayerValueFromID(int id) => layers.First(x => x.id == id).value;
    }
}
namespace UnityEngine.UI { }
