using System.Collections.Generic;
using Expansions;
using Expansions.Serenity;
using KRAB.Graph;
using KRAB.Graph.Evaluation;
using KSP.Localization;
using UnityEngine;

namespace KRAB
{
	/// <summary>
	/// KRAB-9000 controller: blends multiple inputs (player axes, autopilot output,
	/// vessel physics) through a node graph and drives target axis fields the same
	/// way a KAL-1000 does (via RoboticControllerManager, see RoboticManagerBridge).
	///
	/// Current milestone: PartModule skeleton — persistent fields, bindable input
	/// slots, graph ConfigNode round-trip. Graph evaluation and the node editor UI
	/// come in later milestones.
	/// </summary>
	public class ModuleKRABController : PartModule
	{
		public const int InputSlotCount = 4;

		/// <summary>Player-visible name of this controller instance (KAL pattern).</summary>
		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiName = "#LOC_KRAB_controllerName")]
		public string displayName = "";

		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiActiveUnfocused = true, unfocusedRange = 5f, guiName = "#LOC_KRAB_enabled")]
		[UI_Toggle(disabledText = "#autoLOC_8005004", enabledText = "#autoLOC_8005003", scene = UI_Scene.All, affectSymCounterparts = UI_Scene.None)]
		public bool controllerEnabled = true;

		/// <summary>
		/// Same 1..5 priority scale KAL uses; forwarded to RoboticControllerManager
		/// on every queued field update.
		/// </summary>
		[KSPField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiActiveUnfocused = true, unfocusedRange = 5f, guiName = "#LOC_KRAB_priority")]
		[UI_FloatRange(minValue = 1f, maxValue = 5f, stepIncrement = 1f, affectSymCounterparts = UI_Scene.None)]
		public float priorityField = 3f;

		// Bindable input slots (ControllerInput sources). Being KSPAxisFields they show
		// up in the Axis Groups menu like KAL's Play Position, inheriting the 5 action
		// sets, per-set incremental/absolute mode and per-set inversion for free.
		// Absolute by default: mixer semantics (stick position = value); the player can
		// switch to incremental per binding in the Axis Groups UI.

		[KSPAxisField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiActiveUnfocused = true, unfocusedRange = 5f, minValue = 0f, maxValue = 1f, incrementalSpeed = 1f, axisMode = KSPAxisMode.Absolute, guiFormat = "F2", guiName = "#LOC_KRAB_input1")]
		[UI_FloatRange(minValue = 0f, maxValue = 1f, stepIncrement = 0.01f, scene = UI_Scene.All, affectSymCounterparts = UI_Scene.None)]
		public float krabInput1;

		[KSPAxisField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiActiveUnfocused = true, unfocusedRange = 5f, minValue = 0f, maxValue = 1f, incrementalSpeed = 1f, axisMode = KSPAxisMode.Absolute, guiFormat = "F2", guiName = "#LOC_KRAB_input2")]
		[UI_FloatRange(minValue = 0f, maxValue = 1f, stepIncrement = 0.01f, scene = UI_Scene.All, affectSymCounterparts = UI_Scene.None)]
		public float krabInput2;

		[KSPAxisField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiActiveUnfocused = true, unfocusedRange = 5f, minValue = 0f, maxValue = 1f, incrementalSpeed = 1f, axisMode = KSPAxisMode.Absolute, guiFormat = "F2", guiName = "#LOC_KRAB_input3")]
		[UI_FloatRange(minValue = 0f, maxValue = 1f, stepIncrement = 0.01f, scene = UI_Scene.All, affectSymCounterparts = UI_Scene.None)]
		public float krabInput3;

		[KSPAxisField(isPersistant = true, guiActive = true, guiActiveEditor = true, guiActiveUnfocused = true, unfocusedRange = 5f, minValue = 0f, maxValue = 1f, incrementalSpeed = 1f, axisMode = KSPAxisMode.Absolute, guiFormat = "F2", guiName = "#LOC_KRAB_input4")]
		[UI_FloatRange(minValue = 0f, maxValue = 1f, stepIncrement = 0.01f, scene = UI_Scene.All, affectSymCounterparts = UI_Scene.None)]
		public float krabInput4;

		/// <summary>
		/// Raw KRAB_GRAPH ConfigNode, kept as a fallback in case parsing throws:
		/// whatever happens, the player's graph must survive a load+save cycle.
		/// </summary>
		private ConfigNode graphNode;

		/// <summary>
		/// Unity-serialized backup of the graph. Editor part instances are Unity
		/// clones of the prefab (and of each other, for symmetry and alt-copy):
		/// only Unity-serializable fields survive cloning — OnLoad is NOT called on
		/// the clone. KAL solves this with [Serializable] data classes; KRAB keeps
		/// the graph's ConfigNode text and reparses it on the clone. Any future
		/// graph mutation (editor UI) must refresh this backup.
		/// </summary>
		[SerializeField]
		private string graphBackup = string.Empty;

		/// <summary>Typed graph; null when no graph is present or parsing failed.</summary>
		public KrabGraph Graph { get; private set; }

		/// <summary>Graph state shown in the PAW.</summary>
		[KSPField(guiActive = true, guiActiveEditor = true, guiName = "#LOC_KRAB_graphStatus")]
		public string graphStatus = "";

		/// <summary>
		/// Cfg-only developer switch (set `debugMode = true` in the part's MODULE
		/// block — never exposed as a PAW control): reveals the debug aids below.
		/// Superseded by the real editor (M3) for normal play; kept as a quick
		/// regression check for future sessions (2026-07-09, user request).
		/// </summary>
		[KSPField(isPersistant = false)]
		public bool debugMode = false;

		/// <summary>Live value of the first axis output — debug aid, gated by debugMode.</summary>
		[KSPField(guiActive = true, guiFormat = "F3", guiName = "#LOC_KRAB_debugOutput")]
		public float debugOutputValue;

		private KrabEvaluator evaluator;
		private readonly EvalContext evalContext = new EvalContext();
		private bool hasEnoughResources = true;
		private string consumptionString;

		public int Priority => (int)priorityField;

		public float GetControllerInput(int slot)
		{
			switch (slot)
			{
				case 1: return krabInput1;
				case 2: return krabInput2;
				case 3: return krabInput3;
				case 4: return krabInput4;
				default: return 0f;
			}
		}

		public override void OnLoad(ConfigNode node)
		{
			base.OnLoad(node);
			if (node.HasNode(KrabGraph.GraphNodeName))
			{
				LoadGraph(node.GetNode(KrabGraph.GraphNodeName).CreateCopy());
			}
		}

		public override void OnSave(ConfigNode node)
		{
			RestoreGraphFromBackup();
			base.OnSave(node);
			if (Graph != null)
			{
				node.AddNode(Graph.Save());
			}
			else if (graphNode != null)
			{
				node.AddNode(graphNode.CreateCopy());
			}
		}

		private void LoadGraph(ConfigNode source)
		{
			graphNode = source;
			graphBackup = source.ToString();
			try
			{
				Graph = KrabGraph.Load(source);
				List<ValidationIssue> issues = Graph.Validate();
				Debug.LogFormat("[KRAB] {0}: graph loaded — {1} node(s), {2} link(s), {3} issue(s)",
					part != null && part.partInfo != null ? part.partInfo.name : "?",
					Graph.Nodes.Count, Graph.Links.Count, issues.Count);
				LogIssues(issues);
			}
			catch (System.Exception ex)
			{
				// Never lose the graph: the raw copy still round-trips via OnSave.
				Graph = null;
				Debug.LogError("[KRAB] graph parsing failed, keeping raw ConfigNode: " + ex);
			}
		}

		/// <summary>Rebuild the graph on Unity-cloned instances that never saw OnLoad.</summary>
		private void RestoreGraphFromBackup()
		{
			if (Graph != null || graphNode != null || string.IsNullOrEmpty(graphBackup))
			{
				return;
			}
			ConfigNode parsed = ConfigNode.Parse(graphBackup).GetNode(KrabGraph.GraphNodeName);
			if (parsed == null)
			{
				Debug.LogError("[KRAB] graph backup could not be reparsed; graph unavailable on this instance");
				return;
			}
			LoadGraph(parsed);
		}

		private void LogIssues(List<ValidationIssue> issues)
		{
			int errors = 0;
			for (int i = 0; i < issues.Count; i++)
			{
				if (issues[i].severity == IssueSeverity.Error)
				{
					errors++;
				}
				Debug.LogFormat("[KRAB] {0} graph: {1}",
					part != null && part.partInfo != null ? part.partInfo.name : "?", issues[i]);
			}
			if (errors > 0)
			{
				Debug.LogWarningFormat("[KRAB] graph has {0} error(s): evaluation will stay disabled", errors);
			}
		}

		private void Start()
		{
			// DLC guard, mirrors ModuleRoboticController.Start(): KRAB depends on the
			// Serenity RoboticControllerManager for priority arbitration.
			if (!ExpansionsLoader.IsExpansionInstalled("Serenity") && HighLogic.LoadedSceneIsGame)
			{
				enabled = false;
				Destroy(this);
			}
		}

		public override void OnStart(StartState state)
		{
			base.OnStart(state);
			RestoreGraphFromBackup();
			if (string.IsNullOrEmpty(displayName) && part != null && part.partInfo != null)
			{
				displayName = part.partInfo.title;
			}
			RoboticManagerBridge.Initialize();
			UpdateGraphStatus();

			// Debug aids stay in the code (quick regression check for future sessions)
			// but off by default: only a `debugMode = true` in the part's own cfg
			// reveals them, never a PAW toggle (2026-07-09, user request).
			Fields["debugOutputValue"].guiActive = debugMode;
			Events["RunGraphSelfTest"].active = debugMode;
			Events["DebugBindFirstServo"].active = debugMode;
			Events["DebugBindFirstLight"].active = debugMode;

			if (HighLogic.LoadedSceneIsFlight || HighLogic.LoadedSceneIsEditor)
			{
				// In the editor the evaluator only ever runs in simulation mode,
				// driven by the editor window (targets resolve in flight only).
				CompileAndResolve();
			}
			if (HighLogic.LoadedSceneIsFlight)
			{
				GameEvents.onVesselWasModified.Add(OnVesselModified);
			}
		}

		private void OnDestroy()
		{
			GameEvents.onVesselWasModified.Remove(OnVesselModified);
		}

		private void OnVesselModified(Vessel modified)
		{
			// Docking/undocking/decoupling can move or remove target parts: rebind.
			if (evaluator != null && modified == vessel)
			{
				evaluator.ResolveTargets(vessel);
			}
		}

		private void CompileAndResolve()
		{
			evaluator = null;
			if (Graph == null)
			{
				return;
			}
			evaluator = KrabEvaluator.Compile(Graph, out string failReason);
			if (evaluator == null)
			{
				// Defensive only: Compile() no longer refuses on validation errors (a
				// single broken group used to freeze live values for the whole graph,
				// including unrelated outputs — fixed 2026-07-09). Kept in case a future
				// Compile() change reintroduces a genuine fatal path.
				Debug.LogWarningFormat("[KRAB] {0}: graph not compiled — {1}", part.partInfo.name, failReason);
			}
			else
			{
				if (failReason != null)
				{
					Debug.LogWarningFormat("[KRAB] {0}: graph compiled with error(s) — {1}", part.partInfo.name, failReason);
				}
				if (HighLogic.LoadedSceneIsFlight)
				{
					evaluator.ResolveTargets(vessel);
					Debug.LogFormat("[KRAB] {0}: graph compiled — {1} node(s), {2} axis output(s) bound",
						part.partInfo.name, evaluator.NodeCount, evaluator.BoundAxisOutputs);
				}
			}
			UpdateGraphStatus();
		}

		private void UpdateGraphStatus()
		{
			if (Graph == null)
			{
				graphStatus = Localizer.Format("#LOC_KRAB_statusNone");
				return;
			}
			int errors = 0;
			List<ValidationIssue> issues = Graph.Validate();
			for (int i = 0; i < issues.Count; i++)
			{
				if (issues[i].severity == IssueSeverity.Error)
				{
					errors++;
				}
			}
			graphStatus = errors > 0
				? Localizer.Format("#LOC_KRAB_statusErrors", errors)
				: Localizer.Format("#LOC_KRAB_statusOk", Graph.Nodes.Count);
		}

		private void Update()
		{
			if (!HighLogic.LoadedSceneIsFlight || evaluator == null || !controllerEnabled
				|| vessel == null || !vessel.loaded || vessel.packed || !hasEnoughResources)
			{
				return;
			}
			evalContext.module = this;
			evalContext.vessel = vessel;
			evalContext.deltaTime = Time.deltaTime;
			evalContext.time = Time.time;
			evaluator.Run(evalContext);
			debugOutputValue = evaluator.FirstAxisOutputValue;
		}

		private void FixedUpdate()
		{
			// Same resource policy as KAL: consume EC while active, freeze without it.
			if (!HighLogic.LoadedSceneIsFlight || evaluator == null || !controllerEnabled)
			{
				return;
			}
			hasEnoughResources = resHandler.UpdateModuleResourceInputs(
				ref consumptionString, 1.0, 0.999, returnOnFirstLack: false, average: false) > 0.0;
		}

		public KrabEvaluator Evaluator => evaluator;

		public string GraphStatusText => graphStatus;

		private readonly EvalContext simContext = new EvalContext();

		/// <summary>
		/// Run one simulated evaluation pass with the given source overrides
		/// (editor preview, see EvalContext.simulate). Never touches the vessel.
		/// </summary>
		public bool RunSimulation(Dictionary<string, float> overrides)
		{
			if (evaluator == null)
			{
				return false;
			}
			simContext.module = this;
			simContext.vessel = vessel;
			simContext.deltaTime = Time.deltaTime;
			simContext.time = Time.time;
			simContext.simulate = true;
			simContext.simOverrides = overrides;
			evaluator.Run(simContext);
			return true;
		}

		/// <summary>
		/// Re-serialize the graph after a mutation (debug rebind, editor UI):
		/// keeps the raw node and the Unity-cloning backup in sync with the model.
		/// </summary>
		public void RefreshGraphPersistence()
		{
			if (Graph == null)
			{
				return;
			}
			graphNode = Graph.Save();
			graphBackup = graphNode.ToString();
		}

		/// <summary>One call after every editor mutation: persist, revalidate, recompile.</summary>
		public void NotifyGraphEdited()
		{
			RefreshGraphPersistence();
			CompileAndResolve();
		}

		/// <summary>
		/// Recompile without the full Graph.Save()/ToString() persistence write (M4:
		/// dragging a curve keyframe calls this every move for live preview — the
		/// full serialize is comparatively expensive and only needed once the drag
		/// ends, via RefreshGraphPersistence/NotifyGraphEdited).
		/// </summary>
		public void RecompileOnly()
		{
			CompileAndResolve();
		}

		/// <summary>Author an empty graph on a controller that has none (editor UI).</summary>
		public void CreateEmptyGraph()
		{
			if (Graph != null)
			{
				return;
			}
			ConfigNode node = new ConfigNode(KrabGraph.GraphNodeName);
			node.AddValue("graphVersion", 1);
			node.AddValue("nextNodeId", 1);
			LoadGraph(node);
			NotifyGraphEdited();
		}

		/// <summary>Textual snapshot of the current graph (undo stack).</summary>
		public string CaptureGraphSnapshot()
		{
			if (Graph == null)
			{
				return null;
			}
			return Graph.Save().ToString();
		}

		/// <summary>Restore a snapshot taken by CaptureGraphSnapshot (undo/redo).</summary>
		public bool RestoreGraphSnapshot(string snapshot)
		{
			if (string.IsNullOrEmpty(snapshot))
			{
				return false;
			}
			ConfigNode parsed = ConfigNode.Parse(snapshot).GetNode(KrabGraph.GraphNodeName);
			if (parsed == null)
			{
				Debug.LogError("[KRAB] undo snapshot could not be reparsed");
				return false;
			}
			LoadGraph(parsed);
			NotifyGraphEdited();
			return true;
		}

		// Enable/disable actions, mirroring KAL's ToggleControllerEnabled set: a KRAB
		// commands its outputs every frame, so the player needs a way to mute it from
		// action groups (e.g. hand the servo back to a KAL or to manual control).

		[KSPAction("#LOC_KRAB_actionToggle")]
		public void ToggleControllerAction(KSPActionParam param)
		{
			controllerEnabled = !controllerEnabled;
		}

		[KSPAction("#LOC_KRAB_actionEnable")]
		public void EnableControllerAction(KSPActionParam param)
		{
			controllerEnabled = true;
		}

		[KSPAction("#LOC_KRAB_actionDisable")]
		public void DisableControllerAction(KSPActionParam param)
		{
			controllerEnabled = false;
		}

		[KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "#LOC_KRAB_openEditor")]
		public void OpenGraphEditor()
		{
			KRAB.UI.KrabEditorWindow.Toggle(this);
		}

		// Development aid, gated by debugMode (see field above), superseded by the
		// real target picker (M3) but kept as a quick end-to-end regression check
		// (RoboticControllerManager arbitration included) for future sessions.
		[KSPEvent(guiActive = true, guiName = "#LOC_KRAB_debugBind")]
		public void DebugBindFirstServo()
		{
			if (Graph == null || !HighLogic.LoadedSceneIsFlight || vessel == null)
			{
				return;
			}
			KrabNode outputNode = null;
			for (int i = 0; i < Graph.Nodes.Count && outputNode == null; i++)
			{
				KrabNode candidate = Graph.Nodes[i];
				if (candidate.IsKnown && candidate.Info.name == "AxisOutput")
				{
					outputNode = candidate;
				}
			}
			BaseAxisField servoField = null;
			Part servoPart = null;
			PartModule servoModule = null;
			for (int p = 0; p < vessel.parts.Count && servoField == null; p++)
			{
				Part candidate = vessel.parts[p];
				for (int m = 0; m < candidate.Modules.Count && servoField == null; m++)
				{
					if (!(candidate.Modules[m] is BaseServo servo))
					{
						continue;
					}
					for (int f = 0; f < servo.Fields.Count; f++)
					{
						if (servo.Fields[f] is BaseAxisField axisField)
						{
							servoField = axisField;
							servoPart = candidate;
							servoModule = servo;
							break;
						}
					}
				}
			}
			if (outputNode == null || servoField == null)
			{
				ScreenMessages.PostScreenMessage(Localizer.Format("#LOC_KRAB_debugBindFail"), 5f, ScreenMessageStyle.UPPER_CENTER);
				return;
			}
			outputNode.Retained.SetValue("persistentId", servoPart.persistentId.ToString(), true);
			outputNode.Retained.SetValue("moduleId", servoModule.GetPersistentId().ToString(), true);
			outputNode.Retained.SetValue("axisName", servoField.name, true);
			RefreshGraphPersistence();
			CompileAndResolve();
			ScreenMessages.PostScreenMessage(
				Localizer.Format("#LOC_KRAB_debugBindDone", servoPart.partInfo.title, servoField.guiName),
				5f, ScreenMessageStyle.UPPER_CENTER);
		}

		// Development aid, gated by debugMode: binds every ActionTrigger of the
		// graph onto the first light found on the vessel — rising edges fire
		// LightOnAction, falling edges LightOffAction — to exercise the event path.
		[KSPEvent(guiActive = true, guiName = "#LOC_KRAB_debugBindLight")]
		public void DebugBindFirstLight()
		{
			if (Graph == null || !HighLogic.LoadedSceneIsFlight || vessel == null)
			{
				return;
			}
			Part lightPart = null;
			PartModule lightModule = null;
			for (int p = 0; p < vessel.parts.Count && lightModule == null; p++)
			{
				if (vessel.parts[p].Modules.Contains("ModuleLight"))
				{
					lightPart = vessel.parts[p];
					lightModule = lightPart.Modules["ModuleLight"];
				}
			}
			bool anyBound = false;
			if (lightModule != null)
			{
				for (int i = 0; i < Graph.Nodes.Count; i++)
				{
					KrabNode node = Graph.Nodes[i];
					if (!node.IsKnown || node.Info.name != "ActionTrigger")
					{
						continue;
					}
					bool falling = node.GetString("edge", "rising")
						.Equals("falling", System.StringComparison.OrdinalIgnoreCase);
					node.Retained.SetValue("persistentId", lightPart.persistentId.ToString(), true);
					node.Retained.SetValue("moduleId", lightModule.GetPersistentId().ToString(), true);
					node.Retained.SetValue("actionName", falling ? "LightOffAction" : "LightOnAction", true);
					anyBound = true;
				}
			}
			if (!anyBound)
			{
				ScreenMessages.PostScreenMessage(Localizer.Format("#LOC_KRAB_debugBindLightFail"), 5f, ScreenMessageStyle.UPPER_CENTER);
				return;
			}
			RefreshGraphPersistence();
			CompileAndResolve();
			ScreenMessages.PostScreenMessage(
				Localizer.Format("#LOC_KRAB_debugBindLightDone", lightPart.partInfo.title),
				5f, ScreenMessageStyle.UPPER_CENTER);
		}

		// Development aid, gated by debugMode. Tests the KrabGraph/KrabNode data
		// model (parsing, validation, round-trip) independently of the editor UI —
		// kept deliberately as a regression net (2026-07-09, user request), not
		// removed now that the editor exercises the graph directly.
		[KSPEvent(guiActive = true, guiActiveEditor = true, guiName = "#LOC_KRAB_selfTest")]
		public void RunGraphSelfTest()
		{
			bool pass = KrabGraphSelfTest.Run(out string report);
			Debug.Log("[KRAB] graph self-test:\n" + report);
			ScreenMessages.PostScreenMessage(
				Localizer.Format(pass ? "#LOC_KRAB_selfTestPass" : "#LOC_KRAB_selfTestFail"),
				5f, ScreenMessageStyle.UPPER_CENTER);
		}

		public override string GetModuleDisplayName()
		{
			return Localizer.Format("#LOC_KRAB_moduleName");
		}

		public override string GetInfo()
		{
			return Localizer.Format("#LOC_KRAB_moduleInfo");
		}
	}
}
