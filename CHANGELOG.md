# Changelog

## [0.1.0] — First public release

### What it is

KRAB-9000 (Kerbal Routing & Axis Blender) extends the Breaking Ground KAL-1000 controller with a full node-graph mixer. Where the KAL-1000 plays back a hand-authored sequence, the KRAB-9000 listens continuously, every frame, blending player input, autopilot output and live vessel telemetry into whatever a servo, engine or control surface should be doing right now.

### Added

- **Node graph data model**: sources → operators/filters → outputs, with full ConfigNode round-trip (unknown values from newer versions survive   load/save untouched), per-port weights, and stable node ids.
- **Sources**: raw player stick input, post-autopilot effective command, live vessel telemetry (speed, altitude, dynamic pressure, g-force, rotation   rate, and more), action group on/off state, a fixed constant, and 4 assignable Action-Group-driven input slots.
- **Combinators**: Weighted Sum, Product, Min, Max, Gated Blend, and the logic gates And/Or/Not/Xor.
- **Filters**: Remap (linear, or with a dedicated response curve — see below), Derivative, Rate Limit (slew), Comparator with hysteresis, and Hold (track/latch sample-and-hold).
- **Outputs**: Axis Output (drives any part's axis field directly — servo angle, RCS thrust percentage, reaction wheel authority, etc.) and Action Trigger (fires a part action on a signal edge).
- **Axis promoter** (Harmony): promotes `ModuleRCS`/`ModuleRCSFX` thrust percentage and `ModuleReactionWheel` authority limiter to assignable Axis Group fields, so KAL and KRAB can both target them directly.
- **A dedicated editor window**, built entirely in code: tree view of the graph with live per-node telemetry, undo/redo, output tabs (horizontally scrollable, individually renameable), a grouped source picker with a KAL-style "click a part in the scene" target picker, per-source display units (canonical values underneath, never affected by the chosen display unit), and validation feedback inline in the tree.
- **Response curve editor**: a separate modeless window for shaping a Remap's response curve by dragging points on a graph, with vertical/horizontal flip, reset-to-linear, and a cursor that tracks the real input live (in flight or in the VAB/SPH simulator).
- **VAB/SPH condition simulator**: one slider per source actually used in the graph, driving the same evaluator that runs in flight, so what you see before launch is what you get after.
- **Copy/paste** of a whole input/operator combination between output tabs, for replicating a setup without rebuilding it by hand.
- **Hover tooltips** on every icon button and on each operator/term's name in the tree, explaining what it does.
- **Localization**: English and Italian, full parity (every player-facing string in both).
- **Custom part retexture** (stock KAL-1000 model, new diffuse/emissive textures).

### Known limitations

- Curve editing has no manual tangent handles — tangents are auto-smoothed on every edit.
- One editor window open at a time.
- Tooltips don't yet cover parameter-field abbreviations (`thr`, `hys`, `w`...) or picker family headers — only operator names and icon buttons.

### Requirements

KSP 1.12.5, Breaking Ground DLC (hard dependency), Harmony 2, ModuleManager.
