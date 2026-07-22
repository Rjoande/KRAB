using System.Collections.Generic;

namespace KRAB.Graph.Evaluation
{
	/// <summary>Per-frame evaluation context shared by all nodes.</summary>
	public class EvalContext
	{
		public ModuleKRABController module;
		public Vessel vessel;
		/// <summary>Seconds since the previous evaluation (frame time).</summary>
		public float deltaTime;
		/// <summary>UnityEngine.Time.time at evaluation start.</summary>
		public float time;

		/// <summary>
		/// Simulation mode (editor preview): sources return the values in
		/// <see cref="simOverrides"/> instead of reading the vessel, outputs
		/// compute but never write to fields nor fire actions. Same evaluator,
		/// same operator code — the preview matches reality by construction.
		/// </summary>
		public bool simulate;

		/// <summary>Simulated source values, keyed by node id.</summary>
		public Dictionary<string, float> simOverrides;
	}

	/// <summary>One input port binding: an upstream node or an explicit DEFAULT constant.</summary>
	public struct InputBinding
	{
		public RuntimeNode upstream;
		public float constant;

		public float Value => upstream != null ? upstream.Output : constant;
	}

	/// <summary>
	/// Base class of the runtime graph. Signals are plain floats; physical sources
	/// emit canonical human units (SI), command axes are nominal -1..+1 (throttle
	/// 0..1) and the boolean convention is: value >= 0.5 is true, boolean outputs
	/// are exactly 0 or 1 (see notes/catalogo-nodi.md).
	/// </summary>
	public abstract class RuntimeNode
	{
		public const float BoolThreshold = 0.5f;

		/// <summary>Definition this runtime node was compiled from (set by the factory).</summary>
		public KrabNode Definition;

		public InputBinding[] Inputs = new InputBinding[0];

		/// <summary>Disabled nodes are skipped and hold Output = 0.</summary>
		public bool Enabled = true;

		public float Output { get; protected set; }

		protected float In(int port)
		{
			return Inputs[port].Value;
		}

		protected static bool AsBool(float value)
		{
			return value >= BoolThreshold;
		}

		/// <summary>
		/// Sources call this first: in simulation mode the slider value replaces
		/// the vessel reading (see EvalContext.simulate).
		/// </summary>
		protected bool TrySimOverride(EvalContext ctx)
		{
			if (ctx.simulate && ctx.simOverrides != null
				&& ctx.simOverrides.TryGetValue(Definition.id, out float value))
			{
				Output = value;
				return true;
			}
			return false;
		}

		/// <summary>
		/// Cache parameters once Inputs are wired. Return false to disable the node
		/// (bad parameters are a warning, never a crash — same policy as parsing).
		/// </summary>
		public virtual bool OnCompiled()
		{
			return true;
		}

		public abstract void Evaluate(EvalContext ctx);
	}

	/// <summary>Placeholder for unknown subtypes: keeps the slot, outputs 0.</summary>
	public class DisabledRuntime : RuntimeNode
	{
		public DisabledRuntime()
		{
			Enabled = false;
		}

		public override void Evaluate(EvalContext ctx)
		{
		}
	}
}
