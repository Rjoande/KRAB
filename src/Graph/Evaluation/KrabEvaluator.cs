using System.Collections.Generic;
using UnityEngine;

namespace KRAB.Graph.Evaluation
{
	/// <summary>
	/// Compiles a validated KrabGraph into runtime nodes and evaluates them in
	/// topological order once per frame. Compilation refuses graphs with validation
	/// errors; nodes with bad parameters are disabled individually (output 0), the
	/// rest of the graph keeps running.
	/// </summary>
	public class KrabEvaluator
	{
		private RuntimeNode[] ordered;
		private readonly List<AxisOutputRuntime> axisOutputs = new List<AxisOutputRuntime>();
		private readonly List<ActionTriggerRuntime> triggers = new List<ActionTriggerRuntime>();
		private readonly Dictionary<KrabNode, RuntimeNode> byDefinition = new Dictionary<KrabNode, RuntimeNode>();

		/// <summary>Live output of a node, for UI telemetry. False when the node is unknown.</summary>
		public bool TryGetNodeOutput(KrabNode definition, out float value)
		{
			if (definition != null && byDefinition.TryGetValue(definition, out RuntimeNode runtime))
			{
				value = runtime.Output;
				return true;
			}
			value = 0f;
			return false;
		}

		public int NodeCount => ordered.Length;

		/// <summary>Last value of the first axis output — surfaced in the PAW debug field.</summary>
		public float FirstAxisOutputValue => axisOutputs.Count > 0 ? axisOutputs[0].Output : 0f;

		public int BoundAxisOutputs
		{
			get
			{
				int count = 0;
				for (int i = 0; i < axisOutputs.Count; i++)
				{
					if (axisOutputs[i].IsBound)
					{
						count++;
					}
				}
				return count;
			}
		}

		/// <summary>
		/// Compiles whatever the graph currently is — never refuses outright. Verified
		/// 2026-07-09 (root cause of an in-game report of live values freezing across
		/// the *entire* graph, including unrelated outputs, while a single group was
		/// mid-edit with too few terms): every validation-error case already degrades
		/// safely on its own — an unconnected/under-filled port reads its
		/// InputBinding's default `constant` (0f), a dangling link is skipped during
		/// wiring, and a cycle simply never reaches in-degree 0 in TopologicalOrder so
		/// its nodes are silently dropped from evaluation. Refusing the *whole* graph
		/// over one locally-broken group was strictly worse than what the runtime
		/// already handles gracefully node-by-node (the same policy bad parameters
		/// already get via OnCompiled() returning false). failReason is still
		/// populated (joined error messages) for logging even though compilation
		/// no longer aborts because of it.
		/// </summary>
		public static KrabEvaluator Compile(KrabGraph graph, out string failReason)
		{
			List<ValidationIssue> issues = graph.Validate();
			System.Text.StringBuilder errorLog = null;
			for (int i = 0; i < issues.Count; i++)
			{
				if (issues[i].severity != IssueSeverity.Error)
				{
					continue;
				}
				if (errorLog == null)
				{
					errorLog = new System.Text.StringBuilder();
				}
				errorLog.Append(errorLog.Length > 0 ? "; " : "").Append(issues[i].ToString());
			}
			failReason = errorLog?.ToString();

			KrabEvaluator evaluator = new KrabEvaluator();
			Dictionary<KrabNode, RuntimeNode> runtimeByDefinition = evaluator.byDefinition;
			foreach (KrabNode definition in graph.Nodes)
			{
				RuntimeNode runtime = CreateRuntime(definition);
				runtime.Definition = definition;
				runtimeByDefinition.Add(definition, runtime);
				if (runtime is AxisOutputRuntime axisOutput)
				{
					evaluator.axisOutputs.Add(axisOutput);
				}
				else if (runtime is ActionTriggerRuntime trigger)
				{
					evaluator.triggers.Add(trigger);
				}
			}

			// Wire input ports. Validation guarantees contiguity and no duplicates.
			Dictionary<RuntimeNode, List<KrabLink>> inbound = new Dictionary<RuntimeNode, List<KrabLink>>();
			foreach (KrabLink link in graph.Links)
			{
				KrabNode to = graph.FindNode(link.toId);
				if (to == null || !runtimeByDefinition.TryGetValue(to, out RuntimeNode runtime))
				{
					continue;
				}
				if (!inbound.TryGetValue(runtime, out List<KrabLink> list))
				{
					list = new List<KrabLink>();
					inbound.Add(runtime, list);
				}
				list.Add(link);
			}
			foreach (KeyValuePair<KrabNode, RuntimeNode> pair in runtimeByDefinition)
			{
				int portCount = 0;
				inbound.TryGetValue(pair.Value, out List<KrabLink> links);
				if (links != null)
				{
					for (int i = 0; i < links.Count; i++)
					{
						portCount = Mathf.Max(portCount, links[i].toPort + 1);
					}
				}
				foreach (KrabPortDefault def in graph.Defaults)
				{
					if (def.nodeId == pair.Key.id)
					{
						portCount = Mathf.Max(portCount, def.port + 1);
					}
				}
				if (pair.Key.IsKnown && !pair.Key.Info.HasDynamicInputs)
				{
					portCount = pair.Key.Info.fixedInputs;
				}
				pair.Value.Inputs = new InputBinding[portCount];
				foreach (KrabPortDefault def in graph.Defaults)
				{
					// port >= 0 guard: Validate() flags a negative link.toPort but not a
					// negative DEFAULT.port; only reachable via a hand-edited ConfigNode
					// (our own editor UI never emits one), but indexing Inputs[-1] would
					// throw, so it's cheap insurance while touching this code.
					if (def.nodeId == pair.Key.id && def.port >= 0 && def.port < portCount)
					{
						pair.Value.Inputs[def.port].constant = def.value;
					}
				}
				if (links != null)
				{
					for (int i = 0; i < links.Count; i++)
					{
						KrabNode from = graph.FindNode(links[i].fromId);
						if (from != null && links[i].toPort < portCount
							&& runtimeByDefinition.TryGetValue(from, out RuntimeNode upstream))
						{
							pair.Value.Inputs[links[i].toPort].upstream = upstream;
						}
					}
				}
			}

			evaluator.ordered = TopologicalOrder(graph, runtimeByDefinition);

			for (int i = 0; i < evaluator.ordered.Length; i++)
			{
				RuntimeNode node = evaluator.ordered[i];
				if (node.Enabled && !node.OnCompiled())
				{
					node.Enabled = false;
				}
			}
			failReason = null;
			return evaluator;
		}

