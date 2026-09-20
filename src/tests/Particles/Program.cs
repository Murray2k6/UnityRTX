using UnityEngine;
using UnityRemix;

var logger = new BepInEx.Logging.ManualLogSource();
var materials = new RemixMaterialManager();
var capture = new RemixParticleCapture(logger, materials);
var body = new Material(); var trailMaterial = new Material();
var particles = new ParticleSystemRenderer { sharedMaterials = new[] { body }, trailMaterial = trailMaterial };
var trail = new TrailRenderer { sharedMaterials = new[] { body } };
var line = new LineRenderer { sharedMaterials = new[] { body }, useWorldSpace = true };
var camera = new Camera();
RemixFrameCapture.FrameState Frame() { var frame = new RemixFrameCapture.FrameState(); capture.Capture(frame, camera, _ => true); return frame; }
void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
var first = Frame();
Check(first.skinned.Count == 4 && first.skinned.All(x => x.particle), "particle bodies, particle trails, standalone trails and lines are captured");
Check(first.skinned[0].materialId == body.GetInstanceID() && first.skinned[1].materialId == trailMaterial.GetInstanceID(), "trail material remains separate from particle material");
Check(first.skinned[0].uvs[0].x == .75f && first.skinned[0].colors[0].a == 37, "simulated atlas coordinates and opacity reach immutable frame snapshots");
Check(particles.LastCamera == camera && particles.LastTransform && trail.LastCamera == camera && line.LastCamera == camera, "the selected camera is passed to every bake");
Check(first.skinned[0].localToWorld.Kind == "translate" && first.skinned[2].localToWorld.Kind == "identity" && first.skinned[3].localToWorld.Kind == "identity", "baked scale/rotation and world-space trails are not transformed twice");
var second = Frame();
Check(first.skinned.Select(x=>x.remixMeshHash).SequenceEqual(second.skinned.Select(x=>x.remixMeshHash)) && Mesh.Created == 4, "repeated frames reuse mesh ownership and stable identities");
particles.Empty = true; trail.Empty = true; line.Empty = true;
Check(Frame().skinned.Count == 0, "expired particles and empty trails do not retain ghost geometry");
particles.Empty = false; particles.enabled = false;
Check(Frame().skinned.Count == 0, "disabled systems do not submit geometry");
particles.enabled = true; particles.gameObject.layer = 4; camera.cullingMask = ~(1 << 4);
Check(Frame().skinned.Count == 0, "camera masks apply to particle bodies and trails");
camera.cullingMask = -1; line.Empty = false; line.useWorldSpace = false;
Check(Frame().skinned.Last().localToWorld.Kind == "local", "local-space line transform is preserved");
particles.Throw = true;
Frame(); Frame();
Check(logger.Warnings.Count == 2, "failing particle body/trail APIs warn once and do not retry every frame");
UnityEngine.Object.World.Clear(); Time.unscaledTime = 2;
Check(Frame().skinned.Count == 0 && Mesh.Destroyed == 4, "scene removal releases all owned bake meshes");
capture.Cleanup(); capture.Cleanup();
Check(Mesh.Destroyed == 4, "cleanup is idempotent");
Check(UnityMaterialSemantics.Blend("",5,1) == UnityBlendMode.Additive && UnityMaterialSemantics.RemixBlend(UnityMaterialSemantics.Blend("",5,1)) == 1 &&
    UnityMaterialSemantics.RemixBlend(UnityMaterialSemantics.Blend("",1,1)) == 6 &&
    UnityMaterialSemantics.RemixBlend(UnityMaterialSemantics.Blend("",0,3)) == 7 &&
    UnityMaterialSemantics.RemixBlend(UnityMaterialSemantics.Blend("",2,3)) == 8 &&
    UnityMaterialSemantics.Blend("Particles/Alpha Blended Premultiply") == UnityBlendMode.Premultiplied,
    "additive alpha, pure emission, multiply and premultiplied blend modes remain distinct");

