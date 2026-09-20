using UnityEngine;

namespace KRAB.Graph.Evaluation
{
	public class WeightedSumRuntime : RuntimeNode
	{
		private float[] weights;
		private float bias;
		private bool clamp;
		private float clampMin;
		private float clampMax;

		public override bool OnCompiled()
		{
			// One weight per port; missing entries default to 1.
			float[] declared = Definition.GetFloats("weights");
			weights = new float[Inputs.Length];
			for (int i = 0; i < weights.Length; i++)
			{
				weights[i] = i < declared.Length ? declared[i] : 1f;
			}
			bias = Definition.GetFloat("bias", 0f);
			clamp = Definition.HasParam("clampMin") || Definition.HasParam("clampMax");
			clampMin = Definition.GetFloat("clampMin", float.MinValue);
			clampMax = Definition.GetFloat("clampMax", float.MaxValue);
			return true;
		}

		public override void Evaluate(EvalContext ctx)
		{
			float sum = bias;
			for (int i = 0; i < Inputs.Length; i++)
			{
				sum += weights[i] * In(i);
			}
			Output = clamp ? Mathf.Clamp(sum, clampMin, clampMax) : sum;
		}
	}

	public class ProductRuntime : RuntimeNode
	{
		public override void Evaluate(EvalContext ctx)
		{
			float product = 1f;
			for (int i = 0; i < Inputs.Length; i++)
			{
				product *= In(i);
			}
			Output = product;
		}
	}

	public class MinRuntime : RuntimeNode
	{
		public override void Evaluate(EvalContext ctx)
		{
			Output = Mathf.Min(In(0), In(1));
		}
	}

	public class MaxRuntime : RuntimeNode
	{
		public override void Evaluate(EvalContext ctx)
		{
			Output = Mathf.Max(In(0), In(1));
		}
	}

	/// <summary>
	/// Linear inMin..inMax → outMin..outMax by default. A `curve` sub-node takes over
	/// entirely: its keyframes define both domain (x = raw input) and range (y = output),
	/// leaving inMin..outMax only to seed the curve editor. The curve uses KSP's own
	/// FloatCurve ConfigNode format (`key = t v inTan outTan`).
	/// </summary>
	public class RemapRuntime : RuntimeNode
	{
		private float inMin;
		private float inMax;
		private float outMin;
		private float outMax;
		private bool clamp;
		private FloatCurve curve;

		public override bool OnCompiled()
		{
			inMin = Definition.GetFloat("inMin", 0f);
			inMax = Definition.GetFloat("inMax", 1f);
			outMin = Definition.GetFloat("outMin", 0f);
			outMax = Definition.GetFloat("outMax", 1f);
			clamp = Definition.GetBool("clamp", true);
			curve = null;
			ConfigNode curveNode = Definition.GetNode("curve");
			if (curveNode != null)
			{
				FloatCurve loaded = new FloatCurve();
				loaded.Load(curveNode);
				if (loaded.Curve.length >= 2)
				{
					curve = loaded;
				}
			}
			return true;
		}

		public override void Evaluate(EvalContext ctx)
		{
			if (curve != null)
			{
				Output = curve.Evaluate(In(0));
				return;
			}
			float span = inMax - inMin;
			float t = span != 0f ? (In(0) - inMin) / span : 0f;
			if (clamp)
			{
				t = Mathf.Clamp01(t);
			}
			Output = outMin + t * (outMax - outMin);
		}
	}

	/// <summary>
	/// Crossfade between A (port 0) and B (port 1) driven by a control signal (port 2).
	/// With a blend band the transition is a linear fade across [threshold - blendWidth/2,
	/// threshold + blendWidth/2]; blendWidth = 0 degenerates to a hysteresis-guarded switch.
	/// </summary>
	public class GatedBlendRuntime : RuntimeNode
	{
		private float threshold;
		private float hysteresis;
		private float blendWidth;
		private bool engaged;

		public override bool OnCompiled()
		{
			threshold = Definition.GetFloat("threshold", 0.5f);
			hysteresis = Mathf.Max(Definition.GetFloat("hysteresis", 0f), 0f);
			blendWidth = Mathf.Max(Definition.GetFloat("blendWidth", 0f), 0f);
			return true;
		}

		public override void Evaluate(EvalContext ctx)
		{
			float control = In(2);
			if (blendWidth > 0f)
			{
				float t = Mathf.Clamp01((control - (threshold - blendWidth * 0.5f)) / blendWidth);
				Output = Mathf.Lerp(In(0), In(1), t);
				return;
			}
			if (engaged)
			{
				engaged = control > threshold - hysteresis;
			}
			else
			{
				engaged = control >= threshold + hysteresis;
			}
			Output = engaged ? In(1) : In(0);
		}
	}

