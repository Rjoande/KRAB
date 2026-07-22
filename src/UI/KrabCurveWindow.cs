using System.Collections.Generic;
using KRAB.Graph;
using KRAB.Graph.Evaluation;
using KSP.Localization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace KRAB.UI
{
	/// <summary>
	/// M4 — dedicated modeless curve editor for a Remap node's optional `curve`
	/// sub-node. Design decided in notes/design-ui-editor.md §9: separate window
	/// (not widening the main editor), one at a time, the term stays highlighted in
	/// the main tree while its curve window is open (see OpenNodeId / KrabEditorWindow.
	/// NodeColor). No manual tangent handles in v1 (auto-smoothed on every edit via
	/// AnimationCurve.SmoothTangents) — full tangent dragging is most of what makes
	/// KAL's own CurvePanel 2000+ lines long, and it's prefab-driven besides (not
	/// reusable under our "no asset bundles" decision); this covers the practical
	/// "shape a response curve" need without that complexity.
	///
	/// Curve semantics: the curve's own keyframes define domain (x = raw input) and
	/// range (y = output) directly, like KAL's timeValue curve — see RemapRuntime.
	/// Live drag updates recompile only (RecompileOnly, cheap) for immediate visual
	/// and behavioral feedback; the full ConfigNode serialize (RefreshGraphPersistence)
	/// happens once on drag-end, not every frame.
	/// </summary>
	public class KrabCurveWindow : MonoBehaviour
	{
		private const float WindowWidth = 440f;
		private const float GraphWidth = 400f;
		private const float GraphHeight = 220f;
		private const float PointSize = 12f;
		private const float RefreshInterval = 0.1f;
		private const string InputLockId = "KRAB_CURVE_WINDOW";

		private static KrabCurveWindow current;

		/// <summary>Id of the node whose curve window is open, for the main tree's highlight.</summary>
		public static string OpenNodeId => current != null ? current.remapNode.id : null;

		private ModuleKRABController module;
		private KrabEditorWindow owner;
		private KrabNode remapNode;
		private RectTransform windowRect;
		private RectTransform graphArea;
		private KrabCurveLine line;
		private RectTransform cursorMark;
		private Text cursorText;
		private Transform pointsHost;
		private Transform selectedRow;

		private RectTransform[] pointDots;
		private AnimationCurve curve;
		private float domainMin, domainMax, rangeMin, rangeMax;
		private int selectedIndex = -1;
		private float nextRefresh;

		public static void Open(ModuleKRABController forModule, KrabEditorWindow ownerWindow, KrabNode remap)
		{
			if (current != null)
			{
				current.Close();
			}
			GameObject host = new GameObject("KRABCurveWindow");
			current = host.AddComponent<KrabCurveWindow>();
			current.module = forModule;
			current.owner = ownerWindow;
			current.remapNode = remap;
			current.Build();
			ownerWindow.RebuildContent(); // paint the highlight on the now-open term
		}

		public static void CloseIfOpenFor(KrabNode node)
		{
			if (current != null && current.remapNode == node)
			{
				current.Close();
			}
		}

		/// <summary>Called when the owning KrabEditorWindow itself closes — a curve window
		/// pointing at a node from that editor's graph would otherwise be left floating.</summary>
		public static void CloseAny()
		{
			if (current != null)
			{
				current.Close();
			}
		}

		/// <summary>Called after Undo/Redo, which replaces the whole KrabGraph with a
		/// freshly-Loaded one — the curve window's `remapNode` would otherwise point at a
		/// detached instance whose writes never reach the live graph. Re-points at the
		/// node sharing the same id (ids are stable across undo/redo) and reloads the
		/// curve from it, instead of unconditionally closing: closing on every undo
		/// defeated the point of watching a curve edit get undone live (in-game report,
		/// 2026-07-15). Only closes if the node genuinely no longer exists (undo went
		/// past its creation).</summary>
		public static void SyncAfterUndoRedo()
		{
			if (current == null)
			{
				return;
			}
			KrabNode resolved = current.module.Graph.FindNode(current.remapNode.id);
			if (resolved == null || !resolved.HasNode("curve"))
			{
				// No node, or a node whose curve itself got undone away (e.g. undo past
				// "Reset to linear"): nothing left to show. Closing here also avoids
				// LoadCurveFromNode's own seed-a-linear-curve-and-write-it-back path,
				// which would otherwise immediately fight the user's own undo.
				current.Close();
				return;
			}
			current.remapNode = resolved;
			current.selectedIndex = -1;
			current.LoadCurveFromNode();
			current.RedrawGraph();
			current.RefreshSelectedRow();
		}

		private void OnDestroy()
		{
			InputLockManager.RemoveControlLock(InputLockId);
			GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
			if (current == this)
			{
				current = null;
			}
			if (owner != null)
			{
				owner.RebuildContent(); // clear the highlight
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

		// ------------------------------------------------------------------ build

		private void Build()
		{
			GameEvents.onGameSceneLoadRequested.Add(OnSceneChange);
			LoadCurveFromNode();

			Canvas canvas = gameObject.AddComponent<Canvas>();
			canvas.renderMode = RenderMode.ScreenSpaceOverlay;
			canvas.sortingOrder = 901; // above the main editor window
			CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
			scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
			scaler.scaleFactor = GameSettings.UI_SCALE;
			gameObject.AddComponent<GraphicRaycaster>();

			windowRect = KrabUi.Bordered("CurveWindow", transform, KrabUi.Win, KrabUi.Line);
			windowRect.anchorMin = windowRect.anchorMax = new Vector2(0.5f, 0.5f);
			windowRect.pivot = new Vector2(0.5f, 0.5f);
			windowRect.anchoredPosition = new Vector2(-260f, 40f); // to the left of the main editor
			windowRect.sizeDelta = new Vector2(WindowWidth, 100f);
			FocusLock focus = windowRect.gameObject.AddComponent<FocusLock>();
			focus.lockId = InputLockId;
			KrabUi.Vertical(windowRect.gameObject, 1, 0f);
			ContentSizeFitter fitter = windowRect.gameObject.AddComponent<ContentSizeFitter>();
			fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

			BuildTitlebar();
			BuildBody();
		}

		private void BuildTitlebar()
		{
			RectTransform bar = KrabUi.Bordered("Titlebar", windowRect, KrabUi.HeadA, KrabUi.Line);
			KrabUi.Size(bar.gameObject, -1f, 34f);
			KrabUi.Horizontal(bar.gameObject, 8, 8f);

			string nodeLabel = KrabEditorWindow.NodeName(remapNode);
			Text title = KrabUi.Label(bar, Loc("#LOC_KRAB_ui_curveTitle") + " — " + nodeLabel, 13,
				KrabUi.Tan, TextAnchor.MiddleLeft, FontStyle.Bold);
			KrabUi.Size(title.gameObject, -1f, 22f, 1f);
			// Was already the close control here (the footer's own "Close" button below
			// was the redundant one). Kept as the plain glyph rather than icon_close
			// (in-game feedback, 2026-07-20: the custom close icon didn't read well).
			KrabUi.TextButton(bar, "✕", Close, KrabUi.Panel2, KrabUi.TanDim, 13, 26f, 24f);

			GameObject dragHandleGo = bar.gameObject;
			DragHandle drag = dragHandleGo.AddComponent<DragHandle>();
			drag.target = windowRect;
		}

		private void BuildBody()
		{
			GameObject body = KrabUi.Go("Body", windowRect);
			KrabUi.Vertical(body, 10, 8f);

			KrabUi.Label(body.transform, Loc("#LOC_KRAB_ui_curveHint"), 11, KrabUi.Muted);

			RectTransform graphPanel = KrabUi.Bordered("Graph", body.transform, KrabUi.Inset, KrabUi.Line);
			KrabUi.Size(graphPanel.gameObject, GraphWidth + 16f, GraphHeight + 16f);
			GameObject areaGo = KrabUi.Go("Area", graphPanel);
			graphArea = (RectTransform)areaGo.transform;
			graphArea.anchorMin = new Vector2(0.5f, 0.5f);
			graphArea.anchorMax = new Vector2(0.5f, 0.5f);
			graphArea.pivot = new Vector2(0f, 0f);
			graphArea.sizeDelta = new Vector2(GraphWidth, GraphHeight);
			graphArea.anchoredPosition = new Vector2(-GraphWidth * 0.5f, -GraphHeight * 0.5f);
			Image areaBg = areaGo.AddComponent<Image>();
			areaBg.color = new Color(0f, 0f, 0f, 0f); // invisible, catches clicks for "add point"
			ClickCatcher catcher = areaGo.AddComponent<ClickCatcher>();
			catcher.owner = this;

			GameObject lineGo = KrabUi.Go("Line", graphArea);
			RectTransform lineRect = (RectTransform)lineGo.transform;
			KrabUi.Stretch(lineRect, 0f);
			// Stretch only fixes the anchors/size — a fresh RectTransform's pivot
			// still defaults to its center, so OnPopulateMesh's local (0,0) would sit
			// at the box's middle instead of its bottom-left corner (in-game report,
			// 2026-07-15: the curve rendered shifted up-right, spilling past the box).
			lineRect.pivot = Vector2.zero;
			line = lineGo.AddComponent<KrabCurveLine>();
			line.color = KrabUi.GreenHi;
			line.raycastTarget = false;

			GameObject cursorGo = KrabUi.Go("Cursor", graphArea);
			cursorMark = (RectTransform)cursorGo.transform;
			// anchorMin/anchorMax were never set here before (only pivot/sizeDelta) —
			// left at whatever a fresh RectTransform defaults to, which is NOT the
			// point-anchor-at-parent-corner this math assumes (in-game report,
			// 2026-07-15: still offset toward the window's center after the pivot fix).
			cursorMark.anchorMin = cursorMark.anchorMax = Vector2.zero;
			cursorMark.anchoredPosition = Vector2.zero;
			cursorMark.sizeDelta = new Vector2(1.4f, GraphHeight);
			cursorMark.pivot = new Vector2(0.5f, 0f);
			Image cursorImg = cursorGo.AddComponent<Image>();
			cursorImg.color = new Color(KrabUi.Malachite.r, KrabUi.Malachite.g, KrabUi.Malachite.b, 0.55f);
			cursorImg.raycastTarget = false;

			RectTransform pointsHostRect = (RectTransform)KrabUi.Go("Points", graphArea).transform;
			// anchorMin/anchorMax were never set here either (see the Cursor note
			// above) — a plain point-anchor needs (0,0) explicitly, it does not come
			// for free from a fresh RectTransform.
			pointsHostRect.anchorMin = pointsHostRect.anchorMax = Vector2.zero;
			pointsHostRect.anchoredPosition = Vector2.zero;
			pointsHostRect.pivot = Vector2.zero;
			pointsHostRect.sizeDelta = Vector2.zero;
			pointsHost = pointsHostRect;

			GameObject axisRow = KrabUi.Go("Axis", body.transform);
			KrabUi.Horizontal(axisRow, 0, 6f);
			Text domainLabel = KrabUi.Label(axisRow.transform,
				Loc("#LOC_KRAB_ui_curveIn") + " " + domainMin.ToString("G4") + " .. " + domainMax.ToString("G4"), 10, KrabUi.TanDim);
			KrabUi.Size(domainLabel.gameObject, -1f, 18f, 1f);
			Text rangeLabel = KrabUi.Label(axisRow.transform,
				Loc("#LOC_KRAB_ui_curveOut") + " " + rangeMin.ToString("G4") + " .. " + rangeMax.ToString("G4"), 10, KrabUi.TanDim, TextAnchor.MiddleRight);
			KrabUi.Size(rangeLabel.gameObject, -1f, 18f);

			GameObject liveRow = KrabUi.Go("Live", body.transform);
			KrabUi.Horizontal(liveRow, 0, 6f);
			KrabUi.Label(liveRow.transform, Loc("#LOC_KRAB_ui_curveLive"), 10, KrabUi.TanDim);
			cursorText = KrabUi.Label(liveRow.transform, "—", 12, KrabUi.Malachite, TextAnchor.MiddleRight, FontStyle.Bold);
			KrabUi.Size(cursorText.gameObject, -1f, 18f, 1f);

			selectedRow = KrabUi.Go("Selected", body.transform).transform;
			KrabUi.Horizontal(selectedRow.gameObject, 0, 8f);
			KrabUi.Size(selectedRow.gameObject, -1f, 22f);

			// Close now lives only in the titlebar (in-game request, 2026-07-19).
			GameObject footer = KrabUi.Go("Footer", body.transform);
			KrabUi.Horizontal(footer, 0, 8f);
			KrabUi.TextButton(footer.transform, Loc("#LOC_KRAB_ui_curveReset"), ResetToLinear,
				KrabUi.Panel2, KrabUi.Danger, 12, 0f, 24f);
			KrabUi.Spacer(footer.transform);
			// Plain safe glyphs (core Arrows block, already vetted elsewhere in this UI —
			// see KrabUi.IconButton's own note on font coverage), not custom PNGs: flip
			// wasn't part of the icon set the player already drew (in-game request,
			// 2026-07-21).
			Button flipVButton = KrabUi.IconButton(footer.transform, "↕", FlipVertical, KrabUi.TanDim, 22f);
			KrabUi.Tooltip(flipVButton.gameObject, "#LOC_KRAB_tip_flipVertical");
			Button flipHButton = KrabUi.IconButton(footer.transform, "↔", FlipHorizontal, KrabUi.TanDim, 22f);
			KrabUi.Tooltip(flipHButton.gameObject, "#LOC_KRAB_tip_flipHorizontal");

			RedrawGraph();
			RefreshSelectedRow();
		}

		// -------------------------------------------------------------- curve data

		/// <summary>Loads the existing curve, or seeds a linear 2-point one from the
		/// current inMin/inMax/outMin/outMax so opening the window never silently
		/// changes behavior — "Reset to linear" is the explicit way back out.</summary>
		private void LoadCurveFromNode()
		{
			domainMin = remapNode.GetFloat("inMin", 0f);
			domainMax = remapNode.GetFloat("inMax", 1f);
			rangeMin = remapNode.GetFloat("outMin", 0f);
			rangeMax = remapNode.GetFloat("outMax", 1f);
			if (domainMax <= domainMin)
			{
				domainMax = domainMin + 1f;
			}

			ConfigNode existing = remapNode.GetNode("curve");
			if (existing != null)
			{
				FloatCurve loaded = new FloatCurve();
				loaded.Load(existing);
				if (loaded.Curve.length >= 2)
				{
					curve = new AnimationCurve(loaded.Curve.keys);
					return;
				}
			}
			curve = new AnimationCurve(
				new Keyframe(domainMin, rangeMin),
				new Keyframe(domainMax, rangeMax));
			SmoothAll();
			SnapshotForUndo(); // seeding the curve is a real graph write (linear-equivalent, but Undo should still see it)
			PushToNode(recompileOnly: false);
		}

		private void SmoothAll()
		{
			for (int i = 0; i < curve.length; i++)
			{
				curve.SmoothTangents(i, 0f);
			}
		}

		/// <summary>One undo checkpoint per user gesture (click, drag start, field commit) —
		/// never per drag frame, or every mouse-move would fragment into its own undo step.</summary>
		private void SnapshotForUndo()
		{
			if (owner != null)
			{
				owner.CaptureUndoSnapshot();
			}
		}

		private void PushToNode(bool recompileOnly)
		{
			FloatCurve fc = new FloatCurve(curve.keys);
			ConfigNode cn = new ConfigNode();
			fc.Save(cn);
			remapNode.SetNode("curve", cn);
			if (recompileOnly)
			{
				module.RecompileOnly();
			}
			else
			{
				module.NotifyGraphEdited();
			}
		}

		private Vector2 ValueToLocal(float t, float v)
		{
			float x = Mathf.InverseLerp(domainMin, domainMax, t) * GraphWidth;
			float y = Mathf.InverseLerp(rangeMin, rangeMax, v) * GraphHeight;
			return new Vector2(x, y);
		}

		private void LocalToValue(Vector2 local, out float t, out float v)
		{
			t = Mathf.Lerp(domainMin, domainMax, Mathf.Clamp01(local.x / GraphWidth));
			v = Mathf.Lerp(rangeMin, rangeMax, Mathf.Clamp01(local.y / GraphHeight));
		}

		// ------------------------------------------------------------ interactions

		private void AddPoint(Vector2 localPos)
		{
			LocalToValue(localPos, out float t, out float v);
			int index = curve.AddKey(t, v);
			if (index < 0)
			{
				return; // a key already exists at that exact time
			}
			SnapshotForUndo();
			SmoothAll();
			PushToNode(recompileOnly: false);
			selectedIndex = index;
			RedrawGraph();
			RefreshSelectedRow();
		}

		private void SelectPoint(int index)
		{
			selectedIndex = index;
			RefreshSelectedRow();
		}

		private void DragPoint(int index, Vector2 localPos)
		{
			LocalToValue(localPos, out float t, out float v);
			Keyframe[] keys = curve.keys;
			float minT = index > 0 ? keys[index - 1].time + 0.001f : domainMin;
			float maxT = index < keys.Length - 1 ? keys[index + 1].time - 0.001f : domainMax;
			if (minT > maxT)
			{
				minT = maxT = keys[index].time;
			}
			t = Mathf.Clamp(t, minT, maxT);
			v = Mathf.Clamp(v, rangeMin, rangeMax);
			Keyframe kf = keys[index];
			kf.time = t;
			kf.value = v;
			int newIndex = curve.MoveKey(index, kf);
			SmoothNear(newIndex);
			PushToNode(recompileOnly: true); // cheap: live feedback only, no full serialize mid-drag
			selectedIndex = newIndex;
			UpdateLiveDrag(newIndex);
			RefreshSelectedRow();
		}

		private void EndDrag()
		{
			PushToNode(recompileOnly: false); // one full persistence write per drag gesture
		}

		private void SmoothNear(int index)
		{
			for (int i = Mathf.Max(0, index - 1); i <= Mathf.Min(curve.length - 1, index + 1); i++)
			{
				curve.SmoothTangents(i, 0f);
			}
		}

		private void RemoveSelected()
		{
			if (selectedIndex < 0 || curve.length <= 2)
			{
				return;
			}
			SnapshotForUndo();
			curve.RemoveKey(selectedIndex);
			SmoothAll();
			PushToNode(recompileOnly: false);
			selectedIndex = -1;
			RedrawGraph();
			RefreshSelectedRow();
		}

		/// <summary>Mirrors every keyframe's value across the range's midpoint (time
		/// untouched); order is unaffected since the keys stay in the same time
		/// positions, so the selected point (if any) stays selected.</summary>
		private void FlipVertical()
		{
			SnapshotForUndo();
			Keyframe[] keys = curve.keys;
			for (int i = 0; i < keys.Length; i++)
			{
				keys[i].value = rangeMin + rangeMax - keys[i].value;
			}
			curve = new AnimationCurve(keys);
			SmoothAll();
			PushToNode(recompileOnly: false);
			RedrawGraph();
			RefreshSelectedRow();
		}

		/// <summary>Mirrors every keyframe's time across the domain's midpoint (value
		/// untouched) — this reverses the keys' time order, so the array is rebuilt
		/// back-to-front to keep it ascending, and a selected point's index is
		/// remapped to the same physical point instead of jumping to a different one.</summary>
		private void FlipHorizontal()
		{
			SnapshotForUndo();
			Keyframe[] old = curve.keys;
			Keyframe[] flipped = new Keyframe[old.Length];
			for (int i = 0; i < old.Length; i++)
			{
				Keyframe source = old[old.Length - 1 - i];
				flipped[i] = new Keyframe(domainMin + domainMax - source.time, source.value);
			}
			curve = new AnimationCurve(flipped);
			SmoothAll();
			if (selectedIndex >= 0)
			{
				selectedIndex = old.Length - 1 - selectedIndex;
			}
			PushToNode(recompileOnly: false);
			RedrawGraph();
			RefreshSelectedRow();
		}

		private void ResetToLinear()
		{
			SnapshotForUndo();
			remapNode.RemoveNode("curve");
			module.NotifyGraphEdited();
			Close();
		}

		// ------------------------------------------------------------------ draw

		private void RedrawGraph()
		{
			for (int i = pointsHost.childCount - 1; i >= 0; i--)
			{
				Destroy(pointsHost.GetChild(i).gameObject);
			}
			List<Vector2> samples = new List<Vector2>(48);
			for (int i = 0; i <= 47; i++)
			{
				float t = Mathf.Lerp(domainMin, domainMax, i / 47f);
				samples.Add(ValueToLocal(t, curve.Evaluate(t)));
			}
			line.SetPoints(samples);

			Keyframe[] keys = curve.keys;
			pointDots = new RectTransform[keys.Length];
			for (int i = 0; i < keys.Length; i++)
			{
				Vector2 pos = ValueToLocal(keys[i].time, keys[i].value);
				GameObject dot = KrabUi.Go("Point", pointsHost);
				RectTransform dotRect = (RectTransform)dot.transform;
				dotRect.sizeDelta = new Vector2(PointSize, PointSize);
				dotRect.anchorMin = dotRect.anchorMax = Vector2.zero;
				dotRect.pivot = new Vector2(0.5f, 0.5f);
				dotRect.anchoredPosition = pos;
				Image dotImg = dot.AddComponent<Image>();
				dotImg.color = i == selectedIndex ? KrabUi.Malachite : KrabUi.Tan;
				PointHandle handle = dot.AddComponent<PointHandle>();
				handle.owner = this;
				handle.keyIndex = i;
				pointDots[i] = dotRect;
			}
		}

		/// <summary>Moves the dragged dot and reshapes the line in place, without touching
		/// any GameObject — RedrawGraph() destroys and recreates every dot, including the
		/// one Unity's EventSystem is actively mid-drag on, which silently stops delivering
		/// further OnDrag calls to a destroyed target (in-game report, 2026-07-15: a point
		/// only crept by ~0.01 per grab instead of following the mouse — that's the one
		/// OnDrag call that lands before the recreated dot orphans the gesture).</summary>
		private void UpdateLiveDrag(int index)
		{
			List<Vector2> samples = new List<Vector2>(48);
			for (int i = 0; i <= 47; i++)
			{
				float t = Mathf.Lerp(domainMin, domainMax, i / 47f);
				samples.Add(ValueToLocal(t, curve.Evaluate(t)));
			}
			line.SetPoints(samples);

			if (pointDots != null && index >= 0 && index < pointDots.Length && pointDots[index] != null)
			{
				Keyframe kf = curve.keys[index];
				pointDots[index].anchoredPosition = ValueToLocal(kf.time, kf.value);
			}
			else
			{
				RedrawGraph(); // index drifted (shouldn't happen, dragging never reorders keys) — safe fallback
			}
		}

		private void RefreshSelectedRow()
		{
			for (int i = selectedRow.childCount - 1; i >= 0; i--)
			{
				Destroy(selectedRow.GetChild(i).gameObject);
			}
			if (selectedIndex < 0 || selectedIndex >= curve.length)
			{
				KrabUi.Label(selectedRow, Loc("#LOC_KRAB_ui_curveMinPoints"), 10, KrabUi.Faint);
				return;
			}
			Keyframe kf = curve.keys[selectedIndex];
			KrabUi.Label(selectedRow, Loc("#LOC_KRAB_ui_curvePoint") + " " + (selectedIndex + 1), 11, KrabUi.Text);
			KrabUi.Field(selectedRow, kf.time.ToString("G4"), 60f, text => SetSelectedTime(text));
			KrabUi.Field(selectedRow, kf.value.ToString("G4"), 60f, text => SetSelectedValue(text));
			KrabUi.Spacer(selectedRow);
			bool canRemove = curve.length > 2;
			KrabUi.TextButton(selectedRow, Loc("#LOC_KRAB_ui_curveRemovePoint"), RemoveSelected,
				KrabUi.Panel2, canRemove ? KrabUi.Danger : KrabUi.Faint, 11, 0f, 20f);
		}

		private void SetSelectedTime(string text)
		{
			if (selectedIndex < 0 || !float.TryParse(text, System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float t))
			{
				RefreshSelectedRow();
				return;
			}
			SnapshotForUndo();
			DragPoint(selectedIndex, ValueToLocal(t, curve.keys[selectedIndex].value));
			EndDrag();
		}

		private void SetSelectedValue(string text)
		{
			if (selectedIndex < 0 || !float.TryParse(text, System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float v))
			{
				RefreshSelectedRow();
				return;
			}
			SnapshotForUndo();
			DragPoint(selectedIndex, ValueToLocal(curve.keys[selectedIndex].time, v));
			EndDrag();
		}

		// --------------------------------------------------------------- refresh

		private void LateUpdate()
		{
			if (module == null || remapNode == null)
			{
				Close();
				return;
			}
			if (Time.unscaledTime < nextRefresh)
			{
				return;
			}
			nextRefresh = Time.unscaledTime + RefreshInterval;

			if (!TryGetLiveInput0(out float rawInput))
			{
				cursorMark.gameObject.SetActive(false);
				cursorText.text = "—";
				return;
			}
			cursorMark.gameObject.SetActive(true);
			float clampedT = Mathf.Clamp(rawInput, domainMin, domainMax);
			cursorMark.anchoredPosition = new Vector2(ValueToLocal(clampedT, 0f).x, 0f);
			cursorText.text = rawInput.ToString("F2") + " → " + curve.Evaluate(rawInput).ToString("F2");
		}

		/// <summary>Live value flowing into the Remap's port 0 (its raw input) — the same
		/// upstream-lookup KrabEditorWindow uses for per-node telemetry, reused here to
		/// drive the cursor. False (no mark shown) if unconnected or nothing to read yet.</summary>
		private bool TryGetLiveInput0(out float value)
		{
			value = 0f;
			KrabEvaluator evaluator = module.Evaluator;
			if (evaluator == null)
			{
				return false;
			}
			KrabLink link = module.Graph.FindLinkTo(remapNode.id, 0);
			if (link != null)
			{
				KrabNode upstream = module.Graph.FindNode(link.fromId);
				return evaluator.TryGetNodeOutput(upstream, out value);
			}
			KrabPortDefault def = module.Graph.FindDefault(remapNode.id, 0);
			if (def != null)
			{
				value = def.value;
				return true;
			}
			return false;
		}

		// --------------------------------------------------------------- helpers

		/// <summary>Blocks scene input while the pointer is over the window (same pattern as KrabEditorWindow's own FocusLock).</summary>
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

		private class DragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler
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

		private class ClickCatcher : MonoBehaviour, IPointerClickHandler
		{
			public KrabCurveWindow owner;

			public void OnPointerClick(PointerEventData eventData)
			{
				if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
					owner.graphArea, eventData.position, eventData.pressEventCamera, out Vector2 local))
				{
					owner.AddPoint(local);
				}
			}
		}

		private class PointHandle : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
		{
			public KrabCurveWindow owner;
			public int keyIndex;

			public void OnBeginDrag(PointerEventData eventData)
			{
				owner.SelectPoint(keyIndex);
				owner.SnapshotForUndo(); // once per drag gesture, not per frame — see PushToNode(true) below
			}

			public void OnDrag(PointerEventData eventData)
			{
				if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
					owner.graphArea, eventData.position, eventData.pressEventCamera, out Vector2 local))
				{
					owner.DragPoint(keyIndex, local);
				}
			}

			public void OnEndDrag(PointerEventData eventData)
			{
				owner.EndDrag();
			}

			public void OnPointerClick(PointerEventData eventData)
			{
				owner.SelectPoint(keyIndex);
			}
		}
	}
}
