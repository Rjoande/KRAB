using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KRAB.Graph
{
	/// <summary>
	/// Composite edit operations for the editor UI (M2). Each method mutates the
	/// graph through the KrabGraph primitives and keeps the invariants validation
	/// relies on (contiguous ports on dynamic operators, weights list in step with
	/// ports). Callers wrap every operation in the window's Mutate() so a snapshot
	/// is pushed for undo and the module re-persists/revalidates/recompiles after.
	/// </summary>
	public static class KrabGraphEdits
	{
		/// <summary>Operators offered by the group "combination" control.</summary>
		public static readonly string[] CycleableOperators =
		{
			"WeightedSum", "Product", "Min", "Max", "GatedBlend", "And", "Or", "Xor"
		};

		public static int CountInputPorts(KrabGraph graph, KrabNode node)
		{
			if (node.IsKnown && !node.Info.HasDynamicInputs)
			{
				return node.Info.fixedInputs;
			}
			int count = 0;
			foreach (KrabLink link in graph.Links)
			{
				if (link.toId == node.id && link.toPort + 1 > count)
				{
					count = link.toPort + 1;
				}
			}
			foreach (KrabPortDefault def in graph.Defaults)
			{
				if (def.nodeId == node.id && def.port + 1 > count)
				{
					count = def.port + 1;
				}
			}
			return count;
		}

		/// <summary>
		/// Operators whose arity matches the given term count, in CycleableOperators
		/// order (current subtype included, if it fits). Shared by CompatibleOperators
		/// (which strips the current one, for the "is there another option" check) and
		/// CycleOperator (which needs the current one's position to advance from it).
		/// </summary>
		private static List<string> ArityMatches(int terms)
		{
			List<string> result = new List<string>();
			foreach (string name in CycleableOperators)
			{
				SubtypeInfo info = KrabSubtypes.Find(name);
				if (info == null)
				{
					continue;
				}
				bool fits = info.HasDynamicInputs ? terms >= info.MinInputs : info.fixedInputs == terms;
				if (fits)
				{
					result.Add(name);
				}
			}
			return result;
		}

		/// <summary>Operators the group can switch to given its current term count.</summary>
		public static List<string> CompatibleOperators(KrabGraph graph, KrabNode group)
		{
			List<string> result = ArityMatches(CountInputPorts(graph, group));
			result.Remove(group.subtypeName);
			return result;
		}

		/// <summary>
		/// Switch a group's operator to the next arity-compatible one (cycle control).
		/// Advances from the current subtype's position in CycleableOperators order and
		/// wraps around, so repeated presses visit every compatible operator in turn
		/// (picking the first candidate every time, as an earlier version did, made
		/// WeightedSum/Product the only reachable pair since WeightedSum's MinInputs=1
		/// almost always fits and sits first in the array).
		///
		/// Also the escape hatch for a group that removing a term left below its own
		/// minimum arity (e.g. an And/Or/Product with only 1 term left after a
		/// removal, or generally any dynamic op whose MinInputs no longer holds):
		/// such a group's own subtype is absent from the arity-matching list
		/// (IndexOf returns -1), so this lands on the first compatible operator —
		/// typically WeightedSum, since it accepts any arity ≥ 1 — instead of
		/// refusing to act just because only one *other* option exists. In-game
		/// feedback (2026-07-06) asked for exactly this: harmonize the "stuck at a
		/// fixed arity" case (GatedBlend etc., already escapable — its own subtype
		/// is one of several arity matches) with the "stuck below minimum" case
		/// (previously required adding a term back instead of cycling out).
		/// </summary>
		public static bool CycleOperator(KrabGraph graph, KrabNode group)
		{
			List<string> candidates = ArityMatches(CountInputPorts(graph, group));
			if (candidates.Count == 0)
			{
				return false; // nothing at all fits this arity (shouldn't normally happen)
			}
			int index = candidates.IndexOf(group.subtypeName); // -1 when group.subtypeName no longer fits
			string next = candidates[(index + 1) % candidates.Count];
			if (next == group.subtypeName)
			{
				return false; // current is the only arity match: nothing to switch to
			}
			group.SetSubtype(next);
			return true;
		}

		/// <summary>Add a term (Constant 0 placeholder; the M3 picker will offer real sources).</summary>
		public static KrabNode AddTerm(KrabGraph graph, KrabNode group)
		{
			int port = CountInputPorts(graph, group);
			KrabNode source = KrabNode.Create(graph.NewNodeId(), "Constant");
			source.SetParam("value", 0f);
			graph.AddNode(source);
			graph.AddLink(source.id, group.id, port);
			return source;
		}

		/// <summary>
		/// Single/triple-input "shaping" operators that combine no terms of their own
		/// (Remap, Derivative, SlewRate, Comparator, Hold) — as opposed to the N-ary
		/// combiners in CycleableOperators. They're not reachable via the operator-cycle
		/// button (that rotation is for combination semantics, not arity alone) and the
		/// source picker only offers SOURCE-kind subtypes: without this list there was
		/// no way at all to add one from the editor (found while writing the fase-2
		/// test protocol, 2026-07-10 — a real gap, not a design choice).
		/// </summary>
		public static readonly string[] InsertableFilters = { "Remap", "Derivative", "SlewRate", "Comparator", "Hold" };

		/// <summary>
		/// Add a nested sub-group term of the given subtype (the "visual parentheses"),
		/// auto-filling however many ports it needs (its MinInputs if dynamic, else its
		/// fixed arity) with Constant(0) placeholders so it validates immediately —
		/// generalized 2026-07-10 from "always WeightedSum" so it also covers
		/// InsertableFilters (1 port for Remap/Derivative/SlewRate/Comparator, 3 for
		/// Hold) via the same mechanism.
		/// </summary>
		public static KrabNode AddSubgroup(KrabGraph graph, KrabNode group, string subtype = "WeightedSum")
		{
			int port = CountInputPorts(graph, group);
			KrabNode sub = KrabNode.Create(graph.NewNodeId(), subtype);
			graph.AddNode(sub);
			graph.AddLink(sub.id, group.id, port);
			FillRequiredPorts(graph, sub);
			return sub;
		}

		/// <summary>
		/// Fills a brand-new node's required ports (0..N-1) with Constant(0)
		/// placeholders, using explicit indices rather than AddTerm/CountInputPorts.
		///
		/// Bug fixed 2026-07-10 (in-game feedback: Hold showed "0,0,0" with no picker
		/// on any port; same for Derivative/SlewRate/Comparator): CountInputPorts on a
		/// FIXED-arity node always returns its fixed arity (e.g. 3 for Hold),
		/// regardless of how many ports are already linked — correct for its original
		/// purpose (the expected total), wrong when AddTerm used it to mean "the next
		/// empty slot". Looping AddTerm to fill a fresh Hold therefore linked all 3
		/// placeholders at the SAME phantom port index 3 (one past the valid 0..2
		/// range), leaving ports 0-2 completely unlinked. Filling by explicit index
		/// here sidesteps the ambiguity entirely: on a brand-new node there is no
		/// "already filled" state to account for.
		/// </summary>
		private static void FillRequiredPorts(KrabGraph graph, KrabNode node)
		{
			SubtypeInfo info = node.Info;
			int neededPorts = info != null ? (info.HasDynamicInputs ? info.MinInputs : info.fixedInputs) : 1;
			for (int p = 0; p < neededPorts; p++)
			{
				KrabNode placeholder = KrabNode.Create(graph.NewNodeId(), "Constant");
				placeholder.SetParam("value", 0f);
				graph.AddNode(placeholder);
				graph.AddLink(placeholder.id, node.id, p);
			}
		}

		/// <summary>
		/// Remove the term at a port of a dynamic group: unlink, prune the upstream
		/// subtree when it feeds nothing else, close the port gap (validation requires
		/// contiguity) and compact the weights list accordingly.
		/// </summary>
		public static void RemoveTerm(KrabGraph graph, KrabNode group, int port)
		{
			KrabLink link = graph.FindLinkTo(group.id, port);
			if (link != null)
			{
				graph.Links.Remove(link);
				KrabNode upstream = graph.FindNode(link.fromId);
				if (upstream != null)
				{
					PruneIfOrphan(graph, upstream);
				}
			}
			KrabPortDefault def = graph.FindDefault(group.id, port);
			if (def != null)
			{
				graph.Defaults.Remove(def);
			}
			foreach (KrabLink other in graph.Links)
			{
				if (other.toId == group.id && other.toPort > port)
				{
					other.toPort--;
				}
			}
			foreach (KrabPortDefault other in graph.Defaults)
			{
				if (other.nodeId == group.id && other.port > port)
				{
					other.port--;
				}
			}
			CompactWeights(group, port);
		}

		/// <summary>Recursively remove a node (and its exclusive feeders) once nothing consumes it.</summary>
		private static void PruneIfOrphan(KrabGraph graph, KrabNode node)
		{
			foreach (KrabLink link in graph.Links)
			{
				if (link.fromId == node.id)
				{
					return; // still feeds something
				}
			}
			List<KrabNode> feeders = new List<KrabNode>();
			foreach (KrabLink link in graph.Links)
			{
				if (link.toId == node.id)
				{
					KrabNode feeder = graph.FindNode(link.fromId);
					if (feeder != null)
					{
						feeders.Add(feeder);
					}
				}
			}
			graph.RemoveNode(node);
			foreach (KrabNode feeder in feeders)
			{
				PruneIfOrphan(graph, feeder);
			}
		}

		private static void CompactWeights(KrabNode group, int removedPort)
		{
			if (!group.HasParam("weights"))
			{
				return;
			}
			float[] weights = group.GetFloats("weights");
			if (removedPort >= weights.Length)
			{
				return;
			}
			StringBuilder builder = new StringBuilder();
			for (int i = 0; i < weights.Length; i++)
			{
				if (i == removedPort)
				{
					continue;
				}
				builder.Append(builder.Length > 0 ? ", " : "")
					.Append(weights[i].ToString(CultureInfo.InvariantCulture));
			}
			group.SetParam("weights", builder.ToString());
		}

		/// <summary>New output node; port 0 gets an explicit DEFAULT so the graph stays valid.</summary>
		public static KrabNode AddOutput(KrabGraph graph, bool axis)
		{
			KrabNode output = KrabNode.Create(graph.NewNodeId(), axis ? "AxisOutput" : "ActionTrigger");
			if (!axis)
			{
				output.SetParam("edge", "rising");
			}
			graph.AddNode(output);
			graph.SetDefault(output.id, 0, 0f);
			return output;
		}

		/// <summary>
		/// Start a tree on an empty input port (e.g. a fresh output): replaces the
		/// port DEFAULT with a new WeightedSum group holding one placeholder term.
		/// </summary>
		public static KrabNode ConnectNewGroup(KrabGraph graph, KrabNode target, int port)
		{
			KrabPortDefault def = graph.FindDefault(target.id, port);
			if (def != null)
			{
				graph.Defaults.Remove(def);
			}
			KrabNode group = KrabNode.Create(graph.NewNodeId(), "WeightedSum");
			graph.AddNode(group);
			graph.AddLink(group.id, target.id, port);
			AddTerm(graph, group);
			return group;
		}

		/// <summary>Remove an output node; the feeding subtree is pruned when exclusive.</summary>
		public static void RemoveOutput(KrabGraph graph, KrabNode output)
		{
			KrabLink link = graph.FindLinkTo(output.id, 0);
			KrabNode upstream = link != null ? graph.FindNode(link.fromId) : null;
			graph.RemoveNode(output);
			if (upstream != null)
			{
				PruneIfOrphan(graph, upstream);
			}
		}

		// ---- source picker support (M3) ----

		/// <summary>Unlink whatever feeds this port (pruning an exclusive subtree) without moving other ports.</summary>
		private static void ClearPort(KrabGraph graph, KrabNode target, int port)
		{
			KrabLink link = graph.FindLinkTo(target.id, port);
			if (link != null)
			{
				graph.Links.Remove(link);
				KrabNode upstream = graph.FindNode(link.fromId);
				if (upstream != null)
				{
					PruneIfOrphan(graph, upstream);
				}
			}
			KrabPortDefault def = graph.FindDefault(target.id, port);
			if (def != null)
			{
				graph.Defaults.Remove(def);
			}
		}

		/// <summary>Replace the source feeding a port with a fresh node of the given subtype.</summary>
		public static KrabNode ReplaceTermWithSource(KrabGraph graph, KrabNode target, int port, string subtype)
		{
			ClearPort(graph, target, port);
			KrabNode source = KrabNode.Create(graph.NewNodeId(), subtype);
			graph.AddNode(source);
			graph.AddLink(source.id, target.id, port);
			return source;
		}

		/// <summary>
		/// Replace whatever feeds a port with a fresh operator (dynamic combiner or one
		/// of InsertableFilters), auto-filling however many ports it needs — the nested
		/// counterpart of AddSubgroup/AddTerm: those only let you APPEND a new term to a
		/// dynamic group, so a fixed-arity node's single port (e.g. SlewRate's, or
		/// Comparator's) had no way to become an operator itself rather than a plain
		/// source, since fixed-arity nodes never render "+Term/+Group/+Filter" on their
		/// own row. This is reachable from the SAME per-port picker every source leaf
		/// already opens, regardless of the parent's arity kind (2026-07-10, found
		/// while planning fase-2 testing: without it, chains like Remap→SlewRate or
		/// Comparator→Not — the latter already present in the hand-authored test
		/// graph — were simply not buildable in the editor).
		/// </summary>
		public static KrabNode ReplaceTermWithOperator(KrabGraph graph, KrabNode target, int port, string subtype)
		{
			ClearPort(graph, target, port);
			KrabNode node = KrabNode.Create(graph.NewNodeId(), subtype);
			graph.AddNode(node);
			graph.AddLink(node.id, target.id, port);
			FillRequiredPorts(graph, node);
			return node;
		}

		/// <summary>
		/// Feed a port from an already-existing node (signal reuse — the fan-out the
		/// tree view cannot draw twice). Refused when it would create a cycle, i.e.
		/// when the chosen node is downstream of the target.
		/// </summary>
		public static bool ReplaceTermWithExisting(KrabGraph graph, KrabNode target, int port, KrabNode existing)
		{
			if (existing == target || IsDownstream(graph, target, existing))
			{
				return false;
			}
			ClearPort(graph, target, port);
			graph.AddLink(existing.id, target.id, port);
			return true;
		}

		/// <summary>True when <paramref name="node"/> is reachable following links out of <paramref name="from"/>.</summary>
		public static bool IsDownstream(KrabGraph graph, KrabNode from, KrabNode node)
		{
			List<KrabNode> frontier = new List<KrabNode> { from };
			HashSet<KrabNode> seen = new HashSet<KrabNode> { from };
			while (frontier.Count > 0)
			{
				KrabNode current = frontier[frontier.Count - 1];
				frontier.RemoveAt(frontier.Count - 1);
				foreach (KrabLink link in graph.Links)
				{
					if (link.fromId != current.id)
					{
						continue;
					}
					KrabNode next = graph.FindNode(link.toId);
					if (next == node)
					{
						return true;
					}
					if (next != null && seen.Add(next))
					{
						frontier.Add(next);
					}
				}
			}
			return false;
		}

		/// <summary>
		/// Nodes offerable as reused signals for a port of <paramref name="target"/>:
		/// sources and operators (not outputs), excluding the target itself, anything
		/// downstream of it (cycle), and whatever already feeds that port.
		/// </summary>
		public static List<KrabNode> ReusableSignals(KrabGraph graph, KrabNode target, int port)
		{
			KrabLink existing = graph.FindLinkTo(target.id, port);
			List<KrabNode> result = new List<KrabNode>();
			foreach (KrabNode node in graph.Nodes)
			{
				if (!node.IsKnown || node.Info.kind == NodeKind.Output || node == target)
				{
					continue;
				}
				if (existing != null && existing.fromId == node.id)
				{
					continue;
				}
				if (IsDownstream(graph, target, node))
				{
					continue;
				}
				result.Add(node);
			}
			return result;
		}

		// ---- copy/paste of a whole input/operator subtree across outputs ----

		private const string ClipboardNodeName = "KRAB_CLIPBOARD";
		private const string ClipboardRootKey = "rootId";

		/// <summary>All nodes reachable by following link wiring upstream from root
		/// (root included) — the whole tree of inputs/operators that feeds it.</summary>
		private static List<KrabNode> CollectUpstream(KrabGraph graph, KrabNode root)
		{
			List<KrabNode> result = new List<KrabNode>();
			HashSet<KrabNode> seen = new HashSet<KrabNode> { root };
			List<KrabNode> stack = new List<KrabNode> { root };
			while (stack.Count > 0)
			{
				KrabNode current = stack[stack.Count - 1];
				stack.RemoveAt(stack.Count - 1);
				result.Add(current);
				foreach (KrabLink link in graph.Links)
				{
					if (link.toId != current.id)
					{
						continue;
					}
					KrabNode from = graph.FindNode(link.fromId);
					if (from != null && seen.Add(from))
					{
						stack.Add(from);
					}
				}
			}
			return result;
		}

		/// <summary>
		/// Serializes the whole input/operator subtree feeding a port (an output's port
		/// 0, in practice) into a portable string — the clipboard for "replicate this
		/// combination on a different output tab" (in-game request, 2026-07-17). Original
		/// ids are kept in the clipboard text itself; PasteSubtree mints fresh ones on the
		/// way back in, so pasting never collides with the live graph. Null when the port
		/// has nothing wired yet (an empty/default-only port has nothing to copy).
		/// </summary>
		public static string CopySubtree(KrabGraph graph, KrabNode target, int port)
		{
			KrabLink rootLink = graph.FindLinkTo(target.id, port);
			KrabNode root = rootLink != null ? graph.FindNode(rootLink.fromId) : null;
			if (root == null)
			{
				return null;
			}
			List<KrabNode> subtree = CollectUpstream(graph, root);
			HashSet<string> ids = new HashSet<string>();
			foreach (KrabNode n in subtree)
			{
				ids.Add(n.id);
			}
			ConfigNode clip = new ConfigNode(ClipboardNodeName);
			clip.AddValue(ClipboardRootKey, root.id);
			foreach (KrabNode n in subtree)
			{
				clip.AddNode(n.Save());
			}
			foreach (KrabLink link in graph.Links)
			{
				if (ids.Contains(link.fromId) && ids.Contains(link.toId))
				{
					clip.AddNode(link.Save());
				}
			}
			foreach (KrabPortDefault def in graph.Defaults)
			{
				if (ids.Contains(def.nodeId))
				{
					clip.AddNode(def.Save());
				}
			}
			return clip.ToString();
		}

		/// <summary>
		/// Clones a subtree copied by CopySubtree — fresh ids throughout, so pasting the
		/// same clipboard onto several outputs (or back onto the one it came from) always
		/// yields independent copies, never a shared/aliased subtree — and wires the
		/// clone into the port, replacing whatever fed it before (same ClearPort as the
		/// other Replace* operations). False when the clipboard is empty/unparseable.
		/// </summary>
		public static bool PasteSubtree(KrabGraph graph, KrabNode target, int port, string clipboardText)
		{
			if (string.IsNullOrEmpty(clipboardText))
			{
				return false;
			}
			ConfigNode clip = ConfigNode.Parse(clipboardText)?.GetNode(ClipboardNodeName);
			string rootId = clip?.GetValue(ClipboardRootKey);
			if (string.IsNullOrEmpty(rootId))
			{
				return false;
			}

			Dictionary<string, string> idMap = new Dictionary<string, string>();
			List<KrabNode> cloned = new List<KrabNode>();
			foreach (ConfigNode nodeCfg in clip.GetNodes("NODE"))
			{
				KrabNode source = KrabNode.Load(nodeCfg);
				string newId = graph.NewNodeId();
				idMap[source.id] = newId;
				source.id = newId;
				cloned.Add(source);
			}
			foreach (KrabNode n in cloned)
			{
				graph.AddNode(n);
			}
			foreach (ConfigNode linkCfg in clip.GetNodes("LINK"))
			{
				KrabLink link = KrabLink.Load(linkCfg);
				if (idMap.TryGetValue(link.fromId, out string newFrom) && idMap.TryGetValue(link.toId, out string newTo))
				{
					graph.AddLink(newFrom, newTo, link.toPort);
				}
			}
			foreach (ConfigNode defCfg in clip.GetNodes("DEFAULT"))
			{
				KrabPortDefault def = KrabPortDefault.Load(defCfg);
				if (idMap.TryGetValue(def.nodeId, out string newNodeId))
				{
					graph.SetDefault(newNodeId, def.port, def.value);
				}
			}
			if (!idMap.TryGetValue(rootId, out string newRootId))
			{
				return false;
			}
			ClearPort(graph, target, port);
			graph.AddLink(newRootId, target.id, port);
			return true;
		}
	}
}
