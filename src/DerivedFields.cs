using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace KRAB
{
	/// <summary>
	/// One KRAB_DERIVED_FIELD config entry: a Part Field picker candidate read by
	/// reflection on a PartModule member instead of PartModule.Fields. For values whose
	/// stock readout is stale, e.g. currentRPM only refreshes while that PAW is open.
	/// </summary>
	internal class DerivedFieldRule
	{
		public Type moduleType;
		public string member;
		public string replaces;
		public string label;
		public string units;

		public MemberInfo memberInfo;
		public MemberInfo addMemberInfo;

		/// <summary>Optional zero-arg void method invoked right before `member` is read,
		/// for backing fields only refreshed behind a PAW gate (e.g. angleOfAttack, from
		/// CalcAngleOfAttack). A refresh with side effects, not a value like addMember.</summary>
		public MethodInfo refreshMethodInfo;
	}

	/// <summary>
	/// Loads Config/DerivedFields.cfg (KRAB_DERIVED_FIELD nodes, ModuleManager-patchable)
	/// for the Part Field source and its picker. An entry's `replaces` hides the stock
	/// KSPField it supersedes, so modules with no entry still show all their KSPFields.
	/// </summary>
	internal static class DerivedFieldsCatalog
	{
		private static List<DerivedFieldRule> rules;

		private static void EnsureLoaded()
		{
			if (rules != null)
			{
				return;
			}
			if (GameDatabase.Instance == null || !GameDatabase.Instance.IsReady())
			{
				return; // retry next call: config database not ready yet
			}
			LoadRules();
		}

		private static void LoadRules()
		{
			rules = new List<DerivedFieldRule>();
			ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("KRAB_DERIVED_FIELD");
			for (int i = 0; i < nodes.Length; i++)
			{
				ConfigNode node = nodes[i];
				string moduleName = node.GetValue("module");
				string member = node.GetValue("member");
				if (string.IsNullOrEmpty(moduleName) || string.IsNullOrEmpty(member))
				{
					Debug.LogWarning("[KRAB] DerivedFieldsCatalog: entry without module/member, skipped");
					continue;
				}
				Type moduleType = AssemblyLoader.GetClassByName(typeof(PartModule), moduleName);
				if (moduleType == null)
				{
					Debug.LogWarningFormat("[KRAB] DerivedFieldsCatalog: module type '{0}' not found, entry skipped", moduleName);
					continue;
				}
				MemberInfo memberInfo = ResolveMember(moduleType, member);
				if (memberInfo == null)
				{
					Debug.LogWarningFormat("[KRAB] DerivedFieldsCatalog: '{0}.{1}' not found, entry skipped", moduleName, member);
					continue;
				}
				string addMember = node.GetValue("addMember");
				MemberInfo addMemberInfo = null;
				if (!string.IsNullOrEmpty(addMember))
				{
					addMemberInfo = ResolveMember(moduleType, addMember);
					if (addMemberInfo == null)
					{
						Debug.LogWarningFormat("[KRAB] DerivedFieldsCatalog: addMember '{0}.{1}' not found, ignored",
							moduleName, addMember);
					}
				}
				string refreshMethod = node.GetValue("refreshMethod");
				MethodInfo refreshMethodInfo = null;
				if (!string.IsNullOrEmpty(refreshMethod))
				{
					refreshMethodInfo = ResolveMethod(moduleType, refreshMethod);
					if (refreshMethodInfo == null)
					{
						// Unlike addMember, a missing refreshMethod isn't safe to ignore: the
						// whole reason for the entry is that `member` is stale without it —
						// reading it anyway would silently give a misleading value.
						Debug.LogWarningFormat("[KRAB] DerivedFieldsCatalog: refreshMethod '{0}.{1}' not found, entry skipped",
							moduleName, refreshMethod);
						continue;
					}
				}
				rules.Add(new DerivedFieldRule
				{
					moduleType = moduleType,
					member = member,
					replaces = node.GetValue("replaces"),
					label = node.GetValue("label"),
					units = node.GetValue("units"),
					memberInfo = memberInfo,
					addMemberInfo = addMemberInfo,
					refreshMethodInfo = refreshMethodInfo
				});
			}
			Debug.LogFormat("[KRAB] DerivedFieldsCatalog: {0} entry(ies) loaded", rules.Count);
		}

		/// <summary>Walks the type hierarchy explicitly (public and non-public, instance
		/// only) so a member declared on a base class, e.g. BaseServo's internal
		/// transformRateOfMotion, resolves whatever concrete type the entry names.</summary>
		private static MemberInfo ResolveMember(Type type, string name)
		{
			const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
				| BindingFlags.Instance | BindingFlags.DeclaredOnly;
			for (Type t = type; t != null; t = t.BaseType)
			{
				MemberInfo found = t.GetField(name, flags);
				if (found != null)
				{
					return found;
				}
				found = t.GetMethod(name, flags, null, Type.EmptyTypes, null);
				if (found != null)
				{
					return found;
				}
			}
			return null;
		}

		/// <summary>Same hierarchy walk as ResolveMember, but methods only — a
		/// refreshMethod names an action to invoke, not a value to read, so a same-named
		/// field would never make sense as a match here.</summary>
		private static MethodInfo ResolveMethod(Type type, string name)
		{
			const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
				| BindingFlags.Instance | BindingFlags.DeclaredOnly;
			for (Type t = type; t != null; t = t.BaseType)
			{
				MethodInfo found = t.GetMethod(name, flags, null, Type.EmptyTypes, null);
				if (found != null)
				{
					return found;
				}
			}
			return null;
		}

		/// <summary>Entries applicable to a live module instance (its type or a base of
		/// it) — for the Part Field picker.</summary>
		public static List<DerivedFieldRule> RulesFor(PartModule module)
		{
			EnsureLoaded();
			List<DerivedFieldRule> result = new List<DerivedFieldRule>();
			if (rules == null || module == null)
			{
				return result;
			}
			for (int i = 0; i < rules.Count; i++)
			{
				if (rules[i].moduleType.IsInstanceOfType(module))
				{
					result.Add(rules[i]);
				}
			}
			return result;
		}

		/// <summary>True when a KSPField should be hidden from the picker because some
		/// applicable entry declares it as its replacement.</summary>
		public static bool IsReplaced(PartModule module, string fieldName)
		{
			EnsureLoaded();
			if (rules == null || module == null)
			{
				return false;
			}
			for (int i = 0; i < rules.Count; i++)
			{
				if (rules[i].replaces == fieldName && rules[i].moduleType.IsInstanceOfType(module))
				{
					return true;
				}
			}
			return false;
		}

		public static bool TryFind(PartModule module, string member, out DerivedFieldRule rule)
		{
			EnsureLoaded();
			rule = null;
			if (rules == null || module == null || string.IsNullOrEmpty(member))
			{
				return false;
			}
			for (int i = 0; i < rules.Count; i++)
			{
				if (rules[i].member == member && rules[i].moduleType.IsInstanceOfType(module))
				{
					rule = rules[i];
					return true;
				}
			}
			return false;
		}

		public static float ReadValue(DerivedFieldRule rule, PartModule module)
		{
			if (rule.refreshMethodInfo != null)
			{
				rule.refreshMethodInfo.Invoke(module, null);
			}
			float value = Convert.ToSingle(ReadMember(rule.memberInfo, module));
			if (rule.addMemberInfo != null)
			{
				value += Convert.ToSingle(ReadMember(rule.addMemberInfo, module));
			}
			return value;
		}

		private static object ReadMember(MemberInfo member, object instance)
		{
			return member is FieldInfo field ? field.GetValue(instance) : ((MethodInfo)member).Invoke(instance, null);
		}
	}
}
