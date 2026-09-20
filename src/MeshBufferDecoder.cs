using System;

namespace UnityRemix
{
    // Pure buffer decoding, shared by GPU mesh capture and fixture tests.
    internal static class MeshBufferDecoder
    {
        public static int[] ReadIndices(byte[] data, bool wide, int start, int count, int baseVertex, int vertexCount)
        {
            int stride = wide ? 4 : 2;
            if (start < 0 || count < 0 || (long)start + count > data.Length / stride)
                throw new ArgumentException("Submesh index range exceeds the GPU buffer.");
            var indices = new int[count];
            for (int i = 0; i < count; i++)
            {
                int offset = checked((start + i) * stride);
                long index = (wide ? (long)BitConverter.ToUInt32(data, offset) : BitConverter.ToUInt16(data, offset)) + baseVertex;
                if (index < 0 || index >= vertexCount)
                    throw new ArgumentException("Submesh index plus baseVertex exceeds the vertex buffer.");
                indices[i] = (int)index;
            }
            return indices;
        }

        public static void ValidateAttribute(byte[] data, int count, int stride, int offset, int dimension, int componentSize)
        {
            if (count < 0 || stride <= 0 || offset < 0 || dimension < 1 || dimension > 4 ||
                offset + (long)dimension * componentSize > stride ||
                (count > 0 && (long)(count - 1) * stride + offset + dimension * componentSize > data.Length))
                throw new ArgumentException("Vertex attribute layout exceeds the GPU buffer.");
        }

        // Unity VertexAttributeFormat numeric values are stable across the supported runtimes.
        public static float ReadComponent(byte[] data, int offset, int format)
        {
            switch (format)
            {
                case 0: return BitConverter.ToSingle(data, offset);
                case 1: return HalfToFloat(BitConverter.ToUInt16(data, offset));
                case 2: return data[offset] / 255f;
                case 3: return Math.Max(-1f, (sbyte)data[offset] / 127f);
                case 4: return BitConverter.ToUInt16(data, offset) / 65535f;
                case 5: return Math.Max(-1f, BitConverter.ToInt16(data, offset) / 32767f);
                case 6: return data[offset];
                case 7: return (sbyte)data[offset];
                case 8: return BitConverter.ToUInt16(data, offset);
                case 9: return BitConverter.ToInt16(data, offset);
                case 10: return BitConverter.ToUInt32(data, offset);
                case 11: return BitConverter.ToInt32(data, offset);
                default: throw new NotSupportedException($"Unsupported vertex attribute format: {format}");
            }
        }

        private static float HalfToFloat(ushort half)
        {
            int sign = (half >> 15) & 1;
            int exp = (half >> 10) & 31;
            int mantissa = half & 1023;
            if (exp == 31) return mantissa == 0 ? (sign == 1 ? float.NegativeInfinity : float.PositiveInfinity) : float.NaN;
            float value = exp == 0 ? mantissa / 16777216f : (float)((1.0 + mantissa / 1024.0) * Math.Pow(2, exp - 15));
            return sign == 1 ? -value : value;
        }
    }
}
