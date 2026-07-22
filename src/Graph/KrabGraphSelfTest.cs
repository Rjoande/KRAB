using System.Collections.Generic;
using System.Text;

namespace KRAB.Graph
{
	/// <summary>
	/// In-game self-test for the graph data model, reachable from the PAW (advanced
	/// tweakables). Builds sample graphs in code — no test .cfg files in the mod
	/// folder — and checks parsing, validation and round-trip preservation.
	/// </summary>
	public static class KrabGraphSelfTest
	{
		public static bool Run(out string report)
		{
			StringBuilder log = new StringBuilder();
			bool pass = true;
			pass &= TestValidGraphRoundTrip(log);
			pass &= TestInvalidGraphDiagnostics(log);
			log.Append(pass ? "RESULT: PASS" : "RESULT: FAIL");
			report = log.ToString();
			return pass;
		}

		private static bool TestValidGraphRoundTrip(StringBuilder log)
		{
			ConfigNode source = BuildValidSample();
			KrabGraph graph = KrabGraph.Load(source);
			bool pass = true;

			List<ValidationIssue> issues = graph.Validate();
			pass &= Check(log, issues.Count == 0, "valid graph produces no issues",
				issues.Count + " issue(s): " + Join(issues));
			pass &= Check(log, graph.Nodes.Count == 4 && graph.Links.Count == 3 && graph.UiLayout.Count == 1,
				"valid graph element counts", graph.Nodes.Count + "/" + graph.Links.Count + "/" + graph.UiLayout.Count);

			ConfigNode saved = graph.Save();
			pass &= Check(log, saved.GetNodes("NODE").Length == 4 && saved.GetNodes("LINK").Length == 3,
				"round-trip keeps element counts", "nodes=" + saved.GetNodes("NODE").Length + " links=" + saved.GetNodes("LINK").Length);
			pass &= Check(log, saved.GetValue("nextNodeId") == "5", "round-trip keeps nextNodeId", saved.GetValue("nextNodeId"));

			ConfigNode n3 = FindNodeById(saved, "n3");
			pass &= Check(log, n3 != null && n3.GetValue("futureParam") == "42",
				"unknown value survives round-trip", n3 == null ? "n3 missing" : ("futureParam=" + n3.GetValue("futureParam")));
			pass &= Check(log, n3 != null && n3.GetValue("weights") == "1.0, -0.5",
				"known param survives round-trip", n3 == null ? "n3 missing" : ("weights=" + n3.GetValue("weights")));

			// A second load of the saved output must stay clean too.
			List<ValidationIssue> issues2 = KrabGraph.Load(saved).Validate();
			pass &= Check(log, issues2.Count == 0, "reloaded graph still validates clean", Join(issues2));
			return pass;
		}

		private static bool TestInvalidGraphDiagnostics(StringBuilder log)
		{
			ConfigNode source = BuildInvalidSample();
			KrabGraph graph = KrabGraph.Load(source);
			List<ValidationIssue> issues = graph.Validate();
			bool pass = true;

			pass &= Check(log, Has(issues, "portUnconnected", IssueSeverity.Error),
				"missing input port detected", Join(issues));
			pass &= Check(log, Has(issues, "graphCycle", IssueSeverity.Error),
				"cycle detected", Join(issues));
			pass &= Check(log, Has(issues, "nodeUnknownSubtype", IssueSeverity.Warning),
				"unknown subtype flagged as warning", Join(issues));
			pass &= Check(log, Has(issues, "noActiveOutput", IssueSeverity.Warning),
				"inert graph flagged", Join(issues));

			// The unknown-subtype node must survive a round-trip untouched.
			ConfigNode saved = graph.Save();
			ConfigNode unknown = FindNodeById(saved, "n5");
			pass &= Check(log, unknown != null && unknown.GetValue("subtype") == "FluxCapacitor",
				"unknown-subtype node retained on save", unknown == null ? "n5 missing" : unknown.GetValue("subtype"));
			return pass;
		}

