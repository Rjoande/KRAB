using System.Collections.Generic;
using UnityEngine;

namespace KRAB.Graph.Evaluation
{
	/// <summary>
	/// Drives a target BaseAxisField exactly the way KAL does: the input signal is
	/// remapped from [inMin, inMax] onto the target's limits (softLimits when the
	/// module exposes them) and queued through RoboticControllerManager, so priority
	/// arbitration with other KAL/KRAB controllers is inherited. An unbound output
	/// (no persistentId yet) still evaluates: its value is visible in the PAW debug
	/// field, which is how graphs are exercised before the editor UI exists.
	/// </summary>
	public class AxisOutputRuntime : RuntimeNode
	{
		private uint persistentId;
		private uint moduleId;
		private string axisName;
		private float inMin;
		private float inMax;
		private bool applySymmetry;

		private BaseAxisField target;
		private readonly List<BaseAxisField> symmetryTargets = new List<BaseAxisField>();
		private float targetMin;
		private float targetMax;

		public bool IsBound => target != null;

		public override bool OnCompiled()
		{
			uint.TryParse(Definition.GetString("persistentId", "0"), out persistentId);
			uint.TryParse(Definition.GetString("moduleId", "0"), out moduleId);
			axisName = Definition.GetString("axisName", "");
			inMin = Definition.GetFloat("inMin", 0f);
			inMax = Definition.GetFloat("inMax", 1f);
			applySymmetry = Definition.GetBool("applySymmetry", true);
			return true;
		}

		/// <summary>Bind to the live part/module/field; same lookup rules as ControlledAxis.</summary>
		public void ResolveTarget(Vessel vessel)
		{
			target = null;
			symmetryTargets.Clear();
			if (persistentId == 0 || string.IsNullOrEmpty(axisName) || vessel == null)
			{
				return; // unbound by design (e.g. freshly authored output)
			}
			if (!FlightGlobals.FindLoadedPart(persistentId, out Part targetPart) || targetPart.vessel != vessel)
			{
				Debug.LogWarningFormat("[KRAB] output '{0}': target part {1} not found on vessel", Definition.id, persistentId);
				return;
			}
			PartModule targetModule = moduleId != 0 ? targetPart.Modules[moduleId] : null;
			if (targetModule != null)
			{
				target = targetModule.Fields[axisName] as BaseAxisField;
			}
			else
			{
				for (int i = 0; i < targetPart.Modules.Count && target == null; i++)
				{
					target = targetPart.Modules[i].Fields[axisName] as BaseAxisField;
				}
			}
			if (target == null)
			{
				Debug.LogWarningFormat("[KRAB] output '{0}': axis field '{1}' not found on part {2}",
					Definition.id, axisName, targetPart.name);
				return;
			}
			targetMin = target.minValue;
			targetMax = target.maxValue;
			if (target.module is IAxisFieldLimits limits && limits.HasAxisFieldLimit(axisName))
			{
				Vector2 soft = limits.GetAxisFieldLimit(axisName).softLimits;
				targetMin = soft.x;
				targetMax = soft.y;
			}
			if (applySymmetry && targetPart.symmetryCounterparts != null)
			{
				string className = target.module.ClassName;
				for (int i = 0; i < targetPart.symmetryCounterparts.Count; i++)
				{
					Part counterpart = targetPart.symmetryCounterparts[i];
					if (counterpart.Modules.Contains(className)
						&& counterpart.Modules[className].Fields[axisName] is BaseAxisField counterpartField)
					{
						symmetryTargets.Add(counterpartField);
					}
				}
			}
		}

		public override void Evaluate(EvalContext ctx)
		{
			float value = In(0);
			Output = value;
			if (ctx.simulate || target == null)
			{
				return; // preview never writes to the vessel
			}
			float t = Mathf.InverseLerp(inMin, inMax, value);
			float targetValue = Mathf.Lerp(targetMin, targetMax, t);
			int priority = ctx.module.Priority;
			RoboticManagerBridge.QueueFieldUpdate(target, targetValue, priority);
			for (int i = 0; i < symmetryTargets.Count; i++)
			{
				RoboticManagerBridge.QueueFieldUpdate(symmetryTargets[i], targetValue, priority);
			}
		}
	}

