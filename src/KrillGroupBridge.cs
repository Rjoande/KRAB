using System;
using System.Reflection;
using UnityEngine;

namespace KRAB
{
	/// <summary>
	/// Talks to KRILL's KrillQuery/KrillParams purely by reflection, so KRAB never needs
	/// a compile-time reference to KRILL.dll. With KRILL absent or shaped differently,
	/// every method here returns a safe default instead of throwing.
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
		/// Highest group number to offer in KRAB's pickers, read live from KRILL's own
		/// visibility cap so the two UIs never disagree. 99 (KRILL's ceiling) if KRILL
		/// isn't installed or the value can't be read yet.
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
		/// Highest axis number to offer in KRAB's picker, read live from KRILL's own
		/// MaxVisibleAxis, same pattern as MaxVisibleGroup. 40 (KRILL's ceiling) if
		/// unavailable.
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
		/// Current value (-1..1) of a KRILL axis for (vessel, axis), resolving the vessel's
		/// active override set. AxisState.value is already the real runtime level whatever
		/// the axis kind. 0 if KRILL is absent, the axis has no data, or anything fails.
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
		/// The 0/1 level a KRILL extended group presents for (vessel, group), resolving the
		/// vessel's active override set. GroupState.signal is already derived from the
		/// group's kind (Pulse/Toggle/Hold) on KRILL's side. False if anything fails.
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
