using System;
using UnityEngine;

namespace UnityRemix.Legacy
{
    internal static class LegacyMaterialProperties
    {
        private static readonly string[] TextureNames = UnityMaterialSemantics.AlbedoNames;
        private static readonly string[] ColorNames = UnityMaterialSemantics.ColorNames;
        private static readonly string[] StencilNames = { "_Stencil", "_StencilComp", "_StencilOp", "_StencilReadMask", "_StencilWriteMask", "_ColorMask" };
        private static readonly string[] StateNames = { "_Cutoff", "_Cull", "_Ref", "_Stencil", "_StencilComp", "_StencilOp", "_StencilReadMask", "_StencilWriteMask", "_ColorMask" };

        public static long Fingerprint(Material source)
        {
            unchecked
            {
                long value = source.GetInstanceID();
                value = value * 1099511628211L ^ (source.shader == null ? 0 : source.shader.GetInstanceID());
                value = value * 1099511628211L ^ source.renderQueue;
                string texture = TextureProperty(source);
                if (texture != null)
                {
                    Texture asset = source.GetTexture(texture);
                    value = value * 1099511628211L ^ (asset == null ? 0 : asset.GetInstanceID());
                    value = value * 1099511628211L ^ source.GetTextureScale(texture).GetHashCode();
                    value = value * 1099511628211L ^ source.GetTextureOffset(texture).GetHashCode();
                }
                foreach (string name in ColorNames) if (source.HasProperty(name)) value = value * 1099511628211L ^ source.GetColor(name).GetHashCode();
                foreach (string name in StateNames) if (source.HasProperty(name)) value = value * 1099511628211L ^ source.GetFloat(name).GetHashCode();
                return value;
            }
        }

        public static string TextureProperty(Material source)
        {
            foreach (string name in TextureNames) if (source.HasProperty(name) && source.GetTexture(name) != null) return name;
            foreach (string name in TextureNames) if (source.HasProperty(name)) return name;
            return null;
        }

        public static void Copy(Material source, Material target, Texture fallback, bool ui)
        {
            string texture = TextureProperty(source);
            target.mainTexture = texture == null ? fallback : source.GetTexture(texture) ?? fallback;
            target.mainTextureScale = texture == null ? new Vector2(1, 1) : source.GetTextureScale(texture);
            target.mainTextureOffset = texture == null ? new Vector2(0, 0) : source.GetTextureOffset(texture);
            target.color = Color.white;
            foreach (string name in ColorNames) if (source.HasProperty(name)) { target.color = source.GetColor(name); break; }
            if (!ui) target.SetFloat("_Cutoff", source.HasProperty("_Cutoff") ? source.GetFloat("_Cutoff") : 0.5f);
            string shader = source.shader == null ? "" : source.shader.name;
            target.SetFloat("_Stencil", 0);
            target.SetFloat("_StencilComp", 8);
            target.SetFloat("_StencilOp", 0);
            target.SetFloat("_StencilReadMask", 255);
            target.SetFloat("_StencilWriteMask", 255);
            target.SetFloat("_ColorMask", 15);
            foreach (string name in StencilNames) if (source.HasProperty(name)) target.SetFloat(name, source.GetFloat(name));
            if (ui) return;
            target.SetFloat("_Cull", source.HasProperty("_Cull") ? source.GetFloat("_Cull") : CullMode(shader));
            MaterialMode mode = LegacyMaterialPolicy.Classify(shader, source.GetTag("RenderType", false, ""), source.renderQueue);
            target.SetFloat("_ZWrite", mode == MaterialMode.Opaque || mode == MaterialMode.Cutout || mode == MaterialMode.Discard ? 1 : 0);
            target.SetFloat("_ZTest", shader == "Custom/Alpha Blended No Cull" ? 8 : 4);
            if (shader.StartsWith("Custom/NoLight", StringComparison.Ordinal))
            {
                if (source.HasProperty("_Ref")) target.SetFloat("_Stencil", source.GetFloat("_Ref"));
                if (shader == "Custom/NoLightMap") target.SetFloat("_StencilComp", 3);
                if (shader == "Custom/NoLightMapStencilContent" || shader == "Custom/NoLightII" || shader == "Custom/NoLightIIII") target.SetFloat("_StencilOp", 2);
                if (shader == "Custom/NoLightIII") { target.SetFloat("_StencilComp", 4); target.SetFloat("_StencilOp", 2); }
                if (shader == "Custom/NoLightStencilContentNo") { target.SetFloat("_Stencil", 1); target.SetFloat("_StencilComp", 3); }
                if (shader == "Custom/NoLightStencilMask") { target.SetFloat("_StencilComp", 3); target.SetFloat("_StencilOp", 3); target.SetFloat("_ZWrite", 0); }
            }
            target.renderQueue = source.renderQueue;
        }

        public static Color WaterColor(Material source)
        {
            foreach (string name in ColorNames) if (source.HasProperty(name)) return source.GetColor(name);
            return new Color(0.1f, 0.35f, 0.45f, 1);
        }

        public static int CullMode(string shader)
        {
            if (shader == "FX/Diamond") return 1;
            if (shader.IndexOf("Particles/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shader.IndexOf("No Cull", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shader.IndexOf("Double", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shader == "Unlit Transparent Vertex Colored" || shader == "Transparent/Cutout/VertexLit" ||
                shader.StartsWith("Herotwin/ScreenGradient", StringComparison.Ordinal)) return 0;
            return 2;
        }
    }
}
