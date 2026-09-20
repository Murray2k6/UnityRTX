using System.Collections.Generic;
using UnityEngine;

namespace UnityRemix
{
    internal static class UnityObjectCompat
    {
        public static T ReadTextureViaGpu<T>(Texture2D source, System.Func<Texture2D, T> read, int maximum = 0)
        {
            RenderTexture temporary = null;
            Texture2D readable = null;
            var previous = RenderTexture.active;
            try
            {
                var size = TextureCaptureSize.Limit(source.width, source.height, maximum);
                temporary = RenderTexture.GetTemporary(size.width, size.height, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                readable = new Texture2D(size.width, size.height, TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, size.width, size.height), 0, 0, false);
                // ReadPixels already fills CPU storage. Apply would upload a
                // second GPU copy that is immediately thrown away.
                return read(readable);
            }
            finally
            {
                RenderTexture.active = previous;
                if (temporary != null) RenderTexture.ReleaseTemporary(temporary);
                if (readable != null) Object.Destroy(readable);
            }
        }

        public static byte[] ReadTextureBytes(Texture2D texture)
        {
#if BEPINEX6_IL2CPP
            var nativeData = texture.GetRawTextureData();
            var data = nativeData.AsSpan().ToArray();
            System.GC.KeepAlive(nativeData);
            return data;
#else
            return texture.GetRawTextureData();
#endif
        }

        public static Color32[] ReadTexturePixels(Texture2D texture)
        {
#if BEPINEX6_IL2CPP
            // Il2CppArrayBase's implicit conversion invokes the native handle
            // lookup for every element. Copy contiguous pixel data in one pass.
            var nativePixels = texture.GetPixels32();
            var pixels = nativePixels.AsSpan().ToArray();
            System.GC.KeepAlive(nativePixels);
            return pixels;
#else
            return texture.GetPixels32();
#endif
        }

        public static Texture2D AsTexture2D(Texture texture)
        {
#if BEPINEX6_IL2CPP
            // A managed Texture wrapper can represent a native Texture2D.
            return texture == null ? null : texture.TryCast<Texture2D>();
#else
            return texture as Texture2D;
#endif
        }

        // The includeInactive overload of FindObjectsOfType is absent in Unity 2019.
        // Exclude prefab assets and unloaded scenes from Resources' broader result.
        public static T[] FindSceneObjects<T>() where T : Component
        {
            var result = new List<T>();
            foreach (var component in Resources.FindObjectsOfTypeAll<T>())
            {
                if (component != null && component.gameObject.scene.IsValid() && component.gameObject.scene.isLoaded)
                    result.Add(component);
            }
            return result.ToArray();
        }
    }
}
