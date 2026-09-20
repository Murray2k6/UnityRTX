# UnityRemix runtime builds

UnityRemix has one plugin identity (`com.Unity.remix`), one configuration file
(`BepInEx/config/com.Unity.remix.cfg`), and one shared renderer. Each game receives
one `UnityRemix.dll` compiled for its loader. BepInEx 5, BepInEx 6 Mono, and BepInEx 6
IL2CPP have different assembly/API contracts; installing all variants into a game
is not supported.

| Build | Engine/loader | Validation in this workspace |
| --- | --- | --- |
| `BepInEx5MonoLegacy32` | Windows x86, Unity 4 Mono / CLR 2, BepInEx 5.4.23.5 | Compiled against Sonic Dreams Collection's Unity 4.6.6 assemblies; original Mono loads the plugin; standalone D3D9 textures, cubemaps, draws, directional lights, native menu IPC and process exit pass; game launched in non-VR mode with visible sky and original Canvas UI |
| `BepInEx5Mono` | Unity Mono, BepInEx 5 | Compiled with local Unity 6 references, with and without optional UI references |
| `BepInEx6Mono` | Unity Mono, BepInEx 6 | Compiled with local Unity 6 references and BepInEx 6 build 788 |
| `BepInEx6IL2CPP` | Unity IL2CPP, BepInEx 6 with Il2CppInterop | Compiled against Phasmophobia's Unity 2022.3.40f1 interop and BepInEx 6 build 788; native Remix Startup, device registration and scene loading confirmed in non-VR mode; subsequent GC/GPU-loss and memory issues are under runtime validation |

Full rendering fidelity has not been validated. These changes do **not** establish
support for every Unity game. The renderer still uses modern Unity mesh APIs and
Win32/Direct3D. Modern Mono builds target .NET Standard 2.1; the separate Unity 4
x86 variant targets CLR 2 / .NET 3.5. Other older runtimes require further adapters.
IL2CPP builds target .NET 6 and must be built
against the target game's `BepInEx/interop` assemblies. Old Unhollower-based
BepInEx 6 previews are not supported by this adapter.

For modern games, the complete package carries a private native renderer at
`BepInEx/UnityRemix/native/win-x64/UnityRemix.Native.dll`. UnityRemix loads this
absolute path before considering the game's root `d3d9.dll`. Installing or
updating the plugin does not replace the root DLL or its companion libraries.
The package includes a hash manifest, its native dependencies and USD resources.
Incomplete or mixed bundles stop before graphics startup. Without a private
installation, the legacy game-root renderer remains supported if it implements
the required API and shared-output functions.

The BepInEx plugin detects the loaded BepInEx assembly version, build identifier,
Unity version and backend at startup. It probes installed Remix DLLs in a separate,
hidden `UnityRemix.Probe.exe` process and selects bindings automatically for known
API families. Detection runs off the Unity thread; graphics initialization remains
on the Unity thread. A probe has a 15-second timeout and is cancelled on unload.
The probe never starts a graphics device or launches the game. Unselected native
dependencies do not enter the Unity process.

## Unity 4 Mono x86

`install.ps1 -UnityPath C:\Games\SDCWin32` detects the executable architecture,
Unity version, and old Mono framework before choosing `BepInEx5MonoLegacy32`.
For a clean legacy installation it installs the verified BepInEx 5.4.23.5 x86
loader as `version.dll`, one `BepInEx/plugins/UnityRemix.dll`, the 32-bit D3D9
bridge in the game root, and the matching 64-bit server and renderer in `.trex`.
Existing unrelated loader/graphics installations are rejected before writes.
After reviewing an existing Remix installation, `-ReplaceExistingBridge` permits
replacement of its root D3D9 bridge and `.trex` runtime. The entire previous
`.trex` directory is moved into the backup so unused old dependencies cannot
remain active. This switch does not override conflicts with another mod loader.
Updates preserve overwritten files under `BepInEx/UnityRemix/backups` and verify
copied files against their staged SHA-256 hashes. Game executable, assemblies,
assets and original Mono runtime are not replaced.

