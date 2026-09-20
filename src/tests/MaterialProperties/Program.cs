using UnityEngine;
using UnityEngine.Rendering;
using UnityRemix;

var texture = new Texture();
void Check(string expected, params Property[] properties)
{
    var material = new Material(properties);
    var actual = MaterialTextureProperties.FindAlbedo(material);
    if (actual != expected) throw new Exception($"Expected '{expected}', got '{actual}'.");
    // The test Material rejects any read from an absent or non-texture property.
    if (actual != null) material.GetTexture(actual);
}
Check(null);
Check(null, new Property("_NormalMap", texture));
Check("_MainTex", new Property("_MainTex", texture));
Check("_BaseMap", new Property("_BaseMap", texture));
Check("_BaseColorMap", new Property("_BaseColorMap", texture));
Check("_BaseMap", new Property("_MainTex", null), new Property("_BaseMap", texture));
Check("_MainTex", new Property("_MainTex", null));
Check("_BaseMap", new Property("_MainTex", null, ShaderPropertyFlags.MainTexture), new Property("_BaseMap", texture));
Check("_node_8550", new Property("_node_8550", texture));
Check("_node_6769", new Property("_node_6769", texture));
// Shader Graph/Amplify assets in Phasmophobia: equipment, writing books and journals.
Check("_Albedo", new Property("_Albedo", texture), new Property("_Normal", texture));
Check("_Albdeo", new Property("_Albdeo", texture), new Property("_WritingMask", texture));
Check("_BaseColor_T", new Property("_BaseColor_T", texture), new Property("_MicroNormal_Mask", texture));
Check("_Basecolor", new Property("_Basecolor", texture));
Check("_CandleSprite", new Property("_CandleSprite", texture), new Property("_NoiseTexture", texture));
Check("_Albedo", new Property("_MainTex", null), new Property("_Albedo", texture));
Check(null, new Property("_Albedo", null, Type: ShaderPropertyType.Color));
Check("_Albedo1", new Property("_Albedo1", texture), new Property("_Albedo2", texture));
foreach (string name in new[] { "_Colour", "_Liquid_Colour", "_ShalowColor", "_AlbdeoColour" })
    if (MaterialTextureProperties.FindColor(new Material(new[] { new Property(name, null, Type: ShaderPropertyType.Color) })) != name)
        throw new Exception("Custom liquid/albedo color lost: " + name);
if (MaterialTextureProperties.FindNormal(new Material(new[] {
    new Property("_BumpMap", null), new Property("_Normal", texture) })) != "_Normal" ||
    MaterialTextureProperties.FindNormal(new Material(new[] {
    new Property("_Normal", null, Type: ShaderPropertyType.Color), new Property("_Normal_T", texture) })) != "_Normal_T")
    throw new Exception("Custom normal maps lost or non-texture property read.");
Check("Texture2D_CustomGraph", new Property("_MainTex", texture),
    new Property("Texture2D_CustomGraph", texture, ShaderPropertyFlags.MainTexture));
Check(null, new Property("_Color", null, ShaderPropertyFlags.MainTexture, ShaderPropertyType.Color));
if (MaterialTextureProperties.FindAlbedo(null) != null) throw new Exception("Null material not supported.");
Console.WriteLine("PASS: 21 shader albedo cases, 2 normal-map cases, 4 custom colors; absent and mistyped property reads rejected.");
var ocean = new Material(new[] { new Property("_SeaColor", null, Type: ShaderPropertyType.Color) });
if (MaterialTextureProperties.FindColor(ocean) != "_SeaColor" ||
    MaterialTextureProperties.FindColor(new Material(new[] { new Property("_Tint", null, Type: ShaderPropertyType.Color),
        new Property("GraphColor", null, ShaderPropertyFlags.MainColor, ShaderPropertyType.Color) })) != "GraphColor")
    throw new Exception("Shader color metadata or ocean color was lost.");
