#if BEPINEX6_IL2CPP
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppSystem.Runtime.CompilerServices;
using Task = Il2CppSystem.Threading.Tasks.Task;

namespace UnityRemix
{
    internal static class PhasmophobiaCompatibility
    {
        private const string SupportedGameHash = "1785CE30C04666998E3C06A44C7E31F04B055796CF1163BFCFA9A03385A53463";
        private const string SupportedLoaderHash = "8C6CDBC38836DEE87E3368F5DE1994D7C0CCEBF29E4CE7ABA3C0981F9375412C";
        private static Harmony harmony;
        private static ManualLogSource logger;
        private static PropertyInfo reasonProperty, stateProperty, builderProperty;
        private static string verifiedGameRoot;

        public static void Initialize(ConfigFile config, ManualLogSource log)
        {
            if (!string.Equals(Process.GetCurrentProcess().ProcessName, "Phasmophobia", StringComparison.OrdinalIgnoreCase)) return;
            var enabled = config.Bind("Compatibility", "PhasmophobiaLoaderFileWorkaround", false,
                "Opt in to skipping Phasmophobia's report-and-abort path for verified Doorstop loader-file reasons. Only applies to the tested game build.");
            if (!enabled.Value) return;
            logger = log;
            using (var stream = File.OpenRead(Path.Combine(Paths.GameRootPath, "GameAssembly.dll")))
            using (var sha = SHA256.Create())
            {
                if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") != SupportedGameHash)
                {
                    log.LogWarning("Phasmophobia loader-file workaround is unavailable for this game build.");
                    return;
                }
            }
            using (var stream = File.OpenRead(Path.Combine(Paths.GameRootPath, "winhttp.dll")))
            using (var sha = SHA256.Create())
            {
                if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") != SupportedLoaderHash)
                {
                    log.LogWarning("Phasmophobia loader-file workaround is unavailable for this Doorstop build.");
                    return;
                }
            }
            verifiedGameRoot = Paths.GameRootPath;
            var owner = Type.GetType("ObjectPrivateAbstractSealedBoStUnique, Assembly-CSharp", throwOnError: true);
            var taskMethod = owner.GetMethod("Method_Public_Static_Task_String_PDM_0", BindingFlags.Public | BindingFlags.Static);
            var stateMachine = owner.GetNestedType("ValueTypeNPrivateSealedIAsyncStateMachineInAsStreTaUnique", BindingFlags.Public | BindingFlags.NonPublic);
            var moveNext = stateMachine?.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            reasonProperty = stateMachine?.GetProperty("reason");
            stateProperty = stateMachine?.GetProperty("__1__state");
            builderProperty = stateMachine?.GetProperty("__t__builder");
            if (taskMethod == null || moveNext == null || reasonProperty == null || stateProperty == null || builderProperty == null)
                throw new InvalidOperationException("Phasmophobia interop does not match the verified loader-file workaround.");
            harmony = new Harmony(UnityRemixPlugin.PluginGUID + ".phasmophobia-loader-files");
            try
            {
                harmony.Patch(taskMethod, prefix: new HarmonyMethod(typeof(PhasmophobiaCompatibility), nameof(BeforeReport)));
                // Native callers can inline the async entry method; its state machine remains callable.
                harmony.Patch(moveNext, prefix: new HarmonyMethod(typeof(PhasmophobiaCompatibility), nameof(BeforeMoveNext)));
                log.LogWarning("Phasmophobia loader-file workaround enabled for the verified game build.");
            }
            catch
            {
                Shutdown();
                throw;
            }
        }

        private static bool BeforeReport(string __0, ref Task __result)
        {
            // Leave account loading, XR initialization and every other report reason to the game.
            if (!PhasmophobiaLoaderReason.Matches(__0, verifiedGameRoot)) return true;
            __result = Task.CompletedTask;
            logger.LogWarning("Skipped Phasmophobia loader-file abort: " + __0);
            return false;
        }

        private static bool BeforeMoveNext(object __instance)
        {
            var reason = (string)reasonProperty.GetValue(__instance);
            if (!PhasmophobiaLoaderReason.Matches(reason, verifiedGameRoot)) return true;
            var builder = (AsyncTaskMethodBuilder)builderProperty.GetValue(__instance);
            stateProperty.SetValue(__instance, -2);
            builder.SetResult();
            builderProperty.SetValue(__instance, builder);
            logger.LogWarning("Skipped Phasmophobia loader-file abort: " + reason);
            return false;
        }

        public static void Shutdown()
        {
            harmony?.UnpatchSelf();
            harmony = null;
        }
    }
}
#endif
