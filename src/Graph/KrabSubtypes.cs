using System;
using System.Collections.Generic;

namespace KRAB.Graph
{
	public enum NodeKind
	{
		Source,
		Operator,
		Output
	}

	/// <summary>Picker family of an operator; None for sources and outputs.</summary>
	public enum OperatorFamily
	{
		None,
		Math,
		Filter,
		Logic
	}

	/// <summary>Static description of a node subtype: family and input arity.</summary>
	public class SubtypeInfo
	{
		public readonly string name;
		public readonly NodeKind kind;
		public readonly OperatorFamily family;

		/// <summary>Fixed input port count; -1 for dynamic-arity nodes.</summary>
		public readonly int fixedInputs;
		private readonly int minDynamicInputs;

		public bool HasDynamicInputs => fixedInputs < 0;
		public int MinInputs => fixedInputs < 0 ? minDynamicInputs : fixedInputs;

		public SubtypeInfo(string name, NodeKind kind, int fixedInputs, int minDynamicInputs = 0,
			OperatorFamily family = OperatorFamily.None)
		{
			this.name = name;
			this.kind = kind;
			this.fixedInputs = fixedInputs;
			this.minDynamicInputs = minDynamicInputs;
			this.family = family;
		}
	}

	/// <summary>
	/// Registry of known node subtypes. Lookup is tolerant: unknown names never throw and
	/// the caller keeps the node, disabled, so configs from newer versions round-trip.
	/// </summary>
	public static class KrabSubtypes
	{
		private static readonly Dictionary<string, SubtypeInfo> registry =
			new Dictionary<string, SubtypeInfo>(StringComparer.OrdinalIgnoreCase);
		// Dictionary enumeration order is unspecified; the picker needs registration order.
		private static readonly List<SubtypeInfo> ordered = new List<SubtypeInfo>();

		static KrabSubtypes()
		{
			// Sources
			Register(new SubtypeInfo("Constant", NodeKind.Source, 0));
			Register(new SubtypeInfo("ControllerInput", NodeKind.Source, 0));
			Register(new SubtypeInfo("PlayerAxis", NodeKind.Source, 0));
			Register(new SubtypeInfo("ScriptAxis", NodeKind.Source, 0));
			Register(new SubtypeInfo("PhysicalState", NodeKind.Source, 0));
			Register(new SubtypeInfo("ActionGroupState", NodeKind.Source, 0));
			Register(new SubtypeInfo("KrillGroupState", NodeKind.Source, 0));
			Register(new SubtypeInfo("KrillAxisState", NodeKind.Source, 0));
			Register(new SubtypeInfo("PartField", NodeKind.Source, 0));
			// Operators, in picker order within each family.
			// Math: Divide/Power/Log never emit NaN or infinity (undefined cases output 0).
			Operator("WeightedSum", -1, OperatorFamily.Math, 1);
			Operator("Product", -1, OperatorFamily.Math, 2);
			Operator("Divide", 2, OperatorFamily.Math);
			Operator("Min", 2, OperatorFamily.Math);
			Operator("Max", 2, OperatorFamily.Math);
			Operator("Power", 2, OperatorFamily.Math);
			Operator("Log", 1, OperatorFamily.Math);
			Operator("Abs", 1, OperatorFamily.Math);
			// Trigonometry (degrees in/out, matching every other angle in KRAB — AoA, deployAngle...)
			Operator("Sin", 1, OperatorFamily.Math);
			Operator("Cos", 1, OperatorFamily.Math);
			Operator("Tan", 1, OperatorFamily.Math);
			Operator("Asin", 1, OperatorFamily.Math);
			Operator("Acos", 1, OperatorFamily.Math);
			Operator("Atan", 1, OperatorFamily.Math);
			Operator("Atan2", 2, OperatorFamily.Math);
			// Filters: shape a signal, most of them stateful.
			Operator("Remap", 1, OperatorFamily.Filter);
			Operator("GatedBlend", 3, OperatorFamily.Filter);
			Operator("Derivative", 1, OperatorFamily.Filter);
			Operator("Integrator", 2, OperatorFamily.Filter);
			Operator("SlewRate", 1, OperatorFamily.Filter);
			Operator("Hold", 3, OperatorFamily.Filter);
			// Logic (boolean convention: >= 0.5 is true, outputs are exactly 0/1).
			// Comparator lives here because it produces a boolean.
			Operator("Comparator", 1, OperatorFamily.Logic);
			Operator("And", -1, OperatorFamily.Logic, 2);
			Operator("Or", -1, OperatorFamily.Logic, 2);
			Operator("Not", 1, OperatorFamily.Logic);
			Operator("Xor", 2, OperatorFamily.Logic);
			// Outputs
			Register(new SubtypeInfo("AxisOutput", NodeKind.Output, 1));
			Register(new SubtypeInfo("ActionTrigger", NodeKind.Output, 1));
		}

		private static void Register(SubtypeInfo info)
		{
			registry.Add(info.name, info);
			ordered.Add(info);
		}

		private static void Operator(string name, int fixedInputs, OperatorFamily family, int minDynamicInputs = 0)
		{
			Register(new SubtypeInfo(name, NodeKind.Operator, fixedInputs, minDynamicInputs, family));
		}

		/// <summary>Every registered operator, in registration order.</summary>
		public static IEnumerable<SubtypeInfo> Operators()
		{
			foreach (SubtypeInfo info in ordered)
			{
				if (info.kind == NodeKind.Operator)
				{
					yield return info;
				}
			}
		}

		/// <summary>Operators of one picker family, in registration order.</summary>
		public static IEnumerable<SubtypeInfo> OperatorsIn(OperatorFamily family)
		{
			foreach (SubtypeInfo info in Operators())
			{
				if (info.family == family)
				{
					yield return info;
				}
			}
		}

		/// <summary>Look up a subtype regardless of declared kind (for diagnostics).</summary>
		public static SubtypeInfo Find(string subtypeName)
		{
			if (string.IsNullOrEmpty(subtypeName))
			{
				return null;
			}
			registry.TryGetValue(subtypeName, out SubtypeInfo info);
			return info;
		}

		/// <summary>
		/// Resolve a subtype for a node, requiring the declared kind to match.
		/// Returns null (node disabled, never dropped) for unknown or mismatched entries.
		/// </summary>
		public static SubtypeInfo Resolve(string kindName, string subtypeName)
		{
			SubtypeInfo info = Find(subtypeName);
			if (info == null || !TryParseKind(kindName, out NodeKind kind) || kind != info.kind)
			{
				return null;
			}
			return info;
		}

		public static bool TryParseKind(string kindName, out NodeKind kind)
		{
			switch (kindName != null ? kindName.ToUpperInvariant() : null)
			{
				case "SOURCE":
					kind = NodeKind.Source;
					return true;
				case "OPERATOR":
					kind = NodeKind.Operator;
					return true;
				case "OUTPUT":
					kind = NodeKind.Output;
					return true;
				default:
					kind = NodeKind.Source;
					return false;
			}
		}
	}
}
