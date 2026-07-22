namespace KRAB.Graph
{
	/// <summary>
	/// A graph node. Typed members cover what this version of the mod understands;
	/// the full original ConfigNode is retained so unknown values written by newer
	/// versions survive load+save untouched (round-trip decision).
	/// </summary>
	public class KrabNode
	{
		public string id;
		public string typeName;
		public string subtypeName;

		/// <summary>Null when the subtype (or its kind) is unknown: node kept but disabled.</summary>
		public SubtypeInfo Info { get; private set; }

		public bool IsKnown => Info != null;

		/// <summary>Original ConfigNode, source of truth for unknown values.</summary>
		public ConfigNode Retained { get; private set; }

		public static KrabNode Load(ConfigNode node)
		{
			KrabNode result = new KrabNode
			{
				id = node.GetValue("id"),
				typeName = node.GetValue("type"),
				subtypeName = node.GetValue("subtype"),
				Retained = node.CreateCopy()
			};
			result.Info = KrabSubtypes.Resolve(result.typeName, result.subtypeName);
			return result;
		}

		/// <summary>Author a fresh node in code (editor UI). Kind must match the subtype.</summary>
		public static KrabNode Create(string id, string subtypeName)
		{
			SubtypeInfo info = KrabSubtypes.Find(subtypeName);
			string kindName = info != null ? info.kind.ToString().ToUpperInvariant() : "OPERATOR";
			KrabNode node = new KrabNode
			{
				id = id,
				typeName = kindName,
				subtypeName = subtypeName,
				Retained = new ConfigNode("NODE"),
				Info = info
			};
			node.Retained.AddValue("id", id);
			node.Retained.AddValue("type", kindName);
			node.Retained.AddValue("subtype", subtypeName);
			return node;
		}

		// Mutation API (editor UI). Every setter keeps Retained in sync so the
		// round-trip guarantee holds; callers must go through the module's
		// NotifyGraphEdited afterwards (persistence backup + revalidate + recompile).

		public void SetParam(string name, string value)
		{
			Retained.SetValue(name, value, true);
		}

		public void SetParam(string name, float value)
		{
			Retained.SetValue(name, value.ToString(System.Globalization.CultureInfo.InvariantCulture), true);
		}

		public void RemoveParam(string name)
		{
			Retained.RemoveValue(name);
		}

		/// <summary>Switch subtype in place (e.g. cycling a group's operator). Extra params are kept (round-trip).</summary>
		public void SetSubtype(string newSubtypeName)
		{
			SubtypeInfo info = KrabSubtypes.Find(newSubtypeName);
			subtypeName = newSubtypeName;
			if (info != null)
			{
				typeName = info.kind.ToString().ToUpperInvariant();
			}
			Info = KrabSubtypes.Resolve(typeName, subtypeName);
			Retained.SetValue("type", typeName ?? "", true);
			Retained.SetValue("subtype", subtypeName ?? "", true);
		}

		// Typed parameter accessors over the retained node. All tolerant: a missing
		// or malformed value yields the fallback, never an exception.

		public string GetParam(string name)
		{
			return Retained.GetValue(name);
		}

		public string GetString(string name, string fallback)
		{
			string value = Retained.GetValue(name);
			return string.IsNullOrEmpty(value) ? fallback : value;
		}

		public float GetFloat(string name, float fallback)
		{
			float value = fallback;
			Retained.TryGetValue(name, ref value);
			return value;
		}

		public int GetInt(string name, int fallback)
		{
			int value = fallback;
			Retained.TryGetValue(name, ref value);
			return value;
		}

		public bool GetBool(string name, bool fallback)
		{
			bool value = fallback;
			Retained.TryGetValue(name, ref value);
			return value;
		}

		public bool HasParam(string name)
		{
			return Retained.HasValue(name);
		}

		/// <summary>Parse a comma-separated float list (e.g. weights = 1.0, -0.5).</summary>
		public float[] GetFloats(string name)
		{
			string raw = Retained.GetValue(name);
			if (string.IsNullOrEmpty(raw))
			{
				return new float[0];
			}
			string[] parts = raw.Split(',');
			float[] values = new float[parts.Length];
			for (int i = 0; i < parts.Length; i++)
			{
				float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture, out values[i]);
			}
			return values;
		}

		// Sub-node accessors (M4): a curve is a nested ConfigNode (FloatCurve's own
		// `key = t v inTan outTan` format), not a flat value — everything above only
		// handles values. CreateCopy() in Save()/Load() already deep-copies sub-nodes
		// automatically, so once written here a curve round-trips for free.

		public ConfigNode GetNode(string name)
		{
			return Retained.GetNode(name);
		}

		public bool HasNode(string name)
		{
			return Retained.HasNode(name);
		}

		public void SetNode(string name, ConfigNode content)
		{
			Retained.RemoveNode(name);
			ConfigNode copy = content.CreateCopy();
			copy.name = name;
			Retained.AddNode(copy);
		}

		public void RemoveNode(string name)
		{
			Retained.RemoveNode(name);
		}

		public ConfigNode Save()
		{
			ConfigNode copy = Retained.CreateCopy();
			copy.SetValue("id", id, true);
			copy.SetValue("type", typeName ?? "", true);
			copy.SetValue("subtype", subtypeName ?? "", true);
			return copy;
		}
	}
}
