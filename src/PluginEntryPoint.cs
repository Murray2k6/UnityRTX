using BepInEx;
using UnityEngine;
#if BEPINEX6_IL2CPP
using BepInEx.Unity.IL2CPP;
#elif BEPINEX6_MONO
using BepInEx.Unity.Mono;
#endif

namespace UnityRemix
{
    // Each build exposes exactly one plugin with the same identity and config.
    [BepInPlugin(UnityRemixPlugin.PluginGUID, UnityRemixPlugin.PluginName, UnityRemixPlugin.PluginVersion)]
#if BEPINEX6_IL2CPP
    public sealed class PluginEntryPoint : BasePlugin
    {
        private RemixPersistentBehaviour behaviour;
        public override void Load()
        {
            PhasmophobiaCompatibility.Initialize(Config, Log);
            behaviour = AddComponent<RemixPersistentBehaviour>();
            behaviour.Initialize(new UnityRemixPlugin(Config, Log));
        }

        public override bool Unload()
        {
            PhasmophobiaCompatibility.Shutdown();
            if (behaviour != null)
            {
                behaviour.Shutdown();
                Object.Destroy(behaviour);
            }
            return true;
        }
    }
#else
    public sealed class PluginEntryPoint : BaseUnityPlugin
    {
        private RemixPersistentBehaviour behaviour;
        private void Awake()
        {
            var host = new GameObject("UnityRemix_Persistent");
            Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            behaviour = host.AddComponent<RemixPersistentBehaviour>();
            behaviour.Initialize(new UnityRemixPlugin(Config, Logger));
        }

        private void OnDestroy()
        {
            if (behaviour != null)
            {
                behaviour.Shutdown();
                Object.Destroy(behaviour.gameObject);
            }
        }
    }
#endif
}