Use `Start-UnityRemix.cmd` to select Direct3D 9 and non-VR mode. The installer
does not launch it. The bridge renders into Unity's existing window, with the
native Remix menu and input transport. No second Unity output window or shared
D3D11 texture presenter is used for this legacy path.

The plugin converts eligible world materials to opaque, cutout, alpha-blended,
additive, premultiplied or multiply fixed-function passes. Custom albedo slots,
UV transforms, culling and supported stencil rules survive conversion. Unity continues supplying its textures,
animated meshes and transforms through D3D9. Textures are shared rather than
read back and re-uploaded. Converted materials are cached, bounded, and restored
when a renderer is disabled or the plugin unloads. Material animation is updated
without overwriting direct game changes from an unchanged source asset.
Canvas retains its original shader, font/sprite atlas, vertex tint and masking.
Its texture classification also requires screen-space depth state in the native
renderer. UI mesh passes use a distinct owned marker, avoiding global UI tagging
of atlases shared with world geometry.
All Unity API work stays on the Mono main thread; native menu state is read
through an atomic value without managed callbacks from native threads.

Sky discovery runs in code from scene and camera skyboxes, background render tags,
and recognized sky meshes/shaders. Six-sided, cubemap, panoramic, day/night and
cloud texture slots are detected; engines exposing texture-property enumeration
also provide custom slots. Sky changes are rescanned automatically. Original
skybox and unfamiliar procedural shaders retain their own passes, tint, exposure
and sun appearance. Supported simple sky meshes get an unlit sky pass and a
separate marker. Native cube hashes include all six faces. No game-specific sky
configuration is required for these paths. Unknown texture slots on older Unity
versions without property enumeration and arbitrary procedural shaders still need
further integration; retaining a shader alone does not establish faithful capture.

Sky and water are classified separately from ordinary geometry. Water uses a
refractive Remix material with source tint and IOR. Procedural oceans retain the
game's vertex shader for GPU geometry capture, including displacement; their
original pixel shader is not the final water image. Wave maps and reflection
cubemaps identify the water material instead of being mistaken for albedo.
Water no longer triggers early RTX composition that leaves later world objects
rasterized. Unity world shaders use vertex capture and sparse pixel-sampler slots;
orthographic projection alone no longer identifies a Unity draw as screen UI.
Captured clip-space positions receive the perspective divide before conversion
back to world space. Screen UI and sky-environment capture keep their separate roles.
The native Unity path preserves stage-constant/material colors and vertex colors.
Existing game directional
lights keep their direction, color and intensity. A fallback light is owned by
the plugin only when no active game directional light exists, and yields when
one appears. Sky classification does not infer physical sunlight from image pixels.

