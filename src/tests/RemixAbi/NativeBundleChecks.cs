using System;
using System.IO;
using UnityRemix;

static class NativeBundleChecks
{
    public static void Check()
    {
        object apiBox = new RemixAPI.remixapi_Interface();
        foreach (var field in typeof(RemixAPI.remixapi_Interface).GetFields()) field.SetValue(apiBox, new IntPtr(1));
        var api = (RemixAPI.remixapi_Interface)apiBox;
        if (RemixAPI.MissingUnityCapability(api, _ => new IntPtr(1)) != null) throw new Exception("Valid capabilities rejected.");
        api.GetVramStats = IntPtr.Zero;
        if (RemixAPI.MissingUnityCapability(api, _ => new IntPtr(1)) != null) throw new Exception("Optional statistics made mandatory.");
        api.CreateTexture = IntPtr.Zero;
        if (RemixAPI.MissingUnityCapability(api, _ => new IntPtr(1)) != "CreateTexture") throw new Exception("Missing texture upload accepted.");
        api = (RemixAPI.remixapi_Interface)apiBox;
        if (RemixAPI.MissingUnityCapability(api, name => name == "remixapi_GetUnityRenderEvent" ? IntPtr.Zero : new IntPtr(1)) != "remixapi_GetUnityRenderEvent")
            throw new Exception("Incomplete shared output accepted.");
        string game = Path.Combine(Path.GetTempPath(), "UnityRemix-bundle-" + Guid.NewGuid().ToString("N"));
        string bepinex = Path.Combine(game, "custom BepInEx");
        string legacy = Path.Combine(game, "d3d9.dll");
        Directory.CreateDirectory(game);
        File.WriteAllText(legacy, "An unrelated, incompatible version");
        if (RemixRuntimeSelection.Select(game, bepinex) != legacy) throw new Exception("Legacy path changed.");
        string native = Path.Combine(bepinex, "UnityRemix", "native", "win-x64");
        Directory.CreateDirectory(native);
        string dll = Path.Combine(native, RemixRuntimeSelection.LibraryName);
        if (RemixRuntimeSelection.Select(game, bepinex) != dll) throw new Exception("Incomplete bundle fell back to game-root DLL.");
        File.WriteAllText(dll, "private runtime fixture");
        string hash = RemixNativeBundle.Hash(dll);
        string valid = hash + "  " + RemixRuntimeSelection.LibraryName;
        string manifest = Path.Combine(native, "native-manifest.sha256");
        File.WriteAllText(manifest, valid);
        if (RemixNativeBundle.Validate(dll).Count != 1) throw new Exception("Valid native manifest rejected.");
        if (RemixRuntimeSelection.Select(game, bepinex) != dll) throw new Exception("Root DLL overrode the private bundle.");
        foreach (string invalid in new[] { "", valid + "\n" + valid, hash + "  ../../d3d9.dll", hash + "  " + legacy, "G" + valid.Substring(1) })
        {
            File.WriteAllText(manifest, invalid);
            Throws(() => RemixNativeBundle.Validate(dll));
        }
        File.WriteAllText(manifest, valid);
        File.WriteAllText(dll, "changed");
        Throws(() => RemixNativeBundle.Validate(dll));
        File.WriteAllText(dll, "private runtime fixture");
        File.WriteAllText(Path.Combine(native, "unknown.dll"), "mixed runtime");
        Throws(() => RemixNativeBundle.Validate(dll));
        Console.WriteLine("PASS: private renderer selection, incomplete bundle, hash mismatch, duplicate/traversal and mixed-DLL checks.");
    }

    private static void Throws(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Malformed native bundle was accepted.");
    }
}