if (!UnityMaterialSemantics.IsWater("Ocean/Indie/OceanShader", "OceanIndie") ||
    !UnityMaterialSemantics.IsWater("Universal Render Pipeline/Water", "Surface") ||
    UnityMaterialSemantics.IsWater("UI/Default", "Menu") ||
    UnityMaterialSemantics.WaterIor(float.NaN) != 1.333f || UnityMaterialSemantics.WaterIor(0) != 1.333f ||
    UnityMaterialSemantics.WaterIor(1.5f) != 1.5f)
    throw new Exception("Common water detection or IOR validation failed.");
Console.WriteLine("PASS: shared water/color rules for legacy Mono, modern Mono and IL2CPP.");
if (!UnityMaterialSemantics.IsVolumeProxy("VolumetricFog2/VolumetricFog2DURP") ||
    !UnityMaterialSemantics.IsVolumeProxy("Hidden/VolumetricFog2/Empty") ||
    UnityMaterialSemantics.IsVolumeProxy("Particles/Fog") ||
    UnityMaterialSemantics.IsVolumeProxy("Custom/Smoke") ||
    UnityMaterialSemantics.IsVolumeProxy("Universal Render Pipeline/Lit") ||
    UnityMaterialSemantics.IsVolumeProxy(null))
    throw new Exception("Volume bounds classification swallowed real world/particle geometry.");
Console.WriteLine("PASS: volume bounds classification preserves ordinary fog particles, smoke and solid surfaces.");
var packed = new uint[] { 0x01020304, 0xabcdef12, 0x98765432, 0x10101010, 0x20202020, 0x30303030, 0x40404040, 0x50505050, 0x60606060 };
var bytes = new byte[packed.Length * sizeof(uint)];
Buffer.BlockCopy(packed, 0, bytes, 0, bytes.Length);
if (XXHash64.ComputeHash(packed, packed.Length, 17) != XXHash64.ComputeHash(bytes, 0, bytes.Length, 17))
    throw new Exception("Packed geometry hash differs from byte representation.");
var oldHash = XXHash64.ComputeHash(packed, packed.Length);
packed[0]++;
if (oldHash == XXHash64.ComputeHash(packed, packed.Length)) throw new Exception("Geometry change not detected.");
Console.WriteLine("PASS: packed geometry hashing matches byte hashing and detects changed vertices.");
if (TextureCaptureSize.Limit(8192, 4096, 2048) != (2048, 1024) ||
    TextureCaptureSize.Limit(256, 512, 2048) != (256, 512) ||
    TextureCaptureSize.Limit(4096, 8192, 0) != (4096, 8192) ||
    TextureCaptureSize.Limit(1, 8192, 2048) != (1, 2048))
    throw new Exception("Texture cap failed to preserve dimensions or aspect ratio.");
Console.WriteLine("PASS: texture sizing caps large images, preserves small images, and supports original resolution.");

namespace UnityEngine.Rendering
{
    [Flags] public enum ShaderPropertyFlags { None = 0, MainTexture = 128, MainColor = 256 }
    public enum ShaderPropertyType { Color, Texture }
}
namespace UnityEngine
{
    public record Property(string Name, Texture Value,
        ShaderPropertyFlags Flags = ShaderPropertyFlags.None,
        ShaderPropertyType Type = ShaderPropertyType.Texture);
    public class Texture { }
    public class Shader(Property[] properties)
    {
        public int GetPropertyCount() => properties.Length;
        public string GetPropertyName(int index) => properties[index].Name;
        public ShaderPropertyType GetPropertyType(int index) => properties[index].Type;
        public ShaderPropertyFlags GetPropertyFlags(int index) => properties[index].Flags;
    }
    public class Material(Property[] properties)
    {
        public Shader shader { get; } = new Shader(properties);
        public bool HasProperty(string name) => properties.Any(p => p.Name == name);
        public Texture GetTexture(string name) => properties.Single(p => p.Name == name && p.Type == ShaderPropertyType.Texture).Value;
    }
}
