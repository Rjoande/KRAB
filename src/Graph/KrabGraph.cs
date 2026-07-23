using System.Collections.Generic;
using System.Text;

namespace KRAB.Graph
{
	/// <summary>
	/// The KRAB node graph: typed view over a KRAB_GRAPH ConfigNode with full
	/// round-trip preservation (every element keeps its original ConfigNode and
	/// only updates the values it understands on save).
	///
	/// Validation rules (design doc): acyclic, every input port of a known node
	/// covered by exactly one link or an explicit DEFAULT, at least one active
	/// output. Unknown subtypes are kept but disabled — never dropped.
	/// </summary>
	public class KrabGraph
	{
		public const string GraphNodeName = "KRAB_GRAPH";

		public int graphVersion = 1;
		public int nextNodeId = 1;

		public readonly List<KrabNode> Nodes = new List<KrabNode>();
		public readonly List<KrabLink> Links = new List<KrabLink>();
		public readonly List<KrabPortDefault> Defaults = new List<KrabPortDefault>();
		public readonly List<KrabNodeUi> UiLayout = new List<KrabNodeUi>();

		private readonly Dictionary<string, KrabNode> byId = new Dictionary<string, KrabNode>();
		private readonly List<ValidationIssue> loadIssues = new List<ValidationIssue>();

		public KrabNode FindNode(string id)
		{
			if (string.IsNullOrEmpty(id))
			{
				return null;
			}
			byId.TryGetValue(id, out KrabNode node);
			return node;
		}

		/// <summary>Mint a fresh node id. The counter is persisted so ids are never reused.</summary>
		public string NewNodeId()
		{
			return "n" + nextNodeId++;
		}

		// Mutation primitives (editor UI). They keep the id index coherent; callers
		// compose them in KrabGraphEdits and must end with the module's
		// NotifyGraphEdited (backup refresh + revalidation + recompilation).

		public void AddNode(KrabNode node)
		{
			Nodes.Add(node);
			if (!string.IsNullOrEmpty(node.id) && !byId.ContainsKey(node.id))
			{
				byId.Add(node.id, node);
			}
		}

		public void RemoveNode(KrabNode node)
		{
			Nodes.Remove(node);
			if (!string.IsNullOrEmpty(node.id) && byId.TryGetValue(node.id, out KrabNode indexed) && indexed == node)
			{
				byId.Remove(node.id);
			}
			Links.RemoveAll(l => l.fromId == node.id || l.toId == node.id);
			Defaults.RemoveAll(d => d.nodeId == node.id);
			UiLayout.RemoveAll(u => u.nodeId == node.id);
		}

		public KrabLink AddLink(string fromId, string toId, int toPort)
		{
			KrabLink link = KrabLink.Load(new ConfigNode("LINK"));
			link.fromId = fromId;
			link.toId = toId;
			link.toPort = toPort;
			Links.Add(link);
			return link;
		}

		public KrabLink FindLinkTo(string toId, int toPort)
		{
			for (int i = 0; i < Links.Count; i++)
			{
				if (Links[i].toId == toId && Links[i].toPort == toPort)
				{
					return Links[i];
				}
			}
			return null;
		}

		public KrabPortDefault FindDefault(string nodeId, int port)
		{
			for (int i = 0; i < Defaults.Count; i++)
			{
				if (Defaults[i].nodeId == nodeId && Defaults[i].port == port)
				{
					return Defaults[i];
				}
			}
			return null;
		}

		public KrabPortDefault SetDefault(string nodeId, int port, float value)
		{
			KrabPortDefault def = FindDefault(nodeId, port);
			if (def == null)
			{
				def = KrabPortDefault.Load(new ConfigNode("DEFAULT"));
				def.nodeId = nodeId;
				def.port = port;
				Defaults.Add(def);
			}
			def.value = value;
			return def;
		}

