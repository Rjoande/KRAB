using System;
using System.Reflection;
using UnityEngine;

namespace KRAB
{
	/// <summary>
	/// Talks to KRILL's KrillQuery/KrillParams purely via reflection, so KRAB
	/// never needs a hard/compile-time reference to KRILL.dll — same "resolve
	/// everything by name, never a hard reference" philosophy already used by
	/// TimeForScience's RealBatteryPowerLedgerWrapper.cs for RealBattery. If
	/// KRILL isn't installed (or its shape doesn't match), every method below
	/// returns a safe default instead of throwing.
	/// </summary>
	public static class KrillGroupBridge
	{
		private static bool initialized;
		private static bool installed;

		private static MethodInfo getGroupStateMethod; // static KrillQuery.GroupState? GetGroupState(Vessel, int)
		private static FieldInfo signalField;            // GroupState.signal (bool)
		private static Func<int> getMaxVisibleGroup;      // static KrillParams.MaxVisibleGroup

		private static bool axisInstalled;
		private static MethodInfo getAxisStateMethod; // static KrillQuery.AxisState? GetAxisState(Vessel, int)
		private static FieldInfo axisValueField;         // AxisState.value (float)
		private static Func<int> getMaxVisibleAxis;       // static KrillParams.MaxVisibleAxis

		/// <summary>True once resolved and KRILL was found with a compatible shape.</summary>
		public static bool Installed
		{
			get
			{
				EnsureInit();
				return installed;
			}
		}

		/// <summary>
		/// Highest group number to offer in KRAB's own pickers — mirrors KRILL's
		/// own visibility cap (its Difficulty Settings page, default 20, range
		/// 20-99) by reading the live value, so the two UIs never disagree about
		/// how many groups exist (2026-08-30, user request: "KRAB dovrebbe
		/// ereditare questa funzione"). 99 (KRILL's own ceiling) if KRILL isn't
		/// installed, or the value can't be read (e.g. no active save yet).
		/// </summary>
		public static int MaxVisibleGroup
		{
			get
			{
				EnsureInit();
				if (!installed || getMaxVisibleGroup == null)
				{
					return 99;
				}
				try
				{
					return getMaxVisibleGroup();
				}
				catch
				{
					return 99;
				}
			}
		}

		/// <summary>True once resolved and KRILL exposes GetAxisState with a compatible
		/// shape (KRILL &gt;= 0.3.0). False for an older KRILL that only has groups —
		/// the axis source simply isn't offered then, everything else here still works.</summary>
		public static bool AxisInstalled
		{
			get
			{
				EnsureInit();
				return axisInstalled;
			}
		}

		/// <summary>
		/// Highest axis number to offer in KRAB's own picker — mirrors KRILL's own
		/// KrillParams.MaxVisibleAxis (Difficulty Settings page, default 12, range
		/// 5-40), same live-read pattern as MaxVisibleGroup/MaxVisibleGroup above.
		/// 40 (KRILL's own ceiling) if unavailable.
		/// </summary>
		public static int MaxVisibleAxis
		{
			get
			{
				EnsureInit();
				if (!axisInstalled || getMaxVisibleAxis == null)
				{
					return 40;
				}
				try
				{
					return getMaxVisibleAxis();
				}
				catch
				{
					return 40;
				}
			}
		}

		/// <summary>
		/// The current value (-1..1) of a KRILL axis (1-4 mirror stock's own custom
		/// axes; 5+ are KRILL's virtual axes), for (vessel, axis) — auto-resolving the
		/// vessel's active override set, same semantics KrillQuery.GetAxisState(Vessel,
		/// int) exposes to any external mod. Reads AxisState.value: already the real
		/// runtime level regardless of kind (Spring: live level, returns to rest on its
		/// own; Fixed: the persisted value) — KRAB never needs to know or branch on
		/// kind, same contract as GetGroupSignal above. 0 if KRILL isn't installed, the
		/// axis has no data yet, or anything fails.
		/// </summary>
		public static float GetAxisValue(Vessel vessel, int axis)
		{
			EnsureInit();
			if (!axisInstalled || vessel == null)
			{
				return 0f;
			}
			try
			{
				object boxedState = getAxisStateMethod.Invoke(null, new object[] { vessel, axis });
				return boxedState != null ? (float)axisValueField.GetValue(boxedState) : 0f;
			}
			catch
			{
				return 0f;
			}
		}

