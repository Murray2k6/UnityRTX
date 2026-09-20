# UnityRTX

UnityRTX brings NVIDIA RTX Remix path tracing to Unity through the UnityRemix
BepInEx plugin. One package contains the loader-specific builds; the installer
selects one `UnityRemix.dll` for each game. All builds use the same plugin identity,
`com.Unity.remix`.

<img width="600" alt="UnityRTX rendering example" src="https://github.com/user-attachments/assets/421d12cb-e19d-40bb-89ec-69da1370e653" />

## Runtime support

| Build | Unity backend and loader | Plugin target |
| --- | --- | --- |
| `BepInEx5Mono` | Modern Unity Mono with BepInEx 5 | .NET Standard 2.1 |
| `BepInEx6Mono` | Modern Unity Mono with BepInEx 6 bleeding edge | .NET Standard 2.1 |
| `BepInEx6IL2CPP` | Unity IL2CPP with BepInEx 6 / Il2CppInterop | .NET 6 |
| `BepInEx5MonoLegacy32` | Windows x86 Unity 4 / CLR 2 with BepInEx 5.4.23.5 | .NET 3.5 |

Install only the selected variant in a game. These adapters do not establish
compatibility with every Unity version, shader, or game. Old Unhollower-based
BepInEx 6 previews are not supported. See [compatibility and validation notes](src/COMPATIBILITY.md)
for the detailed runtime requirements and remaining limitations.

## Rendering

Capture paths cover static and skinned meshes, supported material and texture
properties, live point/spot/directional lights, and particle, trail, and line
geometry. Unity continues to simulate particles; UnityRemix captures their
renderable geometry for Remix. Sky, water, and game UI have separate handling.
Remix replacements and the native Remix menu remain available.

Modern builds composite Remix's shared scene and native menu into Unity's game
window while retaining game input and UI. This path requires Windows x64,
Direct3D 11 on the same GPU, Unity UGUI assemblies, and the matching native bridge.
The Unity 4 x86 build uses a D3D9 bridge with a separate 64-bit renderer process.
Both paths require an RTX Remix-capable GPU and driver.

Custom shaders, GPU-only VFX systems, and unusual UI or render pipelines may need
additional adapters. Baked illumination does not automatically become live Unity
lights. Rendering fidelity still needs validation in each game.

## Install a package

Use `UnityRemix.zip` built from this branch. Extract it into a separate directory,
close the game, and run PowerShell from that extracted package:

```powershell
.\install.ps1 -UnityPath 'C:\Games\MyGame'
```

The installer detects the game backend and existing BepInEx loader, selects one
plugin, and installs the matching native bundle. If BepInEx is missing, it fetches
the correct modern loader or uses the bundled legacy x86 loader. Existing loaders
are preserved; mixed or conflicting installations produce an error.

A new modern Mono installation defaults to BepInEx 6 bleeding edge when that
variant is packaged. To select BepInEx 5 for a new Mono installation:

```powershell
.\install.ps1 -UnityPath 'C:\Games\MyMonoGame' -Runtime BepInEx5Mono
```

Modern games receive a private native renderer under
`BepInEx/UnityRemix/native/win-x64`; their game-root graphics DLLs are preserved.
The Unity 4 installer supplies the x86 D3D9 bridge and `.trex` renderer bundle.
Use its generated `Start-UnityRemix.cmd` to launch in D3D9, non-VR mode.

For modern games, launch with Direct3D 11 selected; use `-force-d3d11` if the game
supports that Unity launch option. Open the native Remix menu with **Alt+X**.
Plugin settings use `BepInEx/config/com.Unity.remix.cfg`. Build and installation
scripts never launch a game.

## Fetch BepInEx

Run source commands below from the `src` directory:

```powershell
cd src
.\fetch-bepinex.ps1 -Runtime All
```

This caches loaders for all four builds under `artifacts/bepinex`. Version 5 comes
from official BepInEx GitHub releases; version 6 comes from the official
bleeding-edge build service. With `-UnityPath`, the script detects the backend,
existing loader, and executable architecture. Without a game, modern downloads
default to x64; the legacy target always uses x86.

```powershell
# Fetch the loader needed by one game without installing it.
.\fetch-bepinex.ps1 -UnityPath 'C:\Games\MyGame'

# Check for new downloads and refresh the cache selection.
.\fetch-bepinex.ps1 -Runtime All -Refresh

# Explicitly install a missing loader, with the game closed.
.\fetch-bepinex.ps1 -UnityPath 'C:\Games\MyIL2CPPGame' -Install
```

