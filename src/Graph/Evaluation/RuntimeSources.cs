using System;
using UnityEngine;

namespace KRAB.Graph.Evaluation
{
	/// <summary>The 13 command channels shared by PlayerAxis and ScriptAxis.</summary>
	internal enum CommandChannel
	{
		Pitch,
		Yaw,
		Roll,
		TranslateX,
		TranslateY,
		TranslateZ,
		MainThrottle,
		WheelSteer,
		WheelThrottle,
		Custom01,
		Custom02,
		Custom03,
		Custom04
	}

	internal static class CommandChannels
	{
		public static bool TryParse(string name, out CommandChannel channel)
		{
			return Enum.TryParse(name, true, out channel);
		}

		public static float Read(FlightCtrlState state, CommandChannel channel)
		{
			if (state == null)
			{
				return 0f;
			}
			switch (channel)
			{
				case CommandChannel.Pitch: return state.pitch;
				case CommandChannel.Yaw: return state.yaw;
				case CommandChannel.Roll: return state.roll;
				case CommandChannel.TranslateX: return state.X;
				case CommandChannel.TranslateY: return state.Y;
				case CommandChannel.TranslateZ: return state.Z;
				case CommandChannel.MainThrottle: return state.mainThrottle;
				case CommandChannel.WheelSteer: return state.wheelSteer;
				case CommandChannel.WheelThrottle: return state.wheelThrottle;
				case CommandChannel.Custom01: return CustomAxis(state, 0);
				case CommandChannel.Custom02: return CustomAxis(state, 1);
				case CommandChannel.Custom03: return CustomAxis(state, 2);
				case CommandChannel.Custom04: return CustomAxis(state, 3);
				default: return 0f;
			}
		}

		private static float CustomAxis(FlightCtrlState state, int index)
		{
			float[] axes = state.custom_axes;
			return axes != null && index < axes.Length ? axes[index] : 0f;
		}
	}

	public class ConstantRuntime : RuntimeNode
	{
		private float value;

		public override bool OnCompiled()
		{
			value = Definition.GetFloat("value", 0f);
			return true;
		}

		public override void Evaluate(EvalContext ctx)
		{
			Output = value;
		}
	}

	/// <summary>Reads one of the module's bindable input slots (krabInput1..4).</summary>
	public class ControllerInputRuntime : RuntimeNode
	{
		private int slot;

		public override bool OnCompiled()
		{
			slot = Definition.GetInt("slot", 1);
			if (slot < 1 || slot > ModuleKRABController.InputSlotCount)
			{
				Debug.LogWarningFormat("[KRAB] node '{0}': invalid slot {1}, node disabled", Definition.id, slot);
				return false;
			}
			return true;
		}

		public override void Evaluate(EvalContext ctx)
		{
			if (TrySimOverride(ctx))
			{
				return;
			}
			Output = ctx.module.GetControllerInput(slot);
		}
	}

	/// <summary>Raw player command, pre-autopilot chain. Active vessel only.</summary>
	public class PlayerAxisRuntime : RuntimeNode
	{
		private CommandChannel channel;

		public override bool OnCompiled()
		{
			if (!CommandChannels.TryParse(Definition.GetString("channel", ""), out channel))
			{
				Debug.LogWarningFormat("[KRAB] node '{0}': unknown channel '{1}', node disabled",
					Definition.id, Definition.GetParam("channel"));
				return false;
			}
			return true;
		}

		public override void Evaluate(EvalContext ctx)
		{
			if (TrySimOverride(ctx))
			{
				return;
			}
			Output = ctx.vessel != null && ctx.vessel.isActiveVessel
				? CommandChannels.Read(FlightInputHandler.state, channel)
				: 0f;
		}
	}

	/// <summary>
	/// Effective command as the vessel receives it: vessel.ctrlState after the
	/// whole FeedInputFeed chain (SAS/MechJeb/AtmosphereAutopilot included).
	/// Read-only by design — KRAB must never write into the input pipeline.
	/// </summary>
	public class ScriptAxisRuntime : RuntimeNode
	{
		private CommandChannel channel;

		public override bool OnCompiled()
		{
			if (!CommandChannels.TryParse(Definition.GetString("channel", ""), out channel))
			{
				Debug.LogWarningFormat("[KRAB] node '{0}': unknown channel '{1}', node disabled",
					Definition.id, Definition.GetParam("channel"));
				return false;
			}
			return true;
		}

