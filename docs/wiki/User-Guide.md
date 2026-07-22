# KRAB-9000 User Guide

KRAB-9000 (**K**erbal **R**outing **&** **A**xis **B**lender) extends the
Breaking Ground KAL-1000 controller with a full node-graph mixer. Where the
KAL-1000 plays back a sequence you record ahead of time, the KRAB-9000
*listens*, continuously, every frame: it blends player input, autopilot
output and live vessel telemetry into whatever a servo, engine or control
surface should be doing right now.

The editor looks like a lot at first glance. This guide walks through it
piece by piece, starting from the mental model that makes the rest click
into place.

> **[SCREENSHOT PLACEHOLDER]** The KRAB-9000 part in the VAB parts panel
> (Robotics category), to show what it looks like and where to find it.

## Contents

- [The core idea](#the-core-idea)
- [Opening the editor](#opening-the-editor)
- [The editor window, region by region](#the-editor-window-region-by-region)
- [Your first output, step by step](#your-first-output-step-by-step)
- [The source picker](#the-source-picker)
- [Combining terms](#combining-terms)
- [Growing the tree: Term, Group, Filter](#growing-the-tree-term-group-filter)
- [The filter catalog](#the-filter-catalog)
- [Logic gates and conditions](#logic-gates-and-conditions)
- [The response curve editor](#the-response-curve-editor)
- [Outputs in depth](#outputs-in-depth)
- [Naming tabs, copying combinations](#naming-tabs-copying-combinations)
- [The VAB/SPH simulator](#the-vabsph-simulator)
- [Undo, redo, tooltips](#undo-redo-tooltips)
- [The right-click menu (PAW) fields](#the-right-click-menu-paw-fields)
- [A few recipes](#a-few-recipes)
- [Troubleshooting](#troubleshooting)

## The core idea

Every KRAB-9000 controller holds one **graph**: a tree of boxes ("nodes")
wired together. There are three kinds of node:

- **Sources** — where a number comes from: the player's stick, the vessel's
  actual post-autopilot command, a live telemetry reading, an action group's
  on/off state, a fixed constant, or one of the controller's own 4 input
  slots.
- **Operators** — combine or reshape numbers: add them together with
  weights, multiply them, take the min/max, remap a range, rate-limit,
  compare against a threshold, gate one signal with another, or apply logic
  (AND/OR/NOT/XOR).
- **Outputs** — where the result goes: into a part's field (a servo angle, a
  throttle, an RCS thrust limit, ...) or firing a part action.

The easiest way to think about building a graph is **backward from the
output**: "what feeds this output? A single source, or a combination? If a
combination, what feeds each of *those* terms?" You keep answering that
question, one port at a time, until every branch ends in a source or a
constant.

Every node shows its **live value** on the right, whether you're in the VAB
(driven by a simulator, see below) or in flight (driven by the real vessel).

## Opening the editor

Right-click the KRAB-9000 part and choose **Open KRAB Editor**. The window
that opens is independent of the stock right-click menu — you can close the
menu and keep working in it.

> **[SCREENSHOT PLACEHOLDER]** The part's right-click (PAW) menu in the VAB,
> with the "Open KRAB Editor" button visible among the other fields.

## The editor window, region by region

> **[SCREENSHOT PLACEHOLDER]** The full editor window in the VAB, with every
> region described below visible at once: titlebar, output tabs, target
> card, tree, simulator, status line at the bottom.

- **Titlebar.** An editable name field (this is the KRAB's own display name,
  independent of the part's), undo/redo, a status badge (*EDITOR ·
  SIMULATED* in the VAB/SPH, *IN FLIGHT · LIVE* when flying), and the close
  button.
- **Output tabs**, just below the titlebar. Each tab is one output — an
  ◉ Axis Output or a ▲ Action Trigger. Scroll the row with the mouse wheel
  if you have more than a handful. **+ Axis output** and **+ Trigger** add
  new ones.
- **Target card**, top of the body. Shows what the *currently selected tab*
  drives: **Pick target…** to choose a part and field/action in the 3D
  scene, a live **VALUE** readout, a rename field for the tab, and Copy/Paste
  icons (see [below](#naming-tabs-copying-combinations)).
- **Tree**, the main area. The graph feeding the selected output, one row
  per node, indented by nesting depth.
- **Simulator**, VAB/SPH only. One slider per source actually used in the
  graph, so you can see the whole thing react without ever launching.
- **Status line**, bottom. `N node(s), valid` or a validation error count.

## Your first output, step by step

1. Place a KRAB-9000 anywhere reachable from your controls (it doesn't need
   to be near the thing it drives — the connection is entirely logical).
2. Open the editor and click **+ Axis output**.
3. Click **Pick target…**, then click the servo/engine/surface you want to
   drive out in the 3D scene (Esc cancels). Pick the field to drive from the
   list — e.g. a hinge's *Target Angle*.
4. The tree now shows an empty port with a **default** value and a
   **+ Start building** button. Click it: it creates a Weighted Sum with one
   placeholder term (a Constant, value 0).
5. Click that placeholder term's name to open the **source picker**, and
   choose what actually feeds it — e.g. *Player Axis → Pitch*.
6. Watch the **VALUE** readout and the simulator slider for *Player Axis ·
   Pitch*: drag the slider and see the value change live, entirely in the
   VAB, nothing written to your vessel yet.
7. Set **inMin**/**inMax** on the output (in the target card) to the input
   range you expect, so the mapping onto the target's own range makes sense.

> **[SCREENSHOT PLACEHOLDER]** The tree area right after step 5, showing a
> single Axis Output with one Player Axis · Pitch term feeding it, and its
> live VALUE.

## The source picker

Opened from **+ Start building**, or by clicking an existing term's name
(the "▾" button), or from **+ Filter**'s sibling **OPERATORS** family. Entries
are grouped by family:

- **PLAYER AXES (RAW INPUT)** — the stick, exactly as you're moving it,
  before SAS/autopilot touches it.
- **EFFECTIVE COMMAND (POST-AUTOPILOT)** — the same channels, but *after*
  SAS/MechJeb/whatever autopilot has fed in. Use this if you want KRAB to
  react to what the vessel actually ends up doing, not just what you pressed.
- **VESSEL STATE** — live telemetry: speed, altitude, dynamic pressure,
  g-force, rotation rate, and more.
- **ACTION GROUPS** — a stock or custom action group's on/off state, as a 0/1
  signal.
- **KRAB INPUT SLOTS** — this controller's own 4 assignable slots (bind them
  to an Action Group in the part's right-click menu, same idea as the KAL's
  own axis groups).
- **CONSTANT** — a fixed number you type in.
- **OPERATORS** — nest an operator directly into this port instead of a
  plain source (e.g. put a Rate Limit right inside a Weighted Sum's term).
- **REUSE A SIGNAL** — wire in a node that already exists elsewhere in the
  graph, instead of creating a new one. Useful when two branches need the
  same live value (a "fan-out"). It's not offered anywhere it would create a
  loop.

> **[SCREENSHOT PLACEHOLDER]** The source picker popup open, with a couple of
> families expanded so their entries are visible.

## Combining terms

The default combinator when you start a new group is **Weighted Sum** — it
adds all its terms together, each multiplied by its own weight. Click the
↻ button next to a group's name to cycle through the other combinators that
fit its current number of terms: Product, Min, Max, Gated Blend, and the
logic gates.

**A note on the "w" field**, since it isn't obvious at a glance: it's a
**comma-separated list**, one weight per term, in the order the terms are
listed (top to bottom) — not one shared number for everyone. With three
terms you might write `1, 0.5, -1`: the first term counts in full, the
second at half strength, the third gets subtracted. Leave an entry out and
it defaults to `1`.

> **[SCREENSHOT PLACEHOLDER]** A Weighted Sum with 3 terms, its "w" field
> showing something like "1, 0.5, -1", to make the one-weight-per-position
> idea visible at a glance.

## Growing the tree: Term, Group, Filter

Every dynamic group (Weighted Sum, Product, Min, Max, And, Or, Xor) shows
three buttons under its terms:

- **+ Term** — adds a plain new input (a placeholder you then wire up via
  the source picker).
- **+ Group** — adds a *nested* sub-group (another Weighted Sum by default)
  as a new term — the "parentheses" of the graph.
- **+ Filter** — adds one of the shaping operators (Remap, Derivative, Rate
  Limit, Comparator, Hold) directly as a new term, since those don't combine
  multiple terms of their own and so don't get their own +Term/+Group/+Filter
  row.

Removing a term prunes anything feeding it exclusively (nothing is left
dangling behind).

## The filter catalog

| Filter | What it does |
|---|---|
| **Remap** | Maps an input range to an output range — linearly by default, or with a custom [response curve](#the-response-curve-editor). |
| **Derivative** | The rate of change of its input over time — build an event out of *"is this increasing fast"* rather than a raw level. |
| **Rate Limit** | Limits how fast its output can change per second, regardless of how fast the input jumps — smooths out an instant response into a gradual one. |
| **Comparator** | Outputs 1 or 0 based on a threshold, with optional hysteresis so it doesn't flicker near the edge. |
| **Hold** | Sample-and-hold: in *track* mode it follows its input while a gate is high and freezes otherwise; in *latch* mode it captures the input on the gate's rising edge and holds it until a reset. |
| **Gated Blend** | Crossfades between two inputs, driven by a third control signal — with a blend width for a smooth fade, or none for a hard hysteresis-guarded switch. |

## Logic gates and conditions

**All (AND)**, **Any (OR)**, **Not**, **Either (XOR)** read their inputs with
the convention "≥ 0.5 is true" and output exactly 0 or 1. Combined with
**Comparator** (to turn a continuous reading into a 0/1 condition) and
**Action Group** sources, you can build real conditional logic — e.g. "gear
is down AND radar altitude is below 150m" feeding an **Action Trigger** that
switches a light on.

## The response curve editor

Click the **Curve** icon on any Remap term to open a dedicated window for
shaping a custom response curve instead of a straight line.

> **[SCREENSHOT PLACEHOLDER]** The curve editor window open, showing a
> curved (non-linear) line with a few points on it, the axis labels, and the
> live cursor readout.

- **Click empty space** to add a point; **drag a point** to move it.
  Tangents are smoothed automatically — there are no manual tangent handles.
- The **live cursor** (a vertical line on the graph, plus a readout) follows
  the real input in flight, or the VAB/SPH simulator's slider otherwise.
- **↕** flips the curve vertically (mirrors the output); **↔** flips it
  horizontally (mirrors the input).
- **Reset to linear** discards the curve entirely and returns the term to
  the plain inMin/inMax/outMin/outMax fields.
- The window has its own Undo/Redo, shared with the main editor's stack.

## Outputs in depth

- **Axis Output** drives a part field directly. **inMin**/**inMax** map your
  graph's expected input range onto the target field's own range; the live
  VALUE card shows the reading in the target's real units when it can (e.g.
  degrees for a hinge) rather than a raw 0–1.
- **Action Trigger** fires a part action on a signal **edge** — click the
  edge label to cycle *rising* / *falling* / *both*.
- Either kind can be deleted with the ✕ in its target card (anything feeding
  it exclusively is pruned too).

## Naming tabs, copying combinations

- The **name** field in the target card renames just that output's tab —
  useful once you have several of the same kind and "Axis Output, Axis
  Output, Axis Output" stops being helpful. Clear it to fall back to the
  automatic name again.
- The **Copy**/**Paste** icons (top of the target card) copy the *entire*
  input/operator combination feeding the current output, so you can build it
  once and replicate it on a different output tab. Pasting always creates an
  independent copy — it never links the two outputs together.

## The VAB/SPH simulator

One slider per source actually referenced in the graph, plus a toggle for
boolean (Action Group) sources. Moving them feeds the *real* graph
evaluator — the same one that runs in flight — so what you see in the VAB is
exactly what will happen later. Nothing is ever written to the vessel from
here, which is also why the sample-rate field on Vessel State sources is
hidden in the VAB: it only affects flight sampling and has nothing to react
to yet.

## Undo, redo, tooltips

Every edit — adding/removing a term, rewiring a port, editing a curve,
cycling an operator — goes on a single undo stack (↶/↷ in the titlebar). The
curve window shares the same stack. Hovering most icon buttons and any
operator's name in the tree shows a short explanation after about half a
second.

## The right-click menu (PAW) fields

- **KRAB Name** — this controller's own display name (also editable from the
  editor's titlebar).
- **Priority** — arbitration priority, same scheme the KAL-1000 uses when
  more than one controller (KAL or KRAB) targets the same field.
- **KRAB Input 1–4** — four assignable slots, bindable to Action Groups from
  the vessel's Action Group editor, exactly like a KAL's own axis groups.
  Wire them into a graph via the **KRAB INPUT SLOTS** source family.
- **KRAB Graph** — a one-line status: `N node(s), valid`, or an error count
  if the graph doesn't validate.

## A few recipes

- **Gradual authority recovery**: a Gated Blend between "full native
  authority" and "reduced/manual authority," driven by a Vessel State
  reading (e.g. dynamic pressure) with a wide blend band, so control
  authority fades in smoothly instead of snapping.
- **Conditional lighting**: `Comparator(Radar Altitude, threshold=150,
  hysteresis>0)` → `Not` → `And` with an Action Group (Gear) source → an
  Action Trigger on the rising edge, to switch lights on under a set
  altitude only while the gear is down.
- **Smoothed control surface**: a Rate Limit right after a Player Axis
  source, so a fast stick flick doesn't slam a large surface instantly.

## Troubleshooting

- **"not bound"** next to a target — no part/field has been picked yet for
  that output. Click **Pick target…**.
- **"target part missing"** (in red) — the part that was bound no longer
  exists on this craft (deleted, or the save was loaded on a different
  craft). The binding itself is kept, not silently cleared, in case the part
  comes back (undo, re-attach); rebind if it doesn't.
- **Status line shows an error count instead of "valid"** — something in the
  graph doesn't validate (usually from hand-editing a save file rather than
  the editor). Check the KSP.log for the specific validation message.
- **A number field's edit seems to vanish** — the editor only accepts
  well-formed numbers; typing something invalid just restores the last valid
  value instead of crashing.
