using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace KRAB
{
	/// <summary>
	/// Promotes selected plain [KSPField] float fields to BaseAxisField at runtime so
	/// they become bindable in the Axis Groups menu — and, as a side effect, valid
	/// targets for KAL and KRAB controllers (BaseAxisField.CreateAxisList picks them up).
	///
	/// Stock parts hardcode which fields are [KSPAxisField]; ModuleManager cannot add
	/// C# attributes. The stock game itself "promotes" attributed fields in
	/// AxisGroupsManager.BuildBaseAxisFields (called from PartModule.Awake), replacing
	/// the BaseField in pm.Fields with a BaseAxisField over the same FieldInfo. This
	/// postfix does the same for config-selected fields, at the same timing — which
	/// means the stock AXISGROUPS save/load (AxisGroupsManager.Save/LoadAxisFieldNodes)
	/// persists the player's bindings with no extra code.
	///
	/// Rules come from KRAB_AXIS_PROMOTION { PROMOTE { ... } } config nodes, so other
	/// mods (or players) can extend the list via ModuleManager.
	/// </summary>
	[KSPAddon(KSPAddon.Startup.Instantly, true)]
	public class AxisPromoter : MonoBehaviour
	{
		private class PromotionRule
		{
			public string moduleName;
			public string fieldName;
			public float minValue;
			public float maxValue = 100f;
			// Stock percent sliders (e.g. ModuleEngines.thrustPercentage) use 20
			public float incrementalSpeed = 20f;
			public KSPAxisMode axisMode = KSPAxisMode.Incremental;

			public Type moduleType;   // resolved on load; null = rule disabled
			public bool announced;    // log the first successful promotion only
		}

		private static List<PromotionRule> rules;

		private void Awake()
		{
			Harmony harmony = new Harmony("KRAB9000.AxisPromoter");
			harmony.Patch(
				AccessTools.Method(typeof(AxisGroupsManager), nameof(AxisGroupsManager.BuildBaseAxisFields)),
				postfix: new HarmonyMethod(typeof(AxisPromoter), nameof(BuildBaseAxisFields_Postfix)));
			Debug.Log("[KRAB] AxisPromoter: patch on AxisGroupsManager.BuildBaseAxisFields applied");
		}

		private static void LoadRules()
		{
			rules = new List<PromotionRule>();
			ConfigNode[] configNodes = GameDatabase.Instance.GetConfigNodes("KRAB_AXIS_PROMOTION");
			for (int i = 0; i < configNodes.Length; i++)
			{
				ConfigNode[] promoteNodes = configNodes[i].GetNodes("PROMOTE");
				for (int j = 0; j < promoteNodes.Length; j++)
				{
					ConfigNode node = promoteNodes[j];
					PromotionRule rule = new PromotionRule
					{
						moduleName = node.GetValue("module"),
						fieldName = node.GetValue("field")
					};
					if (string.IsNullOrEmpty(rule.moduleName) || string.IsNullOrEmpty(rule.fieldName))
					{
						Debug.LogWarning("[KRAB] AxisPromoter: PROMOTE node without module/field, skipped");
						continue;
					}
					node.TryGetValue("minValue", ref rule.minValue);
					node.TryGetValue("maxValue", ref rule.maxValue);
					node.TryGetValue("incrementalSpeed", ref rule.incrementalSpeed);
					string modeName = node.GetValue("axisMode");
					if (!string.IsNullOrEmpty(modeName))
					{
						try
						{
							rule.axisMode = (KSPAxisMode)Enum.Parse(typeof(KSPAxisMode), modeName, true);
						}
						catch (ArgumentException)
						{
							Debug.LogWarningFormat("[KRAB] AxisPromoter: unknown axisMode '{0}' for {1}.{2}, using Incremental", modeName, rule.moduleName, rule.fieldName);
						}
					}
					// Type matching (IsInstanceOfType below) covers subclasses too,
					// e.g. a ModuleRCS rule also promotes ModuleRCSFX.
					rule.moduleType = AssemblyLoader.GetClassByName(typeof(PartModule), rule.moduleName);
					if (rule.moduleType == null)
					{
						Debug.LogWarningFormat("[KRAB] AxisPromoter: module type '{0}' not found, rule disabled", rule.moduleName);
					}
					rules.Add(rule);
				}
			}
			Debug.LogFormat("[KRAB] AxisPromoter: {0} promotion rule(s) loaded", rules.Count);
		}

		private static void BuildBaseAxisFields_Postfix(PartModule pm)
		{
			if (rules == null)
			{
				// Part prefabs compile only after the config database is ready, so the
				// first call through here can safely read the rules.
				if (GameDatabase.Instance == null || !GameDatabase.Instance.IsReady())
				{
					return;
				}
				LoadRules();
			}
			for (int i = 0; i < rules.Count; i++)
			{
				PromotionRule rule = rules[i];
				if (rule.moduleType != null && rule.moduleType.IsInstanceOfType(pm))
				{
					Promote(pm, rule);
				}
			}
		}

		private static void Promote(PartModule pm, PromotionRule rule)
		{
			BaseField field = pm.Fields[rule.fieldName];
			if (field == null || field is BaseAxisField)
			{
				return; // field absent, already an axis field, or already promoted
			}
			if (field.FieldInfo.FieldType != typeof(float))
			{
				if (!rule.announced)
				{
					rule.announced = true;
					Debug.LogWarningFormat("[KRAB] AxisPromoter: {0}.{1} is not a float field, rule disabled", rule.moduleName, rule.fieldName);
					rule.moduleType = null;
				}
				return;
			}

			// Carry the original PAW presentation over; axis parameters come from the
			// rule. UI controls (UI_FloatRange etc.) live as attributes on the C# field
			// itself, so the BaseAxisField constructor rebuilds them unchanged.
			KSPField source = field.Attribute;
			KSPAxisField attribute = new KSPAxisField
			{
				minValue = rule.minValue,
				maxValue = rule.maxValue,
				incrementalSpeed = rule.incrementalSpeed,
				axisMode = rule.axisMode,
				guiName = source.guiName,
				guiActive = source.guiActive,
				guiActiveEditor = source.guiActiveEditor,
				guiActiveUnfocused = source.guiActiveUnfocused,
				unfocusedRange = source.unfocusedRange,
				guiFormat = source.guiFormat,
				guiUnits = source.guiUnits,
				isPersistant = source.isPersistant,
				category = source.category,
				advancedTweakable = source.advancedTweakable,
				groupName = source.groupName,
				groupDisplayName = source.groupDisplayName,
				groupStartCollapsed = source.groupStartCollapsed
			};

			for (int i = 0; i < pm.Fields.Count; i++)
			{
				if (ReferenceEquals(pm.Fields[i], field))
				{
					pm.Fields[i] = new BaseAxisField(attribute, field.FieldInfo, pm);
					if (!rule.announced)
					{
						rule.announced = true;
						Debug.LogFormat("[KRAB] AxisPromoter: promoting {0}.{1} to axis field", rule.moduleName, rule.fieldName);
					}
					return;
				}
			}
		}
	}
}