	/// <summary>
	/// Rate of change of the input. Upstream sampled sources (PhysicalState) hold their
	/// value between samples, so the derivative is computed over the time between value
	/// changes, not frame time, and held in between.
	/// </summary>
	public class DerivativeRuntime : RuntimeNode
	{
		private float smoothingTau;
		private float scale;
		private float lastValue;
		private float lastChangeTime;
		private bool primed;

		public override bool OnCompiled()
		{
			smoothingTau = Mathf.Max(Definition.GetFloat("smoothing", 0.2f), 0f);
			scale = Definition.GetFloat("scale", 1f);
			return true;
		}

		public override void Evaluate(EvalContext ctx)
		{
			float value = In(0);
			if (!primed)
			{
				primed = true;
				lastValue = value;
				lastChangeTime = ctx.time;
				Output = 0f;
				return;
			}
			if (value == lastValue)
			{
				return; // hold the last derivative until the input actually moves
			}
			float dt = Mathf.Max(ctx.time - lastChangeTime, 1e-4f);
			float raw = (value - lastValue) / dt * scale;
			float alpha = smoothingTau > 0f ? dt / (smoothingTau + dt) : 1f;
			Output += alpha * (raw - Output);
			lastValue = value;
			lastChangeTime = ctx.time;
		}
	}

	/// <summary>
	/// Slew-rate limiter: the output tracks the input but may change no faster than
	/// ratePerSecond signal-units per second. Useful on instantly-responding targets like
	/// RCS thrust or reaction-wheel authority. Single symmetric rate; rate <= 0 = no limit.
	/// </summary>
	public class SlewRateRuntime : RuntimeNode
	{
		private float ratePerSecond;
		private bool primed;

		public override bool OnCompiled()
		{
			ratePerSecond = Definition.GetFloat("ratePerSecond", 0f);
			return true;
		}

		public override void Evaluate(EvalContext ctx)
		{
			float target = In(0);
			if (!primed || ratePerSecond <= 0f)
			{
				primed = true;
				Output = target;
				return;
			}
			float maxStep = ratePerSecond * ctx.deltaTime;
			Output += Mathf.Clamp(target - Output, -maxStep, maxStep);
		}
	}

	/// <summary>
	/// Integrator: Output += In(0) * deltaTime. A PI controller is a Weighted Sum of a
	/// proportional term and this node, so gains live there. clampMin/clampMax (absent =
	/// unclamped) are anti-windup; port 1 = reset (>= 0.5 holds the accumulator at 0).
	/// State is not persisted: it resets to zero on load.
	/// </summary>
	public class IntegratorRuntime : RuntimeNode
	{
		private bool clamp;
		private float clampMin;
		private float clampMax;
		private float accumulator;

		public override bool OnCompiled()
		{
			clamp = Definition.HasParam("clampMin") || Definition.HasParam("clampMax");
			clampMin = Definition.GetFloat("clampMin", float.MinValue);
			clampMax = Definition.GetFloat("clampMax", float.MaxValue);
			return true;
		}

		public override void Evaluate(EvalContext ctx)
		{
			if (AsBool(In(1)))
			{
				accumulator = 0f;
			}
			else
			{
				accumulator += In(0) * ctx.deltaTime;
				if (clamp)
				{
					accumulator = Mathf.Clamp(accumulator, clampMin, clampMax);
				}
			}
			Output = accumulator;
		}
	}

	public class ComparatorRuntime : RuntimeNode
	{
		private float threshold;
		private float hysteresis;
		private bool on;

		public override bool OnCompiled()
		{
			threshold = Definition.GetFloat("threshold", 0.5f);
			hysteresis = Mathf.Max(Definition.GetFloat("hysteresis", 0f), 0f);
			return true;
		}

		public override void Evaluate(EvalContext ctx)
		{
			float value = In(0);
			if (on)
			{
				on = value > threshold - hysteresis;
			}
			else
			{
				on = value >= threshold + hysteresis;
			}
			Output = on ? 1f : 0f;
		}
	}

	// Trigonometry: degrees in and out throughout, never radians, matching every other
	// angle in KRAB. General-purpose primitives only: a "heading" or "sideslip" node
	// would need vector dot products, which KRAB's scalar-only graph doesn't carry.

	public class SinRuntime : RuntimeNode
	{
		public override void Evaluate(EvalContext ctx)
		{
			Output = Mathf.Sin(In(0) * Mathf.Deg2Rad);
		}
	}

