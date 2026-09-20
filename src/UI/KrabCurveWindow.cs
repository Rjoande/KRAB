using System.Collections.Generic;
using KRAB.Graph;
using KRAB.Graph.Evaluation;
using KSP.Localization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace KRAB.UI
{
	/// <summary>Modeless curve editor for a Remap node's optional `curve` sub-node, one
	/// open at a time. The curve's keyframes define domain (x = raw input) and range
	/// (y = output) directly, as RemapRuntime reads them; tangents are auto-smoothed.</summary>
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

		// Last on-screen position, session-scoped like KrabEditorWindow's own
		// lastWindowPosition. Not persisted.
		private static Vector2? lastWindowPosition;

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

		/// <summary>Called when the owning KrabEditorWindow itself closes: a curve window
		/// pointing at a node from that editor's graph would otherwise be left floating.</summary>
		public static void CloseAny()
		{
			if (current != null)
			{
				current.Close();
			}
		}

		/// <summary>Re-points at the node with the same id after Undo/Redo replaces the
		/// whole KrabGraph, since `remapNode` would otherwise be a detached instance whose
		/// writes never reach the live graph. Closes only if that node no longer exists.</summary>
		public static void SyncAfterUndoRedo()
		{
			if (current == null)
			{
				return;
			}
			KrabNode resolved = current.module.Graph.FindNode(current.remapNode.id);
			if (resolved == null || !resolved.HasNode("curve"))
			{
				// No node, or a node whose curve was undone away: nothing left to show.
				// Closing also avoids LoadCurveFromNode's seed-a-linear-curve-and-write-it-
				// back path, which would immediately fight the undo just performed.
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
			if (windowRect != null)
			{
				lastWindowPosition = windowRect.anchoredPosition;
			}
			InputLockManager.RemoveControlLock(InputLockId);
			GameEvents.onGameSceneLoadRequested.Remove(OnSceneChange);
			GameEvents.onHideUI.Remove(HandleHideUI);
			GameEvents.onShowUI.Remove(HandleShowUI);
			GameEvents.onGamePause.Remove(HandleGamePause);
			GameEvents.onGameUnpause.Remove(HandleGameUnpause);
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

		// Independent flags, same pattern as KrabEditorWindow: F2 and Esc can each be
		// toggled on their own.
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
			gameObject.SetActive(!hiddenByUI && !hiddenByPause);
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
			GameEvents.onHideUI.Add(HandleHideUI);
			GameEvents.onShowUI.Add(HandleShowUI);
			GameEvents.onGamePause.Add(HandleGamePause);
			GameEvents.onGameUnpause.Add(HandleGameUnpause);
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
			windowRect.anchoredPosition = lastWindowPosition ?? new Vector2(-260f, 40f); // to the left of the main editor
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
			// Stretch only fixes the anchors and size: a fresh RectTransform's pivot
			// stays at its centre, which would put OnPopulateMesh's local (0,0) in the
			// middle of the box instead of its bottom-left corner.
			lineRect.pivot = Vector2.zero;
			line = lineGo.AddComponent<KrabCurveLine>();
			line.color = KrabUi.GreenHi;
			line.raycastTarget = false;

			GameObject cursorGo = KrabUi.Go("Cursor", graphArea);
			cursorMark = (RectTransform)cursorGo.transform;
			// A fresh RectTransform's anchors are not the point-anchor at the parent's
			// corner that this positioning math assumes, so set them explicitly.
			cursorMark.anchorMin = cursorMark.anchorMax = Vector2.zero;
			cursorMark.anchoredPosition = Vector2.zero;
			cursorMark.sizeDelta = new Vector2(1.4f, GraphHeight);
			cursorMark.pivot = new Vector2(0.5f, 0f);
			Image cursorImg = cursorGo.AddComponent<Image>();
			cursorImg.color = new Color(KrabUi.Malachite.r, KrabUi.Malachite.g, KrabUi.Malachite.b, 0.55f);
			cursorImg.raycastTarget = false;

			RectTransform pointsHostRect = (RectTransform)KrabUi.Go("Points", graphArea).transform;
			// A plain point-anchor needs (0,0) set explicitly here too, for the same
			// reason as the Cursor above.
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

			GameObject footer = KrabUi.Go("Footer", body.transform);
			KrabUi.Horizontal(footer, 0, 8f);
			KrabUi.TextButton(footer.transform, Loc("#LOC_KRAB_ui_curveReset"), ResetToLinear,
				KrabUi.Panel2, KrabUi.Danger, 12, 0f, 24f);
			KrabUi.Spacer(footer.transform);
			Button flipVButton = KrabUi.ImageIconButton(footer.transform, "flipV", FlipVertical, KrabUi.TanDim, 22f);
			KrabUi.Tooltip(flipVButton.gameObject, "#LOC_KRAB_tip_flipVertical");
			Button flipHButton = KrabUi.ImageIconButton(footer.transform, "flipH", FlipHorizontal, KrabUi.TanDim, 22f);
			KrabUi.Tooltip(flipHButton.gameObject, "#LOC_KRAB_tip_flipHorizontal");

			RedrawGraph();
			RefreshSelectedRow();
		}

		// -------------------------------------------------------------- curve data

		/// <summary>Loads the existing curve, or seeds a linear 2-point one from the current
		/// inMin/inMax/outMin/outMax so opening the window never silently changes behavior.
		/// "Reset to linear" is the explicit way back out.</summary>
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

		/// <summary>One undo checkpoint per user gesture (click, drag start, field commit),
		/// never per drag frame, or every mouse-move would be its own undo step.</summary>
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

		/// <summary>Mirrors every keyframe's time across the domain's midpoint, value
		/// untouched. That reverses the time order, so the array is rebuilt back-to-front
		/// to stay ascending and the selected index is remapped to the same point.</summary>
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

		/// <summary>Moves the dragged dot and reshapes the line in place, touching no
		/// GameObject: RedrawGraph() would destroy the dot the EventSystem is mid-drag
		/// on, and a destroyed target stops receiving OnDrag.</summary>
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

		/// <summary>Live value flowing into the Remap's port 0 (its raw input), by the same
		/// upstream lookup KrabEditorWindow uses for per-node telemetry. False, and no mark
		/// shown, if the port is unconnected or there is nothing to read yet.</summary>
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
