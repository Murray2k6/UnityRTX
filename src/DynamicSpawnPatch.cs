using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace UnityRemix
{
    /// <summary>
    /// Hooks instantiation / initialization lifecycle of dynamic gameplay objects
    /// (projectiles, explosions, rockets, magnets, nails, spawned enemies) to register
    /// their renderers immediately on the exact frame they spawn, with zero polling overhead.
    /// </summary>
    public static class DynamicSpawnPatch
    {
        private static bool _applied;

        public static void Apply(Harmony harmony, ManualLogSource logger)
        {
            if (_applied) return;
            _applied = true;

            var postfix = new HarmonyMethod(typeof(DynamicSpawnPatch), nameof(OnDynamicSpawned));

            string[] typesToPatch = new string[]
            {
                "Projectile",
                "Explosion",
                "Grenade",
                "Magnet",
                "Nail",
                "Harpoon",
                "Cannonball",
                "EnemyIdentifier",
                "VirtueInsignia",
                "PhysicalShockwave",
                "Shockwave"
            };

            int patchedCount = 0;
            foreach (var typeName in typesToPatch)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type t = null;
                    try { t = asm.GetType(typeName); } catch { }
                    if (t == null) continue;

                    string[] methodsToHook = new string[] { "Awake", "Start", "OnEnable" };
                    foreach (var mName in methodsToHook)
                    {
                        try
                        {
                            var m = t.GetMethod(mName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                            if (m != null)
                            {
                                harmony.Patch(m, postfix: postfix);
                                patchedCount++;
                            }
                        }
                        catch { }
                    }
                    break;
                }
            }

            logger?.LogInfo($"[DynamicSpawnPatch] Applied lifecycle hooks to {patchedCount} dynamic object methods.");
        }

        private static void OnDynamicSpawned(Component __instance)
        {
            try
            {
                if (__instance != null)
                {
                    RemixFrameCapture.QueueDynamicObjectForTracking(__instance);
                }
            }
            catch { }
        }
    }
}
