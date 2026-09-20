using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityRemix.Legacy
{
    // UnityEngine.UI is optional in Unity 4. Resolve it without a hard assembly reference.
    internal sealed class LegacyUiDiscovery
    {
        private Type graphicType;
        private PropertyInfo textureProperty;
        private readonly HashSet<int> textRenderers = new HashSet<int>();

        public void Scan(Renderer[] renderers)
        {
            textRenderers.Clear();
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null) continue;
                if (renderer.GetComponent<TextMesh>() != null) { textRenderers.Add(renderer.GetInstanceID()); continue; }
                foreach (MonoBehaviour component in renderer.GetComponentsInParent<MonoBehaviour>(true))
                    if (component != null && component.GetType().Name.StartsWith("ArcaneText", StringComparison.Ordinal))
                    { textRenderers.Add(renderer.GetInstanceID()); break; }
            }
            if (graphicType != null) return;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                graphicType = assembly.GetType("UnityEngine.UI.Graphic", false);
                if (graphicType != null) { textureProperty = graphicType.GetProperty("mainTexture"); break; }
            }
        }

        public bool IsText(Renderer renderer) { return textRenderers.Contains(renderer.GetInstanceID()); }
        public Object[] Graphics { get { return textureProperty == null ? new Object[0] : Object.FindObjectsOfType(graphicType); } }
        public Texture GetTexture(Object graphic) { return textureProperty == null ? null : textureProperty.GetValue(graphic, null) as Texture; }
    }
}