		public override void Evaluate(EvalContext ctx)
		{
			if (TrySimOverride(ctx))
			{
				return;
			}
			Output = ctx.vessel != null ? CommandChannels.Read(ctx.vessel.ctrlState, channel) : 0f;
		}
	}

	/// <summary>
	/// Vessel physics metric, sampled at its own rate (independent of frame rate),
	/// optionally EMA-filtered, in canonical human units — never normalized here.
	/// </summary>
	public class PhysicalStateRuntime : RuntimeNode
	{
		private enum Metric
		{
			SrfSpeed,
			HorizontalSrfSpeed,
			VerticalSpeed,
			IndicatedAirSpeed,
			Mach,
			AltitudeASL,
			AltitudeRadar,
			DynamicPressure,
			StaticPressure,
			AtmDensity,
			GForce,
			ExternalTemperature,
			AngularVelocityMag
		}

		private Metric metric;
		private float sampleRate;
		private bool useEma;
		private float emaTau;
		private float sampleTimer;
		private bool primed;

		public override bool OnCompiled()
		{
			if (!Enum.TryParse(Definition.GetString("metric", ""), true, out metric))
			{
				Debug.LogWarningFormat("[KRAB] node '{0}': unknown metric '{1}', node disabled",
					Definition.id, Definition.GetParam("metric"));
				return false;
			}
			// Design floor: never sample faster than 10 Hz.
			sampleRate = Mathf.Max(Definition.GetFloat("sampleRate", 0.1f), 0.1f);
			useEma = string.Equals(Definition.GetString("filter", "none"), "ema", StringComparison.OrdinalIgnoreCase);
			emaTau = Mathf.Max(Definition.GetFloat("filterParam", 0.5f), 0f);
			return true;
		}

		public override void Evaluate(EvalContext ctx)
		{
			if (TrySimOverride(ctx))
			{
				return; // slider sets the exact value: sampling and filter bypassed
			}
			if (ctx.vessel == null)
			{
				return;
			}
			sampleTimer += ctx.deltaTime;
			if (primed && sampleTimer < sampleRate)
			{
				return; // hold the previous sample
			}
			float raw = ReadMetric(ctx.vessel);
			if (useEma && primed && emaTau > 0f)
			{
				float alpha = sampleTimer / (emaTau + sampleTimer);
				Output += alpha * (raw - Output);
			}
			else
			{
				Output = raw;
			}
			sampleTimer = 0f;
			primed = true;
		}

		private float ReadMetric(Vessel vessel)
		{
			switch (metric)
			{
				case Metric.SrfSpeed: return (float)vessel.srfSpeed;
				case Metric.HorizontalSrfSpeed: return (float)vessel.horizontalSrfSpeed;
				case Metric.VerticalSpeed: return (float)vessel.verticalSpeed;
				case Metric.IndicatedAirSpeed: return (float)vessel.indicatedAirSpeed;
				case Metric.Mach: return (float)vessel.mach;
				case Metric.AltitudeASL: return (float)vessel.altitude;
				case Metric.AltitudeRadar: return (float)vessel.radarAltitude;
				case Metric.DynamicPressure: return (float)vessel.dynamicPressurekPa;
				case Metric.StaticPressure: return (float)vessel.staticPressurekPa;
				case Metric.AtmDensity: return (float)vessel.atmDensity;
				case Metric.GForce: return (float)vessel.geeForce;
				case Metric.ExternalTemperature: return (float)vessel.externalTemperature;
				case Metric.AngularVelocityMag: return vessel.angularVelocity.magnitude;
				default: return 0f;
			}
		}
	}

	/// <summary>On/off state of a vessel action group, as a boolean signal (0/1).</summary>
	public class ActionGroupStateRuntime : RuntimeNode
	{
		private KSPActionGroup group;

		public override bool OnCompiled()
		{
			if (!Enum.TryParse(Definition.GetString("group", ""), true, out group) || group == KSPActionGroup.None)
			{
				Debug.LogWarningFormat("[KRAB] node '{0}': unknown action group '{1}', node disabled",
					Definition.id, Definition.GetParam("group"));
				return false;
			}
			return true;
		}

		public override void Evaluate(EvalContext ctx)
		{
			if (TrySimOverride(ctx))
			{
				Output = Output >= BoolThreshold ? 1f : 0f; // keep the boolean contract
				return;
			}
			Output = ctx.vessel != null && ctx.vessel.ActionGroups[group] ? 1f : 0f;
		}
	}
}