		public static KrabGraph Load(ConfigNode node)
		{
			KrabGraph graph = new KrabGraph();
			node.TryGetValue("graphVersion", ref graph.graphVersion);
			node.TryGetValue("nextNodeId", ref graph.nextNodeId);

			ConfigNode[] nodeNodes = node.GetNodes("NODE");
			for (int i = 0; i < nodeNodes.Length; i++)
			{
				KrabNode item = KrabNode.Load(nodeNodes[i]);
				graph.Nodes.Add(item);
				if (string.IsNullOrEmpty(item.id))
				{
					// Keep the node (round-trip promise) but flag it: links cannot reference it.
					graph.loadIssues.Add(new ValidationIssue(IssueSeverity.Error, "nodeMissingId",
						"NODE without id (subtype '" + item.subtypeName + "'), unreachable by links",
						null, item.subtypeName ?? ""));
				}
				else if (graph.byId.ContainsKey(item.id))
				{
					graph.loadIssues.Add(new ValidationIssue(IssueSeverity.Error, "nodeDuplicateId",
						"duplicate node id '" + item.id + "', links resolve to the first occurrence",
						null, item.id));
				}
				else
				{
					graph.byId.Add(item.id, item);
				}
			}

			ConfigNode[] linkNodes = node.GetNodes("LINK");
			for (int i = 0; i < linkNodes.Length; i++)
			{
				graph.Links.Add(KrabLink.Load(linkNodes[i]));
			}
			ConfigNode[] defaultNodes = node.GetNodes("DEFAULT");
			for (int i = 0; i < defaultNodes.Length; i++)
			{
				graph.Defaults.Add(KrabPortDefault.Load(defaultNodes[i]));
			}
			ConfigNode[] uiNodes = node.GetNodes("UI");
			for (int i = 0; i < uiNodes.Length; i++)
			{
				graph.UiLayout.Add(KrabNodeUi.Load(uiNodes[i]));
			}
			return graph;
		}

		public ConfigNode Save()
		{
			ConfigNode node = new ConfigNode(GraphNodeName);
			node.AddValue("graphVersion", graphVersion);
			node.AddValue("nextNodeId", nextNodeId);
			for (int i = 0; i < Nodes.Count; i++)
			{
				node.AddNode(Nodes[i].Save());
			}
			for (int i = 0; i < Links.Count; i++)
			{
				node.AddNode(Links[i].Save());
			}
			for (int i = 0; i < Defaults.Count; i++)
			{
				node.AddNode(Defaults[i].Save());
			}
			for (int i = 0; i < UiLayout.Count; i++)
			{
				node.AddNode(UiLayout[i].Save());
			}
			return node;
		}

