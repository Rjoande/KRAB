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
	/// Editor window. M1: UGUI shell built in code, tree-of-groups view, live flight
	/// telemetry, condition simulator in the editor. M2: live editing — parameter
	/// fields, add/remove terms/groups/outputs, operator cycling, snapshot-based
	/// undo/redo, validation strip with node highlighting.
	/// Every mutation flows through Mutate(): snapshot for undo, then the module
	/// persists/revalidates/recompiles, then the content is rebuilt.
	/// </summary>
	public class KrabEditorWindow : MonoBehaviour
	{
		private const float WindowWidth = 620f;
		// Fixed heights for the two variable-length lists (tree, simulator sliders):
		// with many terms/sources the WINDOW used to grow past the screen edge and
		// become unreachable (in-game feedback, 2026-07-09) — these lists now scroll
		// internally instead, so the window's total height stays constant regardless
		// of graph size. That constancy is also what keeps the titlebar (and its
		// undo/redo buttons) from drifting, the same fix in spirit as pinning the
		// operator-cycle button to a fixed-width slot.
		private const float TreeAreaHeight = 260f;
		private const float SimAreaHeight = 130f;
		private const float RefreshInterval = 0.1f;
		private const int MaxTreeDepth = 8;
		private const int MaxUndo = 50;
		private const string InputLockId = "KRAB_EDITOR_WINDOW";

		private static KrabEditorWindow current;

		// Copy/paste of a whole input/operator subtree across output tabs (in-game
		// request, 2026-07-17). Static and session-scoped like `current` itself — a
		// plain in-memory clipboard, not persisted; cleared on a KSP restart.
		private static string subtreeClipboard;

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
			// AxisOutput preview only: clamp the converted reading to the target
			// field's real range. Without this, an upstream operator producing values
			// outside 0..1 (e.g. a raw physical reading used directly, no Remap) shows
			// nonsense like "442°" on a 0-180° hinge — even though the actual write to
			// the vessel is always clamped (Mathf.InverseLerp already clamps its t to
			// 0..1), so nothing wrong happens in flight, only the preview lied about it
			// (in-game feedback, 2026-07-09).
			public bool hasClamp;
			public float clampMin;
			public float clampMax;
		}

		private readonly List<ValueBinding> valueBindings = new List<ValueBinding>();
		private readonly Dictionary<string, float> simValues = new Dictionary<string, float>();
		private readonly List<string> undoStack = new List<string>();
		private readonly List<string> redoStack = new List<string>();
		private float nextRefresh;

		// ---- M3 pickers ----

		private enum PickerKind
		{
			None,
			Source,      // choosing what feeds pickerTarget:pickerPort
			TargetField, // choosing the axis/action of pickedPart for output pickerTarget
			Filter       // choosing a shaping operator (KrabGraphEdits.InsertableFilters) to add to pickerTarget
		}

		private PickerKind pickerKind;
		private KrabNode pickerTarget;
		private int pickerPort;
		private bool pickingPart;
		private Part pickedPart;
		private Part hoverPart;
		// Captured on mouse-down, confirmed on mouse-up (see HandlePartPicking).
		private Part pendingPickPart;
		private const string PickLockId = "KRAB_EDITOR_PICK";

		private static readonly string[] Channels =
		{
			"Pitch", "Yaw", "Roll", "TranslateX", "TranslateY", "TranslateZ", "MainThrottle",
			"WheelSteer", "WheelThrottle", "Custom01", "Custom02", "Custom03", "Custom04"
		};

		private static readonly string[] Metrics =
		{
			"SrfSpeed", "HorizontalSrfSpeed", "VerticalSpeed", "IndicatedAirSpeed", "Mach",
			"AltitudeASL", "AltitudeRadar", "DynamicPressure", "StaticPressure", "AtmDensity",
			"GForce", "ExternalTemperature", "AngularVelocityMag"
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
			ClearPickerState();
			KrabCurveWindow.CloseAny(); // a curve window points into this editor's graph — don't leave it floating
			InputLockManager.RemoveControlLock(InputLockId);
			GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
			if (current == this)
			{
				current = null;
			}
		}

		private void OnSceneChange(GameScenes scene)
		{
			Close();
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
				case "ControllerInput":
					return Localizer.Format("#LOC_KRAB_ui_slot", node.GetString("slot", "1"));
				case "Constant":
					return node.GetString("value", "0");
				default:
					return "";
			}
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
		/// curve-drag gesture calls this once (drag start / click / field commit) then
		/// does its own persist+recompile — a full Mutate() per drag frame would rebuild
		/// the whole tree and fight the drag. Without this, curve edits had no undo
		/// checkpoint of their own and Undo jumped past them to the prior real Mutate()
		/// (in-game report, 2026-07-15 — undo erased the Remap's own creation too).</summary>
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

			Canvas canvas = gameObject.AddComponent<Canvas>();
			canvas.renderMode = RenderMode.ScreenSpaceOverlay;
			canvas.sortingOrder = 900;
			CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
			scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
			scaler.scaleFactor = GameSettings.UI_SCALE;
			gameObject.AddComponent<GraphicRaycaster>();

			windowRect = KrabUi.Bordered("Window", transform, KrabUi.Win, KrabUi.Line);
			windowRect.anchorMin = windowRect.anchorMax = new Vector2(0.5f, 0.5f);
			windowRect.pivot = new Vector2(0.5f, 0.5f);
			windowRect.anchoredPosition = new Vector2(160f, 20f);
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

			// Editable name (in-game feedback, 2026-07-10: there was no way at all to
			// rename a KRAB — displayName is a plain string KSPField, and stock KSP has
			// no built-in editable-text PAW control for it, unlike UI_FloatRange/
			// UI_Toggle for numbers/bools. The editor window is the natural place,
			// same as KAL's own name field lives in its custom window, not the PAW).
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

			// The only close control now (in-game request, 2026-07-19: the redundant
			// "Close" button at the bottom of the window is gone — see BuildDetail).
			// Kept as the plain glyph rather than icon_close (in-game feedback,
			// 2026-07-20: the custom close icon didn't read well).
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

			// A plain HorizontalLayoutGroup would squeeze every tab once their combined
			// width exceeded the row — past ~4 outputs the labels became unreadable
			// (in-game feedback, 2026-07-17). A horizontal scroll strip keeps each tab
			// at its natural width instead; the mouse wheel scrolls it sideways.
			RectTransform strip = KrabUi.HScrollList(row.transform, TabStripHeight);
			foreach (KrabNode node in outputNodes)
			{
				string id = node.id;
				bool active = id == activeOutputId;
				string marker = node.Info.name == "AxisOutput" ? "◉ " : "▲ ";
				KrabUi.TextButton(strip, marker + TabTitle(node),
					() => { activeOutputId = id; RebuildContent(); },
					active ? KrabUi.Panel2 : KrabUi.Panel,
					active ? KrabUi.Text : KrabUi.Muted, 12, 0f, 26f);
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
			// A custom label always wins — with several outputs of the same kind, the
			// auto-detected name ("Axis Output" until bound, or a raw field name after)
			// made the tab row ambiguous (in-game feedback, 2026-07-15).
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
				ParamLabel(paramRow.transform, "inMin");
				NumberField(paramRow.transform, output, "inMin", "0");
				ParamLabel(paramRow.transform, "inMax");
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

			// Copy/paste the whole input/operator subtree feeding this output's port 0 —
			// lets the player build a combination once and replicate it on another
			// output tab instead of rebuilding it by hand (in-game request, 2026-07-17).
			GameObject copyRow = KrabUi.Go("CopyPaste", right.transform);
			KrabUi.Horizontal(copyRow, 0, 5f);
			// Right-aligned to match the VALUE/✕ row below it (in-game feedback,
			// 2026-07-19: with nothing to anchor to, the icons looked adrift).
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
		/// or danger — the bound part no longer exists on this craft (in-game feedback,
		/// 2026-07-09: deleting a target part left the binding looking valid). The
		/// binding itself is left untouched — flagged, not auto-cleared, since a part
		/// can come back (undo, re-attach) and silently discarding a player's work on a
		/// state we can't fully verify is the wrong default.
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

		/// <summary>Prefers "Part title - Field/action GUI name" (in-game feedback,
		/// 2026-07-15: the raw persisted name, e.g. "targetAngle", told the player
		/// nothing when several outputs point at similar fields) — falls back to the
		/// raw persisted name if the live field/action can't be resolved right now (e.g.
		/// the module happens to be unloaded), so the target still shows something.</summary>
		private static string ResolveTargetLabel(KrabNode output, Part part, string rawField)
		{
			if (output.Info.name == "AxisOutput")
			{
				if (TryResolveAxisField(output, out BaseAxisField axisField, out _))
				{
					string guiName = string.IsNullOrEmpty(axisField.guiName)
						? axisField.name
						: Localizer.Format(axisField.guiName);
					return part.partInfo.title + " - " + guiName;
				}
			}
			else if (TryResolveAction(output, out BaseAction action, out _))
			{
				return part.partInfo.title + " - " + Localizer.Format(action.guiName);
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
				return KrabUi.Malachite; // M4: term stays highlighted while its curve window is open
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
			// Cycle button goes BEFORE the (variable-width) name label, on a fixed-width
			// slot, so its position on screen doesn't shift as the operator name changes
			// length while clicking through several options in a row (in-game feedback).
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
				// Source leaves: name + selection open the grouped source picker (M3).
				KrabNode capturedParent = parentGroup;
				int capturedSlot = port;
				KrabUi.TextButton(row.transform,
					NodeName(node) + " · " + SourceDetail(node) + " ▾",
					() => OpenSourcePicker(capturedParent, capturedSlot),
					KrabUi.Inset, NodeColor(node, false), 12, 0f, 20f);
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

			// removable: direct child of a dynamic group (fixed-arity ports would go invalid).
			// Slot is reserved either way (fixed 18px) — a conditional button here shifted
			// the value column left on every row that had one, breaking the column's
			// vertical alignment (in-game feedback, 2026-07-19; same fix as the cycle
			// button's own CycleSpacer placeholder above).
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

		private void ParamLabel(Transform parent, string text)
		{
			Text label = KrabUi.Label(parent, text, 10, KrabUi.Muted);
			KrabUi.Size(label.gameObject, -1f, 20f);
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
		/// Inline editable parameters per subtype. Vocabulary params (channel, metric,
		/// group) are free text until the M3 pickers land: a typo simply disables the
		/// node with a warning, never a crash (tolerant-parse policy).
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
				// ControllerInput / PlayerAxis / ScriptAxis / ActionGroupState carry no
				// inline fields anymore: their selection lives in the source picker
				// (the "Name · Detail ▾" button on the row).
				case "PhysicalState":
					// Sample rate only affects flight (simulation mode bypasses sampling
					// entirely — see PhysicalStateRuntime.Evaluate): showing a live field
					// that visibly does nothing in the editor just confused testing
					// (in-game feedback, 2026-07-09 — "typed a number, nothing happened").
					if (!Simulated)
					{
						ParamLabel(parent, "s");
						NumberField(parent, node, "sampleRate", "0.1", 38f);
					}
					BuildUnitChip(parent, node);
					break;
				case "WeightedSum":
					ParamLabel(parent, "w");
					TextField(parent, node, "weights", "1", 110f);
					break;
				case "Remap":
					if (node.HasNode("curve"))
					{
						// A curve overrides the four linear fields entirely (RemapRuntime) —
						// showing both would suggest they still do something (M4).
						Button curveButton = KrabUi.ImageIconButton(parent, "curve",
							() => KrabCurveWindow.Open(module, this, node),
							KrabUi.Malachite, 20f);
						KrabUi.Tooltip(curveButton.gameObject, "#LOC_KRAB_tip_curve");
					}
					else
					{
						// Bare numbers with only a "→" between them gave no clue which pair
						// was in vs out (in-game feedback, 2026-07-14) — labeled both sides.
						// "in"/"out" alone still didn't say the two boxes were min/max
						// (in-game feedback, 2026-07-15).
						ParamLabel(parent, "in (min-max)");
						NumberField(parent, node, "inMin", "0", 42f);
						NumberField(parent, node, "inMax", "1", 42f);
						ParamLabel(parent, "out (min-max)");
						NumberField(parent, node, "outMin", "0", 42f);
						NumberField(parent, node, "outMax", "1", 42f);
						Button curveButton = KrabUi.ImageIconButton(parent, "curve",
							() => KrabCurveWindow.Open(module, this, node),
							KrabUi.TanDim, 20f);
						KrabUi.Tooltip(curveButton.gameObject, "#LOC_KRAB_tip_curve");
					}
					break;
				case "GatedBlend":
					ParamLabel(parent, "thr");
					NumberField(parent, node, "threshold", "0.5", 44f);
					ParamLabel(parent, "hys");
					NumberField(parent, node, "hysteresis", "0", 38f);
					ParamLabel(parent, "band");
					NumberField(parent, node, "blendWidth", "0", 38f);
					break;
				case "Comparator":
					ParamLabel(parent, "thr");
					NumberField(parent, node, "threshold", "0.5", 44f);
					ParamLabel(parent, "hys");
					NumberField(parent, node, "hysteresis", "0", 38f);
					break;
				case "Derivative":
					ParamLabel(parent, "τ");
					NumberField(parent, node, "smoothing", "0.2", 38f);
					ParamLabel(parent, "×");
					NumberField(parent, node, "scale", "1", 38f);
					break;
				case "SlewRate":
					NumberField(parent, node, "ratePerSecond", "0", 46f);
					ParamLabel(parent, "/s");
					break;
				case "Hold":
					string mode = node.GetString("mode", "track");
					KrabUi.TextButton(parent, mode,
						() => Mutate(() => node.SetParam("mode", mode == "track" ? "latch" : "track")),
						KrabUi.Inset, KrabUi.Tan, 11, 0f, 20f);
					break;
			}
		}

		// ------------------------------------------------------------- simulator

		private void BuildSimulator()
		{
			RectTransform panel = KrabUi.Bordered("Simulator", contentHost, KrabUi.Panel, KrabUi.Line);
			KrabUi.Vertical(panel.gameObject, 9, 6f);
			KrabUi.Label(panel, Loc("#LOC_KRAB_ui_simTitle"), 10, KrabUi.TanDim);

			// Fixed-height scroll area: the panel (and window) no longer grow with
			// the number of sources (in-game feedback, 2026-07-09).
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
					NodeName(node) + " · " + SourceDetail(node), 12, KrabUi.Text);
				KrabUi.Size(label.gameObject, 200f, 20f);

				if (node.Info.name == "ActionGroupState")
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
					// Slider stays canonical (SI); the readout always shows SI, plus the
					// player's chosen unit in parentheses when it differs (dual format,
					// in-game feedback 2026-07-09 — e.g. "20.7 m/s (41.2 kn)").
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

		/// <summary>Per-source slider ranges; per-metric defaults for physical sources (M1).</summary>
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
						case "angularvelocitymag": min = 0f; max = 5f; return;
						case "verticalspeed": min = -300f; max = 300f; return;
						default: min = 0f; max = 500f; return; // speeds, m/s
					}
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
				// 12px minimum here: this strip was the single least readable line of the
				// window per in-game review — never demote it back to 10px.
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

			// Close now lives only in the titlebar (in-game request, 2026-07-19) — this
			// row is just the status line.
			GameObject row = KrabUi.Go("Footer", contentHost);
			KrabUi.Horizontal(row, 0, 10f);
			Text status = KrabUi.Label(row.transform, module.GraphStatusText, 12, KrabUi.Muted);
			KrabUi.Size(status.gameObject, -1f, 22f, 1f);
		}

		// ------------------------------------------------------------ M3 pickers

		private void OpenSourcePicker(KrabNode target, int port)
		{
			pickerKind = PickerKind.Source;
			pickerTarget = target;
			pickerPort = port;
			RebuildContent();
		}

		/// <summary>
		/// Opens the picker for KrabGraphEdits.InsertableFilters (Remap, Derivative,
		/// SlewRate, Comparator, Hold) — the only way to add one of these to a group,
		/// since they're neither a combination operator (not cycleable) nor a source
		/// (not in the source picker). Added 2026-07-10: without it, the editor could
		/// author every subtype except these five.
		/// </summary>
		private void OpenFilterPicker(KrabNode group)
		{
			pickerKind = PickerKind.Filter;
			pickerTarget = group;
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
			RebuildContent();
		}

		private void ClearPickerState()
		{
			pickerKind = PickerKind.None;
			pickingPart = false;
			pickerTarget = null;
			pickedPart = null;
			pendingPickPart = null;
			if (hoverPart != null)
			{
				hoverPart.SetHighlightDefault();
				hoverPart = null;
			}
			InputLockManager.RemoveControlLock(PickLockId);
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
		/// Scene picking (the KAL gesture): hover highlight + click to select.
		///
		/// Confirms on mouse-UP, not mouse-down (fixed 2026-07-09, in-game feedback:
		/// clicking with the picker active moved the part in the VAB). The previous
		/// version removed the input lock the instant GetMouseButtonDown fired — but
		/// the mouse button is still physically held for the remaining frames of that
		/// same click, and if the editor's own part-drag re-checks its lock every
		/// frame (rather than once at gesture start), that window with no active lock
		/// was enough for it to pick the part up and start following the cursor.
		/// Verified on the decompiled source that ControlTypes.EDITOR_PAD_PICK_PLACE
		/// (the flag the editor checks before allowing a part drag) is already
		/// included in ALLBUTCAMERAS, so this was a timing bug, not a missing flag.
		/// Diagnostic logging is left in (registry-tracked) in case this only
		/// partially fixes it: the log shows the exact lock stack and part position
		/// at the moment of the pick, which the previous static analysis couldn't get.
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
				if (hoverPart != null)
				{
					hoverPart.SetHighlightDefault();
				}
				hoverPart = hovered;
				if (hoverPart != null)
				{
					hoverPart.SetHighlightType(Part.HighlightType.AlwaysOn);
					hoverPart.SetHighlightColor(Color.cyan);
					hoverPart.SetHighlight(true, false);
				}
			}
			if (Input.GetMouseButtonDown(0) && hoverPart != null
				&& (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject()))
			{
				pendingPickPart = hoverPart;
				hoverPart.SetHighlightDefault();
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

			BuildVocabularyFamily(list, "#LOC_KRAB_fam_player", Channels, "#LOC_KRAB_ch_",
				name => ApplyNewSource("PlayerAxis", "channel", name));
			BuildVocabularyFamily(list, "#LOC_KRAB_fam_script", Channels, "#LOC_KRAB_ch_",
				name => ApplyNewSource("ScriptAxis", "channel", name));
			BuildVocabularyFamily(list, "#LOC_KRAB_fam_physical", Metrics, "#LOC_KRAB_met_",
				name => ApplyNewSource("PhysicalState", "metric", name));
			BuildVocabularyFamily(list, "#LOC_KRAB_fam_actionGroup", ActionGroupNames, "#LOC_KRAB_ag_",
				name => ApplyNewSource("ActionGroupState", "group", name));

			KrabUi.Label(list, Loc("#LOC_KRAB_fam_krabInput"), 11, KrabUi.TanDim);
			RectTransform slotGrid = KrabUi.Grid(list, 138f, 22f);
			for (int slot = 1; slot <= ModuleKRABController.InputSlotCount; slot++)
			{
				string value = slot.ToString();
				KrabUi.TextButton(slotGrid, Localizer.Format("#LOC_KRAB_ui_slot", value),
					() => ApplyNewSource("ControllerInput", "slot", value),
					KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
			}

			KrabUi.Label(list, Loc("#LOC_KRAB_fam_constant"), 11, KrabUi.TanDim);
			RectTransform constGrid = KrabUi.Grid(list, 138f, 22f);
			KrabUi.TextButton(constGrid, NodeDisplay("Constant"),
				() => ApplyNewSource("Constant", "value", "0"),
				KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);

			// Turn this leaf into an operator instead of a source — reaches ports that
			// "+Term/+Group/+Filter" cannot (those only append to a DYNAMIC group; this
			// works on any port, including a fixed-arity node's own, enabling chains
			// like Remap→SlewRate or Comparator→Not).
			KrabUi.Label(list, Loc("#LOC_KRAB_fam_operators"), 11, KrabUi.TanDim);
			RectTransform opGrid = KrabUi.Grid(list, 138f, 22f);
			KrabUi.TextButton(opGrid, NodeDisplay("WeightedSum"),
				() => ApplyNewOperator("WeightedSum"), KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
			foreach (string filterSubtype in KrabGraphEdits.InsertableFilters)
			{
				string captured = filterSubtype;
				KrabUi.TextButton(opGrid, NodeDisplay(captured),
					() => ApplyNewOperator(captured), KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
			}

			// Fan-out: feed this port from a node that already exists elsewhere in the
			// graph (the tree view then shows it under both consumers).
			List<KrabNode> reusable = KrabGraphEdits.ReusableSignals(Graph, pickerTarget, pickerPort);
			if (reusable.Count > 0)
			{
				KrabUi.Label(list, Loc("#LOC_KRAB_fam_reuse"), 11, KrabUi.TanDim);
				foreach (KrabNode candidate in reusable)
				{
					KrabNode captured = candidate;
					string detail = candidate.Info.kind == NodeKind.Source
						? SourceDetail(candidate)
						: DescribeOperator(candidate);
					KrabUi.TextButton(list, NodeName(candidate) + " · " + detail + "  [" + candidate.id + "]",
						() => ApplyExistingSource(captured), KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
				}
			}

			Button cancel = KrabUi.TextButton(panel, Loc("#LOC_KRAB_ui_cancel"), CancelPicker,
				KrabUi.Panel2, KrabUi.Muted, 12, 90f, 24f);
			KrabUi.Size(cancel.gameObject, 90f, 24f);
		}

		/// <summary>
		/// Picker for KrabGraphEdits.InsertableFilters, opened by "+ Filter" on a
		/// group. Selecting one adds it as a new term via the generalized AddSubgroup
		/// (auto-filling whatever ports it needs), same mechanism "+ Group" already
		/// used for WeightedSum.
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
				KrabUi.TextButton(list, NodeDisplay(captured), () =>
				{
					ClearPickerState();
					Mutate(() => KrabGraphEdits.AddSubgroup(Graph, target, captured));
				}, KrabUi.Panel2, KrabUi.Text, 12, 0f, 24f);
			}

			Button cancel = KrabUi.TextButton(panel, Loc("#LOC_KRAB_ui_cancel"), CancelPicker,
				KrabUi.Panel2, KrabUi.Muted, 12, 90f, 24f);
			KrabUi.Size(cancel.gameObject, 90f, 24f);
		}

		private void BuildVocabularyFamily(RectTransform list, string familyKey, string[] entries,
			string entryKeyPrefix, System.Action<string> onPick)
		{
			KrabUi.Label(list, Loc(familyKey), 11, KrabUi.TanDim);
			RectTransform grid = KrabUi.Grid(list, 138f, 22f);
			foreach (string entry in entries)
			{
				string captured = entry;
				KrabUi.TextButton(grid, LocOr(entryKeyPrefix + entry, entry), () => onPick(captured),
					KrabUi.Panel2, KrabUi.Text, 11, 0f, 22f);
			}
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
		/// Turns the leaf under this picker into an operator instead of a source —
		/// the "OPERATORS" family of the same picker screen. Reaches fixed-arity nodes'
		/// own ports (e.g. nesting a Remap inside a SlewRate), which "+Filter" alone
		/// cannot: that button only appends a new term to a dynamic group.
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
				// internal 0..1 span — "0.25" reads as a fraction, not as "45°" of a
				// 180° hinge (in-game feedback, 2026-07-09).
				// Same range AxisOutputRuntime.ResolveTarget actually writes into
				// (softLimits when the module declares them, else the field's own
				// min/max) — otherwise the preview's clamp wouldn't match reality.
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
				// (Mathf.InverseLerp already clamps t to 0..1 there) — an upstream
				// operator feeding a raw unbounded value (no Remap) used to show
				// nonsense like "442°" on a 0-180° hinge; nothing wrong ever happened
				// in flight, only the preview lied about it.
				binding.hasClamp = true;
				binding.clampMin = Mathf.Min(targetMin, targetMax);
				binding.clampMax = Mathf.Max(targetMin, targetMax);
				string units = field.Attribute != null ? field.Attribute.guiUnits : null;
				binding.suffix = string.IsNullOrEmpty(units) ? "" : " " + units;
			}
			valueBindings.Add(binding);
		}

		/// <summary>
		/// Unit choice as a small segmented control (one button per option, current one
		/// highlighted) rather than a single cycle-on-click chip: with only 2-3 options
		/// per metric, picking directly beats cycling blindly through them (in-game
		/// feedback, 2026-07-09 — closest thing to the requested dropdown without the
		/// native Dropdown component's prefab/template plumbing).
		/// </summary>
		private void BuildUnitChip(Transform parent, KrabNode node)
		{
			string metric = node.GetString("metric", "");
			UnitOption[] options = KrabUnits.ForMetric(metric);
			if (options.Length <= 1)
			{
				if (options.Length == 1 && options[0].symbol.Length > 0)
				{
					ParamLabel(parent, options[0].symbol);
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
				// Display-only preference: bypasses Mutate on purpose (no undo snapshot,
				// no recompile — the canonical value never changes). Backup still
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
