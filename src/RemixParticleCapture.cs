using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace UnityRemix
{
    // Unity owns simulation, modules, sub-emitters and sorting. Only snapshots cross
    // to the render worker; no Unity objects or engine calls are used there.
    internal sealed class RemixParticleCapture
    {
        private sealed class Entry
        {
            public Renderer Renderer;
            public Mesh Mesh;
            public bool Trails;
            public bool Failed;
        }

        private readonly ManualLogSource logger;
        private readonly RemixMaterialManager materials;
        private readonly Dictionary<long, Entry> entries = new Dictionary<long, Entry>();
        private float nextScan;

        public RemixParticleCapture(ManualLogSource logger, RemixMaterialManager materials)
        {
            this.logger = logger;
            this.materials = materials;
        }

        private void Track(Renderer renderer, bool trails, HashSet<long> live)
        {
            long key = ((long)renderer.GetInstanceID() << 1) | (trails ? 1L : 0L);
            live.Add(key);
            if (!entries.ContainsKey(key))
                entries.Add(key, new Entry { Renderer = renderer, Trails = trails });
        }

        private void Scan()
        {
            var live = new HashSet<long>();
#if UNITY_PARTICLES
            foreach (var renderer in UnityObjectCompat.FindSceneObjects<ParticleSystemRenderer>())
            {
                Track(renderer, false, live);
                Track(renderer, true, live);
            }
#endif
            foreach (var renderer in UnityObjectCompat.FindSceneObjects<TrailRenderer>()) Track(renderer, false, live);
            foreach (var renderer in UnityObjectCompat.FindSceneObjects<LineRenderer>()) Track(renderer, false, live);
            var expired = new List<long>();
            foreach (var pair in entries)
                if (!live.Contains(pair.Key))
                {
                    if (pair.Value.Mesh != null) UnityEngine.Object.Destroy(pair.Value.Mesh);
                    expired.Add(pair.Key);
                }
            foreach (long key in expired) entries.Remove(key);
            nextScan = Time.unscaledTime + 1f;
        }

        public void Capture(RemixFrameCapture.FrameState state, Camera camera, Func<Renderer, bool> visible)
        {
            if (camera == null) return;
            if (Time.unscaledTime >= nextScan) Scan();
            foreach (var pair in entries)
            {
                Entry entry = pair.Value;
                Renderer renderer = entry.Renderer;
                if (renderer == null || entry.Failed || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                    (camera.cullingMask & (1 << renderer.gameObject.layer)) == 0 || !visible(renderer)) continue;
                try
                {
                    if (entry.Mesh == null)
                    {
                        entry.Mesh = new Mesh {
                            name = "UnityRemix particles", hideFlags = HideFlags.HideAndDontSave,
                            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
                        };
                        entry.Mesh.MarkDynamic();
                    }
                    // Empty/dead systems must not reuse the last live mesh.
                    entry.Mesh.Clear();
                    Material[] sourceMaterials = renderer.sharedMaterials;
                    Matrix4x4 transform = renderer.localToWorldMatrix;
#if UNITY_PARTICLES
#if BEPINEX6_IL2CPP
                    var particles = renderer.TryCast<ParticleSystemRenderer>();
                    var trail = renderer.TryCast<TrailRenderer>();
                    var line = renderer.TryCast<LineRenderer>();
#else
                    var particles = renderer as ParticleSystemRenderer;
                    var trail = renderer as TrailRenderer;
                    var line = renderer as LineRenderer;
#endif
                    if (particles != null)
                    {
                        // Including rotation/scale leaves only translation to apply.
                        if (entry.Trails)
                        {
                            if (particles.trailMaterial == null) continue;
                            particles.BakeTrailsMesh(entry.Mesh, camera, true);
                            sourceMaterials = new[] { particles.trailMaterial };
                        }
                        else particles.BakeMesh(entry.Mesh, camera, true);
                        transform = Matrix4x4.Translate(renderer.transform.position);
                    }
                    else
#else
#if BEPINEX6_IL2CPP
                    var trail = renderer.TryCast<TrailRenderer>();
                    var line = renderer.TryCast<LineRenderer>();
#else
                    var trail = renderer as TrailRenderer;
                    var line = renderer as LineRenderer;
#endif
#endif
                    if (trail != null)
                    {
                        trail.BakeMesh(entry.Mesh, camera, false);
                        transform = Matrix4x4.identity;
                    }
                    else if (line != null)
                    {
                        line.BakeMesh(entry.Mesh, camera, false);
                        transform = line.useWorldSpace ? Matrix4x4.identity : renderer.localToWorldMatrix;
                    }
                    else continue;

                    if (entry.Mesh.vertexCount == 0) continue;
                    Vector3[] vertices = entry.Mesh.vertices;
                    Vector3[] normals = entry.Mesh.normals;
                    Vector2[] uvs = entry.Mesh.uv;
                    Color32[] colors = entry.Mesh.colors32;
                    for (int submesh = 0; submesh < entry.Mesh.subMeshCount; ++submesh)
                    {
                        if (entry.Mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
                        int[] triangles = entry.Mesh.GetTriangles(submesh);
                        if (triangles.Length == 0) continue;
                        Material material = sourceMaterials.Length == 0 ? null : sourceMaterials[Math.Min(submesh, sourceMaterials.Length - 1)];
                        if (material == null) continue;
                        int materialId = material.GetInstanceID();
                        materials.CaptureMaterialTextures(material, materialId);
                        // Renderer/stream/submesh identity is stable while topology and
                        // particle count change. Native buffers replace, never accumulate.
                        ulong hash = HashUtils.HashStringFNV("UnityRemix/particles/" + pair.Key + "/" + submesh);
                        state.skinned.Add(new RemixFrameCapture.SkinnedMeshData {
                            particle = true, meshId = renderer.GetInstanceID(), remixMeshHash = hash,
                            materialId = materialId, vertices = vertices, normals = normals,
                            uvs = uvs, colors = colors, triangles = triangles, localToWorld = transform
                        });
                    }
                }
                catch (Exception error)
                {
                    entry.Failed = true;
                    logger.LogWarning("Particle capture unavailable for '" + renderer.name + "': " + error.Message);
                }
            }
        }

        public void Cleanup()
        {
            foreach (var entry in entries.Values)
                if (entry.Mesh != null) UnityEngine.Object.Destroy(entry.Mesh);
            entries.Clear();
            nextScan = 0;
        }
    }
}
