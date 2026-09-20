using System.Collections.Generic;
using KSP.Localization;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace KRAB.UI
{
	/// <summary>Code-built UGUI factory, no asset bundles. Palette: neutral KSP-stock
	/// blue-grey backdrop with an acid-green accent for interactive and live elements
	/// (sliders, telemetry, active states), matching the stock PAW slider color.</summary>
	public static class KrabUi
	{
		// Palette
		public static readonly Color Win = FromHex("262a30");
		public static readonly Color Panel = FromHex("2f343b");
		public static readonly Color Panel2 = FromHex("363c44");
		public static readonly Color Inset = FromHex("1b1e23");
		public static readonly Color Line = FromHex("454c56");
		public static readonly Color HeadA = FromHex("3b424c");
		public static readonly Color Tan = FromHex("d7ddc4");
		public static readonly Color TanDim = FromHex("a3ab8f");
		public static readonly Color Text = FromHex("ccd0d5");
		// Muted/Faint sit at ~5.3:1 / ~4.2:1 against Panel2/Panel: dim enough to read
		// as secondary/tertiary, light enough to stay legible (WCAG AA wants 4.5:1).
		public static readonly Color Muted = FromHex("aeb3ba");
		public static readonly Color Faint = FromHex("90959c");
		public static readonly Color Green = FromHex("9dc23e");
		public static readonly Color GreenHi = FromHex("b9e34f");
		public static readonly Color Malachite = FromHex("c3ea54");
		public static readonly Color Warn = FromHex("d9a441");
		public static readonly Color Danger = FromHex("c8724c");

		private static Font font;

		public static Font Font
		{
			get
			{
				if (font == null)
				{
					font = UnityEngine.Font.CreateDynamicFontFromOSFont(
						new[] { "Segoe UI", "Verdana", "Arial" }, 14);
				}
				return font;
			}
		}

		private static Color FromHex(string hex)
		{
			byte r = byte.Parse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
			byte g = byte.Parse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
			byte b = byte.Parse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);
			return new Color32(r, g, b, 255);
		}

		public static GameObject Go(string name, Transform parent)
		{
			GameObject go = new GameObject(name, typeof(RectTransform));
			go.transform.SetParent(parent, false);
			return go;
		}

		public static Image Panel_(string name, Transform parent, Color color)
		{
			Image image = Go(name, parent).AddComponent<Image>();
			image.color = color;
			return image;
		}

		/// <summary>Panel with a 1px border: border-colored back, inset front fill. The
		/// fill child ignores layout groups so a layout can live on the panel itself.</summary>
		public static RectTransform Bordered(string name, Transform parent, Color fill, Color border)
		{
			Image back = Panel_(name, parent, border);
			Image front = Panel_("Fill", back.transform, fill);
			Stretch(front.rectTransform, 1f);
			front.raycastTarget = false;
			front.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
			return back.rectTransform;
		}

		public static void Stretch(RectTransform rect, float margin = 0f)
		{
			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.offsetMin = new Vector2(margin, margin);
			rect.offsetMax = new Vector2(-margin, -margin);
		}

		public static Text Label(Transform parent, string text, int size, Color color,
			TextAnchor anchor = TextAnchor.MiddleLeft, FontStyle style = FontStyle.Normal)
		{
			Text label = Go("Label", parent).AddComponent<Text>();
			label.font = Font;
			label.fontSize = size;
			label.fontStyle = style;
			label.color = color;
			label.alignment = anchor;
			label.horizontalOverflow = HorizontalWrapMode.Overflow;
			label.verticalOverflow = VerticalWrapMode.Overflow;
			label.text = text;
			label.raycastTarget = false;
			return label;
		}

		public static Button TextButton(Transform parent, string text, UnityAction onClick,
			Color background, Color textColor, int fontSize = 13, float width = 0f, float height = 24f)
		{
			Image image = Panel_("Button", parent, background);
			Button button = image.gameObject.AddComponent<Button>();
			button.targetGraphic = image;
			ColorBlock colors = button.colors;
			colors.normalColor = Color.white;
			colors.highlightedColor = new Color(1.15f, 1.15f, 1.15f);
			colors.pressedColor = new Color(0.85f, 0.85f, 0.85f);
			button.colors = colors;
			button.onClick.AddListener(onClick);
			// Inner layout lets the label's preferred size drive the button width
			// when no fixed width is requested.
			HorizontalLayoutGroup inner = image.gameObject.AddComponent<HorizontalLayoutGroup>();
			inner.padding = new RectOffset(10, 10, 3, 3);
			inner.childAlignment = TextAnchor.MiddleCenter;
			inner.childForceExpandWidth = false;
			inner.childForceExpandHeight = false;
			inner.childControlWidth = true;
			inner.childControlHeight = true;
			Label(image.transform, text, fontSize, textColor, TextAnchor.MiddleCenter);
			LayoutElement le = image.gameObject.AddComponent<LayoutElement>();
			le.preferredHeight = height;
			if (width > 0f)
			{
				le.preferredWidth = width;
			}
			return button;
		}

		public static Slider HSlider(Transform parent, float min, float max, float value,
			UnityAction<float> onChanged, float preferredWidth = 170f)
		{
			GameObject root = Go("Slider", parent);
			LayoutElement le = root.AddComponent<LayoutElement>();
			le.preferredWidth = preferredWidth;
			le.flexibleWidth = 1f;
			le.preferredHeight = 18f;

			Slider slider = root.AddComponent<Slider>();

			Image back = Panel_("Background", root.transform, Line);
			back.rectTransform.anchorMin = new Vector2(0f, 0.5f);
			back.rectTransform.anchorMax = new Vector2(1f, 0.5f);
			back.rectTransform.sizeDelta = new Vector2(0f, 8f);
			Image backFill = Panel_("Inset", back.transform, Inset);
			Stretch(backFill.rectTransform, 1f);
			backFill.raycastTarget = false;

			GameObject fillArea = Go("Fill Area", root.transform);
			RectTransform fillAreaRect = (RectTransform)fillArea.transform;
			fillAreaRect.anchorMin = new Vector2(0f, 0.5f);
			fillAreaRect.anchorMax = new Vector2(1f, 0.5f);
			fillAreaRect.sizeDelta = new Vector2(-8f, 6f);
			fillAreaRect.anchoredPosition = new Vector2(-4f, 0f);
			Image fill = Panel_("Fill", fillArea.transform, Green);
			fill.rectTransform.sizeDelta = new Vector2(4f, 0f);
			fill.raycastTarget = false;

			GameObject handleArea = Go("Handle Slide Area", root.transform);
			RectTransform handleAreaRect = (RectTransform)handleArea.transform;
			Stretch(handleAreaRect);
			handleAreaRect.offsetMin = new Vector2(3f, 0f);
			handleAreaRect.offsetMax = new Vector2(-3f, 0f);
			Image handle = Panel_("Handle", handleArea.transform, Tan);
			handle.rectTransform.sizeDelta = new Vector2(5f, 12f);

			slider.fillRect = fill.rectTransform;
			slider.handleRect = handle.rectTransform;
			slider.targetGraphic = handle;
			slider.direction = Slider.Direction.LeftToRight;
			slider.minValue = min;
			slider.maxValue = max;
			slider.value = value;
			slider.onValueChanged.AddListener(onChanged);
			return slider;
		}

		public static VerticalLayoutGroup Vertical(GameObject go, int padding, float spacing,
			bool childForceExpandWidth = true)
		{
			VerticalLayoutGroup group = go.AddComponent<VerticalLayoutGroup>();
			group.padding = new RectOffset(padding, padding, padding, padding);
			group.spacing = spacing;
			group.childForceExpandWidth = childForceExpandWidth;
			group.childForceExpandHeight = false;
			group.childControlWidth = true;
			group.childControlHeight = true;
			return group;
		}

		public static HorizontalLayoutGroup Horizontal(GameObject go, int padding, float spacing)
		{
			HorizontalLayoutGroup group = go.AddComponent<HorizontalLayoutGroup>();
			group.padding = new RectOffset(padding, padding, padding, padding);
			group.spacing = spacing;
			group.childForceExpandWidth = false;
			group.childForceExpandHeight = false;
			group.childControlWidth = true;
			group.childControlHeight = true;
			group.childAlignment = TextAnchor.MiddleLeft;
			return group;
		}

		public static LayoutElement Size(GameObject go, float width = -1f, float height = -1f, float flexibleWidth = -1f)
		{
			LayoutElement le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
			if (width >= 0f)
			{
				le.preferredWidth = width;
			}
			if (height >= 0f)
			{
				le.preferredHeight = height;
			}
			if (flexibleWidth >= 0f)
			{
				le.flexibleWidth = flexibleWidth;
			}
			return le;
		}

		public static GameObject Spacer(Transform parent, float flexible = 1f)
		{
			GameObject go = Go("Spacer", parent);
			LayoutElement le = go.AddComponent<LayoutElement>();
			le.flexibleWidth = flexible;
			return go;
		}

		/// <summary>Vertical scroll area of fixed height; returns the content transform to
		/// fill, with VerticalLayoutGroup and fitter already attached. Mouse wheel scrolls.</summary>
		public static RectTransform ScrollList(Transform parent, float height)
		{
			RectTransform outer = Bordered("Scroll", parent, Inset, Line);
			Size(outer.gameObject, -1f, height);

			GameObject viewportGo = Go("Viewport", outer);
			RectTransform viewport = (RectTransform)viewportGo.transform;
			Stretch(viewport, 1f);
			viewportGo.AddComponent<RectMask2D>();
			// An (invisible) graphic is required for the mask area to catch drags.
			Image viewportImage = viewportGo.AddComponent<Image>();
			viewportImage.color = Color.clear;

			GameObject contentGo = Go("Content", viewport);
			RectTransform content = (RectTransform)contentGo.transform;
			content.anchorMin = new Vector2(0f, 1f);
			content.anchorMax = new Vector2(1f, 1f);
			content.pivot = new Vector2(0.5f, 1f);
			content.offsetMin = new Vector2(4f, 0f);
			content.offsetMax = new Vector2(-4f, 0f);
			Vertical(contentGo, 4, 3f);
			ContentSizeFitter fitter = contentGo.AddComponent<ContentSizeFitter>();
			fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

			ScrollRect scroll = outer.gameObject.AddComponent<ScrollRect>();
			scroll.viewport = viewport;
			scroll.content = content;
			scroll.horizontal = false;
			scroll.vertical = true;
			scroll.movementType = ScrollRect.MovementType.Clamped;
			scroll.scrollSensitivity = 22f;
			return content;
		}

		/// <summary>Horizontal scroll strip of fixed height; content grows sideways and is
		/// never squeezed, as a plain HorizontalLayoutGroup would shrink every child. Wheel
		/// scrolling needs HorizontalWheelScroll: ScrollRect never remaps a vertical delta.</summary>
		public static RectTransform HScrollList(Transform parent, float height)
		{
			RectTransform outer = Bordered("HScroll", parent, Inset, Line);
			Size(outer.gameObject, -1f, height, 1f);

			GameObject viewportGo = Go("Viewport", outer);
			RectTransform viewport = (RectTransform)viewportGo.transform;
			Stretch(viewport, 1f);
			viewportGo.AddComponent<RectMask2D>();
			Image viewportImage = viewportGo.AddComponent<Image>();
			viewportImage.color = Color.clear;

			GameObject contentGo = Go("Content", viewport);
			RectTransform content = (RectTransform)contentGo.transform;
			content.anchorMin = new Vector2(0f, 0f);
			content.anchorMax = new Vector2(0f, 1f);
			content.pivot = new Vector2(0f, 0.5f);
			content.offsetMin = new Vector2(0f, 3f);
			content.offsetMax = new Vector2(0f, -3f);
			Horizontal(contentGo, 4, 6f);
			ContentSizeFitter fitter = contentGo.AddComponent<ContentSizeFitter>();
			fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

			ScrollRect scroll = outer.gameObject.AddComponent<ScrollRect>();
			scroll.viewport = viewport;
			scroll.content = content;
			scroll.horizontal = true;
			scroll.vertical = false;
			scroll.movementType = ScrollRect.MovementType.Clamped;
			scroll.scrollSensitivity = 22f;
			HorizontalWheelScroll wheel = outer.gameObject.AddComponent<HorizontalWheelScroll>();
			wheel.scroll = scroll;
			return content;
		}

		/// <summary>Redirects a plain vertical mouse-wheel delta onto ScrollRect's horizontal axis.</summary>
		private class HorizontalWheelScroll : MonoBehaviour, UnityEngine.EventSystems.IScrollHandler
		{
			public ScrollRect scroll;

			public void OnScroll(UnityEngine.EventSystems.PointerEventData eventData)
			{
				float delta = Mathf.Abs(eventData.scrollDelta.y) > Mathf.Abs(eventData.scrollDelta.x)
					? eventData.scrollDelta.y
					: eventData.scrollDelta.x;
				scroll.horizontalNormalizedPosition = Mathf.Clamp01(scroll.horizontalNormalizedPosition - delta * 0.12f);
			}
		}

		/// <summary>Grid of uniform cells (picker entries), driven by parent width.</summary>
		public static RectTransform Grid(Transform parent, float cellWidth, float cellHeight, float spacing = 4f)
		{
			GameObject go = Go("Grid", parent);
			GridLayoutGroup grid = go.AddComponent<GridLayoutGroup>();
			grid.cellSize = new Vector2(cellWidth, cellHeight);
			grid.spacing = new Vector2(spacing, spacing);
			grid.childAlignment = TextAnchor.UpperLeft;
			ContentSizeFitter fitter = go.AddComponent<ContentSizeFitter>();
			fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
			return (RectTransform)go.transform;
		}

		/// <summary>Small single-line text field, inset style. While it has keyboard focus
		/// a control lock keeps typed characters away from scene shortcuts; the hover lock
		/// alone leaks if the pointer drifts off the window mid-typing.</summary>
		public static InputField Field(Transform parent, string text, float width,
			UnityAction<string> onEndEdit)
		{
			RectTransform rect = Bordered("Field", parent, Inset, Line);
			Size(rect.gameObject, width, 19f);
			Text textComponent = Label(rect, text, 12, Text, TextAnchor.MiddleCenter);
			Stretch(textComponent.rectTransform, 3f);
			textComponent.supportRichText = false;
			InputField field = rect.gameObject.AddComponent<InputField>();
			field.targetGraphic = rect.GetComponent<Image>();
			field.textComponent = textComponent;
			field.text = text;
			field.lineType = InputField.LineType.SingleLine;
			field.caretWidth = 1;
			field.selectionColor = new Color(Green.r, Green.g, Green.b, 0.35f);
			field.onEndEdit.AddListener(value =>
			{
				InputLockManager.RemoveControlLock(TypingLock.LockId);
				onEndEdit(value);
			});
			rect.gameObject.AddComponent<TypingLock>();
			return field;
		}

		/// <summary>Tiny square icon button. Use glyphs from the core Arrows block
		/// (U+2190-21FF) or Dingbats: the dynamic OS font of <see cref="Font"/> does not
		/// cover Supplemental Arrows-A (e.g. ⟳, U+27F3), which renders as a tofu box.</summary>
		public static Button IconButton(Transform parent, string glyph, UnityAction onClick,
			Color textColor, float size = 20f)
		{
			Image image = Panel_("Icon", parent, Panel2);
			Button button = image.gameObject.AddComponent<Button>();
			button.targetGraphic = image;
			button.onClick.AddListener(onClick);
			// Glyph scales with the button at ~80% of the square, so a 24px undo button
			// gets a 19px arrow; a fixed size reads as tiny on the larger buttons.
			int glyphSize = Mathf.Max(13, Mathf.RoundToInt(size * 0.8f));
			Text label = Label(image.transform, glyph, glyphSize, textColor, TextAnchor.MiddleCenter);
			Stretch(label.rectTransform);
			LayoutElement le = image.gameObject.AddComponent<LayoutElement>();
			le.preferredWidth = size;
			le.preferredHeight = size;
			return button;
		}

		private static readonly Dictionary<string, Sprite> iconSpriteCache = new Dictionary<string, Sprite>();

		/// <summary>Loads Textures/icon_&lt;name&gt;.png once and caches the Sprite for the
		/// session; GameDatabase indexes any plain PNG under GameData automatically. Caches
		/// a null on failure too, so a missing file logs once, not on every button rebuild.</summary>
		private static Sprite LoadIconSprite(string name)
		{
			if (iconSpriteCache.TryGetValue(name, out Sprite cached))
			{
				return cached;
			}
			Texture2D tex = GameDatabase.Instance.GetTexture("KRAB/Textures/icon_" + name, false);
			Sprite sprite = null;
			if (tex != null)
			{
				sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f));
			}
			else
			{
				Debug.LogWarningFormat("[KRAB] icon texture 'KRAB/Textures/icon_{0}' not found", name);
			}
			iconSpriteCache[name] = sprite;
			return sprite;
		}

		/// <summary>Square icon button using a custom white-silhouette PNG instead of a font
		/// glyph: Image.color multiplies the sprite's own pixels, so a pure-white source
		/// comes out in the tint passed. Falls back to a flat square if the PNG is missing.</summary>
		public static Button ImageIconButton(Transform parent, string iconName, UnityAction onClick,
			Color tint, float size = 20f)
		{
			Image back = Panel_("Icon", parent, Panel2);
			Button button = back.gameObject.AddComponent<Button>();
			button.targetGraphic = back;
			button.onClick.AddListener(onClick);
			LayoutElement le = back.gameObject.AddComponent<LayoutElement>();
			le.preferredWidth = size;
			le.preferredHeight = size;

			GameObject glyphGo = Go("Glyph", back.transform);
			Image glyph = glyphGo.AddComponent<Image>();
			glyph.raycastTarget = false;
			glyph.color = tint;
			Sprite sprite = LoadIconSprite(iconName);
			if (sprite != null)
			{
				glyph.sprite = sprite;
				glyph.preserveAspect = true;
			}
			// ~78% of the square, matching IconButton's own glyph-to-button ratio.
			Stretch((RectTransform)glyphGo.transform, size * 0.11f);
			return button;
		}

		private class TypingLock : MonoBehaviour, UnityEngine.EventSystems.ISelectHandler,
			UnityEngine.EventSystems.IDeselectHandler
		{
			public const string LockId = "KRAB_EDITOR_TYPING";

			public void OnSelect(UnityEngine.EventSystems.BaseEventData eventData)
			{
				InputLockManager.SetControlLock(ControlTypes.KEYBOARDINPUT, LockId);
			}

			public void OnDeselect(UnityEngine.EventSystems.BaseEventData eventData)
			{
				InputLockManager.RemoveControlLock(LockId);
			}

			private void OnDisable()
			{
				InputLockManager.RemoveControlLock(LockId);
			}
		}

		private const float TooltipDelay = 0.5f;
		private const float TooltipWidth = 220f;

		/// <summary>Attaches a hover tooltip to any control with a Graphic, forcing that one
		/// Graphic's raycastTarget on (Label leaves it false by default). Shows after a
		/// delay, anchored below the control, not the cursor; one GameObject per hover.</summary>
		public static void Tooltip(GameObject target, string locKey)
		{
			Graphic graphic = target.GetComponent<Graphic>();
			if (graphic != null)
			{
				graphic.raycastTarget = true;
			}
			TooltipTarget hover = target.AddComponent<TooltipTarget>();
			hover.anchor = target.GetComponent<RectTransform>();
			hover.locKey = locKey;
		}

		private class TooltipTarget : MonoBehaviour, UnityEngine.EventSystems.IPointerEnterHandler,
			UnityEngine.EventSystems.IPointerExitHandler
		{
			public RectTransform anchor;
			public string locKey;

			private float hoverStart = -1f;
			private GameObject popup;

			private void Update()
			{
				if (hoverStart >= 0f && popup == null && Time.unscaledTime - hoverStart >= TooltipDelay)
				{
					Show();
				}
			}

			public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData eventData)
			{
				hoverStart = Time.unscaledTime;
			}

			public void OnPointerExit(UnityEngine.EventSystems.PointerEventData eventData)
			{
				hoverStart = -1f;
				Hide();
			}

			private void OnDisable()
			{
				hoverStart = -1f;
				Hide();
			}

			private void Show()
			{
				Canvas canvas = anchor.GetComponentInParent<Canvas>();
				if (canvas == null)
				{
					return;
				}
				RectTransform canvasRect = (RectTransform)canvas.transform;
				Vector3[] corners = new Vector3[4];
				anchor.GetWorldCorners(corners); // [0] = bottom-left
				RectTransformUtility.ScreenPointToLocalPointInRectangle(
					canvasRect, corners[0], null, out Vector2 localPoint);

				RectTransform popupRect = Bordered("Tooltip", canvasRect, Inset, Line);
				popup = popupRect.gameObject;
				popupRect.SetAsLastSibling(); // draw above the window's own content
				// ScreenPointToLocalPointInRectangle returns a point relative to canvasRect's
				// own pivot (typically its centre, not a corner). Anchoring the popup at that
				// same pivot keeps point and anchor in one reference frame.
				popupRect.anchorMin = popupRect.anchorMax = canvasRect.pivot;
				popupRect.pivot = new Vector2(0f, 1f);
				popupRect.sizeDelta = new Vector2(TooltipWidth, 10f);
				// Clamped against the right edge only: the controls that carry tooltips sit
				// in the upper and middle of these windows, so bottom-of-screen overflow is
				// not worth the extra flip logic.
				float x = Mathf.Min(localPoint.x, canvasRect.rect.xMax - TooltipWidth);
				popupRect.anchoredPosition = new Vector2(x, localPoint.y - 6f);

				Vertical(popupRect.gameObject, 6, 0f);
				ContentSizeFitter fitter = popupRect.gameObject.AddComponent<ContentSizeFitter>();
				fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

				Text label = Label(popupRect, Localizer.Format(locKey), 11, Text);
				label.horizontalOverflow = HorizontalWrapMode.Wrap;
				LayoutElement le = label.gameObject.AddComponent<LayoutElement>();
				le.preferredWidth = TooltipWidth - 12f;
			}

			private void Hide()
			{
				if (popup != null)
				{
					Destroy(popup);
					popup = null;
				}
			}
		}
	}
}
