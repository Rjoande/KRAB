# KRAB-9000 - Kerbal Routing & Axis Blender

A Kerbal Space Program mod that extends the Breaking Ground KAL-1000 controller
with a full node-graph mixer: blend player input, autopilot commands and live
vessel telemetry — through sources, combinators, filters, curves and logic
gates — into whatever a servo, engine, or control surface should be doing
right now.

Where the KAL-1000 plays back a hand-authored sequence, the KRAB-9000
*listens* continuously, every frame, and reacts.

![KRAB-9000 logo](docs/KRAB_logo_400.png)

## What it does

- **A real node graph, not a timeline.** Wire sources into operators into
  outputs, nest operators inside each other's ports, reuse a signal in more
  than one place (fan-out).
- **Sources**: raw player stick input, the vessel's post-autopilot effective
  command, live vessel telemetry (speed, altitude, dynamic pressure, g-force,
  rotation rate, ...), action group state, and 4 assignable input slots
  bindable to Action Groups like the KAL's own axis groups.
- **Combinators**: weighted sum, product, min, max, gated blend, and the
  logic gates (AND/OR/NOT/XOR).
- **Filters**: Remap (linear, or with a dedicated point-and-drag response
  curve editor), Derivative, Rate Limit (slew), Comparator with hysteresis,
  Hold (sample-and-track or latch).
- **Outputs**: drive any part's axis field directly (servo angle, RCS
  thrust percentage, reaction wheel authority, ...) or fire a part action on
  a signal edge.
- **Axis promotion**: RCS thrust percentage and reaction wheel authority
  limiter become assignable Axis Group fields, just like a KAL's own targets
  — so KRAB (and KAL) can drive them directly.
- **A dedicated editor window**, built entirely in code (no external UI
  assets): tree view with live per-node telemetry, undo/redo, a VAB/SPH
  condition simulator (drive the graph with sliders before you ever launch),
  copy/paste of a whole input combination between outputs, hover tooltips,
  and English/Italian localization.

## Requirements

- Kerbal Space Program 1.12.5
- **Breaking Ground** DLC (hard dependency — KRAB extends the KAL-1000)
- [Harmony 2](https://github.com/KSPModdingLibs/HarmonyKSP) (used for the axis
  promoter)
- [ModuleManager](https://github.com/sarbian/ModuleManager)

## Installation

Copy the contents of this repository into your `GameData` folder, so you end
up with `GameData/KRAB/...`. Make sure Breaking Ground, Harmony, and
ModuleManager are installed alongside it.

## Known limitations

- Curve editing has no manual tangent handles — tangents are auto-smoothed
  on every edit. Covers shaping a response curve without the complexity of a
  full keyframe/tangent editor.
- One editor window open at a time.
- Vocabulary for tooltips on parameter abbreviations (`thr`, `hys`, `w`, ...)
  and picker family headers isn't covered yet — only operator names and the
  icon buttons have hover tooltips so far.

## License

[MIT](LICENSE).

## Credits

Author: Rjoande. Built with the help of Claude Code.

The KRAB-9000 reuses the stock KAL-1000 model (Breaking Ground DLC) with a
custom retexture.
