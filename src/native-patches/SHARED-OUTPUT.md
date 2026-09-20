# Shared D3D11 output

The native runtime renders offscreen and exchanges GPU images with Unity's
D3D11 device. Scene color and transparent Remix UI are separate images. The
plugin composites scene color below Unity UI, and Remix UI above it. Unity keeps
the original game window, input registrations, camera, event systems and audio.

This path requires Windows x64, D3D11, the same GPU for Unity and Remix, and the
matching native build. The current Unity compositor requires Unity UI/UGUI.
Other graphics APIs, stripped UI players, and arbitrary custom UI rendering are
not covered by the current implementation.

## Unity integration exports

Call `Startup` with `forceNoVkSwapchain=1` and the Unity game HWND, then call
`remixapi_EnableUnityOutput` before the first `Present`.

`remixapi_SetUnityOutputTargets(scene, gui)` takes two `ID3D11Texture2D*` values
on one device. Both must have equal dimensions and format, one mip, one array
layer and one sample. Supported formats are RGBA8_UNORM, BGRA8_UNORM and their
TYPELESS equivalents (including Unity's native RenderTexture allocations).
The runtime retains COM references. Passing two null pointers detaches them;
passing only one null pointer is an error.

`remixapi_GetUnityRenderEvent()` returns a native `void __stdcall(int)` callback.
Invoke it on Unity's render thread with `CommandBuffer.IssuePluginEvent` before
sampling the target textures. It copies the newest completed frame without
waiting for Remix. It never enters CoreCLR or IL2CPP. Detach targets before
destroying Unity textures or shutting down Remix; late callbacks after native
shutdown are ignored.

`remixapi_GetUnityOutputState()` returns an atomic snapshot encoded as:

- Bit 0: Remix menu open.
- Bit 1: Remix menu requests game input blocking.
- Bit 2: shared-output failure; stop consuming output and inspect the native log.
- Bits 8–63: number of frames copied into Unity targets.

The exchange has three slots, each with a D3D11-owned scene and UI image imported
into Vulkan. A reusable private UI image converts premultiplied output to the
straight alpha expected by Unity's default UI material. Vulkan resource
completion tracking prevents D3D11 from reading unfinished frames. D3D11 event
queries prevent Vulkan from reusing frames while Unity's GPU copy is pending.
Resizing replaces the ring only after its GPU work and external leases finish.
There is no per-frame CPU pixel readback.

Unity's RawImages invert the vertical UV axis for these D3D11 targets. Offscreen
rendering is paced by newly published Unity snapshots and by completion of the
previous Remix GPU frame, preventing a hidden presenter from running unbounded
during loading or while Unity is idle. IL2CPP submits from Unity's main thread;
an attached CoreCLR render worker can otherwise be suspended by Boehm while
it is collecting the managed heap, deadlocking Unity's GC callbacks.

KMT allocation owners remain alive until their Vulkan imports are destroyed.
Explicit sharing requests fail if the driver cannot support them. UI callbacks
run without holding the exchange mutex, avoiding lock inversion with API calls.

## External D3D11 consumers

`remixapi_AcquireSharedD3D11Frame` returns KMT handles for a completed scene/UI
pair and their dimensions. Open them on the matching GPU using
`ID3D11Device::OpenSharedResource`. Do not call `CloseHandle` on KMT handles.
The legacy interface function `dxvk_GetSharedD3D11TextureHandle` acquires the
same frame and returns its scene handle. It is no longer a stub.

Both acquisition functions hold one lease per presenter. Repeated acquisition
returns the held frame. Call `remixapi_ReleaseSharedD3D11Frame` **after the
consumer's GPU work finishes** (for example, after a D3D11 event query completes).
Release imported texture references before releasing the lease. A lease protects
the frame against rendering and resize; consumers must release it before
shutdown. Releasing without an active lease is an error.

With no Unity targets, the first acquisition selects BGRA8 output at the native
presenter's dimensions. Submit frames with `Present` and retry acquisition until
a completed frame exists. Before then, acquisition returns GENERAL_FAILURE and
zero outputs. After a native failure, inspect `GetUnityOutputState` bit 2 instead
of retrying indefinitely. Enable shared output or make the first acquisition
before the first Present; this replaces the external Vulkan semaphore protocol.

See [shared-output-api.h](shared-output-api.h) for export signatures and
`tests/SharedOutput` for the real GPU integration test.
