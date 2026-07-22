namespace KRAB.Graph
{
	/// <summary>A connection: output of node <c>fromId</c> into port <c>toPort</c> of node <c>toId</c>.</summary>
	public class KrabLink
	{
		public string fromId;
		public string toId;
		public int toPort;

		public ConfigNode Retained { get; private set; }

		public static KrabLink Load(ConfigNode node)
		{
			KrabLink link = new KrabLink
			{
				fromId = node.GetValue("from"),
				toId = node.GetValue("to"),
				Retained = node.CreateCopy()
			};
			node.TryGetValue("toPort", ref link.toPort);
			return link;
		}

		public ConfigNode Save()
		{
			ConfigNode copy = Retained != null ? Retained.CreateCopy() : new ConfigNode("LINK");
			copy.SetValue("from", fromId ?? "", true);
			copy.SetValue("to", toId ?? "", true);
			copy.SetValue("toPort", toPort, true);
			return copy;
		}

		public override string ToString()
		{
			return fromId + " -> " + toId + ":" + toPort;
		}
	}

	/// <summary>Explicit default value for an unconnected input port (validation requires one).</summary>
	public class KrabPortDefault
	{
		public string nodeId;
		public int port;
		public float value;

		public ConfigNode Retained { get; private set; }

		public static KrabPortDefault Load(ConfigNode node)
		{
			KrabPortDefault def = new KrabPortDefault
			{
				nodeId = node.GetValue("node"),
				Retained = node.CreateCopy()
			};
			node.TryGetValue("port", ref def.port);
			node.TryGetValue("value", ref def.value);
			return def;
		}

		public ConfigNode Save()
		{
			ConfigNode copy = Retained != null ? Retained.CreateCopy() : new ConfigNode("DEFAULT");
			copy.SetValue("node", nodeId ?? "", true);
			copy.SetValue("port", port, true);
			copy.SetValue("value", value, true);
			return copy;
		}
	}

	/// <summary>Editor layout for one node. Purely cosmetic: never affects evaluation.</summary>
	public class KrabNodeUi
	{
		public string nodeId;
		public float x;
		public float y;

		public ConfigNode Retained { get; private set; }

		public static KrabNodeUi Load(ConfigNode node)
		{
			KrabNodeUi ui = new KrabNodeUi
			{
				nodeId = node.GetValue("node"),
				Retained = node.CreateCopy()
			};
			node.TryGetValue("x", ref ui.x);
			node.TryGetValue("y", ref ui.y);
			return ui;
		}

		public ConfigNode Save()
		{
			ConfigNode copy = Retained != null ? Retained.CreateCopy() : new ConfigNode("UI");
			copy.SetValue("node", nodeId ?? "", true);
			copy.SetValue("x", x, true);
			copy.SetValue("y", y, true);
			return copy;
		}
	}
}