		private static ConfigNode BuildValidSample()
		{
			ConfigNode g = new ConfigNode(KrabGraph.GraphNodeName);
			g.AddValue("graphVersion", 1);
			g.AddValue("nextNodeId", 5);
			AddNode(g, "n1", "SOURCE", "PlayerAxis").AddValue("channel", "Pitch");
			ConfigNode phys = AddNode(g, "n2", "SOURCE", "PhysicalState");
			phys.AddValue("metric", "SrfSpeed");
			phys.AddValue("sampleRate", "0.1");
			ConfigNode sum = AddNode(g, "n3", "OPERATOR", "WeightedSum");
			sum.AddValue("weights", "1.0, -0.5");
			sum.AddValue("futureParam", "42"); // simulates a value from a newer mod version
			ConfigNode output = AddNode(g, "n4", "OUTPUT", "AxisOutput");
			output.AddValue("axisName", "targetAngle");
			AddLink(g, "n1", "n3", 0);
			AddLink(g, "n2", "n3", 1);
			AddLink(g, "n3", "n4", 0);
			ConfigNode ui = g.AddNode("UI");
			ui.AddValue("node", "n3");
			ui.AddValue("x", "220");
			ui.AddValue("y", "60");
			return g;
		}

		private static ConfigNode BuildInvalidSample()
		{
			ConfigNode g = new ConfigNode(KrabGraph.GraphNodeName);
			g.AddValue("graphVersion", 1);
			g.AddValue("nextNodeId", 6);
			AddNode(g, "n1", "SOURCE", "Constant").AddValue("value", "0.5");
			AddNode(g, "n2", "OPERATOR", "Min");         // port 1 left unconnected
			AddNode(g, "n3", "OPERATOR", "WeightedSum"); // n3 <-> n4 cycle
			AddNode(g, "n4", "OPERATOR", "WeightedSum");
			AddNode(g, "n5", "OPERATOR", "FluxCapacitor"); // unknown subtype
			AddLink(g, "n1", "n2", 0);
			AddLink(g, "n3", "n4", 0);
			AddLink(g, "n4", "n3", 0);
			return g;
		}

		private static ConfigNode AddNode(ConfigNode graph, string id, string type, string subtype)
		{
			ConfigNode node = graph.AddNode("NODE");
			node.AddValue("id", id);
			node.AddValue("type", type);
			node.AddValue("subtype", subtype);
			return node;
		}

		private static void AddLink(ConfigNode graph, string from, string to, int toPort)
		{
			ConfigNode link = graph.AddNode("LINK");
			link.AddValue("from", from);
			link.AddValue("to", to);
			link.AddValue("toPort", toPort);
		}

		private static ConfigNode FindNodeById(ConfigNode graph, string id)
		{
			ConfigNode[] nodes = graph.GetNodes("NODE");
			for (int i = 0; i < nodes.Length; i++)
			{
				if (nodes[i].GetValue("id") == id)
				{
					return nodes[i];
				}
			}
			return null;
		}

		private static bool Has(List<ValidationIssue> issues, string code, IssueSeverity severity)
		{
			for (int i = 0; i < issues.Count; i++)
			{
				if (issues[i].code == code && issues[i].severity == severity)
				{
					return true;
				}
			}
			return false;
		}

		private static string Join(List<ValidationIssue> issues)
		{
			if (issues.Count == 0)
			{
				return "(none)";
			}
			StringBuilder builder = new StringBuilder();
			for (int i = 0; i < issues.Count; i++)
			{
				builder.Append(i > 0 ? "; " : "").Append(issues[i]);
			}
			return builder.ToString();
		}

		private static bool Check(StringBuilder log, bool condition, string what, string detail)
		{
			log.Append(condition ? "  ok   " : "  FAIL ").Append(what);
			if (!condition)
			{
				log.Append(" — ").Append(detail);
			}
			log.AppendLine();
			return condition;
		}
	}
}