namespace BepInEx.Logging { public class ManualLogSource { public List<string> Warnings = new(); public void LogWarning(string s) => Warnings.Add(s); } }
namespace UnityRemix
{
    public static class UnityObjectCompat { public static T[] FindSceneObjects<T>() => UnityEngine.Object.World.OfType<T>().ToArray(); }
    public class RemixMaterialManager { public void CaptureMaterialTextures(Material m, int id) { } }
    public static class HashUtils { public static ulong HashStringFNV(string s) { ulong h=14695981039346656037; foreach(char c in s) h=unchecked((h^c)*1099511628211); return h; } }
    public class RemixFrameCapture
    {
        public struct SkinnedMeshData { public bool particle; public int meshId,materialId; public ulong remixMeshHash; public Vector3[] vertices,normals; public Vector2[] uvs; public Color32[] colors; public int[] triangles; public Matrix4x4 localToWorld; }
        public class FrameState { public List<SkinnedMeshData> skinned = new(); }
    }
}
namespace UnityEngine
{
    public class Object
    {
        public static List<Object> World = new(); private static int next; private int id=++next;
        public string name="effect"; public HideFlags hideFlags; public int GetInstanceID()=>id;
        public static void Destroy(Object o) { if(o is Mesh m && !m.Disposed) { m.Disposed=true; Mesh.Destroyed++; } }
    }
    public enum HideFlags { HideAndDontSave }
    public enum MeshTopology { Triangles }
    public record struct Vector3(float x,float y,float z);
    public record struct Vector2(float x,float y);
    public struct Color32 { public byte r,g,b,a; }
    public struct Matrix4x4 { public string Kind; public static Matrix4x4 identity => new(){Kind="identity"}; public static Matrix4x4 Translate(Vector3 p)=>new(){Kind="translate"}; }
    public class Transform { public Vector3 position; }
    public class GameObject { public bool activeInHierarchy=true; public int layer; }
    public class Material : Object { }
    public class Camera { public int cullingMask=-1; }
    public static class Time { public static float unscaledTime; }
    public class Mesh : Object
    {
        public UnityEngine.Rendering.IndexFormat indexFormat;
        public static int Created,Destroyed; public bool Disposed; public int vertexCount,subMeshCount;
        public Vector3[] vertices,normals; public Vector2[] uv; public Color32[] colors32;
        public Mesh() { Created++; }
        public void MarkDynamic() { }
        public void Clear() { vertexCount=subMeshCount=0; }
        public MeshTopology GetTopology(int i)=>MeshTopology.Triangles;
        public int[] GetTriangles(int i)=>new[]{0,1,2};
        public void Fill() { vertexCount=3; subMeshCount=1; vertices=new Vector3[3]; normals=new Vector3[3]; uv=new[]{new Vector2(.75f,.5f),new Vector2(),new Vector2()}; colors32=new[]{new Color32{r=210,a=37},new Color32(),new Color32()}; }
    }
    public class Renderer : Object
    {
        public bool enabled=true,Empty,Throw; public Camera LastCamera; public bool LastTransform;
        public GameObject gameObject=new(); public Transform transform=new(); public Material[] sharedMaterials=Array.Empty<Material>();
        public Matrix4x4 localToWorldMatrix=>new(){Kind="local"}; public Renderer() { World.Add(this); }
        public void Bake(Mesh mesh,Camera camera,bool useTransform) { LastCamera=camera; LastTransform=useTransform; if(Throw)throw new Exception("missing bake"); if(!Empty)mesh.Fill(); }
    }
    public class ParticleSystemRenderer : Renderer { public Material trailMaterial; public void BakeMesh(Mesh m,Camera c,bool t)=>Bake(m,c,t); public void BakeTrailsMesh(Mesh m,Camera c,bool t)=>Bake(m,c,t); }
    public class TrailRenderer : Renderer { public void BakeMesh(Mesh m,Camera c,bool t)=>Bake(m,c,t); }
    public class LineRenderer : Renderer { public bool useWorldSpace; public void BakeMesh(Mesh m,Camera c,bool t)=>Bake(m,c,t); }
}
namespace UnityEngine.Rendering { public enum IndexFormat { UInt16, UInt32 } }
