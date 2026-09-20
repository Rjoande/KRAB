using System.Collections.Generic;
using System.Globalization;
using KRAB.Graph;
using KRAB.Graph.Evaluation;
using KSP.Localization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace KRAB.UI
{
	/// <summary>
	/// Editor window: UGUI shell built in code, tree-of-groups view, live telemetry,
	/// in-editor condition simulator, live editing with snapshot undo/redo. Every
	/// mutation flows through Mutate(): snapshot, persist/revalidate/recompile, rebuild.
	/// </summary>
	public class KrabEditorWindow : MonoBehaviour
	{
		private const float WindowWidth = 620f;
		// Fixed heights for the two variable-length lists (tree, simulator sliders):
		// they scroll internally so the window's total height stays constant
		// whatever the graph size, keeping the titlebar from drifting.
		private const float TreeAreaHeight = 260f;
		private const float SimAreaHeight = 130f;
		private const float RefreshInterval = 0.1f;
		private const int MaxTreeDepth = 8;
		private const int MaxUndo = 50;
		private const string InputLockId = "KRAB_EDITOR_WINDOW";

		private static KrabEditorWindow current;

		// Copy/paste of a whole input/operator subtree across output tabs. Static and
		// session-scoped like `current`: a plain in-memory clipboard, not persisted,
		// cleared on a KSP restart.
		private static string subtreeClipboard;

		// Footer toggles, both session-scoped: survive closing/reopening the window,
		// reset on a KSP restart.
		private static bool showNodeIds;
		private static bool invertHighlightPriority;

		// Last on-screen position (top-center, matching windowRect's own pivot), so
		// reopening lands where the window was left. Session-scoped, not persisted.
		private static Vector2? lastWindowPosition;

		private ModuleKRABController module;
		private RectTransform windowRect;
		private Transform contentHost;
		private Button undoButton;
		private Button redoButton;

		private readonly List<KrabNode> outputNodes = new List<KrabNode>();
		private string activeOutputId;

		// nodeId:port -> upstream node / port default (rebuilt on every content rebuild)
		private readonly Dictionary<string, KrabNode> upstreamByPort = new Dictionary<string, KrabNode>();
		private readonly Dictionary<string, KrabPortDefault> defaultByPort = new Dictionary<string, KrabPortDefault>();

		// validation results of the current rebuild (node highlighting + strip)
		private readonly List<ValidationIssue> issues = new List<ValidationIssue>();
		private readonly HashSet<string> errorNodes = new HashSet<string>();
		private readonly HashSet<string> warnNodes = new HashSet<string>();

		private struct ValueBinding
		{
			public KrabNode node;
			public Text label;
			// Display-unit conversion (UI only, physical sources): shown = value*factor+offset.
			public float factor;
			public float offset;
			public string suffix;
			// AxisOutput preview only: clamp the converted reading to the target field's
			// real range. The write to the vessel is always clamped anyway
			// (Mathf.InverseLerp clamps its t to 0..1); this keeps the preview honest.
			public bool hasClamp;
			public float clampMin;
			public float clampMax;
		}

		private readonly List<ValueBinding> valueBindings = new List<ValueBinding>();
		private readonly Dictionary<string, float> simValues = new Dictionary<string, float>();
		private readonly List<string> undoStack = new List<string>();
		private readonly List<string> redoStack = new List<string>();
		private float nextRefresh;

		// ---- pickers ----

		private enum PickerKind
		{
			None,
			Source,      // choosing what feeds pickerTarget:pickerPort
			TargetField, // choosing the axis/action of pickedPart for output pickerTarget
			Filter,      // choosing a shaping operator (KrabGraphEdits.InsertableFilters) to add to pickerTarget
			KrillGroup,  // choosing a KRILL extended group (11+) number for pickerTarget:pickerPort
			KrillAxis    // choosing a KRILL axis number for pickerTarget:pickerPort
		}

		private PickerKind pickerKind;
		private KrabNode pickerTarget;
		private int pickerPort;
		// Non-null while TargetField picks a part+field for a brand-new Part Field
		// source: pickerTarget:pickerPort then name the CONSUMER port, unlike
		// StartPartPick(output) where pickerTarget is the node being retargeted.
		private string pickerNewSourceSubtype;
		private bool pickingPart;
		private Part pickedPart;
		private Part hoverPart;
		// hoverPart's whole symmetry group during a scene pick. hoverPart itself is
		// tracked separately since it's what a click actually confirms.
		private readonly List<Part> hoverGroup = new List<Part>();
		// Captured on mouse-down, confirmed on mouse-up (see HandlePartPicking).
		private Part pendingPickPart;
		private const string PickLockId = "KRAB_EDITOR_PICK";

		// Persistent highlight on the active output tab's tree. Two families, each with
		// a direct/kinship (symmetry sibling) pair: Target = this output's bound part;
		// Source = every part a Part Field in this output's tree reads from.
		private readonly Dictionary<Part, Color> highlightedParts = new Dictionary<Part, Color>();
		private static readonly Color TargetHighlightColor = new Color(0.18f, 0.35f, 0.85f);
		private static readonly Color TargetKinshipColor = new Color(0.42f, 0.28f, 0.82f);
		private static readonly Color SourceHighlightColor = new Color(0.16f, 0.62f, 0.34f);
		private static readonly Color SourceKinshipColor = new Color(0.38f, 0.66f, 0.42f);

		private static readonly string[] Channels =
		{
			"Pitch", "Yaw", "Roll", "TranslateX", "TranslateY", "TranslateZ", "MainThrottle",
			"WheelSteer", "WheelThrottle", "Custom01", "Custom02", "Custom03", "Custom04"
		};

		private static readonly string[] Metrics =
		{
			"SrfSpeed", "HorizontalSrfSpeed", "VerticalSpeed", "IndicatedAirSpeed", "Mach",
			"AltitudeASL", "AltitudeRadar", "DynamicPressure", "StaticPressure", "AtmDensity",
			"GForce", "ExternalTemperature", "AngularVelocityMag",
			"PitchRate", "RollRate", "YawRate", "Mass",
			"Pitch", "Bank", "Heading", "ForwardSpeed", "LateralSpeed"
		};

		private static readonly string[] ActionGroupNames =
		{
			"Gear", "Light", "Brakes", "RCS", "SAS", "Abort", "Custom01", "Custom02", "Custom03",
			"Custom04", "Custom05", "Custom06", "Custom07", "Custom08", "Custom09", "Custom10"
		};

		private static bool Simulated => !HighLogic.LoadedSceneIsFlight;

		private KrabGraph Graph => module.Graph;

		public static void Toggle(ModuleKRABController forModule)
		{
			if (current != null)
			{
				bool same = current.module == forModule;
				current.Close();
				if (same)
				{
					return;
				}
			}
			GameObject host = new GameObject("KRABEditorWindow");
			current = host.AddComponent<KrabEditorWindow>();
			current.module = forModule;
			current.Build();
		}

		private void OnDestroy()
		{
			if (windowRect != null)
			{
				lastWindowPosition = windowRect.anchoredPosition;
			}
			ClearPickerState();
			ClearAllPartHighlights();
			KrabCurveWindow.CloseAny(); // a curve window points into this editor's graph — don't leave it floating
			InputLockManager.RemoveControlLock(InputLockId);
			GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
			GameEvents.onVesselChange.Remove(OnActiveVesselChanged);
			GameEvents.onHideUI.Remove(HandleHideUI);
			GameEvents.onShowUI.Remove(HandleShowUI);
			GameEvents.onGamePause.Remove(HandleGamePause);
			GameEvents.onGameUnpause.Remove(HandleGameUnpause);
			if (current == this)
			{
				current = null;
			}
		}

		private void OnSceneChange(GameScenes scene)
		{
			Close();
		}

		/// <summary>Re-validates the target highlight when the active vessel changes: the
		/// bound target part may have gone out of load range, leaving a stale highlight
		/// on a part of a no-longer-relevant ship.</summary>
		private void OnActiveVesselChanged(Vessel v)
		{
			UpdatePartHighlights();
		}

		// Independent flags: F2 and Esc can each be toggled on their own, the window
		// stays hidden while either is active.
		private bool hiddenByUI;
		private bool hiddenByPause;

		private void HandleHideUI()
		{
			hiddenByUI = true;
			UpdateVisibility();
		}

		private void HandleShowUI()
		{
			hiddenByUI = false;
			UpdateVisibility();
		}

		private void HandleGamePause()
		{
			hiddenByPause = true;
			UpdateVisibility();
		}

		private void HandleGameUnpause()
		{
			hiddenByPause = false;
			UpdateVisibility();
		}

		private void UpdateVisibility()
		{
			bool visible = !hiddenByUI && !hiddenByPause;
			if (!visible && pickingPart)
			{
				// A scene part-pick holds an input lock and needs the (now invisible)
				// prompt to make sense of it — cancel rather than leave both stranded.
				CancelPicker();
			}
			gameObject.SetActive(visible);
		}

		private void Close()
		{
			Destroy(gameObject);
		}

		private static string Loc(string key)
		{
			return Localizer.Format(key);
		}

		/// <summary>Localized display name of a node subtype, falling back to the raw name.</summary>
		internal static string NodeName(KrabNode node)
		{
			if (!node.IsKnown)
			{
				return node.subtypeName ?? "?";
			}
			string localized = Localizer.Format("#LOC_KRAB_node_" + node.Info.name);
			return localized.StartsWith("#LOC_KRAB_") ? node.Info.name : localized;
		}

		/// <summary>Localized entry with the technical name as fallback (vocabulary keys).</summary>
		private static string LocOr(string key, string fallback)
		{
			string localized = Localizer.Format(key);
			return localized.StartsWith("#LOC_KRAB_") ? fallback : localized;
		}

		/// <summary>Localized detail of a source node's selection (channel/metric/group/slot/value).</summary>
		private static string SourceDetail(KrabNode node)
		{
			return SourceDetail(node, PartFieldLabelMaxChars);
		}

		/// <summary>partFieldMaxChars lets a caller ask for the untruncated text (pass
		/// int.MaxValue). Only the Part Field case truncates; every other case ignores
		/// the parameter.</summary>
		private static string SourceDetail(KrabNode node, int partFieldMaxChars)
		{
			switch (node.Info.name)
			{
				case "PlayerAxis":
				case "ScriptAxis":
					string channel = node.GetString("channel", "?");
					return LocOr("#LOC_KRAB_ch_" + channel, channel);
				case "PhysicalState":
					string metric = node.GetString("metric", "?");
					return LocOr("#LOC_KRAB_met_" + metric, metric);
				case "ActionGroupState":
					string group = node.GetString("group", "?");
					return LocOr("#LOC_KRAB_ag_" + group, group);
				case "KrillGroupState":
					return node.GetString("group", "?");
				case "KrillAxisState":
					return node.GetString("axis", "?");
				case "ControllerInput":
					return Localizer.Format("#LOC_KRAB_ui_slot", node.GetString("slot", "1"));
				case "Constant":
					return node.GetString("value", "0");
				case "PartField":
					if (node.GetString("persistentId", "0") == "0")
					{
						return Loc("#LOC_KRAB_ui_notBound");
					}
					if (!TryResolvePartField(node, out Part pfPart, out string pfLabel))
					{
						return Loc("#LOC_KRAB_ui_targetMissing");
					}
					// Truncate the whole "part - field" pair together, not each half on its
					// own, or the result overruns the simulator slider row.
					return Truncate(pfPart.partInfo.title + " - " + pfLabel, partFieldMaxChars);
				default:
					return "";
			}
		}

		/// <summary>
		/// Full "Name · Detail" display string for a node, shared by the tree row, the
		/// simulator row and the REUSE A SIGNAL list. Part Field shows its "part - field"
		/// pair alone, since prefixing it with the subtype name adds nothing.
		/// </summary>
		private static string NodeLabel(KrabNode node)
		{
			return NodeLabel(node, PartFieldLabelMaxChars);
		}

		private static string NodeLabel(KrabNode node, int partFieldMaxChars)
		{
			string detail = node.IsKnown && node.Info.kind == NodeKind.Operator
				? DescribeOperator(node)
				: SourceDetail(node, partFieldMaxChars);
			if (node.IsKnown && node.Info.name == "PartField")
			{
				return detail;
			}
			return NodeName(node) + " · " + detail;
		}

		/// <summary>The untruncated version of NodeLabel, for a tooltip on whatever was
		/// cut short. Only Part Field's "part - field" pair is ever truncated.</summary>
		private static string NodeFullLabel(KrabNode node)
		{
			return NodeLabel(node, int.MaxValue);
		}

		/// <summary>Node id suffix ("n4"-style). Always shown in the REUSE A SIGNAL list,
		/// the one place it is the only way to tell candidate nodes apart; gated behind
		/// the footer's id toggle everywhere else.</summary>
		private static string IdSuffix(KrabNode node)
		{
			return " [" + node.id + "]";
		}

		/// <summary>Simulator row label: NodeLabel plus the id suffix when shown, capped
		/// to fit the fixed-width column next to the slider — tighter by
		/// SimulatorLabelIdMargin while ids are on, to leave room for the "[nX]" suffix.</summary>
		private static string SimulatorRowLabel(KrabNode node)
		{
			string label = NodeLabel(node);
			int budget = SimulatorLabelMaxChars - (showNodeIds ? SimulatorLabelIdMargin : 0);
			label = Truncate(label, budget);
			return showNodeIds ? label + IdSuffix(node) : label;
		}

		// -------------------------------------------------------------- mutation

		/// <summary>All edits flow through here: undo snapshot, edit, persist, rebuild.</summary>
		private void Mutate(System.Action edit)
		{
			CaptureUndoSnapshot();
			edit();
			module.NotifyGraphEdited();
			RebuildContent();
		}

		/// <summary>Just the snapshot half of Mutate(), exposed for KrabCurveWindow: a
		/// curve-drag calls this once per gesture then does its own persist+recompile,
		/// since a full Mutate() per drag frame would rebuild the tree and fight the drag.</summary>
		internal void CaptureUndoSnapshot()
		{
			string snapshot = module.CaptureGraphSnapshot();
			if (snapshot != null)
			{
				undoStack.Add(snapshot);
				if (undoStack.Count > MaxUndo)
				{
					undoStack.RemoveAt(0);
				}
				redoStack.Clear();
			}
		}

		private void Undo()
		{
			if (undoStack.Count == 0)
			{
				return;
			}
			string currentState = module.CaptureGraphSnapshot();
			string snapshot = undoStack[undoStack.Count - 1];
			undoStack.RemoveAt(undoStack.Count - 1);
			if (module.RestoreGraphSnapshot(snapshot) && currentState != null)
			{
				redoStack.Add(currentState);
			}
			KrabCurveWindow.SyncAfterUndoRedo(); // Restore() swaps in a fresh KrabGraph — re-point at the surviving node instead of leaving a detached reference
			RebuildContent();
		}

		private void Redo()
		{
			if (redoStack.Count == 0)
			{
				return;
			}
			string currentState = module.CaptureGraphSnapshot();
			string snapshot = redoStack[redoStack.Count - 1];
			redoStack.RemoveAt(redoStack.Count - 1);
			if (module.RestoreGraphSnapshot(snapshot) && currentState != null)
			{
				undoStack.Add(currentState);
			}
			KrabCurveWindow.SyncAfterUndoRedo();
			RebuildContent();
		}

		/// <summary>Numeric field sanitizer: invariant parse, tolerant of comma decimals.</summary>
		private static string SanitizeNumber(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			text = text.Trim();
			if (text.IndexOf(',') >= 0 && text.IndexOf('.') < 0 && text.IndexOf(',') == text.LastIndexOf(','))
			{
				text = text.Replace(',', '.');
			}
			return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
				? value.ToString(CultureInfo.InvariantCulture)
				: null;
		}

		// ------------------------------------------------------------------ build

		private void Build()
		{
			GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
			GameEvents.onVesselChange.Add(OnActiveVesselChanged);
			// Hide for F2 (hide UI) and Esc (pause menu), like every other KSP UI.
			// Toggling gameObject.SetActive also stops Update/LateUpdate without tearing
			// down state; re-showing needs no rebuild, Unity just resumes calling them.
			GameEvents.onHideUI.Add(HandleHideUI);
			GameEvents.onShowUI.Add(HandleShowUI);
			GameEvents.onGamePause.Add(HandleGamePause);
			GameEvents.onGameUnpause.Add(HandleGameUnpause);

			Canvas canvas = gameObject.AddComponent<Canvas>();
			canvas.renderMode = RenderMode.ScreenSpaceOverlay;
			canvas.sortingOrder = 900;
			CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
			scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
			scaler.scaleFactor = GameSettings.UI_SCALE;
			gameObject.AddComponent<GraphicRaycaster>();

			windowRect = KrabUi.Bordered("Window", transform, KrabUi.Win, KrabUi.Line);
			// Top-anchored pivot: with a centered pivot, ContentSizeFitter growth pushes
			// the titlebar up by half the added height, possibly off-screen. Anchoring to
			// the top edge makes growth extend downward only, with the titlebar fixed.
			windowRect.anchorMin = windowRect.anchorMax = new Vector2(0.5f, 0.5f);
			windowRect.pivot = new Vector2(0.5f, 1f);
			windowRect.anchoredPosition = lastWindowPosition ?? new Vector2(160f, 70f);
			windowRect.sizeDelta = new Vector2(WindowWidth, 100f);
			FocusLock focus = windowRect.gameObject.AddComponent<FocusLock>();
			focus.lockId = InputLockId;

			KrabUi.Vertical(windowRect.gameObject, 1, 0f);
			ContentSizeFitter fitter = windowRect.gameObject.AddComponent<ContentSizeFitter>();
			fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

			BuildTitlebar();

			contentHost = KrabUi.Go("Content", windowRect).transform;
			KrabUi.Vertical(contentHost.gameObject, 10, 9f);

			RebuildContent();
		}

		private void BuildTitlebar()
		{
			RectTransform bar = KrabUi.Bordered("Titlebar", windowRect, KrabUi.HeadA, KrabUi.Line);
			KrabUi.Size(bar.gameObject, -1f, 34f);
			KrabUi.Horizontal(bar.gameObject, 8, 8f);

			// Editable name: displayName is a plain string KSPField and stock KSP has no
			// editable-text PAW control for it, unlike UI_FloatRange/UI_Toggle for
			// numbers/bools, so the rename lives here as KAL's own does in its window.
			GameObject titleRow = KrabUi.Go("TitleRow", bar.transform);
			KrabUi.Horizontal(titleRow, 0, 6f);
			KrabUi.Size(titleRow, -1f, 22f, 1f);
			Text titlePrefix = KrabUi.Label(titleRow.transform, Loc("#LOC_KRAB_ui_windowTitle") + " —", 14,
				KrabUi.Tan, TextAnchor.MiddleLeft, FontStyle.Bold);
			KrabUi.Size(titlePrefix.gameObject, -1f, 22f);
			KrabUi.Field(titleRow.transform, module.displayName, 150f, text =>
			{
				module.displayName = string.IsNullOrEmpty(text) ? module.displayName : text.Trim();
			});

			undoButton = KrabUi.ImageIconButton(bar, "undo", Undo, KrabUi.TanDim, 24f);
			KrabUi.Tooltip(undoButton.gameObject, "#LOC_KRAB_tip_undo");
			redoButton = KrabUi.ImageIconButton(bar, "redo", Redo, KrabUi.TanDim, 24f);
			KrabUi.Tooltip(redoButton.gameObject, "#LOC_KRAB_tip_redo");

			Text badge = KrabUi.Label(bar,
				Loc(Simulated ? "#LOC_KRAB_ui_simEditor" : "#LOC_KRAB_ui_liveFlight"),
				10, KrabUi.Malachite, TextAnchor.MiddleRight);
			KrabUi.Size(badge.gameObject, 130f, 22f);

			KrabUi.TextButton(bar, "✕", Close, KrabUi.Panel2, KrabUi.TanDim, 13, 26f, 24f);

			DragHandler drag = bar.gameObject.AddComponent<DragHandler>();
			drag.target = windowRect;
		}

		// --------------------------------------------------------- content rebuild

		internal void RebuildContent()
		{
			// Detach before deferred Destroy so layout ignores the old content this frame.
			for (int i = contentHost.childCount - 1; i >= 0; i--)
			{
				Transform child = contentHost.GetChild(i);
				child.SetParent(null, false);
				Destroy(child.gameObject);
			}
			valueBindings.Clear();

			if (undoButton != null)
			{
				undoButton.interactable = undoStack.Count > 0;
				redoButton.interactable = redoStack.Count > 0;
			}

			if (Graph == null)
			{
				ClearAllPartHighlights();
				KrabUi.Label(contentHost, Loc("#LOC_KRAB_ui_noGraph"), 13, KrabUi.Muted, TextAnchor.MiddleCenter);
				Button create = KrabUi.TextButton(contentHost, Loc("#LOC_KRAB_ui_createGraph"),
					() => { module.CreateEmptyGraph(); RebuildContent(); },
					KrabUi.Panel2, KrabUi.GreenHi, 12, 0f, 26f);
				KrabUi.Size(create.gameObject, 160f, 26f);
				BuildFooter();
				return;
			}

			Validate();
			CollectOutputs();
			BuildPortLookups();
			UpdatePartHighlights();

			// Picker modes replace tabs/tree/simulator until resolved or cancelled.
			if ((pickerKind != PickerKind.None || pickingPart)
				&& (pickerTarget == null || !Graph.Nodes.Contains(pickerTarget)))
			{
				ClearPickerState(); // target vanished (undo, removal): drop the picker
			}
			if (pickingPart)
			{
				BuildPickPrompt();
				BuildFooter();
				return;
			}
			if (pickerKind == PickerKind.Source)
			{
				BuildSourcePicker();
				BuildFooter();
				return;
			}
			if (pickerKind == PickerKind.TargetField)
			{
				BuildTargetFieldPicker();
				BuildFooter();
				return;
			}
			if (pickerKind == PickerKind.Filter)
			{
				BuildFilterPicker();
				BuildFooter();
				return;
			}
			if (pickerKind == PickerKind.KrillGroup)
			{
				BuildKrillGroupPicker();
				BuildFooter();
				return;
			}
			if (pickerKind == PickerKind.KrillAxis)
			{
				BuildKrillAxisPicker();
				BuildFooter();
				return;
			}

			BuildTabs();
			BuildDetail();
			if (Simulated)
			{
				BuildSimulator();
			}
			BuildFooter();
		}

		private void Validate()
		{
			issues.Clear();
			errorNodes.Clear();
			warnNodes.Clear();
			issues.AddRange(Graph.Validate());
			// Graph.Validate() is scene-agnostic (it also runs from KrabGraphSelfTest with
			// no vessel loaded), so live part/field resolution can't live there. This is a
			// UI-only addition.
			foreach (KrabNode node in Graph.Nodes)
			{
				if (node.IsKnown && node.Info.name == "PartField"
					&& node.GetString("persistentId", "0") != "0"
					&& !TryResolvePartField(node, out _, out _))
				{
					issues.Add(new ValidationIssue(IssueSeverity.Warning, "partFieldMissing",
						"PartField node '" + node.id + "': target part/field not found", node.id, node.id));
				}
			}
			foreach (ValidationIssue issue in issues)
			{
				if (string.IsNullOrEmpty(issue.nodeId))
				{
					continue;
				}
				if (issue.severity == IssueSeverity.Error)
				{
					errorNodes.Add(issue.nodeId);
				}
				else if (issue.severity == IssueSeverity.Warning)
				{
					warnNodes.Add(issue.nodeId);
				}
			}
		}

		private void CollectOutputs()
		{
			outputNodes.Clear();
			foreach (KrabNode node in Graph.Nodes)
			{
				if (node.IsKnown && node.Info.kind == NodeKind.Output)
				{
					outputNodes.Add(node);
				}
			}
			if (FindOutputIndex(activeOutputId) < 0)
			{
				activeOutputId = outputNodes.Count > 0 ? outputNodes[0].id : null;
			}
		}

		private int FindOutputIndex(string id)
		{
			for (int i = 0; i < outputNodes.Count; i++)
			{
				if (outputNodes[i].id == id)
				{
					return i;
				}
			}
			return -1;
		}

		private void BuildPortLookups()
		{
			upstreamByPort.Clear();
			defaultByPort.Clear();
			foreach (KrabLink link in Graph.Links)
			{
				KrabNode from = Graph.FindNode(link.fromId);
				if (from != null)
				{
					upstreamByPort[link.toId + ":" + link.toPort] = from;
				}
			}
			foreach (KrabPortDefault def in Graph.Defaults)
			{
				defaultByPort[def.nodeId + ":" + def.port] = def;
			}
		}

		// ------------------------------------------------------------------- tabs

		private const float TabStripHeight = 32f;

		private void BuildTabs()
		{
			GameObject row = KrabUi.Go("Tabs", contentHost);
			KrabUi.Horizontal(row, 0, 6f);
			KrabUi.Size(row, -1f, TabStripHeight);

			// A plain HorizontalLayoutGroup squeezes every tab once their combined width
			// exceeds the row, making labels unreadable past ~4 outputs. A horizontal
			// scroll strip keeps each tab at its natural width; the wheel scrolls it.
			RectTransform strip = KrabUi.HScrollList(row.transform, TabStripHeight);
			foreach (KrabNode node in outputNodes)
			{
				string id = node.id;
				bool active = id == activeOutputId;
				string marker = node.Info.name == "AxisOutput" ? "◉ " : "▲ ";
				Button tab = KrabUi.TextButton(strip, marker + TabTitle(node),
					() => { activeOutputId = id; RebuildContent(); },
					active ? KrabUi.Panel2 : KrabUi.Panel,
					active ? KrabUi.GreenHi : KrabUi.Muted, 12, 0f, 26f);
				if (active)
				{
					// Panel2 vs Panel alone read as almost the same shade, so the active tab
					// also gets a bottom accent strip. Pivot (0.5, 1) plus a small negative Y
					// hangs it fully below the button instead of eating its bottom padding.
					RectTransform accent = (RectTransform)KrabUi.Go("ActiveAccent", tab.transform).transform;
					accent.anchorMin = new Vector2(0f, 0f);
					accent.anchorMax = new Vector2(1f, 0f);
					accent.pivot = new Vector2(0.5f, 1f);
					accent.anchoredPosition = new Vector2(0f, -1f);
					accent.sizeDelta = new Vector2(0f, 2f);
					Image accentImage = accent.gameObject.AddComponent<Image>();
					accentImage.color = KrabUi.GreenHi;
					accentImage.raycastTarget = false;
					accent.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
				}
			}

			KrabUi.TextButton(row.transform, Loc("#LOC_KRAB_ui_addAxis"),
				() => AddOutput(true), KrabUi.Panel, KrabUi.GreenHi, 11, 0f, 24f);
			KrabUi.TextButton(row.transform, Loc("#LOC_KRAB_ui_addTrigger"),
				() => AddOutput(false), KrabUi.Panel, KrabUi.GreenHi, 11, 0f, 24f);
		}

		private void AddOutput(bool axis)
		{
			Mutate(() =>
			{
				KrabNode output = KrabGraphEdits.AddOutput(Graph, axis);
				activeOutputId = output.id;
			});
		}

		private string TabTitle(KrabNode node)
		{
			// A custom label always wins: with several outputs of the same kind the
			// auto-detected name ("Axis Output" until bound, a raw field name after)
			// makes the tab row ambiguous.
			string label = node.GetString("label", "");
			if (!string.IsNullOrEmpty(label))
			{
				return label;
			}
			string detail = node.Info.name == "AxisOutput"
				? node.GetString("axisName", "")
				: node.GetString("actionName", "");
			return string.IsNullOrEmpty(detail) ? NodeName(node) : detail;
		}

		// ----------------------------------------------------------------- detail

		private void BuildDetail()
		{
			int index = FindOutputIndex(activeOutputId);
			if (index < 0)
			{
				KrabUi.Label(contentHost, Loc("#LOC_KRAB_ui_noOutputs"), 12, KrabUi.Muted, TextAnchor.MiddleCenter);
				return;
			}
			KrabNode output = outputNodes[index];
			BuildTargetCard(output);

			RectTransform treePanel = KrabUi.ScrollList(contentHost, TreeAreaHeight);
			KrabNode rootUpstream = UpstreamAt(output, 0);
			if (rootUpstream != null)
			{
				// The output is the root's parent: enables the source picker on a bare
				// root source (its fixed 1-port arity already suppresses the ✕ button).
				BuildTreeRow(treePanel, rootUpstream, 0, output, 0);
			}
			else
			{
				BuildDefaultRow(treePanel, output, 0, 0);
				Button start = KrabUi.TextButton(treePanel, Loc("#LOC_KRAB_ui_createGroup"),
					() => Mutate(() => KrabGraphEdits.ConnectNewGroup(Graph, output, 0)),
					KrabUi.Panel2, KrabUi.GreenHi, 11, 0f, 24f);
				KrabUi.Size(start.gameObject, 140f, 24f);
			}
		}

		private void BuildTargetCard(KrabNode output)
		{
			RectTransform card = KrabUi.Bordered("Target", contentHost, KrabUi.Panel2, KrabUi.Line);
			KrabUi.Horizontal(card.gameObject, 10, 12f);

			GameObject left = KrabUi.Go("Left", card);
			KrabUi.Vertical(left, 0, 3f);
			KrabUi.Size(left, -1f, -1f, 1f);
			KrabUi.Label(left.transform, Loc("#LOC_KRAB_ui_drives"), 10, KrabUi.TanDim);

			GameObject nameRow = KrabUi.Go("Name", left.transform);
			KrabUi.Horizontal(nameRow, 0, 6f);
			KrabUi.Size(nameRow, -1f, 20f);
			ParamLabel(nameRow.transform, Loc("#LOC_KRAB_ui_tabName"));
			string currentLabel = output.GetString("label", "");
			// A custom tab name; clearing it back to blank falls back to the
			// auto-detected name again (TextField's generic helper treats an emptied
			// field as "no change", which would make it impossible to un-rename).
			KrabUi.Field(nameRow.transform, currentLabel, 130f, text =>
			{
				text = text != null ? text.Trim() : "";
				if (text == currentLabel)
				{
					RebuildContent();
					return;
				}
				Mutate(() =>
				{
					if (text.Length == 0)
					{
						output.RemoveParam("label");
					}
					else
					{
						output.SetParam("label", text);
					}
				});
			});

			GameObject targetRow = KrabUi.Go("TargetRow", left.transform);
			KrabUi.Horizontal(targetRow, 0, 8f);
			KrabUi.Size(targetRow, -1f, 22f);
			string targetText = DescribeTarget(output, out Color targetColor);
			Text targetLabel = KrabUi.Label(targetRow.transform, targetText, 13, targetColor);
			KrabUi.Size(targetLabel.gameObject, -1f, 22f);
			KrabUi.TextButton(targetRow.transform, Loc("#LOC_KRAB_ui_pickPart"),
				() => StartPartPick(output), KrabUi.Panel, KrabUi.GreenHi, 11, 0f, 20f);

			GameObject paramRow = KrabUi.Go("Params", left.transform);
			KrabUi.Horizontal(paramRow, 0, 6f);
			KrabUi.Size(paramRow, -1f, 20f);
			if (output.Info.name == "AxisOutput")
			{
				ParamLabel(paramRow.transform, "inMin", "#LOC_KRAB_tip_paramInRange");
				NumberField(paramRow.transform, output, "inMin", "0");
				ParamLabel(paramRow.transform, "inMax", "#LOC_KRAB_tip_paramInRange");
				NumberField(paramRow.transform, output, "inMax", "1");
			}
			else
			{
				ParamLabel(paramRow.transform, Loc("#LOC_KRAB_ui_edge"));
				string edge = output.GetString("edge", "rising");
				KrabUi.TextButton(paramRow.transform, edge,
					() => Mutate(() => output.SetParam("edge", NextEdge(edge))),
					KrabUi.Inset, KrabUi.Tan, 11, 0f, 20f);
			}

			GameObject right = KrabUi.Go("Right", card);
			KrabUi.Vertical(right, 0, 3f);
			KrabUi.Size(right, 130f, -1f);

			// Copy/paste the whole input/operator subtree feeding this output's port 0,
			// so a combination built once can be replicated on another output tab.
			GameObject copyRow = KrabUi.Go("CopyPaste", right.transform);
			KrabUi.Horizontal(copyRow, 0, 5f);
			// Right-aligned to match the VALUE/✕ row below it.
			KrabUi.Spacer(copyRow.transform);
			bool hasSubtree = Graph.FindLinkTo(output.id, 0) != null;
			Button copyButton = KrabUi.ImageIconButton(copyRow.transform, "copy",
				() => { subtreeClipboard = KrabGraphEdits.CopySubtree(Graph, output, 0); },
				hasSubtree ? KrabUi.TanDim : KrabUi.Faint, 18f);
			KrabUi.Tooltip(copyButton.gameObject, "#LOC_KRAB_tip_copy");
			Button pasteButton = KrabUi.ImageIconButton(copyRow.transform, "paste",
				() =>
				{
					if (!string.IsNullOrEmpty(subtreeClipboard))
					{
						Mutate(() => KrabGraphEdits.PasteSubtree(Graph, output, 0, subtreeClipboard));
					}
				},
				!string.IsNullOrEmpty(subtreeClipboard) ? KrabUi.TanDim : KrabUi.Faint, 18f);
			KrabUi.Tooltip(pasteButton.gameObject, "#LOC_KRAB_tip_paste");

			GameObject topRight = KrabUi.Go("Top", right.transform);
			KrabUi.Horizontal(topRight, 0, 5f);
			KrabUi.Spacer(topRight.transform);
			KrabUi.Label(topRight.transform, Loc("#LOC_KRAB_ui_value"), 10, KrabUi.TanDim, TextAnchor.MiddleRight);
			Button removeOutputButton = KrabUi.IconButton(topRight.transform, "✕",
				() => Mutate(() =>
				{
					KrabGraphEdits.RemoveOutput(Graph, output);
					activeOutputId = null;
				}), KrabUi.Danger, 18f);
			KrabUi.Tooltip(removeOutputButton.gameObject, "#LOC_KRAB_tip_removeOutput");
			Text value = KrabUi.Label(right.transform, "—", 15, KrabUi.Malachite, TextAnchor.MiddleRight, FontStyle.Bold);
			AddValueBinding(output, value);
		}

		private static string NextEdge(string edge)
		{
			switch (edge.ToLowerInvariant())
			{
				case "rising": return "falling";
				case "falling": return "both";
				default: return "rising";
			}
		}

		/// <summary>
		/// Target description plus a state color: normal (bound), muted (never bound),
		/// or danger (the bound part no longer exists on this craft). The binding is
		/// flagged, never auto-cleared: a part can come back via undo or re-attach.
		/// </summary>
		private string DescribeTarget(KrabNode output, out Color color)
		{
			string field = output.Info.name == "AxisOutput"
				? output.GetString("axisName", "")
				: output.GetString("actionName", "");
			string persistentIdText = output.GetString("persistentId", "0");
			bool bound = persistentIdText != "0" && !string.IsNullOrEmpty(field);
			if (!bound)
			{
				color = KrabUi.Muted;
				return NodeName(output) + " · " + Loc("#LOC_KRAB_ui_notBound");
			}
			uint.TryParse(persistentIdText, out uint persistentId);
			if (!TryFindPart(persistentId, out Part part))
			{
				color = KrabUi.Danger;
				return NodeName(output) + " · " + Loc("#LOC_KRAB_ui_targetMissing");
			}
			color = KrabUi.Text;
			return NodeName(output) + " → " + ResolveTargetLabel(output, part, field);
		}

		private const int TargetLabelMaxChars = 15;
		private const int PartFieldLabelMaxChars = 35;
		// Simulator row label column is fixed-width and sits next to the slider, so an
		// overlong label runs into it. 35 matches that column's width; the -3 margin
		// makes room for the "[nX]" suffix when node ids are shown.
		private const int SimulatorLabelMaxChars = 35;
		private const int SimulatorLabelIdMargin = 3;

		/// <summary>Default cap, sized so a long part/field name can't stretch the target
		/// card past its layout.</summary>
		private static string Truncate(string text)
		{
			return Truncate(text, TargetLabelMaxChars);
		}

		/// <summary>Caps a string to maxChars total, ellipsizing the tail.</summary>
		private static string Truncate(string text, int maxChars)
		{
			if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
			{
				return text;
			}
			return text.Substring(0, maxChars - 1) + "…";
		}

		/// <summary>Prefers "Part title - Field/action GUI name", falling back to the raw
		/// persisted name when the live field/action can't be resolved (e.g. an unloaded
		/// module). Both halves truncate independently so neither hides the other.</summary>
		private static string ResolveTargetLabel(KrabNode output, Part part, string rawField)
		{
			string partName = Truncate(part.partInfo.title);
			if (output.Info.name == "AxisOutput")
			{
				if (TryResolveAxisField(output, out BaseAxisField axisField, out _))
				{
					string guiName = string.IsNullOrEmpty(axisField.guiName)
						? axisField.name
						: Localizer.Format(axisField.guiName);
					return partName + " - " + Truncate(guiName);
				}
			}
			else if (TryResolveAction(output, out BaseAction action, out _))
			{
				return partName + " - " + Truncate(Localizer.Format(action.guiName));
			}
			return rawField;
		}

		/// <summary>Resolves an ActionTrigger's live BaseAction, mirroring TryResolveAxisField.
		/// moduleId == 0 means a part-level action (see ApplyTarget/the picker).</summary>
		private static bool TryResolveAction(KrabNode output, out BaseAction action, out Part part)
		{
			action = null;
			uint.TryParse(output.GetString("persistentId", "0"), out uint persistentId);
			if (!TryFindPart(persistentId, out part))
			{
				return false;
			}
			string actionName = output.GetString("actionName", "");
			if (string.IsNullOrEmpty(actionName))
			{
				return false;
			}
			uint.TryParse(output.GetString("moduleId", "0"), out uint moduleId);
			if (moduleId == 0)
			{
				action = part.Actions[actionName];
			}
			else
			{
				for (int i = 0; i < part.Modules.Count; i++)
				{
					if (part.Modules[i].GetPersistentId() == moduleId)
					{
						action = part.Modules[i].Actions[actionName];
						break;
					}
				}
			}
			return action != null;
		}

		/// <summary>Finds a part by persistentId on the current craft, in flight or in the editor.</summary>
		private static bool TryFindPart(uint persistentId, out Part part)
		{
			part = null;
			if (persistentId == 0)
			{
				return false;
			}
			if (HighLogic.LoadedSceneIsFlight)
			{
				return FlightGlobals.FindLoadedPart(persistentId, out part);
			}
			if (EditorLogic.fetch == null || EditorLogic.fetch.ship == null)
			{
				return false;
			}
			List<Part> parts = EditorLogic.fetch.ship.parts;
			for (int i = 0; i < parts.Count; i++)
			{
				if (parts[i].persistentId == persistentId)
				{
					part = parts[i];
					return true;
				}
			}
			return false;
		}

		/// <summary>Resolves an AxisOutput's live BaseAxisField, in flight or in the editor (UI-only lookup).</summary>
		private static bool TryResolveAxisField(KrabNode output, out BaseAxisField field, out Part part)
		{
			field = null;
			uint.TryParse(output.GetString("persistentId", "0"), out uint persistentId);
			if (!TryFindPart(persistentId, out part))
			{
				return false;
			}
			string axisName = output.GetString("axisName", "");
			if (string.IsNullOrEmpty(axisName))
			{
				return false;
			}
			uint.TryParse(output.GetString("moduleId", "0"), out uint moduleId);
			if (moduleId != 0)
			{
				for (int i = 0; i < part.Modules.Count; i++)
				{
					if (part.Modules[i].GetPersistentId() == moduleId)
					{
						field = part.Modules[i].Fields[axisName] as BaseAxisField;
						break;
					}
				}
			}
			if (field == null)
			{
				for (int i = 0; i < part.Modules.Count && field == null; i++)
				{
					field = part.Modules[i].Fields[axisName] as BaseAxisField;
				}
			}
			return field != null;
		}

		/// <summary>Resolves a PartField source's live part+module and a display label
		/// for its bound member — a DerivedFieldsCatalog entry if one applies, otherwise
		/// the module's own BaseField (UI-only lookup, mirrors TryResolveAxisField).</summary>
		private static bool TryResolvePartField(KrabNode node, out Part part, out string label)
		{
			label = null;
			uint.TryParse(node.GetString("persistentId", "0"), out uint persistentId);
			if (!TryFindPart(persistentId, out part))
			{
				return false;
			}
			string fieldName = node.GetString("fieldName", "");
			uint.TryParse(node.GetString("moduleId", "0"), out uint moduleId);
			if (string.IsNullOrEmpty(fieldName) || moduleId == 0)
			{
				return false;
			}
			PartModule module = null;
			for (int i = 0; i < part.Modules.Count; i++)
			{
				if (part.Modules[i].GetPersistentId() == moduleId)
				{
					module = part.Modules[i];
					break;
				}
			}
			if (module == null)
			{
				return false;
			}
			if (DerivedFieldsCatalog.TryFind(module, fieldName, out DerivedFieldRule rule))
			{
				label = LocOr(rule.label, fieldName);
				return true;
			}
			BaseField field = module.Fields[fieldName];
			if (field == null)
			{
				return false;
			}
			label = string.IsNullOrEmpty(field.guiName) ? field.name : Localizer.Format(field.guiName);
			return true;
		}

		// ------------------------------------------------------------------- tree

		private KrabNode UpstreamAt(KrabNode node, int port)
		{
			upstreamByPort.TryGetValue(node.id + ":" + port, out KrabNode upstream);
			return upstream;
		}

		private Color NodeColor(KrabNode node, bool isGroup)
		{
			if (errorNodes.Contains(node.id))
			{
				return KrabUi.Danger;
			}
			if (node.id == KrabCurveWindow.OpenNodeId)
			{
				return KrabUi.Malachite; // term stays highlighted while its curve window is open
			}
			if (warnNodes.Contains(node.id))
			{
				return KrabUi.Warn;
			}
			return isGroup ? KrabUi.Tan : KrabUi.Text;
		}

		private void BuildTreeRow(Transform parent, KrabNode node, int depth, KrabNode parentGroup, int port)
		{
			if (depth > MaxTreeDepth)
			{
				return;
			}
			bool isGroup = node.IsKnown && node.Info.kind == NodeKind.Operator;

			GameObject row = KrabUi.Go("Row", parent);
			KrabUi.Horizontal(row, 0, 6f);
			KrabUi.Size(row, -1f, 21f);
			if (depth > 0)
			{
				KrabUi.Size(KrabUi.Go("Indent", row.transform), depth * 18f, 4f);
			}
			// Cycle button goes BEFORE the variable-width name label, in a fixed-width
			// slot, so it doesn't shift on screen as the operator name changes length
			// while clicking through several options in a row.
			if (isGroup && KrabGraphEdits.CompatibleOperators(Graph, node).Count > 0)
			{
				Button cycleButton = KrabUi.IconButton(row.transform, "↻",
					() => Mutate(() => KrabGraphEdits.CycleOperator(Graph, node)), KrabUi.TanDim, 18f);
				KrabUi.Tooltip(cycleButton.gameObject, "#LOC_KRAB_tip_cycleOperator");
			}
			else if (isGroup)
			{
				KrabUi.Size(KrabUi.Go("CycleSpacer", row.transform), 18f, 4f);
			}

			bool isSource = node.IsKnown && node.Info.kind == NodeKind.Source;
			if (isSource && parentGroup != null)
			{
				// Source leaves: name + selection open the grouped source picker.
				KrabNode capturedParent = parentGroup;
				int capturedSlot = port;
				Button sourceButton = KrabUi.TextButton(row.transform,
					NodeLabel(node) + (showNodeIds ? IdSuffix(node) : "") + " ▾",
					() => OpenSourcePicker(capturedParent, capturedSlot),
					KrabUi.Inset, NodeColor(node, false), 12, 0f, 20f);
				KrabUi.Tooltip(sourceButton.gameObject, NodeFullLabel(node));
			}
			else
			{
				Text name = KrabUi.Label(row.transform, NodeName(node), 12,
					NodeColor(node, isGroup), TextAnchor.MiddleLeft,
					isGroup ? FontStyle.Bold : FontStyle.Normal);
				KrabUi.Size(name.gameObject, -1f, 21f);
				if (node.IsKnown)
				{
					KrabUi.Tooltip(name.gameObject, "#LOC_KRAB_tip_node_" + node.Info.name);
				}
			}

			BuildParamFields(row.transform, node);

			KrabUi.Spacer(row.transform);
			Text value = KrabUi.Label(row.transform, "—", 12, KrabUi.Malachite, TextAnchor.MiddleRight);
			KrabUi.Size(value.gameObject, 74f, 21f);
			AddValueBinding(node, value);

			// Removable: direct child of a dynamic group (fixed-arity ports would go
			// invalid). The 18px slot is reserved either way, or the value column shifts
			// left on rows without a button and loses its vertical alignment.
			if (parentGroup != null && parentGroup.IsKnown && parentGroup.Info.HasDynamicInputs)
			{
				int capturedPort = port;
				KrabNode capturedGroup = parentGroup;
				Button removeTermButton = KrabUi.IconButton(row.transform, "✕",
					() => Mutate(() => KrabGraphEdits.RemoveTerm(Graph, capturedGroup, capturedPort)),
					KrabUi.Danger, 18f);
				KrabUi.Tooltip(removeTermButton.gameObject, "#LOC_KRAB_tip_removeTerm");
			}
			else
			{
				KrabUi.Size(KrabUi.Go("RemoveSpacer", row.transform), 18f, 4f);
			}

			if (!isGroup)
			{
				return;
			}
			int ports = KrabGraphEdits.CountInputPorts(Graph, node);
			for (int p = 0; p < ports; p++)
			{
				KrabNode upstream = UpstreamAt(node, p);
				if (upstream != null)
				{
					BuildTreeRow(parent, upstream, depth + 1, node, p);
				}
				else
				{
					BuildDefaultRow(parent, node, p, depth + 1);
				}
			}
			if (node.Info.HasDynamicInputs)
			{
				GameObject addRow = KrabUi.Go("AddRow", parent);
				KrabUi.Horizontal(addRow, 0, 6f);
				KrabUi.Size(KrabUi.Go("Indent", addRow.transform), (depth + 1) * 18f, 4f);
				KrabNode captured = node;
				KrabUi.TextButton(addRow.transform, Loc("#LOC_KRAB_ui_addTerm"),
					() => Mutate(() => KrabGraphEdits.AddTerm(Graph, captured)),
					KrabUi.Panel2, KrabUi.GreenHi, 11, 0f, 20f);
				KrabUi.TextButton(addRow.transform, Loc("#LOC_KRAB_ui_addGroup"),
					() => Mutate(() => KrabGraphEdits.AddSubgroup(Graph, captured)),
					KrabUi.Panel2, KrabUi.GreenHi, 11, 0f, 20f);
				KrabUi.TextButton(addRow.transform, Loc("#LOC_KRAB_ui_addFilter"),
					() => OpenFilterPicker(captured), KrabUi.Panel2, KrabUi.GreenHi, 11, 0f, 20f);
			}
		}

		private void BuildDefaultRow(Transform parent, KrabNode node, int port, int depth)
		{
			defaultByPort.TryGetValue(node.id + ":" + port, out KrabPortDefault def);
			GameObject row = KrabUi.Go("Default", parent);
			KrabUi.Horizontal(row, 0, 6f);
			KrabUi.Size(row, -1f, 21f);
			if (depth > 0)
			{
				KrabUi.Size(KrabUi.Go("Indent", row.transform), depth * 18f, 4f);
			}
			KrabUi.Label(row.transform, Loc("#LOC_KRAB_ui_default"), 11, KrabUi.Faint);
			string currentValue = def != null ? def.value.ToString(CultureInfo.InvariantCulture) : "0";
			string nodeId = node.id;
			int capturedPort = port;
			KrabUi.Field(row.transform, currentValue, 52f, text =>
			{
				string sanitized = SanitizeNumber(text);
				if (sanitized == null || sanitized == currentValue)
				{
					RebuildContent();
					return;
				}
				Mutate(() => Graph.SetDefault(nodeId, capturedPort,
					float.Parse(sanitized, CultureInfo.InvariantCulture)));
			});
		}

		// ---------------------------------------------------------- param editing

		private void ParamLabel(Transform parent, string text, string tipKey = null)
		{
			Text label = KrabUi.Label(parent, text, 10, KrabUi.Muted);
			KrabUi.Size(label.gameObject, -1f, 20f);
			if (tipKey != null)
			{
				KrabUi.Tooltip(label.gameObject, tipKey);
			}
		}

		private void NumberField(Transform parent, KrabNode node, string param, string fallback, float width = 50f)
		{
			string currentValue = node.GetString(param, fallback);
			KrabUi.Field(parent, currentValue, width, text =>
			{
				string sanitized = SanitizeNumber(text);
				if (sanitized == null || sanitized == currentValue)
				{
					RebuildContent(); // restore the shown value
					return;
				}
				Mutate(() => node.SetParam(param, sanitized));
			});
		}

		private void TextField(Transform parent, KrabNode node, string param, string fallback, float width = 92f)
		{
			string currentValue = node.GetString(param, fallback);
			KrabUi.Field(parent, currentValue, width, text =>
			{
				text = text != null ? text.Trim() : "";
				if (text.Length == 0 || text == currentValue)
				{
					RebuildContent();
					return;
				}
				Mutate(() => node.SetParam(param, text));
			});
		}

		/// <summary>
		/// Optional clampMin/clampMax on WeightedSum and Integrator. Presence is the
		/// toggle, since the fields can't tell "absent" from "zero": a button adds both
		/// at a wide-open -1000/1000, an X removes both. The pair is atomic.
		/// </summary>
		private void BuildClampFields(Transform parent, KrabNode node)
		{
			if (!node.HasParam("clampMin") && !node.HasParam("clampMax"))
			{
				Button addClamp = KrabUi.TextButton(parent, Loc("#LOC_KRAB_ui_addClamp"), () => Mutate(() =>
				{
					node.SetParam("clampMin", -1000f);
					node.SetParam("clampMax", 1000f);
				}), KrabUi.Panel2, KrabUi.Tan, 11, 0f, 20f);
				KrabUi.Tooltip(addClamp.gameObject, "#LOC_KRAB_tip_addClamp");
				return;
			}
			ParamLabel(parent, "clamp", "#LOC_KRAB_tip_paramClamp");
			NumberField(parent, node, "clampMin", "-1000", 44f);
			NumberField(parent, node, "clampMax", "1000", 44f);
			Button removeClamp = KrabUi.IconButton(parent, "✕", () => Mutate(() =>
			{
				node.RemoveParam("clampMin");
				node.RemoveParam("clampMax");
			}), KrabUi.Danger, 18f);
			KrabUi.Tooltip(removeClamp.gameObject, "#LOC_KRAB_tip_removeClamp");
		}

		/// <summary>
		/// Inline editable parameters per subtype. An unrecognized vocabulary value
		/// disables the node with a warning, never a crash (tolerant-parse policy).
		/// </summary>
		private void BuildParamFields(Transform parent, KrabNode node)
		{
			if (!node.IsKnown)
			{
				return;
			}
			switch (node.Info.name)
			{
				case "Constant":
					NumberField(parent, node, "value", "0");
					break;
				// ControllerInput / PlayerAxis / ScriptAxis / ActionGroupState /
				// KrillGroupState / KrillAxisState carry no inline fields: their selection
				// lives in the source picker (the "Name · Detail ▾" button).
				case "PhysicalState":
					// Sample rate only affects flight: simulation mode bypasses sampling
					// entirely (see PhysicalStateRuntime.Evaluate), so the field would do
					// nothing visible in the editor.
					if (!Simulated)
					{
						ParamLabel(parent, "s", "#LOC_KRAB_tip_paramSampleRate");
						NumberField(parent, node, "sampleRate", "0.1", 38f);
					}
					BuildUnitChip(parent, node);
					break;
				case "WeightedSum":
					ParamLabel(parent, "w", "#LOC_KRAB_tip_paramWeights");
					TextField(parent, node, "weights", "1", 110f);
					BuildClampFields(parent, node);
					break;
				case "Integrator":
					BuildClampFields(parent, node);
					break;
				case "Remap":
					if (node.HasNode("curve"))
					{
						// A curve overrides the four linear fields entirely (RemapRuntime),
						// so they are hidden while one exists.
						Button curveButton = KrabUi.ImageIconButton(parent, "curve",
							() => KrabCurveWindow.Open(module, this, node),
							KrabUi.Malachite, 20f);
						KrabUi.Tooltip(curveButton.gameObject, "#LOC_KRAB_tip_curve");
					}
					else
					{
						// Both sides carry a full "in (min-max)"/"out (min-max)" label: bare
						// numbers say neither which pair is in vs out, nor that each pair
						// is a min/max.
						ParamLabel(parent, "in (min-max)", "#LOC_KRAB_tip_paramRemapRange");
						NumberField(parent, node, "inMin", "0", 42f);
						NumberField(parent, node, "inMax", "1", 42f);
						ParamLabel(parent, "out (min-max)", "#LOC_KRAB_tip_paramRemapRange");
						NumberField(parent, node, "outMin", "0", 42f);
						NumberField(parent, node, "outMax", "1", 42f);
						Button curveButton = KrabUi.ImageIconButton(parent, "curve",
							() => KrabCurveWindow.Open(module, this, node),
							KrabUi.TanDim, 20f);
						KrabUi.Tooltip(curveButton.gameObject, "#LOC_KRAB_tip_curve");
					}
					break;
				case "GatedBlend":
					ParamLabel(parent, "thr", "#LOC_KRAB_tip_paramThreshold");
					NumberField(parent, node, "threshold", "0.5", 44f);
					ParamLabel(parent, "hys", "#LOC_KRAB_tip_paramHysteresis");
					NumberField(parent, node, "hysteresis", "0", 38f);
					ParamLabel(parent, "band", "#LOC_KRAB_tip_paramBand");
					NumberField(parent, node, "blendWidth", "0", 38f);
					break;
				case "Comparator":
					ParamLabel(parent, "thr", "#LOC_KRAB_tip_paramThreshold");
					NumberField(parent, node, "threshold", "0.5", 44f);
					ParamLabel(parent, "hys", "#LOC_KRAB_tip_paramHysteresis");
					NumberField(parent, node, "hysteresis", "0", 38f);
					break;
				case "Derivative":
					ParamLabel(parent, "τ", "#LOC_KRAB_tip_paramTau");
					NumberField(parent, node, "smoothing", "0.2", 38f);
					ParamLabel(parent, "×", "#LOC_KRAB_tip_paramScale");
					NumberField(parent, node, "scale", "1", 38f);
					break;
				case "SlewRate":
					NumberField(parent, node, "ratePerSecond", "0", 46f);
					ParamLabel(parent, "/s", "#LOC_KRAB_tip_paramRatePerSecond");
					break;
				case "Hold":
					string mode = node.GetString("mode", "track");
					Button modeButton = KrabUi.TextButton(parent, mode,
						() => Mutate(() => node.SetParam("mode", mode == "track" ? "latch" : "track")),
						KrabUi.Inset, KrabUi.Tan, 11, 0f, 20f);
					KrabUi.Tooltip(modeButton.gameObject, "#LOC_KRAB_tip_holdMode");
					break;
			}
		}

		// ------------------------------------------------------------- simulator

		private void BuildSimulator()
		{
			RectTransform panel = KrabUi.Bordered("Simulator", contentHost, KrabUi.Panel, KrabUi.Line);
			KrabUi.Vertical(panel.gameObject, 9, 6f);
			KrabUi.Label(panel, Loc("#LOC_KRAB_ui_simTitle"), 10, KrabUi.TanDim);

			// Fixed-height scroll area: the panel (and window) don't grow with the
			// number of sources.
			RectTransform list = KrabUi.ScrollList(panel, SimAreaHeight);
			bool anySlider = false;
			foreach (KrabNode node in Graph.Nodes)
			{
				if (!node.IsKnown || node.Info.kind != NodeKind.Source || node.Info.name == "Constant")
				{
					continue;
				}
				anySlider = true;
				string id = node.id;
				if (!simValues.ContainsKey(id))
				{
					simValues[id] = 0f;
				}

				GameObject row = KrabUi.Go("SimRow", list);
				KrabUi.Horizontal(row, 0, 8f);
				KrabUi.Size(row, -1f, 20f);
				Text label = KrabUi.Label(row.transform,
					SimulatorRowLabel(node), 12, KrabUi.Text);
				KrabUi.Size(label.gameObject, 200f, 20f);
				KrabUi.Tooltip(label.gameObject, NodeFullLabel(node));

				if (node.Info.name == "ActionGroupState" || node.Info.name == "KrillGroupState")
				{
					KrabUi.Spacer(row.transform);
					Button toggle = null;
					toggle = KrabUi.TextButton(row.transform,
						Loc(simValues[id] >= 0.5f ? "#LOC_KRAB_ui_on" : "#LOC_KRAB_ui_off"), () =>
					{
						simValues[id] = simValues[id] >= 0.5f ? 0f : 1f;
						toggle.GetComponentInChildren<Text>().text =
							Loc(simValues[id] >= 0.5f ? "#LOC_KRAB_ui_on" : "#LOC_KRAB_ui_off");
					}, KrabUi.Inset, KrabUi.Tan, 11, 56f, 20f);
				}
				else
				{
					// Slider stays canonical (SI); the readout shows SI plus the player's
					// chosen unit in parentheses when it differs, e.g. "20.7 m/s (41.2 kn)".
					bool isPhysical = node.Info.name == "PhysicalState";
					string metric = isPhysical ? node.GetString("metric", "") : "";
					string displayUnit = isPhysical ? node.GetString("displayUnit", "") : "";
					Text valueText = null;
					GetSimRange(node, out float min, out float max);
					KrabUi.HSlider(row.transform, min, max, simValues[id], v =>
					{
						simValues[id] = v;
						if (valueText != null)
						{
							valueText.text = isPhysical ? KrabUnits.DualFormat(metric, displayUnit, v) : v.ToString("F2");
						}
					});
					valueText = KrabUi.Label(row.transform,
						isPhysical ? KrabUnits.DualFormat(metric, displayUnit, simValues[id]) : simValues[id].ToString("F2"),
						12, KrabUi.Malachite, TextAnchor.MiddleRight);
					KrabUi.Size(valueText.gameObject, 118f, 20f);
				}
			}
			if (!anySlider)
			{
				KrabUi.Label(list, "—", 12, KrabUi.Muted);
			}
			KrabUi.Label(panel, Loc("#LOC_KRAB_ui_simHint"), 11, KrabUi.Muted);
		}

		/// <summary>Per-source slider ranges; per-metric defaults for physical sources.</summary>
		private static void GetSimRange(KrabNode node, out float min, out float max)
		{
			min = -1f;
			max = 1f;
			switch (node.Info.name)
			{
				case "ControllerInput":
					min = 0f;
					max = 1f;
					return;
				case "PlayerAxis":
				case "ScriptAxis":
					if (node.GetString("channel", "").Equals("MainThrottle", System.StringComparison.OrdinalIgnoreCase))
					{
						min = 0f;
					}
					return;
				case "PhysicalState":
					switch (node.GetString("metric", "").ToLowerInvariant())
					{
						case "mach": min = 0f; max = 10f; return;
						case "altitudeasl": min = 0f; max = 20000f; return;
						case "altituderadar": min = 0f; max = 2000f; return;
						case "dynamicpressure": min = 0f; max = 120f; return;
						case "staticpressure": min = 0f; max = 120f; return;
						case "atmdensity": min = 0f; max = 1.5f; return;
						case "gforce": min = 0f; max = 12f; return;
						case "externaltemperature": min = 0f; max = 1500f; return;
						case "angularvelocitymag": min = 0f; max = 300f; return;
						case "verticalspeed": min = -300f; max = 300f; return;
						case "pitchrate":
						case "rollrate":
						case "yawrate": min = -180f; max = 180f; return;
						case "mass": min = 0f; max = 50f; return; // tons, a helicopter-sized default, not a hard cap
						case "pitch":
						case "bank": min = -180f; max = 180f; return;
						case "heading": min = 0f; max = 360f; return;
						case "forwardspeed": min = -100f; max = 500f; return; // m/s, can go negative (reversing)
						case "lateralspeed": min = -100f; max = 100f; return; // m/s, sideslip/drift
						default: min = 0f; max = 500f; return; // speeds, m/s
					}
				case "PartField":
					// No per-field metadata to size this from (readouts rarely carry a
					// UI_FloatRange/UI_MinMaxRange) — same generic-sensor fallback as an
					// unlisted PhysicalState metric above.
					min = 0f;
					max = 500f;
					return;
			}
		}

		// ---------------------------------------------------------------- footer

		private void BuildFooter()
		{
			if (Graph != null && issues.Count > 0)
			{
				RectTransform strip = KrabUi.Bordered("Issues", contentHost, KrabUi.Panel, KrabUi.Line);
				KrabUi.Vertical(strip.gameObject, 7, 2f);
				int shown = 0;
				// 12px minimum here: at 10px this strip is the least readable line of
				// the window.
				foreach (ValidationIssue issue in issues)
				{
					if (shown++ >= 4)
					{
						KrabUi.Label(strip, "… +" + (issues.Count - 4), 12, KrabUi.Muted);
						break;
					}
					Color color = issue.severity == IssueSeverity.Error ? KrabUi.Danger
						: issue.severity == IssueSeverity.Warning ? KrabUi.Warn : KrabUi.Muted;
					KrabUi.Label(strip, "● " + issue.LocalizedText(), 12, color);
				}
			}

			// Close lives only in the titlebar; this row is the status line plus the two
			// session-scoped display toggles.
			GameObject row = KrabUi.Go("Footer", contentHost);
			KrabUi.Horizontal(row, 0, 10f);
			Text status = KrabUi.Label(row.transform, module.GraphStatusText, 12, KrabUi.Muted);
			KrabUi.Size(status.gameObject, -1f, 22f, 1f);

			Button idsToggle = KrabUi.TextButton(row.transform,
				Loc(showNodeIds ? "#LOC_KRAB_ui_idsShown" : "#LOC_KRAB_ui_idsHidden"),
				() => { showNodeIds = !showNodeIds; RebuildContent(); },
				KrabUi.Inset, showNodeIds ? KrabUi.Tan : KrabUi.Muted, 11, 0f, 20f);
			KrabUi.Tooltip(idsToggle.gameObject, "#LOC_KRAB_tip_toggleIds");

			Button priorityToggle = KrabUi.TextButton(row.transform,
				Loc(invertHighlightPriority ? "#LOC_KRAB_ui_prioritySource" : "#LOC_KRAB_ui_priorityTarget"),
				() => { invertHighlightPriority = !invertHighlightPriority; UpdatePartHighlights(); },
				KrabUi.Inset, invertHighlightPriority ? KrabUi.Tan : KrabUi.Muted, 11, 0f, 20f);
			KrabUi.Tooltip(priorityToggle.gameObject, "#LOC_KRAB_tip_togglePriority");
		}

		// --------------------------------------------------------------- pickers

		private void OpenSourcePicker(KrabNode target, int port)
		{
			pickerKind = PickerKind.Source;
			pickerTarget = target;
			pickerPort = port;
			RebuildContent();
		}

		/// <summary>
		/// Opens the picker for KrabGraphEdits.InsertableFilters (Remap, Derivative,
		/// SlewRate, Comparator, Hold): the only way to add one to a group, since they
		/// are neither a cycleable combination operator nor a source.
		/// </summary>
		private void OpenFilterPicker(KrabNode group)
		{
			pickerKind = PickerKind.Filter;
			pickerTarget = group;
			RebuildContent();
		}

		/// <summary>
		/// Opens the KRILL extended-group number picker for a brand-new source;
		/// pickerTarget:pickerPort are already set by OpenSourcePicker, this only swaps
		/// which panel shows. Not a scene gesture, so no InputLockManager involved.
		/// </summary>
		private void StartKrillGroupPick()
		{
			pickerKind = PickerKind.KrillGroup;
			RebuildContent();
		}

		/// <summary>Opens the KRILL axis number picker (5..MaxVisibleAxis) for a brand-new
		/// source: same non-scene, no-InputLockManager shape as StartKrillGroupPick.</summary>
		private void StartKrillAxisPick()
		{
			pickerKind = PickerKind.KrillAxis;
			RebuildContent();
		}

		private void StartPartPick(KrabNode output)
		{
			pickerTarget = output;
			pickerPort = 0;
			pickedPart = null;
			pickingPart = true;
			// Scene clicks must reach us, not grab editor parts / open PAWs.
			InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, PickLockId);
			SetCrewHatchInterface(false);
			RebuildContent();
		}

		/// <summary>
		/// Same scene part-pick as StartPartPick, but for authoring a new Part Field
		/// source. pickerTarget:pickerPort (set by OpenSourcePicker) must NOT be
		/// overwritten: they name the consumer the new source will feed.
		/// </summary>
		private void StartPartFieldPick()
		{
			pickerNewSourceSubtype = "PartField";
			pickedPart = null;
			pickingPart = true;
			InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, PickLockId);
			SetCrewHatchInterface(false);
			RebuildContent();
		}

		/// <summary>
		/// Suppresses the stock crew-hatch/EVA popup while a scene part-pick is active
		/// (flight only). CrewHatchController.LateUpdate never consults InputLockManager,
		/// so the picker's ALLBUTCAMERAS lock does not stop the popup on its own.
		/// </summary>
		private bool hatchInterfaceDisabledByPicker;

		private void SetCrewHatchInterface(bool enabled)
		{
			if (!HighLogic.LoadedSceneIsFlight || CrewHatchController.fetch == null)
			{
				return;
			}
			if (!enabled)
			{
				CrewHatchController.fetch.DisableInterface();
				hatchInterfaceDisabledByPicker = true;
				return;
			}
			if (!hatchInterfaceDisabledByPicker) // never re-enable what we did not disable
			{
				return;
			}
			hatchInterfaceDisabledByPicker = false;
			CameraManager cam = CameraManager.Instance;
			bool inIva = cam != null && (cam.currentCameraMode == CameraManager.CameraMode.IVA
				|| cam.currentCameraMode == CameraManager.CameraMode.Internal);
			if (!inIva) // stock keeps it off on its own while in IVA
			{
				CrewHatchController.fetch.EnableInterface();
			}
		}

		/// <summary>The active output tab's bound part, if any and if still resolvable
		/// (loaded). Null when nothing is bound, or the target no longer exists.</summary>
		private Part ResolveActiveOutputTargetPart()
		{
			int index = FindOutputIndex(activeOutputId);
			if (index < 0)
			{
				return null;
			}
			KrabNode output = outputNodes[index];
			uint.TryParse(output.GetString("persistentId", "0"), out uint persistentId);
			if (persistentId == 0 || !TryFindPart(persistentId, out Part part))
			{
				return null;
			}
			return part;
		}

		/// <summary>A part's symmetry siblings only, not the part itself: callers add that
		/// separately, at a different priority tier. Full group with no parent filter,
		/// which would break mirror symmetry such as left/right gear on different parents.</summary>
		private static HashSet<Part> SymmetryGroupOf(Part part)
		{
			HashSet<Part> result = new HashSet<Part>();
			if (part == null || part.symmetryCounterparts == null)
			{
				return result;
			}
			for (int i = 0; i < part.symmetryCounterparts.Count; i++)
			{
				if (part.symmetryCounterparts[i] != null)
				{
					result.Add(part.symmetryCounterparts[i]);
				}
			}
			return result;
		}

		private static void ApplyTier(Dictionary<Part, Color> desired, Part part, Color color)
		{
			if (part != null)
			{
				desired[part] = color;
			}
		}

		private static void ApplyTier(Dictionary<Part, Color> desired, HashSet<Part> parts, Color color)
		{
			foreach (Part p in parts)
			{
				if (p != null)
				{
					desired[p] = color;
				}
			}
		}

		/// <summary>Every part read by a Part Field reachable from the active output's
		/// tree. Direct binds and their symmetry siblings stay separate so direct can
		/// outrank kin. Scoped to the active tab, not to the whole graph.</summary>
		private void CollectSourceGroups(HashSet<Part> direct, HashSet<Part> kin)
		{
			int index = FindOutputIndex(activeOutputId);
			if (index < 0)
			{
				return;
			}
			HashSet<KrabNode> visited = new HashSet<KrabNode>();
			CollectPartFieldNodes(UpstreamAt(outputNodes[index], 0), visited, direct, kin);
		}

		/// <summary>visited guards against walking the same node twice through a
		/// REUSE A SIGNAL fan-out (a node can feed more than one port in the same
		/// tree).</summary>
		private void CollectPartFieldNodes(KrabNode node, HashSet<KrabNode> visited, HashSet<Part> direct, HashSet<Part> kin)
		{
			if (node == null || !visited.Add(node))
			{
				return;
			}
			if (node.IsKnown && node.Info.name == "PartField" && TryResolvePartField(node, out Part part, out _))
			{
				direct.Add(part);
				foreach (Part sibling in SymmetryGroupOf(part))
				{
					kin.Add(sibling);
				}
			}
			if (!node.IsKnown || node.Info.kind != NodeKind.Operator)
			{
				return;
			}
			int ports = KrabGraphEdits.CountInputPorts(Graph, node);
			for (int p = 0; p < ports; p++)
			{
				CollectPartFieldNodes(UpstreamAt(node, p), visited, direct, kin);
			}
		}

		/// <summary>
		/// Recomputes which parts glow and in what color, then applies only the diff
		/// (idempotent, called on every RebuildContent, so it must stay cheap). The
		/// weakest tier is applied first so a stronger tier's dictionary write wins.
		/// </summary>
		private void UpdatePartHighlights()
		{
			Part target = ResolveActiveOutputTargetPart();
			HashSet<Part> targetKin = SymmetryGroupOf(target);
			HashSet<Part> sourceDirect = new HashSet<Part>();
			HashSet<Part> sourceKin = new HashSet<Part>();
			CollectSourceGroups(sourceDirect, sourceKin);

			Dictionary<Part, Color> desired = new Dictionary<Part, Color>();
			if (invertHighlightPriority)
			{
				ApplyTier(desired, targetKin, TargetKinshipColor);
				ApplyTier(desired, target, TargetHighlightColor);
				ApplyTier(desired, sourceKin, SourceKinshipColor);
				ApplyTier(desired, sourceDirect, SourceHighlightColor);
			}
			else
			{
				ApplyTier(desired, sourceKin, SourceKinshipColor);
				ApplyTier(desired, sourceDirect, SourceHighlightColor);
				ApplyTier(desired, targetKin, TargetKinshipColor);
				ApplyTier(desired, target, TargetHighlightColor);
			}

			List<Part> stale = null;
			foreach (KeyValuePair<Part, Color> kv in highlightedParts)
			{
				if (!desired.ContainsKey(kv.Key))
				{
					if (stale == null)
					{
						stale = new List<Part>();
					}
					stale.Add(kv.Key);
				}
			}
			if (stale != null)
			{
				for (int i = 0; i < stale.Count; i++)
				{
					highlightedParts.Remove(stale[i]);
					if (!hoverGroup.Contains(stale[i]))
					{
						stale[i].SetHighlightDefault();
					}
				}
			}
			foreach (KeyValuePair<Part, Color> kv in desired)
			{
				if (highlightedParts.TryGetValue(kv.Key, out Color current) && current == kv.Value)
				{
					continue;
				}
				highlightedParts[kv.Key] = kv.Value;
				if (hoverGroup.Contains(kv.Key))
				{
					continue; // the transient cyan hover wins on screen while it lasts
				}
				kv.Key.SetHighlightType(Part.HighlightType.AlwaysOn);
				kv.Key.SetHighlightColor(kv.Value);
				kv.Key.SetHighlight(true, false);
			}
		}

		private void ClearAllPartHighlights()
		{
			foreach (KeyValuePair<Part, Color> kv in highlightedParts)
			{
				if (!hoverGroup.Contains(kv.Key))
				{
					kv.Key.SetHighlightDefault();
				}
			}
			highlightedParts.Clear();
		}

		/// <summary>Restores a part's persistent highlight color if it still has one,
		/// otherwise clears it to default. Used when the transient pick-hover moves off a
		/// part, so it doesn't erase a target/source highlight underneath.</summary>
		private void RestoreOrClearHighlight(Part part)
		{
			if (part == null)
			{
				return;
			}
			if (highlightedParts.TryGetValue(part, out Color color))
			{
				part.SetHighlightType(Part.HighlightType.AlwaysOn);
				part.SetHighlightColor(color);
				part.SetHighlight(true, false);
			}
			else
			{
				part.SetHighlightDefault();
			}
		}

		private void ClearPickerState()
		{
			pickerKind = PickerKind.None;
			pickingPart = false;
			pickerTarget = null;
			pickerNewSourceSubtype = null;
			pickedPart = null;
			pendingPickPart = null;
			for (int i = 0; i < hoverGroup.Count; i++)
			{
				RestoreOrClearHighlight(hoverGroup[i]);
			}
			hoverGroup.Clear();
			hoverPart = null;
			InputLockManager.RemoveControlLock(PickLockId);
			SetCrewHatchInterface(true);
		}

		private void CancelPicker()
		{
			ClearPickerState();
			RebuildContent();
		}

		private bool PartOnSameCraft(Part candidate)
		{
			if (HighLogic.LoadedSceneIsFlight)
			{
				return candidate.vessel != null && candidate.vessel == module.vessel;
			}
			return EditorLogic.fetch != null && EditorLogic.fetch.ship != null
				&& EditorLogic.fetch.ship.parts.Contains(candidate);
		}

		/// <summary>
		/// Scene picking (the KAL gesture): hover highlight, confirm on mouse-UP.
		/// Releasing the input lock on mouse-down lets the editor's own part-drag
		/// grab the part while the button is still physically held.
		/// </summary>
		private void HandlePartPicking()
		{
			if (Input.GetKeyDown(KeyCode.Escape))
			{
				CancelPicker();
				return;
			}

			// A part was pressed on; keep the lock exactly as-is (don't touch hover)
			// until the button is actually released, so the editor never sees an
			// unlocked frame while it's still physically held down.
			if (pendingPickPart != null)
			{
				if (Input.GetMouseButtonUp(0))
				{
					pickedPart = pendingPickPart;
					pendingPickPart = null;
					pickingPart = false;
					InputLockManager.RemoveControlLock(PickLockId);
					SetCrewHatchInterface(true);
					pickerKind = PickerKind.TargetField;
					RebuildContent();
				}
				return;
			}

			Part hovered = Mouse.HoveredPart;
			if (hovered != null && !PartOnSameCraft(hovered))
			{
				hovered = null;
			}
			if (hovered != hoverPart)
			{
				// Same guard as ClearPickerState: don't erase a persistent target/source
				// highlight just because the transient pick-hover moved off of it.
				for (int i = 0; i < hoverGroup.Count; i++)
				{
					RestoreOrClearHighlight(hoverGroup[i]);
				}
				hoverGroup.Clear();
				hoverPart = hovered;
				if (hoverPart != null)
				{
					// Preview the whole symmetry group, not just the part under the cursor.
					hoverGroup.Add(hoverPart);
					hoverGroup.AddRange(SymmetryGroupOf(hoverPart));
					for (int i = 0; i < hoverGroup.Count; i++)
					{
						hoverGroup[i].SetHighlightType(Part.HighlightType.AlwaysOn);
						hoverGroup[i].SetHighlightColor(Color.cyan);
						hoverGroup[i].SetHighlight(true, false);
					}
				}
			}
			if (Input.GetMouseButtonDown(0) && hoverPart != null
				&& (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject()))
			{
				pendingPickPart = hoverPart;
				for (int i = 0; i < hoverGroup.Count; i++)
				{
					RestoreOrClearHighlight(hoverGroup[i]);
				}
				hoverGroup.Clear();
				hoverPart = null;
				// Lock stays active — released only once mouse-up confirms the pick.
			}
		}

		private void BuildPickPrompt()
		{
			RectTransform panel = KrabUi.Bordered("PickPrompt", contentHost, KrabUi.Panel2, KrabUi.Line);
			KrabUi.Vertical(panel.gameObject, 12, 6f);
			KrabUi.Label(panel, Loc("#LOC_KRAB_ui_pickPrompt"), 13, KrabUi.Tan, TextAnchor.MiddleCenter);
			Button cancel = KrabUi.TextButton(panel, Loc("#LOC_KRAB_ui_cancel"), CancelPicker,
				KrabUi.Panel, KrabUi.Muted, 12, 90f, 24f);
			KrabUi.Size(cancel.gameObject, 90f, 24f);
		}

		private void BuildSourcePicker()
		{
			RectTransform panel = KrabUi.Bordered("SourcePicker", contentHost, KrabUi.Panel, KrabUi.Line);
			KrabUi.Vertical(panel.gameObject, 9, 6f);
			KrabUi.Label(panel, Loc("#LOC_KRAB_ui_pickSource"), 10, KrabUi.TanDim);
			RectTransform list = KrabUi.ScrollList(panel, 300f);

			RectTransform playerGrid = BuildVocabularyFamily(list, "#LOC_KRAB_fam_player", Channels, "#LOC_KRAB_ch_",
				name => ApplyNewSource("PlayerAxis", "channel", name), "#LOC_KRAB_tip_fam_player");
			// KRILL's virtual axes (5+; 1-4 mirror stock's Custom01..04, already above)
			// join the PLAYER AXES family as one inline button rather than getting their
			// own header. Hidden unless the installed KRILL exposes GetAxisState (0.3.0+).
			if (KrillGroupBridge.AxisInstalled)
			{
				Button krillAxisButton = KrabUi.TextButton(playerGrid, Loc("#LOC_KRAB_ui_pickKrillAxis"),
					StartKrillAxisPick, KrabUi.Panel2, KrabUi.GreenHi, 11, 0f, 22f);
				KrabUi.Tooltip(krillAxisButton.gameObject, "#LOC_KRAB_tip_krillAxis");
			}
			BuildVocabularyFamily(list, "#LOC_KRAB_fam_script", Channels, "#LOC_KRAB_ch_",
				name => ApplyNewSource("ScriptAxis", "channel", name), "#LOC_KRAB_tip_fam_script");
			BuildVocabularyFamily(list, "#LOC_KRAB_fam_physical", Metrics, "#LOC_KRAB_met_",
				name => ApplyNewSource("PhysicalState", "metric", name), "#LOC_KRAB_tip_fam_physical");
			RectTransform actionGroupGrid = BuildVocabularyFamily(list, "#LOC_KRAB_fam_actionGroup",
				ActionGroupNames, "#LOC_KRAB_ag_",
				name => ApplyNewSource("ActionGroupState", "group", name), "#LOC_KRAB_tip_fam_actionGroup");
			// KRILL's extended groups (11+) join the ACTION GROUP family as one inline
			// button, not a grid of their own: up to 89 groups would bloat this list, so
			// the number picker it opens holds them. Hidden if KRILL isn't installed.
			if (KrillGroupBridge.Installed)
			{
				Button krillGroupButton = KrabUi.TextButton(actionGroupGrid, Loc("#LOC_KRAB_ui_pickKrillGroup"),
					StartKrillGroupPick, KrabUi.Panel2, KrabUi.GreenHi, 11, 0f, 22f);
				KrabUi.Tooltip(krillGroupButton.gameObject, "#LOC_KRAB_tip_krillGroup");
			}

			// No fixed vocabulary (depends on the picked part): same "Pick target…" scene
			// gesture as an output's target, but it starts a NEW source instead of
			// retargeting an existing node (StartPartFieldPick, not StartPartPick).
			Text partFieldHeader = KrabUi.Label(list, Loc("#LOC_KRAB_fam_partField"), 11, KrabUi.TanDim);
			KrabUi.Tooltip(partFieldHeader.gameObject, "#LOC_KRAB_tip_fam_partField");
			RectTransform partFieldGrid = KrabUi.Grid(list, 138f, 22f);
			KrabUi.TextButton(partFieldGrid, Loc("#LOC_KRAB_ui_pickPart"), StartPartFieldPick,
				KrabUi.Panel2, KrabUi.GreenHi, 11, 0f, 22f);

			// Hidden when this controller's own "Show KRAB Input axes" PAW toggle is off,
			// matching what that toggle already does to PAW and the Axis Groups screen.
			if (module.showInputAxes)
			{
				Text krabInputHeader = KrabUi.Label(list, Loc("#LOC_KRAB_fam_krabInput"), 11, KrabUi.TanDim);
				KrabUi.Tooltip(krabInputHeader.gameObject, "#LOC_KRAB_tip_fam_krabInput");
				RectTransform slotGrid = KrabUi.Grid(list, 138f, 22f);
				for (int slot = 1; slot <= ModuleKRABController.InputSlotCount; slot++)
				{
					string value = slot.ToString();
					KrabUi.TextButton(slotGrid, Localizer.Format("#LOC_KRAB_ui_slot", value),
						() => ApplyNewSource("ControllerInput", "slot", value),
						KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
				}
			}

			Text constantHeader = KrabUi.Label(list, Loc("#LOC_KRAB_fam_constant"), 11, KrabUi.TanDim);
			KrabUi.Tooltip(constantHeader.gameObject, "#LOC_KRAB_tip_fam_constant");
			RectTransform constGrid = KrabUi.Grid(list, 138f, 22f);
			KrabUi.TextButton(constGrid, NodeDisplay("Constant"),
				() => ApplyNewSource("Constant", "value", "0"),
				KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);

			// Turn this leaf into an operator instead of a source. Reaches any port,
			// including a fixed-arity node's own (chains like Remap→SlewRate), unlike
			// "+Term/+Group/+Filter" which only append to a dynamic group.
			Text operatorsHeader = KrabUi.Label(list, Loc("#LOC_KRAB_fam_operators"), 11, KrabUi.TanDim);
			KrabUi.Tooltip(operatorsHeader.gameObject, "#LOC_KRAB_tip_fam_operators");
			RectTransform opGrid = KrabUi.Grid(list, 138f, 22f);
			Button weightedSumButton = KrabUi.TextButton(opGrid, NodeDisplay("WeightedSum"),
				() => ApplyNewOperator("WeightedSum"), KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
			KrabUi.Tooltip(weightedSumButton.gameObject, "#LOC_KRAB_tip_node_WeightedSum");
			foreach (string filterSubtype in KrabGraphEdits.InsertableFilters)
			{
				string captured = filterSubtype;
				Button filterButton = KrabUi.TextButton(opGrid, NodeDisplay(captured),
					() => ApplyNewOperator(captured), KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
				KrabUi.Tooltip(filterButton.gameObject, "#LOC_KRAB_tip_node_" + captured);
			}

			// Same insertion mechanism as the shaping filters above, in its own labeled
			// sub-section: pure math functions, no state or params.
			Text trigHeader = KrabUi.Label(list, Loc("#LOC_KRAB_fam_trig"), 11, KrabUi.TanDim);
			KrabUi.Tooltip(trigHeader.gameObject, "#LOC_KRAB_tip_fam_trig");
			RectTransform trigGrid = KrabUi.Grid(list, 138f, 22f);
			foreach (string trigSubtype in KrabGraphEdits.InsertableTrigFunctions)
			{
				string captured = trigSubtype;
				Button trigButton = KrabUi.TextButton(trigGrid, NodeDisplay(captured),
					() => ApplyNewOperator(captured), KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
				KrabUi.Tooltip(trigButton.gameObject, "#LOC_KRAB_tip_node_" + captured);
			}

			// Fan-out: feed this port from a node that already exists elsewhere in the
			// graph (the tree view then shows it under both consumers).
			List<KrabNode> reusable = KrabGraphEdits.ReusableSignals(Graph, pickerTarget, pickerPort);
			if (reusable.Count > 0)
			{
				Text reuseHeader = KrabUi.Label(list, Loc("#LOC_KRAB_fam_reuse"), 11, KrabUi.TanDim);
				KrabUi.Tooltip(reuseHeader.gameObject, "#LOC_KRAB_tip_fam_reuse");
				foreach (KrabNode candidate in reusable)
				{
					KrabNode captured = candidate;
					Button reuseButton = KrabUi.TextButton(list, NodeLabel(candidate) + IdSuffix(candidate),
						() => ApplyExistingSource(captured), KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
					KrabUi.Tooltip(reuseButton.gameObject, NodeFullLabel(candidate));
				}
			}

			Button cancel = KrabUi.TextButton(panel, Loc("#LOC_KRAB_ui_cancel"), CancelPicker,
				KrabUi.Panel2, KrabUi.Muted, 12, 90f, 24f);
			KrabUi.Size(cancel.gameObject, 90f, 24f);
		}

		/// <summary>
		/// Picker for KrabGraphEdits.InsertableFilters and InsertableTrigFunctions,
		/// opened by "+ Filter" on a group. Selecting one adds it as a new term via
		/// AddSubgroup, which auto-fills whatever ports it needs.
		/// </summary>
		private void BuildFilterPicker()
		{
			RectTransform panel = KrabUi.Bordered("FilterPicker", contentHost, KrabUi.Panel, KrabUi.Line);
			KrabUi.Vertical(panel.gameObject, 9, 6f);
			KrabUi.Label(panel, Loc("#LOC_KRAB_ui_pickFilter"), 10, KrabUi.TanDim);
			RectTransform list = KrabUi.ScrollList(panel, 160f);

			KrabNode target = pickerTarget;
			foreach (string subtype in KrabGraphEdits.InsertableFilters)
			{
				string captured = subtype;
				Button filterButton = KrabUi.TextButton(list, NodeDisplay(captured), () =>
				{
					ClearPickerState();
					Mutate(() => KrabGraphEdits.AddSubgroup(Graph, target, captured));
				}, KrabUi.Panel2, KrabUi.Text, 12, 0f, 24f);
				KrabUi.Tooltip(filterButton.gameObject, "#LOC_KRAB_tip_node_" + captured);
			}

			Text trigHeader = KrabUi.Label(list, Loc("#LOC_KRAB_fam_trig"), 10, KrabUi.TanDim);
			KrabUi.Tooltip(trigHeader.gameObject, "#LOC_KRAB_tip_fam_trig");
			foreach (string trigSubtype in KrabGraphEdits.InsertableTrigFunctions)
			{
				string captured = trigSubtype;
				Button trigButton = KrabUi.TextButton(list, NodeDisplay(captured), () =>
				{
					ClearPickerState();
					Mutate(() => KrabGraphEdits.AddSubgroup(Graph, target, captured));
				}, KrabUi.Panel2, KrabUi.Text, 12, 0f, 24f);
				KrabUi.Tooltip(trigButton.gameObject, "#LOC_KRAB_tip_node_" + captured);
			}

			Button cancel = KrabUi.TextButton(panel, Loc("#LOC_KRAB_ui_cancel"), CancelPicker,
				KrabUi.Panel2, KrabUi.Muted, 12, 90f, 24f);
			KrabUi.Size(cancel.gameObject, 90f, 24f);
		}

		/// <summary>
		/// Number picker for a KRILL extended group (11..KrillGroupBridge.MaxVisibleGroup,
		/// mirroring KRILL's own visibility cap live). Numbers only: resolving group names
		/// would need KrillQuery.GetGroupName plus the ship's part list.
		/// </summary>
		private void BuildKrillGroupPicker()
		{
			RectTransform panel = KrabUi.Bordered("KrillGroupPicker", contentHost, KrabUi.Panel, KrabUi.Line);
			KrabUi.Vertical(panel.gameObject, 9, 6f);
			KrabUi.Label(panel, Loc("#LOC_KRAB_ui_pickKrillGroupHeader"), 10, KrabUi.TanDim);
			RectTransform list = KrabUi.ScrollList(panel, 160f);

			RectTransform grid = KrabUi.Grid(list, 60f, 22f);
			int cap = KrillGroupBridge.MaxVisibleGroup;
			for (int g = 11; g <= cap; g++)
			{
				string value = g.ToString();
				KrabUi.TextButton(grid, value, () => ApplyNewSource("KrillGroupState", "group", value),
					KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
			}

			Button cancel = KrabUi.TextButton(panel, Loc("#LOC_KRAB_ui_cancel"), CancelPicker,
				KrabUi.Panel2, KrabUi.Muted, 12, 90f, 24f);
			KrabUi.Size(cancel.gameObject, 90f, 24f);
		}

		/// <summary>
		/// Number picker for a KRILL axis (5..KrillGroupBridge.MaxVisibleAxis, mirroring
		/// KRILL's own visibility cap live). Starts at 5 because axes 1-4 are stock's own
		/// custom axes, already offered as Custom01..04 in the PLAYER AXES family.
		/// </summary>
		private void BuildKrillAxisPicker()
		{
			RectTransform panel = KrabUi.Bordered("KrillAxisPicker", contentHost, KrabUi.Panel, KrabUi.Line);
			KrabUi.Vertical(panel.gameObject, 9, 6f);
			KrabUi.Label(panel, Loc("#LOC_KRAB_ui_pickKrillAxisHeader"), 10, KrabUi.TanDim);
			RectTransform list = KrabUi.ScrollList(panel, 160f);

			RectTransform grid = KrabUi.Grid(list, 60f, 22f);
			int cap = KrillGroupBridge.MaxVisibleAxis;
			for (int a = 5; a <= cap; a++)
			{
				string value = a.ToString();
				KrabUi.TextButton(grid, value, () => ApplyNewSource("KrillAxisState", "axis", value),
					KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
			}

			Button cancel = KrabUi.TextButton(panel, Loc("#LOC_KRAB_ui_cancel"), CancelPicker,
				KrabUi.Panel2, KrabUi.Muted, 12, 90f, 24f);
			KrabUi.Size(cancel.gameObject, 90f, 24f);
		}

		/// <summary>Returns the family's grid so a caller can append extra buttons inline
		/// with the vocabulary entries; most callers ignore the return value.</summary>
		private RectTransform BuildVocabularyFamily(RectTransform list, string familyKey, string[] entries,
			string entryKeyPrefix, System.Action<string> onPick, string tipKey = null)
		{
			Text header = KrabUi.Label(list, Loc(familyKey), 11, KrabUi.TanDim);
			if (tipKey != null)
			{
				KrabUi.Tooltip(header.gameObject, tipKey);
			}
			RectTransform grid = KrabUi.Grid(list, 138f, 22f);
			foreach (string entry in entries)
			{
				string captured = entry;
				KrabUi.TextButton(grid, LocOr(entryKeyPrefix + entry, entry), () => onPick(captured),
					KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
			}
			return grid;
		}

		private void ApplyNewSource(string subtype, string paramName, string paramValue)
		{
			KrabNode target = pickerTarget;
			int port = pickerPort;
			ClearPickerState();
			Mutate(() =>
			{
				KrabNode source = KrabGraphEdits.ReplaceTermWithSource(Graph, target, port, subtype);
				source.SetParam(paramName, paramValue);
			});
		}

		/// <summary>
		/// Turns the leaf under this picker into an operator instead of a source.
		/// Reaches fixed-arity nodes' own ports (e.g. nesting a Remap inside a SlewRate),
		/// which "+Filter" cannot: it only appends a new term to a dynamic group.
		/// </summary>
		private void ApplyNewOperator(string subtype)
		{
			KrabNode target = pickerTarget;
			int port = pickerPort;
			ClearPickerState();
			Mutate(() => KrabGraphEdits.ReplaceTermWithOperator(Graph, target, port, subtype));
		}

		private void ApplyExistingSource(KrabNode existing)
		{
			KrabNode target = pickerTarget;
			int port = pickerPort;
			ClearPickerState();
			Mutate(() => KrabGraphEdits.ReplaceTermWithExisting(Graph, target, port, existing));
		}

		private void BuildTargetFieldPicker()
		{
			if (pickedPart == null)
			{
				CancelPicker();
				return;
			}
			if (pickerNewSourceSubtype == "PartField")
			{
				BuildPartFieldPicker();
				return;
			}
			bool axis = pickerTarget.Info.name == "AxisOutput";
			RectTransform panel = KrabUi.Bordered("TargetPicker", contentHost, KrabUi.Panel, KrabUi.Line);
			KrabUi.Vertical(panel.gameObject, 9, 6f);
			KrabUi.Label(panel,
				Localizer.Format(axis ? "#LOC_KRAB_ui_pickAxisOn" : "#LOC_KRAB_ui_pickActionOn", pickedPart.partInfo.title),
				10, KrabUi.TanDim);
			RectTransform list = KrabUi.ScrollList(panel, 280f);

			int found = 0;
			if (axis)
			{
				for (int m = 0; m < pickedPart.Modules.Count; m++)
				{
					PartModule candidate = pickedPart.Modules[m];
					for (int f = 0; f < candidate.Fields.Count; f++)
					{
						if (!(candidate.Fields[f] is BaseAxisField axisField))
						{
							continue;
						}
						found++;
						PartModule capturedModule = candidate;
						BaseAxisField capturedField = axisField;
						string label = candidate.GetModuleDisplayName() + " · "
							+ (string.IsNullOrEmpty(axisField.guiName) ? axisField.name : Localizer.Format(axisField.guiName));
						KrabUi.TextButton(list, label,
							() => ApplyTarget(capturedModule.GetPersistentId(), capturedField.name),
							KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
					}
				}
			}
			else
			{
				foreach (BaseAction action in pickedPart.Actions)
				{
					found++;
					BaseAction captured = action;
					KrabUi.TextButton(list, Localizer.Format(captured.guiName),
						() => ApplyTarget(0u, captured.name), KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
				}
				for (int m = 0; m < pickedPart.Modules.Count; m++)
				{
					PartModule candidate = pickedPart.Modules[m];
					foreach (BaseAction action in candidate.Actions)
					{
						found++;
						PartModule capturedModule = candidate;
						BaseAction captured = action;
						KrabUi.TextButton(list,
							candidate.GetModuleDisplayName() + " · " + Localizer.Format(captured.guiName),
							() => ApplyTarget(capturedModule.GetPersistentId(), captured.name),
							KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
					}
				}
			}
			if (found == 0)
			{
				KrabUi.Label(list, Loc("#LOC_KRAB_ui_noBindables"), 12, KrabUi.Muted);
			}

			GameObject buttons = KrabUi.Go("Buttons", panel);
			KrabUi.Horizontal(buttons, 0, 8f);
			KrabUi.TextButton(buttons.transform, Loc("#LOC_KRAB_ui_pickAnother"),
				() => { KrabNode output = pickerTarget; ClearPickerState(); StartPartPick(output); },
				KrabUi.Panel2, KrabUi.Text, 12, 0f, 24f);
			KrabUi.Spacer(buttons.transform);
			KrabUi.TextButton(buttons.transform, Loc("#LOC_KRAB_ui_cancel"), CancelPicker,
				KrabUi.Panel2, KrabUi.Muted, 12, 90f, 24f);
		}

		/// <summary>
		/// Field list for a new Part Field source (BuildTargetFieldPicker's third branch):
		/// KRAB_DERIVED_FIELD catalog entries applicable to each module, then that module's
		/// own readable KSPFields (float/double/int/bool) minus any IsReplaced declares.
		/// </summary>
		private void BuildPartFieldPicker()
		{
			RectTransform panel = KrabUi.Bordered("TargetPicker", contentHost, KrabUi.Panel, KrabUi.Line);
			KrabUi.Vertical(panel.gameObject, 9, 6f);
			KrabUi.Label(panel,
				Localizer.Format("#LOC_KRAB_ui_pickFieldOn", pickedPart.partInfo.title),
				10, KrabUi.TanDim);
			RectTransform list = KrabUi.ScrollList(panel, 280f);

			int found = 0;
			for (int m = 0; m < pickedPart.Modules.Count; m++)
			{
				PartModule candidate = pickedPart.Modules[m];
				PartModule capturedModule = candidate;
				List<DerivedFieldRule> derived = DerivedFieldsCatalog.RulesFor(candidate);
				for (int d = 0; d < derived.Count; d++)
				{
					found++;
					string member = derived[d].member;
					string label = candidate.GetModuleDisplayName() + " · " + LocOr(derived[d].label, member);
					KrabUi.TextButton(list, label,
						() => ApplyNewPartFieldSource(capturedModule.GetPersistentId(), member),
						KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
				}
				for (int f = 0; f < candidate.Fields.Count; f++)
				{
					BaseField baseField = candidate.Fields[f];
					if (baseField.FieldInfo.FieldType != typeof(float) && baseField.FieldInfo.FieldType != typeof(double)
						&& baseField.FieldInfo.FieldType != typeof(int) && baseField.FieldInfo.FieldType != typeof(bool))
					{
						continue;
					}
					if (DerivedFieldsCatalog.IsReplaced(candidate, baseField.name))
					{
						continue;
					}
					found++;
					string fieldName = baseField.name;
					string label = candidate.GetModuleDisplayName() + " · "
						+ (string.IsNullOrEmpty(baseField.guiName) ? baseField.name : Localizer.Format(baseField.guiName));
					KrabUi.TextButton(list, label,
						() => ApplyNewPartFieldSource(capturedModule.GetPersistentId(), fieldName),
						KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
				}
			}
			if (found == 0)
			{
				KrabUi.Label(list, Loc("#LOC_KRAB_ui_noBindables"), 12, KrabUi.Muted);
			}

			GameObject buttons = KrabUi.Go("Buttons", panel);
			KrabUi.Horizontal(buttons, 0, 8f);
			KrabUi.TextButton(buttons.transform, Loc("#LOC_KRAB_ui_pickAnother"),
				() =>
				{
					KrabNode capturedTarget = pickerTarget;
					int capturedPort = pickerPort;
					ClearPickerState();
					pickerTarget = capturedTarget;
					pickerPort = capturedPort;
					StartPartFieldPick();
				},
				KrabUi.Panel2, KrabUi.Text, 12, 0f, 24f);
			KrabUi.Spacer(buttons.transform);
			KrabUi.TextButton(buttons.transform, Loc("#LOC_KRAB_ui_cancel"), CancelPicker,
				KrabUi.Panel2, KrabUi.Muted, 12, 90f, 24f);
		}

		private void ApplyNewPartFieldSource(uint moduleId, string fieldName)
		{
			KrabNode target = pickerTarget;
			int port = pickerPort;
			Part part = pickedPart;
			ClearPickerState();
			Mutate(() =>
			{
				KrabNode source = KrabGraphEdits.ReplaceTermWithSource(Graph, target, port, "PartField");
				source.SetParam("persistentId", part.persistentId.ToString());
				source.SetParam("moduleId", moduleId.ToString());
				source.SetParam("fieldName", fieldName);
			});
		}

		private void ApplyTarget(uint moduleId, string bindingName)
		{
			KrabNode output = pickerTarget;
			Part part = pickedPart;
			bool axis = output.Info.name == "AxisOutput";
			ClearPickerState();
			Mutate(() =>
			{
				output.SetParam("persistentId", part.persistentId.ToString());
				output.SetParam("moduleId", moduleId.ToString());
				output.SetParam(axis ? "axisName" : "actionName", bindingName);
			});
		}

		private static string NodeDisplay(string subtypeName)
		{
			string localized = Localizer.Format("#LOC_KRAB_node_" + subtypeName);
			return localized.StartsWith("#LOC_KRAB_") ? subtypeName : localized;
		}

		private static string DescribeOperator(KrabNode node)
		{
			switch (node.Info.name)
			{
				case "WeightedSum": return "w = " + node.GetString("weights", "1");
				case "Comparator":
				case "GatedBlend": return "thr " + node.GetString("threshold", "0.5");
				default: return node.Info.name;
			}
		}

		// --------------------------------------------------------------- refresh

		private void LateUpdate()
		{
			if (module == null)
			{
				Close();
				return;
			}
			if (pickingPart)
			{
				HandlePartPicking();
				return;
			}
			if (Time.unscaledTime < nextRefresh)
			{
				return;
			}
			nextRefresh = Time.unscaledTime + RefreshInterval;
			KrabEvaluator evaluator = module.Evaluator;
			if (evaluator == null)
			{
				return;
			}
			if (Simulated)
			{
				module.RunSimulation(simValues);
			}
			for (int i = 0; i < valueBindings.Count; i++)
			{
				ValueBinding binding = valueBindings[i];
				if (binding.label != null
					&& evaluator.TryGetNodeOutput(binding.node, out float value))
				{
					float shown = value * binding.factor + binding.offset;
					if (binding.hasClamp)
					{
						shown = Mathf.Clamp(shown, binding.clampMin, binding.clampMax);
					}
					binding.label.text = shown.ToString("F2") + binding.suffix;
				}
			}
		}

		private void AddValueBinding(KrabNode node, Text label)
		{
			ValueBinding binding = new ValueBinding { node = node, label = label, factor = 1f, offset = 0f, suffix = "" };
			if (node.IsKnown && node.Info.name == "PhysicalState")
			{
				UnitOption unit = KrabUnits.Resolve(node.GetString("metric", ""), node.GetString("displayUnit", ""));
				binding.factor = unit.factor;
				binding.offset = unit.offset;
				binding.suffix = unit.symbol.Length > 0 ? " " + unit.symbol : "";
			}
			else if (node.IsKnown && node.Info.name == "AxisOutput" && TryResolveAxisField(node, out BaseAxisField field, out _))
			{
				// Show the bound field's own units (e.g. a hinge's degrees), not the
				// internal 0..1 span. Uses the same range AxisOutputRuntime.ResolveTarget
				// writes into: softLimits when the module declares them, else min/max.
				float targetMin = field.minValue;
				float targetMax = field.maxValue;
				if (field.module is IAxisFieldLimits limits && limits.HasAxisFieldLimit(node.GetString("axisName", "")))
				{
					Vector2 soft = limits.GetAxisFieldLimit(node.GetString("axisName", "")).softLimits;
					targetMin = soft.x;
					targetMax = soft.y;
				}
				float inMin = node.GetFloat("inMin", 0f);
				float inMax = node.GetFloat("inMax", 1f);
				float span = inMax - inMin;
				if (span != 0f)
				{
					binding.factor = (targetMax - targetMin) / span;
					binding.offset = targetMin - inMin * binding.factor;
				}
				// Clamp the preview to the same range the actual write clamps to
				// (Mathf.InverseLerp already clamps t to 0..1), so an unbounded upstream
				// value can't read as e.g. "442°" on a 0-180° hinge.
				binding.hasClamp = true;
				binding.clampMin = Mathf.Min(targetMin, targetMax);
				binding.clampMax = Mathf.Max(targetMin, targetMax);
				string units = field.Attribute != null ? field.Attribute.guiUnits : null;
				binding.suffix = string.IsNullOrEmpty(units) ? "" : " " + units;
			}
			valueBindings.Add(binding);
		}

		/// <summary>
		/// Unit choice as a small segmented control, one button per option with the
		/// current one highlighted: with 2-3 options per metric, picking directly beats
		/// cycling, and UGUI's Dropdown would need prefab/template plumbing.
		/// </summary>
		private void BuildUnitChip(Transform parent, KrabNode node)
		{
			string metric = node.GetString("metric", "");
			UnitOption[] options = KrabUnits.ForMetric(metric);
			if (options.Length <= 1)
			{
				if (options.Length == 1 && options[0].symbol.Length > 0)
				{
					ParamLabel(parent, options[0].symbol, "#LOC_KRAB_tip_paramUnitSymbol");
				}
				return;
			}
			string current = node.GetString("displayUnit", "");
			if (string.IsNullOrEmpty(current))
			{
				current = options[0].symbol;
			}
			GameObject seg = KrabUi.Go("UnitSeg", parent);
			KrabUi.Horizontal(seg, 0, 2f);
			foreach (UnitOption option in options)
			{
				string symbol = option.symbol;
				bool active = symbol == current;
				// Display-only preference: bypasses Mutate (no undo snapshot, no
				// recompile, the canonical value never changes). The backup is still
				// refreshed so the choice survives Unity cloning and saves.
				KrabUi.TextButton(seg.transform, symbol, () =>
				{
					node.SetParam("displayUnit", symbol);
					module.RefreshGraphPersistence();
					RebuildContent();
				}, active ? KrabUi.Panel2 : KrabUi.Panel, active ? KrabUi.Tan : KrabUi.Muted, 11, 0f, 20f);
			}
		}

		// --------------------------------------------------------------- helpers

		private class DragHandler : MonoBehaviour, IBeginDragHandler, IDragHandler
		{
			public RectTransform target;
			private Vector2 offset;

			public void OnBeginDrag(PointerEventData eventData)
			{
				RectTransformUtility.ScreenPointToLocalPointInRectangle(
					(RectTransform)target.parent, eventData.position, eventData.pressEventCamera, out Vector2 point);
				offset = target.anchoredPosition - point;
			}

			public void OnDrag(PointerEventData eventData)
			{
				if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
					(RectTransform)target.parent, eventData.position, eventData.pressEventCamera, out Vector2 point))
				{
					target.anchoredPosition = point + offset;
				}
			}
		}

		/// <summary>Blocks scene input while the pointer is over the window (UGUI already blocks UI clicks).</summary>
		private class FocusLock : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
		{
			public string lockId;

			public void OnPointerEnter(PointerEventData eventData)
			{
				InputLockManager.SetControlLock(ControlTypes.ALLBUTCAMERAS, lockId);
			}

			public void OnPointerExit(PointerEventData eventData)
			{
				InputLockManager.RemoveControlLock(lockId);
			}

			private void OnDisable()
			{
				InputLockManager.RemoveControlLock(lockId);
			}
		}
	}
}
