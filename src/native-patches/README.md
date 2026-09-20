# Native Remix Unity bridge patches

Base: `sambow23/dxvk-remix-gmod`, branch `gmod-numos3`, commit
`c6b48ed1a40f3451546137766d759dc13815f3a6` (Remix API 0.1000.0).

`0001-retire-unused-blas.patch` corrects the unchanged-scene fast path. Only
acceleration structures referenced by the current cached scene receive a new
last-used frame. Retired entries can then age out while normal previous-frame
TLAS lifetime protection remains intact.

`0002-reset-scene-before-resource-destruction.patch` adds the optional
`remixapi_ResetScene` export. It queues a native scene clear and GPU-idle barrier
before the plugin queues destruction of the old scene's meshes, materials and
textures. The 0.1000.0 interface table is unchanged.

`0003-unity-shared-output.patch` implements the shared D3D11 scene/UI exchange,
native Unity render events, standalone frame leases, transparent UI compositing,
and queued input for Unity's original window. External presenters skip Vulkan
window-swapchain creation so the host's D3D11 swapchain can remain active.
It also releases the per-Present
swapchain COM reference and fixes single-dimension resize detection. See
[SHARED-OUTPUT.md](SHARED-OUTPUT.md) for ownership and integration details.

`0004-private-runtime-dependencies.patch` resolves DLSS discovery and the FSR
delay-loaded library relative to the renderer module. This permits a private
UnityRemix renderer without selecting different dependencies from the game root.

`0005-legacy-d3d9-bridge.patch` adds the Unity 4 x86 bridge capability query and
UI/sky/water texture tagging for 2D and cube textures, forwards native menu state over IPC, and makes the existing
client menu flag atomic. It rejects an upstream API number for the fork's table.
Process termination skips blocking cleanup under the Windows loader lock; the
server's existing process-exit monitor handles renderer teardown. Both x86
client and x64 server must be rebuilt together using
`native-adapters/build-legacy-bridge.ps1` from the UnityRemix workspace.

`0006-legacy-draw-categories.patch` recognizes sky and water markers in any active
texture stage, preserving a separate albedo shared with ordinary geometry. The
legacy plugin binds tiny owned marker textures only on the corresponding passes.
Canvas retains its original shader and uses a separate texture category qualified
by depth-write-off and an always/disabled depth test. World geometry using the
same atlas with ordinary depth testing stays outside this screen UI category.
Cube hashes include all six faces, retaining only small hashes when staging data
is released. Explicit sky cubes are accepted without enabling ordinary reflection
cubes as albedo. The category export is
optional and detected at runtime; older bridges retain original effect materials.
The original procedural-water raster fallback in this patch is replaced by 0008.
The Unity material path retains stage-constant tint and vertex colors.

`0007-unity-texture-selection.patch` adds the optional native Unity texture-session
export. The bridge enables it during capability negotiation. Texture previews
use Unity's UV orientation while the native menu itself remains upright. A sky
texture filter provides selection of rasterized sky imagery without requiring a
ray-traced object ID. A sun painted into the sky shares its image's selection.

`0008-unity-path-traced-world.patch` removes early water composition, captures
Unity world vertex shaders, and separates programmable sampler/UV state from
fixed-function state. It fixes the missing perspective divide in vertex capture.
Water metadata crosses the x86/x64 bridge into a bounded, device-owned registry
without retaining textures. The scene manager creates refractive materials while
allowing authored Remix replacements to take precedence. Both bridge binaries and
the renderer must be rebuilt together. This patch also fixes sparse sampler
selection for non-water shader geometry. Particle and flare vertex-shader COLOR0
and animated UV0 outputs survive capture. Fixed-function texture/vertex color
and a following material-tint stage are multiplied independently in RGB and alpha,
including opacity-micromap baking, using spare surface flag bits without changing
the public Remix ABI or GPU surface size. Opacity cache identities include the
new alpha multiplier. Shader dependencies include the changed source files;
the build helper generates shader headers before evaluating C++ dependencies.
The isolated graphics probe reads pixels
to check these effects remain in the traced scene. Apply it after 0001 through 0007.

Patch 0009 adds a signed screen-space motion-vector debug view (index 880).
Red/green encode horizontal/vertical previous-minus-current UV motion with 0.5
representing zero and the range covering +/-6.25% of the screen. Normalizing by
the G-buffer extent makes this diagnostic independent of DLSS input resolution.
It also runs the incremental shader dependency scanner on each build, preventing
new shader edits from silently reusing stale generated headers.

Patch 0010 completes the submitted shared-output frame before the external
presenter returns to Unity. Otherwise the immediate copy can reuse an older
camera image while Unity draws current-frame geometry/UI. It waits only after
the Vulkan command list has been submitted, outside the exchange mutex. The
normal D3D9/legacy swapchain is unchanged. The motion regression copies exactly
once after Present: the prior build fails and the corrected build passes all
36 translation/rotation/settling frames without polling for later output.

`0011-optional-private-build-modules.patch` checks for the actual Meson entry
file before entering optional private source directories. Public checkouts can
contain empty directories for unavailable private modules; their presence alone
must not make configuration fail. This preserves the guards used by the tested
native build.

Apply the patches in order from the native repository root and build its `d3d9` target through Meson
in the configured MSVC/Vulkan SDK environment. The tested local checkout is
`artifacts/remix-native/source`; it already contains this change. Its runtime
build command is:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build-native.ps1
```

Pass `-PythonPath` if Python/Meson is not on PATH. This builds shaders first,
compiles `d3d9`, installs the `output` dependency set, and stages the new renderer
with a SHA-256 build receipt. `build.ps1 -Package` runs this step automatically
(plus both bridge builds for the legacy target); `-SkipNativeBuild` explicitly
reuses prebuilt output. `build.ps1 -Package`
includes this complete runtime under `native/win-x64`, renaming the renderer to
`UnityRemix.Native.dll`. Its hash and all dependency hashes are recorded in
`native-manifest.sha256`. The installer places them under BepInEx and preserves
the game's root runtime.

The runtime target built successfully. The broader release build hit a linker
out-of-memory failure in `test_rtx_option.exe`; native unit tests have not been
claimed as passing. Managed ABI, shutdown, shader-property, texture-size and
geometry-hash checks are separate from that native build.

The shared-output GPU test passes on the RTX 5060 with a D3D11 swapchain already
owning the host window, including independent-device
imports, target validation, resizing, held leases, transparent background pixels,
visible Remix menu pixels and late callbacks after Shutdown. Both transport-only
and `--raytrace` runs passed, including typed/typeless RGBA/BGRA targets.
Shader prewarming is disabled in the test process. This is separate from game
rendering and long-idle validation. Native
Shutdown still reports the fork's existing common-device-object cleanup warning.