		private static RuntimeNode[] TopologicalOrder(KrabGraph graph, Dictionary<KrabNode, RuntimeNode> runtimeByDefinition)
		{
			// Kahn's algorithm; the graph is guaranteed acyclic at this point.
			Dictionary<KrabNode, int> inDegree = new Dictionary<KrabNode, int>();
			Dictionary<KrabNode, List<KrabNode>> outgoing = new Dictionary<KrabNode, List<KrabNode>>();
			foreach (KrabNode node in graph.Nodes)
			{
				inDegree[node] = 0;
			}
			foreach (KrabLink link in graph.Links)
			{
				KrabNode from = graph.FindNode(link.fromId);
				KrabNode to = graph.FindNode(link.toId);
				if (from == null || to == null)
				{
					continue;
				}
				if (!outgoing.TryGetValue(from, out List<KrabNode> list))
				{
					list = new List<KrabNode>();
					outgoing.Add(from, list);
				}
				list.Add(to);
				inDegree[to]++;
			}
			Queue<KrabNode> ready = new Queue<KrabNode>();
			foreach (KrabNode node in graph.Nodes)
			{
				if (inDegree[node] == 0)
				{
					ready.Enqueue(node);
				}
			}
			List<RuntimeNode> result = new List<RuntimeNode>(graph.Nodes.Count);
			while (ready.Count > 0)
			{
				KrabNode node = ready.Dequeue();
				result.Add(runtimeByDefinition[node]);
				if (!outgoing.TryGetValue(node, out List<KrabNode> children))
				{
					continue;
				}
				for (int i = 0; i < children.Count; i++)
				{
					if (--inDegree[children[i]] == 0)
					{
						ready.Enqueue(children[i]);
					}
				}
			}
			return result.ToArray();
		}

		private static RuntimeNode CreateRuntime(KrabNode definition)
		{
			if (!definition.IsKnown)
			{
				return new DisabledRuntime();
			}
			switch (definition.Info.name)
			{
				case "Constant": return new ConstantRuntime();
				case "ControllerInput": return new ControllerInputRuntime();
				case "PlayerAxis": return new PlayerAxisRuntime();
				case "ScriptAxis": return new ScriptAxisRuntime();
				case "PhysicalState": return new PhysicalStateRuntime();
				case "ActionGroupState": return new ActionGroupStateRuntime();
				case "WeightedSum": return new WeightedSumRuntime();
				case "Product": return new ProductRuntime();
				case "Min": return new MinRuntime();
				case "Max": return new MaxRuntime();
				case "Remap": return new RemapRuntime();
				case "GatedBlend": return new GatedBlendRuntime();
				case "Derivative": return new DerivativeRuntime();
				case "SlewRate": return new SlewRateRuntime();
				case "Comparator": return new ComparatorRuntime();
				case "Hold": return new HoldRuntime();
				case "And": return new AndRuntime();
				case "Or": return new OrRuntime();
				case "Not": return new NotRuntime();
				case "Xor": return new XorRuntime();
				case "AxisOutput": return new AxisOutputRuntime();
				case "ActionTrigger": return new ActionTriggerRuntime();
				default:
					// Registry knows it but the factory does not: implementation drift.
					Debug.LogErrorFormat("[KRAB] no runtime for subtype '{0}', node disabled", definition.Info.name);
					return new DisabledRuntime();
			}
		}

		/// <summary>(Re)bind output targets to live parts; call on start and vessel changes.</summary>
		public void ResolveTargets(Vessel vessel)
		{
			for (int i = 0; i < axisOutputs.Count; i++)
			{
				axisOutputs[i].ResolveTarget(vessel);
			}
			for (int i = 0; i < triggers.Count; i++)
			{
				triggers[i].ResolveTarget(vessel);
			}
		}

		public void Run(EvalContext ctx)
		{
			for (int i = 0; i < ordered.Length; i++)
			{
				if (ordered[i].Enabled)
				{
					ordered[i].Evaluate(ctx);
				}
			}
		}
	}
}
