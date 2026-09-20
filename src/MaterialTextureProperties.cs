using UnityEngine;
using UnityEngine.Rendering;

namespace UnityRemix
{
    internal static class MaterialTextureProperties
    {
        private static readonly string[] AlbedoNames = UnityMaterialSemantics.AlbedoNames;

        // mainTexture falls back to _MainTex even when it is absent, which logs
        // an engine error on every call. Resolve the shader's actual property.
        public static string FindAlbedo(Material material)
        {
            if (material == null) return null;
            var shader = material.shader;
            string unassigned = null;
            if (shader != null)
            {
                for (int i = 0; i < shader.GetPropertyCount(); i++)
                    if (shader.GetPropertyType(i) == ShaderPropertyType.Texture &&
                        (shader.GetPropertyFlags(i) & ShaderPropertyFlags.MainTexture) != 0)
                    {
                        string name = shader.GetPropertyName(i);
                        if (material.GetTexture(name) != null) return name;
                        if (unassigned == null) unassigned = name;
                    }
            }
            foreach (var name in AlbedoNames)
            {
                if (!HasTypedProperty(material, name, ShaderPropertyType.Texture)) continue;
                if (material.GetTexture(name) != null) return name;
                if (unassigned == null) unassigned = name;
            }
            return unassigned;
        }

        public static string FindNormal(Material material)
        {
            if (material == null) return null;
            foreach (string name in UnityMaterialSemantics.NormalNames)
                if (HasTypedProperty(material, name, ShaderPropertyType.Texture) && material.GetTexture(name) != null)
                    return name;
            return null;
        }

        private static bool HasTypedProperty(Material material, string name, ShaderPropertyType type)
        {
            if (!material.HasProperty(name)) return false;
            var shader = material.shader;
            if (shader == null) return false;
            for (int i = 0; i < shader.GetPropertyCount(); ++i)
                if (shader.GetPropertyName(i) == name)
                    return shader.GetPropertyType(i) == type;
            return false;
        }

        public static string FindColor(Material material)
        {
            if (material == null) return null;
            var shader = material.shader;
            if (shader != null)
                for (int i = 0; i < shader.GetPropertyCount(); ++i)
                    if (shader.GetPropertyType(i) == ShaderPropertyType.Color &&
                        (shader.GetPropertyFlags(i) & ShaderPropertyFlags.MainColor) != 0)
                        return shader.GetPropertyName(i);
            foreach (string name in UnityMaterialSemantics.ColorNames)
                if (HasTypedProperty(material, name, ShaderPropertyType.Color)) return name;
            return null;
        }
    }
}
