# Unity 4 Mono x86 validation

Validated 2026-09-20 for `C:\Games\SDCWin32` (Sonic Dreams Collection):
Unity 4.6.6.15215477, Windows x86, original Mono/CLR 2 assemblies.

- All four plugin variants build. The legacy variant uses the game's original
  framework and monolithic UnityEngine references, with no UnityEngine.UI dependency.
- 49 material ownership, bounded cache, UI classification, restoration and cursor
  checks pass in the engine model (`tests/LegacyMono`). This model does not compile shaders.
- 17 legacy installer checks pass, including loader detection, x86/x64 rejection,
  conflicts, complete runtime backups, preserving configuration, and avoiding mixed dependencies.
- 16 existing runtime/installer checks pass.
- The original game's x86 `mono.dll`, hosted independently, loaded the final plugin,
  resolved its BepInEx/Unity base types, and executed its material policy successfully.
- The isolated x86 graphics probe created the x64 Remix device, submitted textures
  and fixed-function geometry, presented frames, tagged a UI texture, rejected an
  incompatible API contract, and opened/read/closed the native menu over IPC.
  The client and renderer subsequently exited; no helper processes remained.
- The packaged legacy DLL matches the deployed DLL:
  `8D4EC44D201FD65AD4766FCFEE754D271EF3179A6DFE7E0835DD9D2CE37197AC`.
- All 195 installed files were verified. 121 existing game/other files stayed
  unchanged. 167 previous Remix files were preserved under
  `C:\Games\SDCWin32\BepInEx\UnityRemix\backups\bb987165d2f941288ff828387d1c6564`.

The game was **not launched**, as requested. Unity's ShaderLab compilation,
actual game rendering/input, custom shader fidelity, scene changes, and extended
GC/VRAM stability remain untested in the player. The installed
`Start-UnityRemix.cmd` selects Direct3D 9 and non-VR mode when the user runs it.

The legacy plugin is still one `com.Unity.remix` plugin. Its version/capability
query targets the matching packaged D3D9 bridge; it does not claim arbitrary
upstream native ABI compatibility or universal support for every Unity version.
