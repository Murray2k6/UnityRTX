using UnityRemix;
int checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
var urp = new Urp { antialiasing = Mode.TemporalAntiAliasing };
var state = new TemporalAntialiasingOverride(urp, "antialiasing");
Check(state.Update() && urp.antialiasing == Mode.None, "Second temporal resolve suppressed");
Check(!state.Update(), "Steady frames don't rewrite camera state");
state.Restore(); Check(urp.antialiasing == Mode.TemporalAntiAliasing, "Original temporal mode restored");
state.Restore(); Check(urp.antialiasing == Mode.TemporalAntiAliasing, "Restore is idempotent");
state.Update(); urp.antialiasing = Mode.SMAA;
state.Restore(); Check(urp.antialiasing == Mode.SMAA, "Game changes aren't overwritten on shutdown");
Check(!state.Update() && urp.antialiasing == Mode.SMAA, "Spatial AA untouched");
urp.antialiasing = Mode.TemporalAntiAliasing; state.Update(); urp.antialiasing = Mode.FXAA; state.Update();
urp.antialiasing = Mode.None; state.Restore(); Check(urp.antialiasing == Mode.None, "Ownership relinquished after game selects spatial AA");
var builtIn = new BuiltIn { antialiasingMode = Mode.TemporalAntiAliasing };
var field = new TemporalAntialiasingOverride(builtIn, "antialiasingMode");
Check(field.Update() && builtIn.antialiasingMode == Mode.None, "Built-in postprocess field supported");
field.Restore(); Check(builtIn.antialiasingMode == Mode.TemporalAntiAliasing, "Built-in mode restored");
Check(!new TemporalAntialiasingOverride(new object(), "antialiasing").Update(), "Missing pipeline API is harmless");
Console.WriteLine($"PASS: {checks} temporal-AA ownership, restore and pipeline compatibility checks.");
enum Mode { None = 4, FXAA = 5, SMAA = 9, TemporalAntiAliasing = 12 }
class Urp { public Mode antialiasing { get; set; } }
class BuiltIn { public Mode antialiasingMode; }
