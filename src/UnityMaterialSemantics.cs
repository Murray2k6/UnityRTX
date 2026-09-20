using System;

namespace UnityRemix
{
    internal enum UnityBlendMode { Opaque, Alpha, Premultiplied, Additive, PureAdditive, SoftAdditive, Multiply, DoubleMultiply }
    // Kept free of engine APIs so the same rules compile for CLR 2, Mono and IL2CPP.
    internal static class UnityMaterialSemantics
    {
        public static readonly string[] AlbedoNames = { "_BaseMap", "_BaseColorMap", "_MainTex", "_AlbedoTex", "_AlbedoMap", "_BaseTexture", "_Albedo", "_Albdeo", "_BaseColor_T", "_Basecolor", "_CandleSprite", "_Albedo1", "_node_8550", "_node_6769" };
        public static readonly string[] NormalNames = { "_BumpMap", "_NormalMap", "_Normal", "_Normal_T", "_Normal1" };
        public static readonly string[] ColorNames = { "_SeaColor", "_BaseColor", "_Color", "_MainColor", "_Colour", "_Liquid_Colour", "_ShallowColor", "_ShalowColor", "_DeepColor", "_AlbedoColour", "_AlbdeoColour", "_Colorr", "_TintColor", "_Tint", "_Color0" };
        public static readonly string[] WaterInputs = { "_FresnelLookUp", "_SkyBox", "_ReflectionTex", "_ReflectionCube", "_DisplacementMap" };
        public static bool IsVolumeProxy(string shader)
        {
            // Volumetric Fog 2 ray marches inside a bounds mesh; its cube is not
            // a wall. Ordinary smoke/fog particles remain drawable surfaces.
            return !string.IsNullOrEmpty(shader) &&
                (shader.StartsWith("VolumetricFog2/", StringComparison.OrdinalIgnoreCase) ||
                 shader.StartsWith("Hidden/VolumetricFog2/", StringComparison.OrdinalIgnoreCase));
        }
        public static bool IsWater(string shader, string material)
        {
            string name = (shader ?? "").ToLowerInvariant();
            return name.Contains("water") || name.StartsWith("ocean/") ||
                (material ?? "").StartsWith("checkpointWater", StringComparison.OrdinalIgnoreCase);
        }
        public static float WaterIor(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) || value < 1 ? 1.333f : Math.Min(value, 3);
        }
        public static UnityBlendMode Blend(string shader, int source = -1, int destination = -1)
        {
            // UnityEngine.Rendering.BlendMode values; kept engine-independent for CLR 2.
            if (source == 1 && destination == 0) return UnityBlendMode.Opaque;
            if (source == 0 && destination == 3) return UnityBlendMode.Multiply;
            if (source == 2 && destination == 3) return UnityBlendMode.DoubleMultiply;
            if (source == 1 && destination == 10) return UnityBlendMode.Premultiplied;
            if (source == 1 && destination == 1) return UnityBlendMode.PureAdditive;
            if (source == 5 && destination == 1) return UnityBlendMode.Additive;
            if (source == 4 && destination == 1) return UnityBlendMode.SoftAdditive;
            if (source == 5 && destination == 10) return UnityBlendMode.Alpha;
            string name = (shader ?? "").ToLowerInvariant();
            if (name.Contains("premult")) return UnityBlendMode.Premultiplied;
            if (name.Contains("multiply")) return name.Contains("2x") ? UnityBlendMode.DoubleMultiply : UnityBlendMode.Multiply;
            if (name.Contains("additive") || name.Contains("particle add"))
                return name.Contains("soft") ? UnityBlendMode.SoftAdditive : UnityBlendMode.Additive;
            return UnityBlendMode.Alpha;
        }
        public static int RemixBlend(UnityBlendMode mode)
        {
            switch (mode)
            {
                case UnityBlendMode.PureAdditive: return 6;
                case UnityBlendMode.SoftAdditive: return 4;
                case UnityBlendMode.Multiply: return 7;
                case UnityBlendMode.DoubleMultiply: return 8;
                default: return 1;
            }
        }
    }
}
