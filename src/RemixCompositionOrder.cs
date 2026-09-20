using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_UI
using UnityEngine.UI;
#endif

namespace UnityRemix
{
    // A camera-space canvas participates in transparent world sorting. Putting it
    // at short.MinValue draws glass/particles over the (older) shared scene again.
    internal sealed class RemixCompositionOrder : IDisposable
    {
#if UNITY_UI
        private sealed class SavedCanvas
        {
            public Canvas Canvas;
            public int Layer, Order, AppliedLayer, AppliedOrder;
        }
        private readonly Dictionary<int, SavedCanvas> saved = new Dictionary<int, SavedCanvas>();
        private sealed class SavedRenderer
        {
            public Renderer Renderer;
            public int Order, AppliedOrder;
        }
        private readonly List<SavedRenderer> shiftedWorld = new List<SavedRenderer>();
        private float nextScan;
        private Camera lastCamera;

        public void Update(Camera camera, Canvas scene, Canvas gui)
        {
            if (!scene.enabled || camera == null) { Dispose(); return; }
            if (lastCamera == camera && Time.unscaledTime < nextScan) return;
            lastCamera = camera;
            nextScan = Time.unscaledTime + 1;
            Restore();

            // Use the last existing sorting layer, including games with custom
            // layers. Reserve a strict world -> scene -> UI order, without relying
            // on equal-order distance sorting or changing any material queues.
            int layer = 0, layerValue = int.MinValue;
            foreach (var candidate in SortingLayer.layers)
                if (candidate.value > layerValue) { layer = candidate.id; layerValue = candidate.value; }
            int order = 0;
            var world = new List<Renderer>();
            foreach (var renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                    (camera.cullingMask & (1 << renderer.gameObject.layer)) == 0) continue;
                if (renderer.sortingLayerID == layer)
                {
                    order = Math.Max(order, renderer.sortingOrder);
                    world.Add(renderer);
                }
            }

            var canvases = new List<Canvas>();
            foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (canvas == scene || canvas == gui || canvas.renderMode == RenderMode.ScreenSpaceOverlay ||
                    (!canvas.isRootCanvas && !canvas.overrideSorting)) continue;
                canvases.Add(canvas);
            }
            canvases.Sort((a, b) => {
                int result = SortingLayer.GetLayerValueFromID(a.sortingLayerID).CompareTo(SortingLayer.GetLayerValueFromID(b.sortingLayerID));
                return result != 0 ? result : a.sortingOrder.CompareTo(b.sortingOrder);
            });
            order = Math.Min(order + 1, Math.Max(short.MinValue + 1, short.MaxValue - canvases.Count));
            // Sorting orders are signed 16-bit in Unity. Make room when a game
            // already uses the maximum; never wrap a canvas behind the world.
            foreach (var renderer in world)
                if (renderer.sortingOrder >= order)
                {
                    shiftedWorld.Add(new SavedRenderer { Renderer = renderer, Order = renderer.sortingOrder, AppliedOrder = order - 1 });
                    renderer.sortingOrder = order - 1;
                }
            scene.sortingLayerID = layer;
            scene.sortingOrder = order;
            int rank = order;
            int previousLayer = int.MinValue, previousOrder = int.MinValue;
            foreach (var canvas in canvases)
            {
                int originalLayer = canvas.sortingLayerID, originalOrder = canvas.sortingOrder;
                if (originalLayer != previousLayer || originalOrder != previousOrder)
                    rank = Math.Min(short.MaxValue, rank + 1);
                previousLayer = originalLayer;
                previousOrder = originalOrder;
                var state = new SavedCanvas { Canvas = canvas, Layer = originalLayer, Order = originalOrder,
                    AppliedLayer = layer, AppliedOrder = rank };
                saved.Add(canvas.GetInstanceID(), state);
                canvas.sortingLayerID = layer;
                canvas.sortingOrder = rank;
            }
        }

        private void Restore()
        {
            foreach (var state in saved.Values)
            {
                if (state.Canvas == null) continue;
                // Do not overwrite a game's own changes made since the last scan.
                if (state.Canvas.sortingLayerID == state.AppliedLayer) state.Canvas.sortingLayerID = state.Layer;
                if (state.Canvas.sortingOrder == state.AppliedOrder) state.Canvas.sortingOrder = state.Order;
            }
            saved.Clear();
            foreach (var state in shiftedWorld)
                if (state.Renderer != null && state.Renderer.sortingOrder == state.AppliedOrder)
                    state.Renderer.sortingOrder = state.Order;
            shiftedWorld.Clear();
        }
#endif
        public void Dispose()
        {
#if UNITY_UI
            Restore();
            lastCamera = null;
            nextScan = 0;
#endif
        }
    }
}
