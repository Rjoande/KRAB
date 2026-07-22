using UnityEngine;

namespace KRAB.UI
{
	/// <summary>One display unit for a physical metric: display = canonical * factor + offset.</summary>
	public struct UnitOption
	{
		/// <summary>SI-style unit symbol (not localized: unit symbols are international).</summary>
		public readonly string symbol;
		public readonly float factor;
		public readonly float offset;

		public UnitOption(string symbol, float factor, float offset = 0f)
		{
			this.symbol = symbol;
			this.factor = factor;
			this.offset = offset;
		}

		public string Format(float canonical, string numberFormat = "F1")
		{
			string number = (canonical * factor + offset).ToString(numberFormat);
			return symbol.Length > 0 ? number + " " + symbol : number;
		}
	}

	/// <summary>
	/// Display-unit registry for PhysicalState metrics. Conversion is UI-only by
	/// design (see notes/catalogo-nodi.md): stored values and graph math always stay
	/// in the canonical unit; the chosen symbol is persisted per node as the
	/// `displayUnit` param, which has no effect on evaluation.
	/// </summary>
	public static class KrabUnits
	{
		private static readonly UnitOption[] Speeds =
		{
			new UnitOption("m/s", 1f),
			new UnitOption("km/h", 3.6f),
			new UnitOption("kn", 1.943844f)
		};

		private static readonly UnitOption[] Lengths =
		{
			new UnitOption("m", 1f),
			new UnitOption("km", 0.001f)
		};

		private static readonly UnitOption[] Pressures =
		{
			new UnitOption("kPa", 1f),
			new UnitOption("atm", 1f / 101.325f)
		};

		private static readonly UnitOption[] Temperatures =
		{
			new UnitOption("K", 1f),
			new UnitOption("°C", 1f, -273.15f)
		};

		private static readonly UnitOption[] RotationRates =
		{
			new UnitOption("rad/s", 1f),
			new UnitOption("°/s", Mathf.Rad2Deg)
		};

		private static readonly UnitOption[] Density = { new UnitOption("kg/m³", 1f) };
		private static readonly UnitOption[] Gees = { new UnitOption("g", 1f) };
		private static readonly UnitOption[] Dimensionless = { new UnitOption("", 1f) };

		public static UnitOption[] ForMetric(string metric)
		{
			switch (metric != null ? metric.ToLowerInvariant() : "")
			{
				case "srfspeed":
				case "horizontalsrfspeed":
				case "verticalspeed":
				case "indicatedairspeed":
					return Speeds;
				case "altitudeasl":
				case "altituderadar":
					return Lengths;
				case "dynamicpressure":
				case "staticpressure":
					return Pressures;
				case "externaltemperature":
					return Temperatures;
				case "angularvelocitymag":
					return RotationRates;
				case "atmdensity":
					return Density;
				case "gforce":
					return Gees;
				default:
					return Dimensionless;
			}
		}

		/// <summary>Resolve the persisted symbol against the metric's set (fallback: canonical).</summary>
		public static UnitOption Resolve(string metric, string symbol)
		{
			UnitOption[] set = ForMetric(metric);
			for (int i = 0; i < set.Length; i++)
			{
				if (set[i].symbol == symbol)
				{
					return set[i];
				}
			}
			return set[0];
		}

		/// <summary>The symbol after the given one, wrapping (unit chip cycling).</summary>
		public static string NextSymbol(string metric, string symbol)
		{
			UnitOption[] set = ForMetric(metric);
			for (int i = 0; i < set.Length; i++)
			{
				if (set[i].symbol == symbol)
				{
					return set[(i + 1) % set.Length].symbol;
				}
			}
			return set[0].symbol;
		}

		public static bool HasAlternatives(string metric)
		{
			return ForMetric(metric).Length > 1;
		}

		/// <summary>
		/// "20.7 m/s (41.2 kn)" — always shows the canonical (SI) value; appends the
		/// player's chosen unit in parentheses only when it differs from canonical
		/// (in-game feedback, 2026-07-09: the simulator should never hide the SI
		/// reading the rest of the graph actually reasons in).
		/// </summary>
		public static string DualFormat(string metric, string displayUnitSymbol, float canonical, string numberFormat = "F1")
		{
			UnitOption canonicalUnit = ForMetric(metric)[0];
			string si = canonicalUnit.Format(canonical, numberFormat);
			if (string.IsNullOrEmpty(displayUnitSymbol) || displayUnitSymbol == canonicalUnit.symbol)
			{
				return si;
			}
			return si + " (" + Resolve(metric, displayUnitSymbol).Format(canonical, numberFormat) + ")";
		}
	}
}