Reference setup and builds also fetch missing loader dependencies automatically.
Cached downloads are hash-checked before reuse and work offline. Published
checksums are verified when available; bleeding-edge downloads use locally
recorded cache hashes. `-Refresh` does not upgrade a game's installed loader or
replace already prepared reference assemblies.

## Build from source

Managed builds require a .NET SDK with the relevant targeting packs and the
target game's Unity assemblies. Native builds additionally require Visual Studio
C++ x86/x64 tools, the Vulkan SDK, Python with Meson/Ninja, and the matching native
source checkout and dependencies. See [native bridge patches](src/native-patches/README.md)
for the source revision and patches. Build scripts do not fetch or switch native
source branches.

### Prepare references and build for one game

From `src`:

```powershell
# Detect the runtime, prepare references, and compile the plugin.
.\build.ps1 -UnityPath 'C:\Games\MyGame'

# Prepare references separately without compiling or installing.
.\setup-references.ps1 -UnityPath 'C:\Games\MyGame'
```

Mono references come directly from the game's managed assemblies. **IL2CPP needs
its own generated interop assemblies:** install BepInEx if missing, launch and
close that game once, then prepare references from its `BepInEx/interop` folder.
Downloading BepInEx cannot replace this generation step.

Reference sets are stored in `lib/<runtime>`. Use `-Runtime` to select a specific
build and `-LoaderPath` to use an extracted loader or another loader installation
containing `BepInEx/core`. This also lets you prepare both Mono variants from one
compatible game's engine assemblies without changing its installed loader:

```powershell
.\setup-references.ps1 -UnityPath 'C:\Games\MyMonoGame' `
    -Runtime BepInEx6Mono -LoaderPath 'C:\Loaders\BepInEx6Mono'
```

The selected loader must match the requested runtime. Missing or invalid source
assemblies leave existing reference sets untouched.

### Build all variants and package

Prepare the game reference set for each of the four runtimes first. Then run:

```powershell
.\build.ps1 -Runtime All -Package

# Supply Python explicitly when Python/Meson is not on PATH.
.\build.ps1 -Runtime All -Package -PythonPath 'C:\Python\python.exe' -Jobs 2
```

Without `-UnityPath`, `build.ps1` defaults to all four builds. To compile only one
prepared variant, use, for example, `-Runtime BepInEx6Mono`. Plugin outputs are in
`bin/Release/<runtime>/<framework>/UnityRemix.dll`.

`-Package` creates `artifacts/UnityRemix.zip` with only the variants built during
that invocation. It rebuilds shaders and the native renderer, stages dependencies,
and builds both legacy bridge architectures when the legacy target is selected.
The renderer's build receipt and package manifests prevent changed or incomplete
native bundles from being silently packaged or installed.

The default native output is `artifacts/remix-native/source/_output`. Use
`-NativeRuntimePath` for another output whose parent is the native checkout, or
combine it with `-SkipNativeBuild` to deliberately reuse a complete prebuilt
runtime. Packaging still compiles the native version probe, so Visual Studio C++
tools remain required with `-SkipNativeBuild`.

To rebuild only the native renderer, use `build-native.ps1`; it supports
`-PythonPath`, `-ToolsPath`, `-SourcePath`, `-Jobs`, and `-Reconfigure`. Native
builds use Meson, discover Visual Studio through `vswhere`, and honor
`VULKAN_SDK`. The compiled renderer is staged automatically; no manual DLL copy
is needed before packaging.

To build and deploy to one closed game:

```powershell
.\build.ps1 -UnityPath 'C:\Games\MyGame' -Deploy
```

`-Deploy` also builds the required native components and installs BepInEx if it is
missing. Use `-PythonPath` here as needed. Reference-only setup and ordinary
compilation do not modify the game.

## Validation and troubleshooting

All four plugin variants, the native renderer, and both legacy bridge
architectures build in the current workspace. Loader/reference tests cover
runtime detection, official download selection, offline cache reuse, integrity
failures, and installation conflicts. Build success is separate from in-game
rendering compatibility.

From `src`, after fetching all loaders and preparing/building the reference sets:

```powershell
.\tests\runtime-tools.tests.ps1
.\tests\bepinex-tools.tests.ps1
.\tests\native-runtime.tests.ps1
.\tests\setup-references.tests.ps1 -LegacyGame 'C:\Games\SDCWin32'
```

For a startup failure, inspect `BepInEx/LogOutput.log` and Unity's `Player.log`.
For a Remix API mismatch, use the matching package's complete native bundle;
changing a version number does not make incompatible native layouts compatible.
See [COMPATIBILITY.md](src/COMPATIBILITY.md) for loader detection, material and
particle limitations, native API families, and detailed verification procedures.
