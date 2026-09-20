using UnityRemix;
using System.Runtime.InteropServices;

int checks = 0;
void Check(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
void Reject(Action action, string name)
{
    checks++;
    try { action(); } catch (ArgumentException) { return; }
    throw new Exception(name);
}

// A large statically batched mesh uses local 16-bit indices in its second range.
byte[] packed16 = { 99, 0, 0, 0, 2, 0, 1, 0 };
Check(MeshBufferDecoder.ReadIndices(packed16, false, 1, 3, 70000, 70003)
    .SequenceEqual(new[] { 70000, 70002, 70001 }), "16-bit baseVertex and indexStart");
byte[] packed32 = { 99, 0, 0, 0, 0, 0, 1, 0, 2, 0, 1, 0, 1, 0, 1, 0 };
Check(MeshBufferDecoder.ReadIndices(packed32, true, 1, 3, -65536, 3)
    .SequenceEqual(new[] { 0, 2, 1 }), "32-bit indices and signed baseVertex");
Reject(() => MeshBufferDecoder.ReadIndices(packed16, false, 1, 3, 70000, 70002), "Out-of-range vertex accepted");
Reject(() => MeshBufferDecoder.ReadIndices(packed16, false, 1, 4, 0, 100), "Truncated index data accepted");
Reject(() => MeshBufferDecoder.ReadIndices(packed16, false, int.MaxValue, 3, 0, 100), "Index range overflow accepted");
Reject(() => MeshBufferDecoder.ReadIndices(new byte[] {255,255,255,255}, true, 0, 1, 0, int.MaxValue), "Unsigned index overflow accepted");

// Independent interleaved buffers: position float32 and UV float16 use distinct strides.
byte[] positions = new byte[32];
BitConverter.GetBytes(1.25f).CopyTo(positions, 4);
BitConverter.GetBytes(-2.5f).CopyTo(positions, 20);
byte[] uvs = { 0, 0x38, 0, 0x3c, 0, 0x34, 0, 0xbc };
MeshBufferDecoder.ValidateAttribute(positions, 2, 16, 4, 3, 4);
MeshBufferDecoder.ValidateAttribute(uvs, 2, 4, 0, 2, 2);
Check(MeshBufferDecoder.ReadComponent(positions, 20, 0) == -2.5f, "Position stride");
Check(MeshBufferDecoder.ReadComponent(uvs, 0, 1) == 0.5f &&
      MeshBufferDecoder.ReadComponent(uvs, 6, 1) == -1f, "Separate packed UV stream");
Reject(() => MeshBufferDecoder.ValidateAttribute(positions, 3, 16, 4, 3, 4), "Short vertex buffer accepted");
Reject(() => MeshBufferDecoder.ValidateAttribute(positions, 2, 8, 4, 3, 4), "Overlapping vertex stride accepted");

Check(MeshBufferDecoder.ReadComponent(new byte[] {255}, 0, 2) == 1f, "UNorm8");
Check(MeshBufferDecoder.ReadComponent(new byte[] {128}, 0, 3) == -1f, "SNorm8 minimum");
Check(MeshBufferDecoder.ReadComponent(new byte[] {255,255}, 0, 4) == 1f, "UNorm16");
Check(MeshBufferDecoder.ReadComponent(new byte[] {0,128}, 0, 5) == -1f, "SNorm16 minimum");
Check(MeshBufferDecoder.ReadComponent(new byte[] {1,0}, 0, 1) == (float)BitConverter.UInt16BitsToHalf(1), "Half subnormal");
Check(float.IsNaN(MeshBufferDecoder.ReadComponent(new byte[] {1,124}, 0, 1)), "Half NaN");
Console.WriteLine($"PASS: {checks} GPU mesh buffer fixtures (base vertices, ranges, layouts, formats).");

// Exercise the native context lock without launching a game or creating a window.
if (OperatingSystem.IsWindows())
{
    int hr = D3D11.CreateDevice(IntPtr.Zero, 5 /* WARP */, IntPtr.Zero, 0, IntPtr.Zero, 0, 7,
        out var device, out _, out var context);
    Marshal.ThrowExceptionForHR(hr);
    try
    {
        using var attempted = new ManualResetEventSlim();
        using var acquired = new ManualResetEventSlim();
        Exception failure = null;
        Thread contender;
        using (new D3D11ContextGuard(context))
        {
            contender = new Thread(() => {
                try
                {
                    attempted.Set();
                    using (new D3D11ContextGuard(context)) acquired.Set();
                }
                catch (Exception error) { failure = error; }
            });
            contender.IsBackground = true;
            contender.Start();
            Check(attempted.Wait(5000), "Contender started");
            Check(!acquired.Wait(100), "Immediate context lock excludes concurrent access");
        }
        Check(contender.Join(5000), "Native context lock released");
        if (failure != null) throw failure;
        Check(acquired.IsSet, "Contender acquired context after release");
        Console.WriteLine("PASS: D3D11 WARP context lock excludes a second thread and releases cleanly.");
    }
    finally { Marshal.Release(context); Marshal.Release(device); }
}

static class D3D11
{
    [DllImport("d3d11.dll", EntryPoint="D3D11CreateDevice")]
    internal static extern int CreateDevice(IntPtr adapter, uint type, IntPtr software, uint flags,
        IntPtr levels, uint count, uint sdk, out IntPtr device, out uint level, out IntPtr context);
}
