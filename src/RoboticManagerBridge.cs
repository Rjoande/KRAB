using System;
using System.Reflection;
using Expansions.Serenity;
using UnityEngine;

namespace KRAB
{
	/// <summary>
	/// Bridge to RoboticControllerManager.QueueFieldUpdate, which is internal in
	/// Assembly-CSharp. Routing writes through the manager gives KRAB the same
	/// priority arbitration KAL controllers use (highest priority wins, equal
	/// priorities are averaged), so KRAB and KAL can coexist on the same field.
	/// The reflection cost is paid once: the method is cached as an open-instance
	/// delegate.
	/// </summary>
	internal static class RoboticManagerBridge
	{
		private static Action<RoboticControllerManager, BaseAxisField, float, int> queueFieldUpdate;
		private static bool initFailed;

		internal static bool Initialize()
		{
			if (queueFieldUpdate != null)
			{
				return true;
			}
			if (initFailed)
			{
				return false;
			}
			MethodInfo method = typeof(RoboticControllerManager).GetMethod(
				"QueueFieldUpdate",
				BindingFlags.Instance | BindingFlags.NonPublic,
				null,
				new[] { typeof(BaseAxisField), typeof(float), typeof(int) },
				null);
			if (method == null)
			{
				Debug.LogError("[KRAB] RoboticControllerManager.QueueFieldUpdate not found; falling back to direct SetValue (no priority arbitration with KAL)");
				initFailed = true;
				return false;
			}
			queueFieldUpdate = (Action<RoboticControllerManager, BaseAxisField, float, int>)Delegate.CreateDelegate(
				typeof(Action<RoboticControllerManager, BaseAxisField, float, int>), method);
			return true;
		}

		/// <summary>
		/// Queue a value for a target axis field at the given controller priority.
		/// Falls back to a direct SetValue when the manager is unavailable, which is
		/// what ControlledAxis.UpdateFieldValue does too.
		/// </summary>
		internal static void QueueFieldUpdate(BaseAxisField field, float value, int priority)
		{
			RoboticControllerManager manager = RoboticControllerManager.Instance;
			if (manager != null && (queueFieldUpdate != null || Initialize()))
			{
				queueFieldUpdate(manager, field, value, priority);
			}
			else
			{
				field.SetValue(value, field.module);
			}
		}
	}
}
