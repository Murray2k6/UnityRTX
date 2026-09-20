# Native ABI verification

Run `dotnet run --project tests/RemixAbi/RemixAbi.csproj -c Release -p:Platform=x64` from the source directory.

The expected sizes, offsets, and enum values in `native-layout.json` were produced
by MSVC x64 against `sambow23/dxvk-remix-gmod`, branch `gmod-numos3`, commit
`c6b48ed1a40f3451546137766d759dc13815f3a6`. The manifest records the header hash.
The checks compile the actual shared `RemixAPI.cs` bindings; Unity is not required.

The runner also verifies automatic API negotiation, function-table mapping,
unknown-layout rejection and native-menu/texture-sharing capability checks.
Managed test failures print `FAIL` and exit nonzero without opening a Windows
application-error dialog.

To test published upstream layouts, download the official `remix_c.h` files for
the release tags into `artifacts/upstream-api/remix-<release>.h`, then run:

```powershell
./native-adapters/test-fixtures.ps1
dotnet run --project tests/RemixAbi/RemixAbi.csproj -c Release -p:Platform=x64 -- --adapters artifacts/native-adapters/fixtures artifacts/native-adapters/UnityRemix.Probe.exe
```

The fixtures compile each header with MSVC, export compiler-derived field offsets,
and exercise a mapped native call and the isolated probe. They validate API
contracts, not rendering behavior in those releases. API 0.4.1 adds Startup and
Present to the 0.4 table; API 0.6 has both 21- and 22-function variants.

After installing the complete package, use `--detect GAME_PATH BEPINEX_PATH` to
check the same asynchronous-worker inspection code used by the plugin. This
performs file validation and API detection without graphics startup. The normal
`--runtime` check below separately exercises initialization of the selected DLL.

To check a native runtime in a separate process, append
`-- --runtime C:\path\to\d3d9.dll`. Keep that runtime's dependency DLLs beside it.
This verifies API initialization and populated function pointers. It does not
create a graphics device or prove that a game renders correctly.

Append `--startup` after the DLL path to also create an independent hidden window,
initialize the native graphics device and swapchain, and call Shutdown. This needs
a supported GPU and may take a minute to shut down; it does not verify frame output.

The runner also checks that the Phasmophobia compatibility helper recognizes only
the verified loader-file reasons and the exact loader path under the verified game
root. Unknown reasons, other files and other directories are rejected.

When updating the native ABI, regenerate the expected values by compiling a
native program against the new pinned header. Do not copy expected values from
the C# bindings. The local regeneration helper is in
`artifacts/remix-native/generate-abi.py`, with its generated `abi-layout.cpp`.
