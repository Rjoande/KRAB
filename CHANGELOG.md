# Changelog

## [0.4.0]

### Added

- **7 trigonometric operators**: Sin, Cos, Tan, Asin, Acos, Atan, Atan2 — degrees in, degrees out (or in, for the inverse functions), consistent with every other angle in KRAB. Shown in their own "TRIGONOMETRY" group in both the source picker's OPERATORS family and the "+ Filter" popup, separate from the shaping filters (Remap, Derivative, ...).
- **7 new Vessel State metrics for building a helicopter-style FADEC/fly-by-wire**: signed per-axis angular rates (PitchRate/RollRate/YawRate, °/s), current vessel mass (Mass, tons), and attitude relative to the local horizon (Pitch/Bank/Heading, °) — the last three use the same formula as stock's own F12 "Aero Data" debug readout, so they match what the navball shows for the active vessel, but work for any loaded vessel.
- **Part highlighting now covers full symmetry groups**, not just the clicked part — matching KRILL. Source parts feeding the active output tab (via Part Field) are highlighted too, in a distinct color from the output's own target; when a part is both, target wins by default, with a footer toggle to invert that priority for a quick look at sources instead.
- **Node ids** (`n4`, etc.) can be shown next to every term via a new footer toggle — makes REUSE A SIGNAL traceable in graphs with more than a couple of fan-outs.
- **The active output tab is now clearly marked** (accent color + underline), instead of a barely-visible tint.
- **Tooltips everywhere they were still missing**: the "+ Clamp" button, Hold's track/latch toggle, every entry in the OPERATORS/"+ Filter" pickers, every parameter abbreviation (`inMin`, `thr`, `hys`, `τ`, `/s`, ...) and every picker family header, and the full untruncated label on any term that can get cut off in the tree, the simulator, or REUSE A SIGNAL.
- **Both the main editor and the curve editor now reopen where you left them**, for the rest of the game session (not saved to file).

### Fixed

- The curve editor window didn't hide with F2/Esc like the main editor already does.
- Growing the main editor window (more nodes = taller) resized it symmetrically around its center, which could push the titlebar off the top of the screen if the window was already positioned near it. It now grows downward only, keeping the titlebar fixed.

## [0.3.0]

### Added

- **Part Field source**: read a live numeric or on/off field from a specific part+module — the first KRAB source that reads *from* a part instead of only writing to one. Same "click a part in the scene" gesture as an output's target. A small built-in set of "derived fields" (`Config/DerivedFields.cfg`, ModuleManager-extensible from any mod) works around a handful of stock fields that only refresh while their part's right-click menu happens to be open — covers a Breaking Ground rotor's RPM, a hinge/rotation servo's angle, and a control surface's angle of attack.
- **Integrator filter**: accumulates its input over time, for building closed-loop regulators (PI/PID) directly in the graph — the integral gain lives in a downstream Weighted Sum's weight, not in the node itself. A second port resets the accumulator on demand.
- **Anti-windup clamp, now editable in the window**: Weighted Sum and Integrator can both be given a min/max clamp on their result without hand-editing the save file — one button adds the pair (wide open by default), one ✕ removes them together.
- **Preliminary MFD Extension compatibility**: if [MFD Extension](https://github.com/Rjoande/MFD-Extension) and Avionics Systems (MAS) are both installed, KRAB registers a bay on the shared IVA monitor. Today it's a hello-world page proving the integration end-to-end; real KRAB telemetry content is still to come.

### Fixed

- The editor window now hides with **F2** (hide UI) and **Esc** (pause menu), like every other in-game window (it used to stay on screen through both).

### Notes

These came out of an extended attempt to build a constant-speed propeller governor entirely from a KRAB graph. The closed-loop approach that motivated Part Field and Integrator turned out to need more than a graph could give it cleanly, but the fix that actually worked was simpler: a measured speed→pitch curve, buildable with tools KRAB already had in 0.1.0 (Vessel State, a Remap with a curve, Axis Output). The governor flies; it just didn't end up needing anything new. Part Field, Integrator and the clamp stay in this release as generally useful primitives in their own right, exercised and proven by the attempt that produced them.

## [0.2.0]

### Added

- **Loading screen tips**: five KRAB-flavoured tips — crab puns and logic gates — in English and Italian. They need the optional [LoadingTipsPlus](https://github.com/JPLRepo/LoadingTipsPlus) mod to show up, and they're added to the existing tip pool rather than replacing the stock rotation. Without LoadingTipsPlus installed, nothing changes.

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
