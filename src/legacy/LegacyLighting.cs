using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityRemix.Legacy
{
    internal sealed class LegacyLighting
    {
        private Light fallback;
        public int DirectionalCount { get; private set; }

        public void Update(bool allowFallback, float intensity)
        {
            DirectionalCount = 0;
            foreach (Light light in Object.FindObjectsOfType<Light>())
                if (light != null && light != fallback && light.enabled && light.gameObject.activeInHierarchy &&
                    light.type == LightType.Directional && light.intensity > 0) DirectionalCount++;
            bool needed = allowFallback && DirectionalCount == 0 && intensity > 0;
            if (needed && fallback == null)
            {
                var owner = new GameObject("UnityRemix fallback directional light") { hideFlags = HideFlags.HideAndDontSave };
                Object.DontDestroyOnLoad(owner);
                fallback = owner.AddComponent<Light>();
                fallback.type = LightType.Directional;
                fallback.renderMode = LightRenderMode.ForceVertex;
                fallback.color = Color.white;
                fallback.transform.rotation = Quaternion.Euler(50, -30, 0);
            }
            if (fallback != null) { fallback.enabled = needed; fallback.intensity = Mathf.Max(0, intensity); }
        }

        public void Shutdown()
        {
            if (fallback != null) Object.Destroy(fallback.gameObject);
            fallback = null;
        }
    }
}
