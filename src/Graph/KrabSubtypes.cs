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

	/// <summary>Static description of a node subtype: family and input arity.</summary>
	public class SubtypeInfo
	{
		public readonly string name;
		public readonly NodeKind kind;

		/// <summary>Fixed input port count; -1 for dynamic-arity nodes.</summary>
		public readonly int fixedInputs;
		private readonly int minDynamicInputs;

		public bool HasDynamicInputs => fixedInputs < 0;
		public int MinInputs => fixedInputs < 0 ? minDynamicInputs : fixedInputs;

		public SubtypeInfo(string name, NodeKind kind, int fixedInputs, int minDynamicInputs = 0)
		{
			this.name = name;
			this.kind = kind;
			this.fixedInputs = fixedInputs;
			this.minDynamicInputs = minDynamicInputs;
		}
	}

	/// <summary>
	/// Registry of known node subtypes (see notes/catalogo-nodi.md). Lookup is
	/// tolerant: unknown names never throw — the caller keeps the node, disabled,
	/// so configs from newer mod versions survive a round-trip (design decision).
	/// </summary>
	public static class KrabSubtypes
	{
		private static readonly Dictionary<string, SubtypeInfo> registry =
			new Dictionary<string, SubtypeInfo>(StringComparer.OrdinalIgnoreCase);

		static KrabSubtypes()
		{
			// Sources
			Register(new SubtypeInfo("Constant", NodeKind.Source, 0));
			Register(new SubtypeInfo("ControllerInput", NodeKind.Source, 0));
			Register(new SubtypeInfo("PlayerAxis", NodeKind.Source, 0));
			Register(new SubtypeInfo("ScriptAxis", NodeKind.Source, 0));
			Register(new SubtypeInfo("PhysicalState", NodeKind.Source, 0));
			Register(new SubtypeInfo("ActionGroupState", NodeKind.Source, 0));
			// Operators
			Register(new SubtypeInfo("WeightedSum", NodeKind.Operator, -1, 1));
			Register(new SubtypeInfo("Product", NodeKind.Operator, -1, 2));
			Register(new SubtypeInfo("Min", NodeKind.Operator, 2));
			Register(new SubtypeInfo("Max", NodeKind.Operator, 2));
			Register(new SubtypeInfo("Remap", NodeKind.Operator, 1));
			Register(new SubtypeInfo("GatedBlend", NodeKind.Operator, 3));
			Register(new SubtypeInfo("Derivative", NodeKind.Operator, 1));
			Register(new SubtypeInfo("SlewRate", NodeKind.Operator, 1));
			Register(new SubtypeInfo("Comparator", NodeKind.Operator, 1));
			Register(new SubtypeInfo("Hold", NodeKind.Operator, 3));
			// Logic gates (boolean convention: >= 0.5 is true, outputs are exactly 0/1)
			Register(new SubtypeInfo("And", NodeKind.Operator, -1, 2));
			Register(new SubtypeInfo("Or", NodeKind.Operator, -1, 2));
			Register(new SubtypeInfo("Not", NodeKind.Operator, 1));
			Register(new SubtypeInfo("Xor", NodeKind.Operator, 2));
			// Outputs
			Register(new SubtypeInfo("AxisOutput", NodeKind.Output, 1));
			Register(new SubtypeInfo("ActionTrigger", NodeKind.Output, 1));
		}

		private static void Register(SubtypeInfo info)
		{
			registry.Add(info.name, info);
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