	/// <summary>
	/// Fires a KSPAction on the rising/falling/both edge of its boolean input.
	/// Same firing guards as KAL (active, requireFullControl); minInterval is the
	/// internal anti-burst protection (persisted, not exposed in UI — decision).
	/// </summary>
	public class ActionTriggerRuntime : RuntimeNode
	{
		private static readonly KSPActionParam actionParam = new KSPActionParam(KSPActionGroup.None, KSPActionType.Toggle);

		private uint persistentId;
		private uint moduleId;
		private string actionName;
		private bool fireOnRising;
		private bool fireOnFalling;
		private float minInterval;

		private BaseAction action;
		private bool lastState;
		private bool primed;
		private float lastFireTime = float.MinValue;

		public bool IsBound => action != null;

		public override bool OnCompiled()
		{
			uint.TryParse(Definition.GetString("persistentId", "0"), out persistentId);
			uint.TryParse(Definition.GetString("moduleId", "0"), out moduleId);
			actionName = Definition.GetString("actionName", "");
			string edge = Definition.GetString("edge", "rising");
			fireOnRising = edge.Equals("rising", System.StringComparison.OrdinalIgnoreCase)
				|| edge.Equals("both", System.StringComparison.OrdinalIgnoreCase);
			fireOnFalling = edge.Equals("falling", System.StringComparison.OrdinalIgnoreCase)
				|| edge.Equals("both", System.StringComparison.OrdinalIgnoreCase);
			if (!fireOnRising && !fireOnFalling)
			{
				Debug.LogWarningFormat("[KRAB] trigger '{0}': unknown edge '{1}', node disabled", Definition.id, edge);
				return false;
			}
			minInterval = Mathf.Max(Definition.GetFloat("minInterval", 0.1f), 0f);
			return true;
		}

		public void ResolveTarget(Vessel vessel)
		{
			action = null;
			if (persistentId == 0 || string.IsNullOrEmpty(actionName) || vessel == null)
			{
				return;
			}
			if (!FlightGlobals.FindLoadedPart(persistentId, out Part targetPart) || targetPart.vessel != vessel)
			{
				Debug.LogWarningFormat("[KRAB] trigger '{0}': target part {1} not found on vessel", Definition.id, persistentId);
				return;
			}
			PartModule targetModule = moduleId != 0 ? targetPart.Modules[moduleId] : null;
			if (targetModule != null)
			{
				action = targetModule.Actions[actionName];
			}
			if (action == null)
			{
				action = targetPart.Actions[actionName];
			}
			for (int i = 0; i < targetPart.Modules.Count && action == null; i++)
			{
				action = targetPart.Modules[i].Actions[actionName];
			}
			if (action == null)
			{
				Debug.LogWarningFormat("[KRAB] trigger '{0}': action '{1}' not found on part {2}",
					Definition.id, actionName, targetPart.name);
			}
		}

		public override void Evaluate(EvalContext ctx)
		{
			bool state = AsBool(In(0));
			Output = state ? 1f : 0f;
			if (!primed)
			{
				primed = true;
				lastState = state;
				return; // never fire on the very first evaluation
			}
			bool rising = state && !lastState;
			bool falling = !state && lastState;
			lastState = state;
			if (ctx.simulate)
			{
				return; // preview shows the edge state but never fires actions
			}
			if (action == null || !(rising && fireOnRising || falling && fireOnFalling))
			{
				return;
			}
			if (ctx.time - lastFireTime < minInterval || !action.active)
			{
				return;
			}
			// Same gate KAL applies before invoking controlled actions.
			if (action.requireFullControl && InputLockManager.IsLocked(ControlTypes.TWEAKABLES_FULLONLY))
			{
				return;
			}
			lastFireTime = ctx.time;
			action.Invoke(actionParam);
		}
	}
}