		public List<ValidationIssue> Validate()
		{
			List<ValidationIssue> issues = new List<ValidationIssue>(loadIssues);

			// Unknown subtypes: node kept but disabled.
			for (int i = 0; i < Nodes.Count; i++)
			{
				KrabNode n = Nodes[i];
				if (n.IsKnown)
				{
					continue;
				}
				SubtypeInfo elsewhere = KrabSubtypes.Find(n.subtypeName);
				if (elsewhere != null)
				{
					issues.Add(new ValidationIssue(IssueSeverity.Warning, "nodeWrongKind",
						"node '" + n.id + "' disabled: subtype '" + n.subtypeName + "' belongs to kind "
							+ elsewhere.kind + ", not '" + n.typeName + "'", n.id,
						n.id, n.subtypeName ?? "", elsewhere.kind.ToString(), n.typeName ?? ""));
				}
				else
				{
					issues.Add(new ValidationIssue(IssueSeverity.Warning, "nodeUnknownSubtype",
						"node '" + n.id + "' disabled: unknown subtype '" + n.subtypeName + "' (newer mod version?)", n.id,
						n.id, n.subtypeName ?? ""));
				}
			}

			// Port occupancy per node: port index -> covered by link (true) or default (false).
			Dictionary<KrabNode, Dictionary<int, bool>> ports = new Dictionary<KrabNode, Dictionary<int, bool>>();
			// Adjacency for cycle/reachability checks, resolved endpoints only.
			Dictionary<KrabNode, List<KrabNode>> outgoing = new Dictionary<KrabNode, List<KrabNode>>();
			Dictionary<KrabNode, List<KrabNode>> incoming = new Dictionary<KrabNode, List<KrabNode>>();

			for (int i = 0; i < Links.Count; i++)
			{
				KrabLink link = Links[i];
				KrabNode from = FindNode(link.fromId);
				KrabNode to = FindNode(link.toId);
				if (from == null || to == null)
				{
					issues.Add(new ValidationIssue(IssueSeverity.Error, "linkDangling",
						"link " + link + " references a missing node", null, link.ToString()));
					continue;
				}
				if (from.IsKnown && from.Info.kind == NodeKind.Output)
				{
					issues.Add(new ValidationIssue(IssueSeverity.Error, "linkFromOutput",
						"link " + link + " starts from an OUTPUT node, which has no output port", null, link.ToString()));
					continue;
				}
				if (to.IsKnown && to.Info.kind == NodeKind.Source)
				{
					issues.Add(new ValidationIssue(IssueSeverity.Error, "linkIntoSource",
						"link " + link + " targets a SOURCE node, which has no input ports", null, link.ToString()));
					continue;
				}
				if (link.toPort < 0)
				{
					issues.Add(new ValidationIssue(IssueSeverity.Error, "linkBadPort",
						"link " + link + " has a negative port index", null, link.ToString()));
					continue;
				}
				if (!ports.TryGetValue(to, out Dictionary<int, bool> occupied))
				{
					occupied = new Dictionary<int, bool>();
					ports.Add(to, occupied);
				}
				if (occupied.ContainsKey(link.toPort))
				{
					issues.Add(new ValidationIssue(IssueSeverity.Error, "portMultipleLinks",
						"node '" + to.id + "' port " + link.toPort + " has more than one incoming link", to.id,
						to.id, link.toPort.ToString()));
					continue;
				}
				occupied.Add(link.toPort, true);
				AddEdge(outgoing, from, to);
				AddEdge(incoming, to, from);
			}

			for (int i = 0; i < Defaults.Count; i++)
			{
				KrabPortDefault def = Defaults[i];
				KrabNode target = FindNode(def.nodeId);
				if (target == null)
				{
					issues.Add(new ValidationIssue(IssueSeverity.Error, "defaultDangling",
						"DEFAULT for missing node '" + def.nodeId + "'", null, def.nodeId ?? ""));
					continue;
				}
				if (!ports.TryGetValue(target, out Dictionary<int, bool> occupied))
				{
					occupied = new Dictionary<int, bool>();
					ports.Add(target, occupied);
				}
				if (occupied.TryGetValue(def.port, out bool viaLink))
				{
					issues.Add(new ValidationIssue(viaLink ? IssueSeverity.Warning : IssueSeverity.Error,
						viaLink ? "defaultShadowed" : "defaultDuplicate",
						"node '" + target.id + "' port " + def.port + (viaLink
							? " has both a link and a DEFAULT; the DEFAULT is ignored"
							: " has more than one DEFAULT"), target.id,
						target.id, def.port.ToString()));
					continue;
				}
				occupied.Add(def.port, false);
			}

			// Arity: every input port of a known non-source node needs a link or a DEFAULT.
			for (int i = 0; i < Nodes.Count; i++)
			{
				KrabNode n = Nodes[i];
				if (!n.IsKnown || n.Info.kind == NodeKind.Source)
				{
					continue;
				}
				ports.TryGetValue(n, out Dictionary<int, bool> occupied);
				int covered = occupied != null ? occupied.Count : 0;
				int expected;
				if (n.Info.HasDynamicInputs)
				{
					int maxIndex = -1;
					if (occupied != null)
					{
						foreach (int port in occupied.Keys)
						{
							if (port > maxIndex)
							{
								maxIndex = port;
							}
						}
					}
					expected = maxIndex + 1;
					if (covered < n.Info.MinInputs)
					{
						issues.Add(new ValidationIssue(IssueSeverity.Error, "tooFewInputs",
							"node '" + n.id + "' (" + n.subtypeName + ") has " + covered + " input(s), needs at least " + n.Info.MinInputs, n.id,
							n.id, n.subtypeName ?? "", covered.ToString(), n.Info.MinInputs.ToString()));
						continue;
					}
				}
				else
				{
					expected = n.Info.fixedInputs;
				}
				for (int port = 0; port < expected; port++)
				{
					if (occupied == null || !occupied.ContainsKey(port))
					{
						issues.Add(new ValidationIssue(IssueSeverity.Error, "portUnconnected",
							"node '" + n.id + "' (" + n.subtypeName + ") port " + port + " has no link and no DEFAULT", n.id,
							n.id, n.subtypeName ?? "", port.ToString()));
					}
				}
			}

			DetectCycles(outgoing, issues);

			// At least one output whose input is covered, otherwise the graph is inert.
			bool anyActiveOutput = false;
			for (int i = 0; i < Nodes.Count && !anyActiveOutput; i++)
			{
				KrabNode n = Nodes[i];
				anyActiveOutput = n.IsKnown && n.Info.kind == NodeKind.Output
					&& ports.TryGetValue(n, out Dictionary<int, bool> occupied) && occupied.ContainsKey(0);
			}
			if (!anyActiveOutput)
			{
				issues.Add(new ValidationIssue(IssueSeverity.Warning, "noActiveOutput",
					"no connected output node: the graph drives nothing", null));
			}

			// Nodes that feed no output do no harm, but flag them for the player.
			HashSet<KrabNode> feedingOutput = new HashSet<KrabNode>();
			Queue<KrabNode> frontier = new Queue<KrabNode>();
			for (int i = 0; i < Nodes.Count; i++)
			{
				if (Nodes[i].IsKnown && Nodes[i].Info.kind == NodeKind.Output)
				{
					feedingOutput.Add(Nodes[i]);
					frontier.Enqueue(Nodes[i]);
				}
			}
			while (frontier.Count > 0)
			{
				if (!incoming.TryGetValue(frontier.Dequeue(), out List<KrabNode> feeders))
				{
					continue;
				}
				for (int i = 0; i < feeders.Count; i++)
				{
					if (feedingOutput.Add(feeders[i]))
					{
						frontier.Enqueue(feeders[i]);
					}
				}
			}
			for (int i = 0; i < Nodes.Count; i++)
			{
				KrabNode n = Nodes[i];
				if (n.IsKnown && n.Info.kind != NodeKind.Output && !feedingOutput.Contains(n))
				{
					issues.Add(new ValidationIssue(IssueSeverity.Info, "nodeOrphan",
						"node '" + n.id + "' (" + n.subtypeName + ") feeds no output", n.id,
						n.id, n.subtypeName ?? ""));
				}
			}

			return issues;
		}

