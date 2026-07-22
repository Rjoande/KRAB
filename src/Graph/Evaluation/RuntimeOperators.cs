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
			// One weight per port; missing entries default to 1 (catalog).
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
	/// Linear inMin..inMax → outMin..outMax by default. If a `curve` sub-node is
	/// present (M4, 2026-07-14), it takes over entirely: the curve's own keyframes
	/// define both domain (x = raw input) and range (y = output) directly, the same
	/// way KAL's own timeValue curve maps its axis — inMin/inMax/outMin/outMax are
	/// then unused (kept in the cfg only as the curve editor's initial seed range,
	/// see KrabCurveWindow). Reuses KSP's own FloatCurve for the ConfigNode format
	/// (`key = t v inTan outTan`), identical to KAL's — no new format to invent.
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
	/// Crossfade between A (port 0) and B (port 1) driven by a control signal
	/// (port 2). With a blend band the transition is a linear fade across
	/// [threshold - blendWidth/2, threshold + blendWidth/2]; with blendWidth = 0
	/// it degenerates to a hysteresis-guarded switch. To calibrate in flight tests.
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
	/// Rate of change of the input. Upstream sampled sources (PhysicalState) hold
	/// their value between samples, so the derivative is computed over the time
	/// between value *changes*, not frame time, and held in between (design note).
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
	/// ratePerSecond signal-units per second. Fills the gap no other node covers
	/// (temporal rate limiting); useful on instantly-responding targets like RCS
	/// thrust, reaction-wheel authority or thrust percentage, and for the design
	/// doc's "gradually recover native authority". Single symmetric rate for now;
	/// asymmetric rise/fall is a trivial future extension. rate <= 0 means no limit.
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

	/// <summary>
	/// Sample-and-hold. Ports: 0 = signal, 1 = gate, 2 = reset (wire a DEFAULT 0
	/// when unused). mode = track: follows the signal while the gate is high,
	/// freezes while low. mode = latch: captures the signal on the gate's rising
	/// edge and holds it until reset goes high.
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