		/// <summary>
		/// The 0/1 level a KRILL extended group is currently presenting, for
		/// (vessel, group), auto-resolving the vessel's active override set —
		/// same semantics KrillQuery.GetGroupState(Vessel, int) exposes to any
		/// external mod. Reads GroupState.signal (2026-08-31 KRILL refactor):
		/// a plain level already derived from the group's kind on KRILL's side
		/// — Pulse lit for KrillActivation.PulseSeconds after it fires then off
		/// on its own, Toggle the persisted bool, Hold lit while held — so KRAB
		/// never needs to know or branch on kind, unlike before this refactor
		/// (was reading GroupState.active, private bookkeeping that only made
		/// sense for Toggle; a Pulse group read as flip-flop noise — the bug a
		/// KRILL session reported and then fixed at the source). False if
		/// KRILL isn't installed, the group has no data yet, or anything fails.
		/// </summary>
		public static bool GetGroupSignal(Vessel vessel, int group)
		{
			EnsureInit();
			if (!installed || vessel == null)
			{
				return false;
			}
			try
			{
				object boxedState = getGroupStateMethod.Invoke(null, new object[] { vessel, group });
				return boxedState != null && (bool)signalField.GetValue(boxedState);
			}
			catch
			{
				return false;
			}
		}

		private static void EnsureInit()
		{
			if (initialized)
			{
				return;
			}
			initialized = true;
			installed = false;
			try
			{
				Type queryType = null;
				Type paramsType = null;
				foreach (AssemblyLoader.LoadedAssembly loaded in AssemblyLoader.loadedAssemblies)
				{
					if (loaded.name != "KRILL")
					{
						continue;
					}
					queryType = loaded.assembly.GetType("KRILL.KrillQuery");
					paramsType = loaded.assembly.GetType("KRILL.KrillParams");
					break;
				}
				if (queryType == null || paramsType == null)
				{
					return;
				}

				getGroupStateMethod = queryType.GetMethod("GetGroupState",
					BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vessel), typeof(int) }, null);
				if (getGroupStateMethod == null)
				{
					return;
				}
				Type groupStateType = Nullable.GetUnderlyingType(getGroupStateMethod.ReturnType);
				if (groupStateType == null)
				{
					return;
				}
				signalField = groupStateType.GetField("signal", BindingFlags.Public | BindingFlags.Instance);
				if (signalField == null)
				{
					return;
				}

				PropertyInfo maxVisibleProp = paramsType.GetProperty("MaxVisibleGroup",
					BindingFlags.Public | BindingFlags.Static);
				MethodInfo maxVisibleGetter = maxVisibleProp?.GetGetMethod();
				getMaxVisibleGroup = maxVisibleGetter != null
					? (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), maxVisibleGetter)
					: null;

				installed = true;

				// Separate try: an older KRILL (< 0.3.0, groups only, no GetAxisState
				// yet) must not disable anything resolved above — axisInstalled just
				// stays false and the axis source is quietly not offered.
				try
				{
					getAxisStateMethod = queryType.GetMethod("GetAxisState",
						BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vessel), typeof(int) }, null);
					if (getAxisStateMethod == null)
					{
						return;
					}
					Type axisStateType = Nullable.GetUnderlyingType(getAxisStateMethod.ReturnType);
					if (axisStateType == null)
					{
						return;
					}
					axisValueField = axisStateType.GetField("value", BindingFlags.Public | BindingFlags.Instance);
					if (axisValueField == null)
					{
						return;
					}

					PropertyInfo maxVisibleAxisProp = paramsType.GetProperty("MaxVisibleAxis",
						BindingFlags.Public | BindingFlags.Static);
					MethodInfo maxVisibleAxisGetter = maxVisibleAxisProp?.GetGetMethod();
					getMaxVisibleAxis = maxVisibleAxisGetter != null
						? (Func<int>)Delegate.CreateDelegate(typeof(Func<int>), maxVisibleAxisGetter)
						: null;

					axisInstalled = true;
				}
				catch (Exception e)
				{
					axisInstalled = false;
					Debug.LogWarningFormat("[KRAB] KrillGroupBridge axis init failed, KRILL axes disabled: {0}", e);
				}
			}
			catch (Exception e)
			{
				installed = false;
				Debug.LogWarningFormat("[KRAB] KrillGroupBridge init failed, KRILL groups disabled: {0}", e);
			}
		}
	}
}
