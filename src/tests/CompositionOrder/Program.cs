using UnityEngine;
using UnityRemix;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
var camera = new Camera();
var scene = new Canvas { renderMode = RenderMode.ScreenSpaceCamera };
var gui = new Canvas { renderMode = RenderMode.ScreenSpaceOverlay, sortingOrder = 32767 };
var glass = new Renderer { sortingLayerID = 9, sortingOrder = 20 };
var hud = new Canvas { sortingLayerID = 0, sortingOrder = -4 };
var nested = new Canvas { isRootCanvas = false, sortingOrder = -10 };
var popup = new Canvas { sortingLayerID = 9, sortingOrder = 2 };
var equalPopup = new Canvas { sortingLayerID = 9, sortingOrder = 2 };
var overlay = new Canvas { renderMode = RenderMode.ScreenSpaceOverlay, sortingOrder = -50 };
using var order = new RemixCompositionOrder();
order.Update(camera, scene, gui);
Check(scene.sortingLayerID == 9 && scene.sortingOrder > glass.sortingOrder, "Glass must draw before shared scene.");
Check(hud.sortingOrder > scene.sortingOrder && popup.sortingOrder > hud.sortingOrder, "Preserve relative game UI order above scene.");
Check(popup.sortingOrder == equalPopup.sortingOrder, "Equal original UI priorities must stay equal.");
Check(nested.sortingOrder == -10 && !nested.overrideSorting, "Nested canvas must inherit its original hierarchy.");
Check(overlay.sortingOrder == -50 && gui.sortingOrder == 32767, "Overlay UI is independent.");
// A camera swap forces rescan without waiting a second. Orders must not drift.
var previousPopup = popup.sortingOrder;
order.Update(new Camera(), scene, gui);
Check(popup.sortingOrder == previousPopup, "Rescan must not accumulate offsets.");
// A game-owned change should survive release of the compositor.
hud.sortingOrder = 45;
order.Dispose();
Check(hud.sortingOrder == 45 && hud.sortingLayerID == 0, "Preserve game edits and restore unchanged properties.");
Check(popup.sortingOrder == 2 && popup.sortingLayerID == 9, "Restore original canvas priority.");
glass.sortingOrder = 32767;
order.Update(camera, scene, gui);
Check(glass.sortingOrder < scene.sortingOrder && scene.sortingOrder < popup.sortingOrder, "Make headroom at signed 16-bit limit.");
Check(popup.sortingOrder <= 32767, "Do not wrap Unity sorting orders.");
scene.enabled = false;
order.Update(camera, scene, gui);
Check(glass.sortingOrder == 32767 && popup.sortingOrder == 2, "Restore world/UI when shared scene becomes unavailable.");
Console.WriteLine("PASS: world/scene/UI ordering, custom layers, equal priorities, nested/overlay UI, camera swap, ownership, signed-order limit, release.");