Unity 4 supports constructing these simple fixed-function materials from
[ShaderLab source strings](https://docs.unity3d.com/460/Documentation/ScriptReference/Material-ctor.html).
This adapter does not translate arbitrary pixel-shader effects, all normal maps,
emission models, or every game's UI system. The previously tested Sonic Dreams
run rendered the sky texture and readable Canvas menu; full geometry/water fidelity
and prolonged gameplay remain unverified. Water input discovery uses recognized
shader properties; shared reflection inputs and unfamiliar procedural shaders
still require game validation. Managed ownership, routing and compatibility checks
and the isolated native graphics probe are separate from that gameplay validation.
Unity 5 and later do not use this legacy fixed-function path.

The isolated D3D9 regression reads actual GPU pixels from a scene containing
water followed by a programmable world object. It checks the object's slot-3
albedo instead of its magenta raster shader, and verifies captured vertices
outside the undeformed mesh silhouette. The shared tests pass 90 legacy checks,
13 texture-property cases, and 156 independently compiled native ABI checks.

## Particles and effect geometry

The shared modern Mono/IL2CPP capture path snapshots Unity's simulated particle
renderer into reusable meshes on the main thread. It captures billboard and mesh
particles, particle-system trails, TrailRenderer and LineRenderer geometry, with
their UVs, vertex colors, opacity and separate trail materials. Unity continues
running particle lifetimes, forces, collisions, sub-emitters and animation; the
plugin does not run a second simulation. Per-renderer mesh identities stay stable
as particle counts change, expired geometry stops drawing, and owned bake meshes
are released on renderer removal or scene cleanup. Camera masks and existing
renderer/layer/distance filters apply. Bake failures are reported once per stream.

The native Unity D3D9 path captures shader output COLOR0 and UV0 for effects whose
pixel shader consumes vertex color. Fixed-function particle passes preserve
texture × vertex RGBA × material RGBA, including independent alpha multiplication.
These effects remain geometry in the traced scene. No flare atlas is globally
classified as UI and no game `.conf` changes are required. Additive, pure additive,
multiply and double-multiply materials retain distinct Remix blend modes.

Validation covers particle lifecycle/capture contracts with engine test doubles,
compilation of all four plugin variants, and native GPU readbacks for animated
atlas coordinates, vertex tint, vertex opacity, and material tint/opacity. The
Sonic Dreams beach menu was inspected with the native code fix: the oversized
gray flare discs and line lattice disappeared without the temporary UI texture
tag. Modern Mono/IL2CPP particle placement still needs in-engine validation;
the capture tests alone do not establish every engine version's bake behavior.

This is not arbitrary shader translation: GPU VFX Graph effects, custom vertex
streams consumed only by bespoke shaders, distortion, depth-based soft-particle
fades and every texture-sheet blending shader are not yet reproduced. Engines
without the corresponding BakeMesh APIs require an additional adapter. Unity 4
uses its existing D3D9 draws rather than the modern bake APIs.

The modern implementation uses Unity's documented
[particle BakeMesh](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ParticleSystemRenderer.BakeMesh.html)
and [BakeTrailsMesh](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ParticleSystemRenderer.BakeTrailsMesh.html)
snapshot APIs.

The native texture browser flips Unity source previews to the correct orientation
without changing game UVs or render-target previews. Its Sky and sun textures filter
exposes sky imagery that has no ray-traced picking ID; a scene click with no object
opens that selection. A sun painted into a sky texture is selected with that image,
not as an independent mesh or physical light.

Build the bridge with `native-adapters/build-legacy-bridge.ps1 -Architecture x86`
and again with `-Architecture x64`. Build the plugin with
`legacy/build.ps1 -UnityPath C:\Games\SDCWin32`; this uses the game's original
CLR 2 assemblies and requires no .NET Standard shim. `build.ps1 -Runtime All
-Package` includes this variant and the verified loader payload alongside the
modern variants. Native versions must match their ABI contracts; a version
number is never rewritten to disguise an incompatible function table.

| Remix product release | API family / native table |
| --- | --- |
| 0.4.0–0.4.1 | 0.2, 17 function pointers |
| 0.5.0–0.5.4 | 0.4, 19 function pointers |
| 0.6.0 | 0.4.1, 21 function pointers |
| 1.0.0–1.3.6 (published tags tested) | 0.5, 21 function pointers |
| 1.4.0–1.4.2 | 0.6, 21 function pointers |
| 1.5.0–1.5.2 | 0.6, 22 function pointers |
| Packaged Unity bridge | 0.1000, 41 function pointers |

Function positions are mapped from each upstream contract to the plugin's core
interface. Tests compile 22 official release headers and check native offsets,
mapped calls and detection. The API initializer does not distinguish patch
versions, so detection reports `0.6.x`, for example, rather than inventing an exact
patch version. The DLL's file version is reported separately when available.
Unknown table lengths and errors other than an incompatible version are rejected.

Core API negotiation does **not** supply missing rendering features. Published
upstream runtimes through
[1.5.2](https://github.com/NVIDIAGameWorks/dxvk-remix/blob/remix-1.5.2/public/include/remix/remix_c.h)
lack the Unity bridge's texture-upload and shared scene/native-menu exports.
The full package uses its private renderer for these features, independently of
the game-root version; the log names both detected and selected runtimes. This
preserves the native Remix menu inside Unity rather than substituting a settings
menu. It does not download or replace runtime files at startup. Without the
private renderer, missing capabilities produce a specific installation error.
The packaged renderer still uses **custom API 0.1000.0** on Windows x64. Full direct
rendering with arbitrary upstream versions is not implemented by the core table
adapters; unknown breaking ABIs require additional bindings and capability support.

Native dependencies load from the private folder, and DLSS/FSR discovery uses that
folder too. Windows can reuse already-loaded modules by filename; the loader
checks for conflicting native dependencies before loading the bundle and reports
the specific DLL instead of mixing versions. This is not process isolation and
does not establish support for two incompatible Remix renderers active together.

The native runtime was built from the current
[`gmod-numos3` branch](https://github.com/sambow23/dxvk-remix-gmod/tree/c6b48ed1a40f3451546137766d759dc13815f3a6),
commit `c6b48ed1a40f3451546137766d759dc13815f3a6`. The UnityRTX base is
[`unity-bridge-numos3`](https://github.com/Veitaserum4/UnityRTX/tree/f43e5c49a2cc804c1a8fd6fcbf6a2feb9f1de2be).
The shared bindings now match the native category flags and light structure.
145 checks against values compiled from the native header pass, and a separate
process successfully initializes the built DLL and checks every interface pointer.
The independent graphics probe also successfully creates a native graphics device
and swapchain, then shuts down. Native shutdown logs a resource-cleanup warning;
the probe does not validate rendered frames.

Phasmophobia's original supplied DLL returned `REMIXAPI_ERROR_CODE_INCOMPATIBLE_VERSION`.
Replacing it with the source build resolves that error: the non-VR game loads the
Remix API, initializes plugin components and creates the Remix window. Subsequent
crashes came from the game's report-and-abort path for Doorstop loader files.
The opt-in workaround below resolves those verified checks. The September 19,
2026 retry logs `Remix Startup succeeded!`, device registration and scene loading,
and the user confirmed the crash was fixed. Rendering completeness and prolonged
gameplay still need validation.

The game also crashed with BepInEx disabled but mod files still present. A clean
non-VR Steam launch with the extra mod/setup files moved out stayed running.
The matching native runtime and corrected plugin were installed for that test;
the original mod/setup files are preserved under `artifacts/Phasmophobia-clean-baseline-*`.

### Phasmophobia compatibility option

`Compatibility.PhasmophobiaLoaderFileWorkaround` defaults to `false`. It was enabled
for this test in `BepInEx/config/com.Unity.remix.cfg`. It applies only to the
Phasmophobia process with these SHA-256 hashes:

- GameAssembly.dll: `1785CE30C04666998E3C06A44C7E31F04B055796CF1163BFCFA9A03385A53463`
- Doorstop winhttp.dll: `8C6CDBC38836DEE87E3368F5DE1994D7C0CCEBF29E4CE7ABA3C0981F9375412C`

The adapter skips only the verified `.doorstop_version | version`,
`doorstop_config.ini | config`, `winhttp.dll | winhttp`, and exact game-root
`winhttp.dll` path report reasons. Other report reasons continue through the game.
Game updates and different loader binaries require a new compatibility assessment.

Account authentication and XR initialization remain owned by the game. The retry
log confirms successful Steam/Unity Services sign-in and Steam stats retrieval.
The earlier `Exception: Loaded User Account` text belonged to the abort routine;
it did not indicate failed authentication. The game attempts XR initialization
even in non-VR mode. This PC has no registered OpenXR runtime and reports
`XR_ERROR_RUNTIME_UNAVAILABLE`; desktop startup still succeeds. UnityRemix does
not invoke guessed account methods, fake XR availability or change the VR mode.

### Mesh read errors and diagnostics

Scene scanning now checks `Mesh.isReadable` before accessing CPU vertex data and
uses D3D11 readback directly for GPU-only meshes. Skinned topology and UI capture
also avoid CPU getters on non-readable meshes; skinned debug counters use the
already captured triangles. This preserves capture without changing game assets
or filtering Unity errors. Verification progressed from the 634-mesh startup scene
to the full main-menu scan: 3,527 meshes queued, including 3,192 using GPU readback,
with zero mesh-read errors. The previous run contained 2,188 invalid vertex-read
errors.

`Debug.VerboseTextureLogging` now controls per-material capture logs, and
`Debug.DetailedLogInterval = 0` also disables persistent-frame diagnostics.
Per-mesh GPU readback and renderer-cache details use BepInEx's Debug log level;
scene summaries and genuine errors remain visible. The native branch lacks the
optional DrawList and overlay callback exports, so debug boxes/HUD remain
unavailable with explicit warnings. Those warnings do not indicate an old DLL.

### IL2CPP worker lifetime

IL2CPP submits Remix frames on Unity's main thread. Registering a long-lived
CoreCLR render worker with IL2CPP exposed a cross-runtime GC deadlock; see the
single-window validation notes below. File-only bake workers do not enter IL2CPP.
Native ImGui callbacks preserve the calling thread's existing registration.
Earlier `Collecting from unknown thread` and allocation-pressure failures are
recorded separately; they are not treated as one proven root cause.

Shutdown runs cleanup after setting the stopped flag and waits for any Mono
render worker before releasing native resources. Unity owns its original window.
A close request stops rendering; an unexpected render exception stops further
submissions instead of repeatedly calling a failed renderer. Full scene loading
and shutdown are checked in the game; initial
startup alone is insufficient to establish stability.

### Texture-copy loading stall

A noninvasive stack snapshot located the loading stall in
`UploadUnityTexture`, inside Il2CppInterop's implicit array conversion. Large
texture arrays were copied through a native handle lookup for every byte/pixel.
IL2CPP texture reads now bulk-copy their contiguous spans while keeping the
native array alive; Mono retains its ordinary managed-array path. The corrected
run completed the full main-menu scan and advanced to subsequent scene loading.
Later runs exposed additional material, memory-pressure and GC failures, so the
initial clean log was not a complete gameplay validation.

### Materials and memory ownership

Material capture resolves the shader's `[MainTexture]` property and common
Built-in/URP/HDRP albedo names before reading textures and their tiling. It avoids
the invalid `Material.mainTexture` fallback on shaders without `_MainTex`.
An unassigned `[MainTexture]` does not hide another assigned albedo property.
Custom albedo aliases include `_Albedo`, `_Albdeo`, `_BaseColor_T`, `_Basecolor`
and `_CandleSprite`; modern builds also recognize `_Normal` and `_Normal_T`.
Layered materials can use their primary `_Albedo1`/`_Normal1` inputs; arbitrary
procedural blending with a second layer is not reproduced. Liquid color aliases
include `_Colour`, `_Liquid_Colour`, `_ShallowColor` and `_ShalowColor`.
Modern shader metadata validates property types before reading them.
Legacy Mono, modern Mono and IL2CPP share albedo/color aliases and water detection.
Modern builds honor `[MainColor]`, retain albedo tint brightness instead of
normalizing it as emission, and create translucent water through the native API.
The new translucent structure is checked against independently compiled C++ sizes
and offsets. Modern direct-API capture still needs explicit integration for
arbitrary GPU-only vertex deformation; it does not automatically run Unity's
D3D11 vertex shaders in Remix.
The scene scanner may preload inactive objects, but submits only live, enabled
renderers. An empty visibility snapshot remains empty instead of falling back to
every scanned instance. Known Volumetric Fog 2 bounds are excluded from solid
geometry in modern builds; their local fog density is not yet translated into
Remix volumes. Ordinary fog/smoke particle surfaces are still captured. Legacy
conversion leaves these volume shaders intact instead of replacing them with
opaque materials.
Generated texture uploads track negative Unity instance IDs correctly. Temporary
readback resources are released in `finally` blocks without redundant GPU uploads.
Material creation drains uploads again to cover textures queued after the frame's
initial drain, preserving native texture-before-material command ordering.

Scene replacement/unload resets queued frames at a synchronized render boundary,
releases meshes before materials and textures, and rebuilds all still-loaded
scenes. Additive loads preserve existing resources. Active, tinted, solid-color
and placeholder textures are included in cleanup. Skinned geometry is uploaded
only for a changed snapshot/content; native hash handles are replaced before
registration, never destroyed after registering their replacement.

The patched native runtime also exports `remixapi_ResetScene`, which queues
native scene-cache invalidation and GPU idle before subsequent destruction
commands. Finishing the managed Present call alone does not drain in-flight GPU
work. The export extends the bridge without changing its interface-table ABI;
older native builds log a compatibility warning. Reproducible native changes
are in `native-patches/`.

`Performance.MaxTextureDimension` limits the extra texture copy owned by Remix;
the default is 2048, and 0 preserves original resolution. This test PC's 8 GB GPU
uses 1024 because unrestricted readback expands compressed game textures into
multi-gigabyte RGBA copies. This changes captured texture detail and hashes.
Scene reload is required after changing the limit. It is a memory budget measure,
separate from repairing resource lifetimes.

The native source has a local fix on top of `c6b48ed`: the unchanged-scene BLAS
cache refreshes only structures referenced by its cached buckets, allowing
retired pool entries to expire. Every 30 seconds, if retained allocator space
exceeds 512 MiB, the bridge requests native compaction, releasing completely
empty chunks without purging live textures. Partially occupied chunks cannot
be returned by this operation. `[RemixMemory]` logs distinguish texture, acceleration,
render-target and retained-pool usage. Full scene/gameplay stability remains a
runtime validation requirement.

The optional `profiles/shared-gpu-8gb/dxvk.conf` reduces native allocation blocks
from the fork's 320 / 128 MiB defaults to 64 / 32 MiB. The current game uses it
with the patched native scene reset. Partially occupied blocks held roughly
1 GiB of unused space before this adjustment; active-rendering samples with the
profile recorded about 300 MiB. Total VRAM remains workload-dependent, around
5.3–6.0 GiB in the latest startup run; this is not a claim of low overall usage
or universal game compatibility.

A debugger also caught a separate exit crash in `<Unloaded_d3d9.dll>` after
native Shutdown returned. Cleanup now invalidates the API and calls Shutdown
once, retaining the DLL mapping until process exit so remaining native workers
cannot execute unmapped code. This is separate from the intermittent IL2CPP GC
failure, whose cause has not yet been confirmed.

### Single game window and shared output

The current display path uses Unity's original HWND and D3D11 swapchain. Remix
renders offscreen into a bounded shared scene/UI texture ring. A native Unity
render event copies completed frames on the GPU; it does not invoke managed
code or enter IL2CPP. Unity composites the scene below game UI and the transparent
Remix menu above it. Unity keeps its input registrations, focus and event systems.
Remix input messages are queued for its frame submission path. Both shared
images use vertically inverted UVs in Unity's UI compositor.

This path requires Windows x64, D3D11 on the same GPU, UGUI assemblies and the
matching native shared-output extensions. Stripped UGUI players and other graphics
APIs are not supported by this compositor. `-force-d3d11` is usable only when the
game supports that backend. World-space UI, custom render passes and different
Unity UI sorting arrangements still require game-specific visual validation.
See `native-patches/SHARED-OUTPUT.md` for the API contract and GPU tests.

The latest idle failure wrote `VirtualAlloc remapping failed` to the game's GC
log. It coincided with native compiler allocation failures on a system with about
32 GiB RAM and 34 GiB commit limit. This is evidence of allocation pressure,
not proof that every earlier GC failure has the same cause. Long-idle runtime
validation tracks process memory, GPU memory and system commit without a native
compiler running beside the game.

A subsequent frozen run provided a separate, symbol-resolved GC deadlock:
Unity's main/GC helper threads waited in `WKS::GCHeap::WaitUntilGCComplete`,
while the IL2CPP-registered Remix worker was suspended inside CoreCLR's
`CheckPromoted` during scene mesh streaming. IL2CPP now submits Remix frames
from Unity's main thread instead of registering a long-lived CoreCLR render
worker with Boehm. Pure file-reading bake workers remain unregistered. Native
render commands and GPU copies still run on their native threads. Idle stability
and visual/input behavior require live validation; a successful build is not
evidence that every historical GC failure is fixed.

The next non-VR run reached the main menu and was reported responsive. During
the short follow-up observation, dedicated GPU memory settled near 6.0 GiB and
process private memory fell from roughly 12.2 GiB during loading to 11.0 GiB,
without another GC dialog. This is a limited observation, not long-idle proof.

GPU mesh capture now applies `SubMeshDescriptor.baseVertex`, validates index
ranges, and reads position/normal/UV attributes from their actual vertex streams
and formats. The scanner retries data unavailable during scene loading and clones
batch indices before compacting them. These address capture defects that can
produce missing or misplaced surfaces. Sixteen buffer fixtures cover base-vertex
offsets, malformed ranges, stream layouts and packed formats; game geometry still
requires visual confirmation.

The following geometry build exited during loading with Vulkan
`VK_ERROR_DEVICE_LOST`; the GC log was unchanged. GPU mesh readback had been
using Unity's immediate D3D11 context on the main thread without native thread
protection, concurrently with Unity rendering/shared-output events. Readback now
enables `ID3D11Multithread` protection and holds its lock through Copy/Map/Unmap.
Unsupported contexts fail readback instead of proceeding without protection.
A standalone WARP test verifies cross-thread exclusion and release. The game
was left closed at the user's request, so the latest fix has no live-game
validation and the device-loss cause is not considered fully established.

## Build for a game

Reference preparation supports all four builds. It reads Mono engine assemblies
directly from the game; Mono does not need to launch first. IL2CPP requires the
game-specific `BepInEx/interop` generated by BepInEx; ordinary Mono Unity DLLs
cannot substitute for those files. Compilation requires a .NET SDK with the
relevant targeting packs.

```powershell
.\build.ps1 -UnityPath 'C:\Games\MyGame'
# Optional deployment, after closing the game:
.\build.ps1 -UnityPath 'C:\Games\MyGame' -Deploy
```

The script detects the backend and loader, validates/copies references into
`lib/<runtime>`, and builds `bin/Release/<runtime>/<framework>/UnityRemix.dll`.
Reference preparation and compilation do not modify the game. If no loader is
installed or supplied through `-LoaderPath`, reference setup downloads the correct
official BepInEx archive into `artifacts/bepinex`. A new modern Mono installation
defaults to BepInEx 6 bleeding edge; `-Runtime BepInEx5Mono` selects version 5.
IL2CPP uses version 6, and Unity 4 uses the verified version 5 x86 release.
An existing game loader is reused. Explicit deployment installs a missing loader; engine
and framework references are never deployed with the plugin.
Ambiguous game roots, mixed loaders, incompatible deployment targets, and
duplicate UnityRemix installs produce errors.

To prepare reference sets independently, run `setup-references.ps1 -UnityPath ...`
once for each target runtime. It only validates and copies references; it does
not compile, install, or launch anything. `-Runtime` can be `Auto` (the default),
`BepInEx5Mono`, `BepInEx6Mono`, `BepInEx6IL2CPP`, or `BepInEx5MonoLegacy32`.
Use `-LoaderPath` with a game or extracted loader root containing `BepInEx/core`
when preparing references before loader installation. Unity 4 reuses the local
x86 loader when available, otherwise downloads it. An existing
mixed or incompatible loader is rejected unless a different reference source
is explicitly selected. IL2CPP interop always comes from the target game.

Legacy reference sets contain the player's `mscorlib`, `System`, `System.Core`,
monolithic `UnityEngine`, and `BepInEx` assemblies. Modern reference sets retain
the modular Unity assemblies, optional UI assemblies, and each loader's required
assemblies. Missing or invalid input leaves the previous references untouched.

```powershell
.\setup-references.ps1 -UnityPath 'C:\Games\SDCWin32'
.\setup-references.ps1 -UnityPath 'C:\Games\MyMonoGame' -LoaderPath 'C:\Loaders\BepInEx6Mono'
```

Fetch loaders independently, or bootstrap an IL2CPP game before generating its
interop assemblies:

```powershell
.\fetch-bepinex.ps1 -Runtime All
# Installs only when explicitly requested, with the game closed:
.\fetch-bepinex.ps1 -UnityPath 'C:\Games\MyIL2CPPGame' -Install
# Then launch and close that game once before setup-references.ps1.
```

Downloads come from the official BepInEx GitHub releases (5) and
[bleeding-edge builds](https://builds.bepinex.dev/projects/bepinex_be) (6).
Architecture is read from the game executable; without a game, modern loaders
default to x64 and the legacy target uses x86. The cache verifies file hashes
before reuse and supports offline builds. `fetch-bepinex.ps1 -Refresh` checks for
new releases and replaces the cache selection, without upgrading an installed
game. Release checksums are verified when the publisher supplies them; cached
bleeding-edge downloads use locally recorded hashes. Existing loader files are
never silently overwritten.

Then build a selected runtime or package all four (all four game reference sets
must be present and compile successfully). Missing loader references are fetched
automatically; missing Unity or IL2CPP interop assemblies require reference setup:

```powershell
.\build.ps1 -Runtime BepInEx6Mono
.\build.ps1 -Runtime All -Package
```

`build.ps1` without a game defaults to all four targets. `-Package` and `-Deploy`
build the native shaders and renderer, stage their dependencies, and build both
legacy bridge architectures when the legacy target is selected. Native builds
require Visual Studio C++ tools, the Vulkan SDK, and Python with Meson/Ninja.
Visual Studio is discovered through `vswhere`; `VULKAN_SDK` is honored, with
`C:\VulkanSDK` discovery as a fallback. Use `-PythonPath 'C:\Python\python.exe'`
when Python/Meson is not on PATH, and `-Jobs 2` to control native compilation.
The local `artifacts/remix-native/build-tools` modules are used when available.
`build-native.ps1` also supports `-SourcePath`, `-ToolsPath`, and `-Reconfigure`.

`-Package` creates `artifacts/UnityRemix.zip` containing only variants built by that
invocation plus the private native renderer. `-NativeRuntimePath` selects a complete
native build output directory; by default it uses `artifacts/remix-native/source/_output`.
Its parent is the native source checkout. The build stages the compiled renderer
automatically and records its SHA-256; packaging rejects a renderer that differs
from that receipt. Use `-SkipNativeBuild` with a prebuilt native output when
intentionally reusing it. Source checkout/patch application remains a separate
step; the scripts do not switch native branches. Extract the ZIP outside the
game, then run with the game closed:

```powershell
.\install.ps1 -UnityPath 'C:\Games\MyGame'
```

The installer selects one variant, fetching/installing BepInEx if missing and
updating an existing `UnityRemix.dll` in place. `-Runtime` can explicitly choose
the loader for a new install; existing installations must match that choice.
If that variant is absent, it stops. It validates and stages the complete native
bundle before replacing a previous private installation, preserving the old folder
as a backup. The package includes the verified legacy loader payload; modern
loaders are downloaded when needed. Unity reference assemblies are never packaged.
No game is launched by the build or install scripts.

## Verification

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\runtime-tools.tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\native-runtime.tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\bepinex-tools.tests.ps1
dotnet run --project tests/RemixAbi/RemixAbi.csproj -c Release -p:Platform=x64
dotnet run --project tests/MaterialProperties/MaterialProperties.csproj -c Release
```

The script checks runtime detection, wrong-loader rejection, missing interop,
duplicate installs, and upgrades in disposable fixture folders under `artifacts`.
Before declaring a game supported, test plugin discovery, scene transitions,
camera/mesh/light capture, texture upload, settings, and shutdown inside it.

Loader API references: [BepInEx plugin guide](https://docs.bepinex.dev/master/articles/dev_guide/plugin_tutorial/2_plugin_start.html)
and [IL2CPP class injection](https://github.com/BepInEx/Il2CppInterop/blob/master/Documentation/Class-Injection.md).
