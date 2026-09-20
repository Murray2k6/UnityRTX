# Unity / Remix shared GPU profile

Merge these two `dxvk.conf` settings into the game's existing native configuration,
or copy the file beside the executable if no `dxvk.conf` exists. Restart to apply.
Do not replace unrelated settings in an existing file.

This native fork defaults to 320 MiB device-local and 128 MiB other allocation
blocks. The profile uses 64 / 32 MiB to reduce unused space in partially occupied
blocks. It does not change texture resolution or ray-tracing features. More
allocations may cost some allocation-time overhead.

Use the native scene-lifetime patches in `native-patches/` with the updated plugin.
Testing smaller blocks without the scene reset exposed GPU device losses during
startup scene replacement. Disabling opacity micromaps did not resolve those
failures. Micromaps are enabled again in the current test.

Phasmophobia currently also has `Performance.MaxTextureDimension = 1024` in its
plugin configuration; that separate texture-size limit changes captured detail.
The shared-GPU profile is optional and is not automatically installed by the
plugin installer.