	public class CosRuntime : RuntimeNode
	{
		public override void Evaluate(EvalContext ctx)
		{
			Output = Mathf.Cos(In(0) * Mathf.Deg2Rad);
		}
	}

	public class TanRuntime : RuntimeNode
	{
		public override void Evaluate(EvalContext ctx)
		{
			Output = Mathf.Tan(In(0) * Mathf.Deg2Rad);
		}
	}

	/// <summary>Input clamped to [-1, 1]: asin is undefined outside that domain, and a
	/// value drifting past ±1 from upstream float error would turn into NaN and poison
	/// everything downstream.</summary>
	public class AsinRuntime : RuntimeNode
	{
		public override void Evaluate(EvalContext ctx)
		{
			Output = Mathf.Asin(Mathf.Clamp(In(0), -1f, 1f)) * Mathf.Rad2Deg;
		}
	}

	/// <summary>Same domain clamp as Asin, same reason.</summary>
	public class AcosRuntime : RuntimeNode
	{
		public override void Evaluate(EvalContext ctx)
		{
			Output = Mathf.Acos(Mathf.Clamp(In(0), -1f, 1f)) * Mathf.Rad2Deg;
		}
	}

	public class AtanRuntime : RuntimeNode
	{
		public override void Evaluate(EvalContext ctx)
		{
			Output = Mathf.Atan(In(0)) * Mathf.Rad2Deg;
		}
	}

	/// <summary>Ports: 0 = y, 1 = x, matching Mathf.Atan2(y, x) and atan2 everywhere else.
	/// Not KRAB's usual port order, but the familiar argument order is the less surprising
	/// choice here.</summary>
	public class Atan2Runtime : RuntimeNode
	{
		public override void Evaluate(EvalContext ctx)
		{
			Output = Mathf.Atan2(In(0), In(1)) * Mathf.Rad2Deg;
		}
	}

	/// <summary>
	/// Sample-and-hold. Ports: 0 = signal, 1 = gate, 2 = reset (wire a DEFAULT 0 when
	/// unused). mode = track follows the signal while the gate is high and freezes while
	/// low; mode = latch captures on the gate's rising edge and holds until reset is high.
	/// </summary>
	public class HoldRuntime : RuntimeNode
	{
		private bool latchMode;
		private bool latched;
		private bool lastGate;
		private float held;
		private bool primed;

		public override bool OnCompiled()
		{
			string mode = Definition.GetString("mode", "track");
			latchMode = string.Equals(mode, "latch", System.StringComparison.OrdinalIgnoreCase);
			if (!latchMode && !string.Equals(mode, "track", System.StringComparison.OrdinalIgnoreCase))
			{
				UnityEngine.Debug.LogWarningFormat("[KRAB] node '{0}': unknown mode '{1}', node disabled", Definition.id, mode);
				return false;
			}
			return true;
		}

		public override void Evaluate(EvalContext ctx)
		{
			float signal = In(0);
			bool gate = AsBool(In(1));
			if (!primed)
			{
				primed = true;
				held = signal;
				lastGate = gate;
			}
			if (latchMode)
			{
				if (gate && !lastGate)
				{
					latched = true;
					held = signal;
				}
				if (AsBool(In(2)))
				{
					latched = false;
				}
				Output = latched ? held : signal;
			}
			else
			{
				if (gate)
				{
					held = signal;
				}
				Output = held;
			}
			lastGate = gate;
		}
	}

	// Logic gates: inputs read with the >= 0.5 convention, outputs exactly 0/1.

	public class AndRuntime : RuntimeNode
	{
		public override void Evaluate(EvalContext ctx)
		{
			for (int i = 0; i < Inputs.Length; i++)
			{
				if (!AsBool(In(i)))
				{
					Output = 0f;
					return;
				}
			}
			Output = 1f;
		}
	}

	public class OrRuntime : RuntimeNode
	{
		public override void Evaluate(EvalContext ctx)
		{
			for (int i = 0; i < Inputs.Length; i++)
			{
				if (AsBool(In(i)))
				{
					Output = 1f;
					return;
				}
			}
			Output = 0f;
		}
	}

	public class NotRuntime : RuntimeNode
	{
		public override void Evaluate(EvalContext ctx)
		{
			Output = AsBool(In(0)) ? 0f : 1f;
		}
	}

	public class XorRuntime : RuntimeNode
	{
		public override void Evaluate(EvalContext ctx)
		{
			Output = AsBool(In(0)) != AsBool(In(1)) ? 1f : 0f;
		}
	}
}
