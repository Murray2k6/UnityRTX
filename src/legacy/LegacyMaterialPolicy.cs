using System;

namespace UnityRemix.Legacy
{
    internal enum MaterialMode { Opaque, Cutout, Transparent, Additive, Premultiplied, Multiply, Discard, UI, Sky, Water }

    internal static class LegacyMaterialPolicy
    {
        public const string ShaderPrefix = "UnityRemix/Legacy/";
        public static bool IsSky(string shader, string material, string renderer)
        {
            return (!string.IsNullOrEmpty(shader) && (shader.IndexOf("Skybox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shader.StartsWith("Sky/", StringComparison.OrdinalIgnoreCase) || shader.IndexOf("Skydome", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                IsSkyName(material) || IsSkyName(renderer);
        }
        private static bool IsSkyName(string name)
        {
            name = (name ?? "").Replace(" (Instance)", "").ToLowerInvariant();
            return name == "sky" || name == "skydome" || name == "nightsky" || name == "hotelsky" || name == "skybox" || name == "sun" || name == "moon";
        }
        public static bool IsWater(string shader, string material)
        {
            return UnityMaterialSemantics.IsWater(shader, material);
        }
        public static bool CanConvertSky(string shader)
        {
            return shader == "Shader Forge/VertexColor" || shader == "Unlit/Texture" || shader == "Unlit/Color" || shader == "Unlit/Transparent";
        }
        public static bool IsUiShader(string shader)
        {
            return !string.IsNullOrEmpty(shader) && (shader.StartsWith("UI/", StringComparison.OrdinalIgnoreCase) ||
                shader.StartsWith("GUI/", StringComparison.OrdinalIgnoreCase) ||
                shader.IndexOf("/UI/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                shader.StartsWith("ArcaneText/", StringComparison.OrdinalIgnoreCase));
        }
        public static bool ShouldConvert(string shader)
        {
            if (string.IsNullOrEmpty(shader)) return false;
            return !shader.StartsWith(ShaderPrefix, StringComparison.Ordinal) &&
                !UnityMaterialSemantics.IsVolumeProxy(shader) &&
                !IsUiShader(shader) &&
                !shader.StartsWith("Hidden/", StringComparison.OrdinalIgnoreCase) &&
                shader.IndexOf("Skybox", StringComparison.OrdinalIgnoreCase) < 0;
        }

        public static MaterialMode Classify(string shader, string renderType, int queue)
        {
            string name = (shader ?? "").ToLowerInvariant();
            if (name == "custom/discard") return MaterialMode.Discard;
            if (name.Contains("premultiply") || name.Contains("premultiplied")) return MaterialMode.Premultiplied;
            if (name.Contains("particles/multiply")) return MaterialMode.Multiply;
            if (name.Contains("additive") || name == "custom/unlittransparent" || name == "fx/diamond") return MaterialMode.Additive;
            if (name.Contains("cutout") || string.Equals(renderType, "TransparentCutout", StringComparison.OrdinalIgnoreCase)) return MaterialMode.Cutout;
            if (name.StartsWith("nature/tree creator leaves")) return MaterialMode.Cutout;
            if (name.Contains("transparent") || name.Contains("particles/") || name == "herotwin/solid color alpha" || queue >= 3000 ||
                string.Equals(renderType, "Transparent", StringComparison.OrdinalIgnoreCase)) return MaterialMode.Transparent;
            return MaterialMode.Opaque;
        }

        public static string ShaderSource(MaterialMode mode)
        {
            if (mode == MaterialMode.UI) return UiShaderSource();
            if (mode == MaterialMode.Sky) return "Shader \"" + ShaderPrefix + "Sky\" { Properties { _MainTex (\"Sky\", 2D) = \"white\" {} _UnityRemixCategory (\"Draw category\", 2D) = \"white\" {} _Color (\"Tint\", Color) = (1,1,1,1) } " +
                "SubShader { Tags { \"Queue\"=\"Background\" \"RenderType\"=\"Background\" } Pass { " + VertexChannels + " Cull Off ZWrite Off Lighting On Material { Emission [_Color] } ColorMaterial Emission Fog { Mode Off } " +
                "SetTexture [_MainTex] { combine texture * primary } SetTexture [_MainTex] { constantColor [_Color] combine previous * constant } " +
                "SetTexture [_UnityRemixCategory] { combine previous * texture, previous } } } }";
            bool transparent = mode == MaterialMode.Transparent || mode == MaterialMode.Additive || mode == MaterialMode.Premultiplied || mode == MaterialMode.Multiply || mode == MaterialMode.Water;
            string renderType = transparent ? "Transparent" : mode == MaterialMode.Cutout ? "TransparentCutout" : "Opaque";
            string queue = transparent ? "Transparent" : mode == MaterialMode.Cutout ? "AlphaTest" : "Geometry";
            string blend = mode == MaterialMode.Additive ? "Blend SrcAlpha One" : mode == MaterialMode.Premultiplied ? "Blend One OneMinusSrcAlpha" :
                mode == MaterialMode.Multiply ? "Blend Zero SrcColor" : transparent ? "Blend SrcAlpha OneMinusSrcAlpha" : "Blend Off";
            string alpha = mode == MaterialMode.Cutout ? "AlphaTest Greater [_Cutoff]" : "AlphaTest Off";
            // Unity 4's player supports runtime ShaderLab fixed-function passes.
            // D3D9 then supplies actual world/view transforms, vertices and textures
            // to Remix, including Unity's animated meshes and texture updates.
            string properties = "Shader \"" + ShaderPrefix + mode + "\" { Properties { " +
                "_MainTex (\"Texture\", 2D) = \"white\" {} _Color (\"Color\", Color) = (1,1,1,1) " +
                "_UnityRemixCategory (\"Draw category\", 2D) = \"white\" {} _Cutoff (\"Cutoff\", Range(0,1)) = 0.5 _Cull (\"Cull\", Float) = 2 _ZWrite (\"Depth write\", Float) = " + (transparent ? "0" : "1") + " " +
                "_ZTest (\"Depth test\", Float) = 4 " + StencilProperties + " } SubShader { Tags { \"RenderType\"=\"" + renderType + "\" \"Queue\"=\"" + queue + "\" } ";
            string pass = "Pass { " + (!transparent ? "Tags { \"LightMode\"=\"Vertex\" } " : "") + VertexChannels + "Cull [_Cull] ZWrite [_ZWrite] ZTest [_ZTest] " + (mode == MaterialMode.Discard ? "ColorMask 0 " : "") + blend + " " + alpha + StencilState +
                (transparent ? " Lighting Off Color (1,1,1,1) " : " Lighting On Material { Diffuse [_Color] Ambient [_Color] } ColorMaterial AmbientAndDiffuse ") +
                "Fog { Mode Off } SetTexture [_MainTex] { constantColor [_Color] combine texture * " +
                "primary, texture * " + (transparent ? "primary" : "constant") + " } " +
                (transparent ? "SetTexture [_MainTex] { constantColor [_Color] combine previous * constant, previous * constant } " : "") +
                (mode == MaterialMode.Water ? "SetTexture [_UnityRemixCategory] { combine previous * texture, previous } " : "") + "} ";
            return properties + pass + (transparent ? "" : pass.Replace("\"Vertex\"", "\"VertexLM\"") + pass.Replace("\"Vertex\"", "\"VertexLMRGBM\"")) + "} }";
        }

        private const string StencilProperties = "_Stencil (\"Stencil\", Float) = 0 _StencilComp (\"Stencil comparison\", Float) = 8 " +
            "_StencilOp (\"Stencil operation\", Float) = 0 _StencilReadMask (\"Stencil read mask\", Float) = 255 _StencilWriteMask (\"Stencil write mask\", Float) = 255 _ColorMask (\"Color mask\", Float) = 15";
        private const string StencilState = " Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] } ";
        private const string VertexChannels = " BindChannels { Bind \"vertex\", Vertex Bind \"normal\", Normal Bind \"color\", Color Bind \"texcoord\", TexCoord } ";

        private static string UiShaderSource()
        {
            // The last stage multiplies RGB by white and leaves alpha intact.
            // Only UI draws bind this distinct texture; shared atlases stay untagged.
            return "Shader \"" + ShaderPrefix + "UI\" { Properties { _MainTex (\"Texture\", 2D) = \"white\" {} " +
                "_Color (\"Color\", Color) = (1,1,1,1) _UnityRemixUiMarker (\"UI marker\", 2D) = \"white\" {} " +
                "_ZTest (\"Depth test\", Float) = 8 " + StencilProperties + " } SubShader { Tags { \"Queue\"=\"Overlay\" \"RenderType\"=\"Transparent\" } " +
                "Pass { " + VertexChannels + "Color [_Color] Cull Off ZWrite Off ZTest [_ZTest] ColorMask [_ColorMask] Blend SrcAlpha OneMinusSrcAlpha Lighting Off " + StencilState +
                "Fog { Mode Off } SetTexture [_MainTex] { combine texture * primary } " +
                "SetTexture [_MainTex] { constantColor [_Color] combine previous * constant } " +
                "SetTexture [_UnityRemixUiMarker] { combine previous * texture, previous } } } }";
        }
    }
}