		private static void AddEdge(Dictionary<KrabNode, List<KrabNode>> adjacency, KrabNode key, KrabNode value)
		{
			if (!adjacency.TryGetValue(key, out List<KrabNode> list))
			{
				list = new List<KrabNode>();
				adjacency.Add(key, list);
			}
			list.Add(value);
		}

		private void DetectCycles(Dictionary<KrabNode, List<KrabNode>> outgoing, List<ValidationIssue> issues)
		{
			// Iterative DFS with three-state marking; reports one representative cycle.
			Dictionary<KrabNode, int> state = new Dictionary<KrabNode, int>(); // 1 = on stack, 2 = done
			for (int i = 0; i < Nodes.Count; i++)
			{
				if (state.ContainsKey(Nodes[i]))
				{
					continue;
				}
				List<KrabNode> stack = new List<KrabNode> { Nodes[i] };
				List<int> childIndex = new List<int> { 0 };
				state[Nodes[i]] = 1;
				while (stack.Count > 0)
				{
					KrabNode current = stack[stack.Count - 1];
					List<KrabNode> children = outgoing.TryGetValue(current, out List<KrabNode> c) ? c : null;
					int index = childIndex[childIndex.Count - 1];
					if (children != null && index < children.Count)
					{
						childIndex[childIndex.Count - 1] = index + 1;
						KrabNode child = children[index];
						if (!state.TryGetValue(child, out int childState))
						{
							state[child] = 1;
							stack.Add(child);
							childIndex.Add(0);
						}
						else if (childState == 1)
						{
							string cycle = DescribeCycle(stack, child);
							issues.Add(new ValidationIssue(IssueSeverity.Error, "graphCycle",
								"cycle detected: " + cycle, null, cycle));
							return; // one report is enough; the graph is invalid anyway
						}
					}
					else
					{
						state[current] = 2;
						stack.RemoveAt(stack.Count - 1);
						childIndex.RemoveAt(childIndex.Count - 1);
					}
				}
			}
		}

		private static string DescribeCycle(List<KrabNode> stack, KrabNode repeated)
		{
			StringBuilder builder = new StringBuilder();
			int start = stack.IndexOf(repeated);
			for (int i = start; i < stack.Count; i++)
			{
				builder.Append(stack[i].id).Append(" -> ");
			}
			builder.Append(repeated.id);
			return builder.ToString();
		}
	}
}
