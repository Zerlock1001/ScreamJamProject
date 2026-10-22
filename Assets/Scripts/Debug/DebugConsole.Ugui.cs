using System;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ScreamJam.Debugging
{
    public sealed partial class DebugConsole
    {
        private GameObject _uguiRoot;
        private Canvas _uguiCanvas;
        private ScrollRect _uguiScroll;
        private Text _uguiLog;
        private InputField _uguiInput;
        private RawImage _uguiImage;
        private Font _uguiFont;
        private bool _uguiDirty = true;

        private void CreateUguiConsole()
        {
            if (_uguiRoot != null)
                return;

            try
            {
                _uguiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                if (_uguiFont == null)
                    _uguiFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
                if (_uguiFont == null)
                    _uguiFont = _font;

                _uguiRoot = new GameObject(
                    "DebugConsoleWindow",
                    typeof(RectTransform),
                    typeof(Canvas),
                    typeof(CanvasScaler),
                    typeof(GraphicRaycaster));

                _uguiRoot.transform.SetParent(transform, false);

                _uguiCanvas = _uguiRoot.GetComponent<Canvas>();
                _uguiCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _uguiCanvas.sortingOrder = 32000;

                var scaler = _uguiRoot.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;

                if (EventSystem.current == null)
                {
                    var eventSystem = new GameObject(
                        "DebugConsoleEventSystem",
                        typeof(EventSystem),
                        typeof(StandaloneInputModule));
                    eventSystem.transform.SetParent(transform, false);
                }

                BuildUguiWindow();
                _uguiRoot.SetActive(false);
                _uguiDirty = true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Failed to create the UGUI debug console: " + exception.Message);

                if (_uguiRoot != null)
                    Destroy(_uguiRoot);

                _uguiRoot = null;
                _uguiInput = null;
                _uguiLog = null;
                _uguiScroll = null;
                _uguiImage = null;
            }
        }

        private void BuildUguiWindow()
        {
            GameObject panelObject = CreateUiObject(
                "Panel",
                _uguiRoot.transform,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));

            var panelRect = panelObject.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0f, 1f);
            panelRect.anchorMax = new Vector2(0f, 1f);
            panelRect.pivot = new Vector2(0f, 1f);
            panelRect.anchoredPosition = new Vector2(24f, -24f);
            panelRect.sizeDelta = new Vector2(900f, 620f);

            var panelImage = panelObject.GetComponent<Image>();
            panelImage.color = new Color(0.04f, 0.04f, 0.06f, 0.97f);
            panelImage.raycastTarget = true;

            GameObject headerObject = CreateUiObject(
                "Header",
                panelObject.transform,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            var headerRect = headerObject.GetComponent<RectTransform>();
            headerRect.anchorMin = new Vector2(0f, 1f);
            headerRect.anchorMax = new Vector2(1f, 1f);
            headerRect.pivot = new Vector2(0.5f, 1f);
            headerRect.anchoredPosition = Vector2.zero;
            headerRect.sizeDelta = new Vector2(0f, 36f);
            headerObject.GetComponent<Image>().color = new Color(0.12f, 0.12f, 0.16f, 1f);

            Text title = CreateText(
                "Title",
                headerObject.transform,
                "Debug Console",
                16,
                TextAnchor.MiddleLeft,
                new Color(0.86f, 0.9f, 1f, 1f));
            var titleRect = title.rectTransform;
            titleRect.anchorMin = new Vector2(0f, 0f);
            titleRect.anchorMax = new Vector2(1f, 1f);
            titleRect.offsetMin = new Vector2(12f, 0f);
            titleRect.offsetMax = new Vector2(-46f, 0f);

            Button closeButton = CreateButton(
                "Close",
                headerObject.transform,
                "X",
                () => SetOpen(false));
            var closeRect = closeButton.GetComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(1f, 0.5f);
            closeRect.anchorMax = new Vector2(1f, 0.5f);
            closeRect.pivot = new Vector2(1f, 0.5f);
            closeRect.anchoredPosition = new Vector2(-6f, 0f);
            closeRect.sizeDelta = new Vector2(28f, 28f);

            GameObject scrollObject = CreateUiObject(
                "LogScroll",
                panelObject.transform,
                typeof(RectTransform),
                typeof(ScrollRect));
            var scrollRectTransform = scrollObject.GetComponent<RectTransform>();
            scrollRectTransform.anchorMin = Vector2.zero;
            scrollRectTransform.anchorMax = Vector2.one;
            scrollRectTransform.offsetMin = new Vector2(8f, 244f);
            scrollRectTransform.offsetMax = new Vector2(-8f, -44f);

            _uguiScroll = scrollObject.GetComponent<ScrollRect>();
            _uguiScroll.horizontal = false;
            _uguiScroll.vertical = true;
            _uguiScroll.movementType = ScrollRect.MovementType.Clamped;
            _uguiScroll.scrollSensitivity = 30f;

            GameObject viewportObject = CreateUiObject(
                "Viewport",
                scrollObject.transform,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Mask));
            var viewportRect = viewportObject.GetComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = Vector2.zero;
            var viewportImage = viewportObject.GetComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0.01f);
            viewportObject.GetComponent<Mask>().showMaskGraphic = false;

            GameObject contentObject = CreateUiObject(
                "Content",
                viewportObject.transform,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Text),
                typeof(ContentSizeFitter));
            var contentRect = contentObject.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0f, 0f);

            _uguiLog = contentObject.GetComponent<Text>();
            _uguiLog.font = _uguiFont;
            _uguiLog.fontSize = 14;
            _uguiLog.lineSpacing = 1f;
            _uguiLog.color = new Color(0.86f, 0.88f, 0.9f, 1f);
            _uguiLog.alignment = TextAnchor.UpperLeft;
            _uguiLog.horizontalOverflow = HorizontalWrapMode.Wrap;
            _uguiLog.verticalOverflow = VerticalWrapMode.Overflow;
            _uguiLog.supportRichText = false;
            _uguiLog.raycastTarget = false;

            var fitter = contentObject.GetComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _uguiScroll.viewport = viewportRect;
            _uguiScroll.content = contentRect;

            GameObject imageObject = CreateUiObject(
                "ImagePreview",
                panelObject.transform,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(RawImage));
            var imageRect = imageObject.GetComponent<RectTransform>();
            imageRect.anchorMin = new Vector2(0f, 0f);
            imageRect.anchorMax = new Vector2(1f, 0f);
            imageRect.pivot = new Vector2(0.5f, 0f);
            imageRect.anchoredPosition = new Vector2(0f, 54f);
            imageRect.sizeDelta = new Vector2(-16f, 180f);

            _uguiImage = imageObject.GetComponent<RawImage>();
            _uguiImage.color = Color.white;
            _uguiImage.raycastTarget = false;
            imageObject.SetActive(false);

            GameObject footerObject = CreateUiObject(
                "InputRow",
                panelObject.transform,
                typeof(RectTransform));
            var footerRect = footerObject.GetComponent<RectTransform>();
            footerRect.anchorMin = new Vector2(0f, 0f);
            footerRect.anchorMax = new Vector2(1f, 0f);
            footerRect.pivot = new Vector2(0.5f, 0f);
            footerRect.anchoredPosition = new Vector2(0f, 8f);
            footerRect.sizeDelta = new Vector2(0f, 38f);

            CreateInputField(footerObject.transform);

            Button clearButton = CreateButton(
                "Clear",
                footerObject.transform,
                "Clear",
                Clear);
            var clearRect = clearButton.GetComponent<RectTransform>();
            clearRect.anchorMin = new Vector2(1f, 0.5f);
            clearRect.anchorMax = new Vector2(1f, 0.5f);
            clearRect.pivot = new Vector2(1f, 0.5f);
            clearRect.anchoredPosition = new Vector2(-70f, 0f);
            clearRect.sizeDelta = new Vector2(58f, 30f);

            Button copyButton = CreateButton(
                "Copy",
                footerObject.transform,
                "Copy",
                CopyAll);
            var copyRect = copyButton.GetComponent<RectTransform>();
            copyRect.anchorMin = new Vector2(1f, 0.5f);
            copyRect.anchorMax = new Vector2(1f, 0.5f);
            copyRect.pivot = new Vector2(1f, 0.5f);
            copyRect.anchoredPosition = new Vector2(-132f, 0f);
            copyRect.sizeDelta = new Vector2(58f, 30f);

            Button runButton = CreateButton(
                "Run",
                footerObject.transform,
                "Run",
                SubmitUguiInput);
            var runRect = runButton.GetComponent<RectTransform>();
            runRect.anchorMin = new Vector2(1f, 0.5f);
            runRect.anchorMax = new Vector2(1f, 0.5f);
            runRect.pivot = new Vector2(1f, 0.5f);
            runRect.anchoredPosition = new Vector2(-6f, 0f);
            runRect.sizeDelta = new Vector2(58f, 30f);
        }

        private void CreateInputField(Transform parent)
        {
            GameObject inputObject = CreateUiObject(
                "CommandInput",
                parent,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(InputField));

            var inputRect = inputObject.GetComponent<RectTransform>();
            inputRect.anchorMin = Vector2.zero;
            inputRect.anchorMax = Vector2.one;
            inputRect.offsetMin = new Vector2(8f, 4f);
            inputRect.offsetMax = new Vector2(-198f, -4f);

            var background = inputObject.GetComponent<Image>();
            background.color = new Color(0.97f, 0.97f, 0.98f, 1f);

            Text text = CreateText(
                "Text",
                inputObject.transform,
                string.Empty,
                14,
                TextAnchor.MiddleLeft,
                new Color(0.1f, 0.1f, 0.12f, 1f));
            text.raycastTarget = false;
            var textRect = text.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8f, 2f);
            textRect.offsetMax = new Vector2(-8f, -2f);

            Text placeholder = CreateText(
                "Placeholder",
                inputObject.transform,
                "Type /help",
                14,
                TextAnchor.MiddleLeft,
                new Color(0.5f, 0.5f, 0.55f, 1f));
            placeholder.raycastTarget = false;
            var placeholderRect = placeholder.rectTransform;
            placeholderRect.anchorMin = Vector2.zero;
            placeholderRect.anchorMax = Vector2.one;
            placeholderRect.offsetMin = new Vector2(8f, 2f);
            placeholderRect.offsetMax = new Vector2(-8f, -2f);

            _uguiInput = inputObject.GetComponent<InputField>();
            _uguiInput.targetGraphic = background;
            _uguiInput.textComponent = text;
            _uguiInput.placeholder = placeholder;
            _uguiInput.lineType = InputField.LineType.SingleLine;
            _uguiInput.caretColor = new Color(0.1f, 0.1f, 0.12f, 1f);
            _uguiInput.selectionColor = new Color(0.7f, 0.8f, 1f, 0.6f);
            _uguiInput.onEndEdit.AddListener(OnUguiEndEdit);
        }

        private void OnUguiEndEdit(string value)
        {
            if (!_open || string.IsNullOrWhiteSpace(value))
                return;

            _uguiInput.text = string.Empty;
            ExecuteCommand(value.Trim());
            FocusUguiInput();
        }

        private void SubmitUguiInput()
        {
            if (_uguiInput == null)
                return;

            string value = _uguiInput.text;
            _uguiInput.text = string.Empty;
            if (!string.IsNullOrWhiteSpace(value))
                ExecuteCommand(value.Trim());

            FocusUguiInput();
        }

        private void FocusUguiInput()
        {
            if (_uguiInput == null)
                return;

            EventSystem eventSystem = EventSystem.current;
            if (eventSystem != null &&
                eventSystem.currentSelectedGameObject != _uguiInput.gameObject)
            {
                eventSystem.SetSelectedGameObject(_uguiInput.gameObject);
            }

            _uguiInput.ActivateInputField();
        }

        private void UpdateUguiConsole()
        {
            if (_uguiRoot == null || !_open)
                return;

            if (_uguiDirty)
                RefreshUguiConsole();

            if (_uguiInput != null &&
                (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)))
                SubmitUguiInput();
        }

        private void RefreshUguiConsole()
        {
            if (_uguiLog == null)
                return;

            var builder = new StringBuilder();
            int start = Mathf.Max(0, _entries.Count - 300);

            for (int i = start; i < _entries.Count; i++)
            {
                LogEntry entry = _entries[i];
                builder.Append(entry.Time.ToString("HH:mm:ss.fff"))
                    .Append(" [").Append(TagFor(entry)).Append("] ")
                    .Append(entry.Message);

                if (_showStackTraces && !string.IsNullOrEmpty(entry.StackTrace))
                    builder.Append('\n').Append(entry.StackTrace);

                builder.Append('\n');
            }

            _uguiLog.text = builder.ToString();

            Texture2D latestTexture = null;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Texture != null)
                    latestTexture = _entries[i].Texture;
            }

            if (_uguiImage != null)
            {
                _uguiImage.texture = latestTexture;
                _uguiImage.gameObject.SetActive(latestTexture != null);
            }

            _uguiDirty = false;

            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(_uguiLog.rectTransform);

            if (_uguiScroll != null)
                _uguiScroll.verticalNormalizedPosition = 0f;
        }

        private static GameObject CreateUiObject(string name, Transform parent, params Type[] components)
        {
            var gameObject = new GameObject(name, components);
            gameObject.transform.SetParent(parent, false);
            return gameObject;
        }

        private Text CreateText(
            string name,
            Transform parent,
            string value,
            int size,
            TextAnchor alignment,
            Color color)
        {
            GameObject textObject = CreateUiObject(
                name,
                parent,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Text));

            var text = textObject.GetComponent<Text>();
            text.font = _uguiFont;
            text.fontSize = size;
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.supportRichText = false;
            text.raycastTarget = false;
            text.text = value;
            return text;
        }

        private Button CreateButton(
            string name,
            Transform parent,
            string label,
            Action onClick)
        {
            GameObject buttonObject = CreateUiObject(
                name,
                parent,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));

            var background = buttonObject.GetComponent<Image>();
            background.color = new Color(0.2f, 0.22f, 0.28f, 1f);
            background.raycastTarget = true;

            var button = buttonObject.GetComponent<Button>();
            button.targetGraphic = background;
            if (onClick != null)
                button.onClick.AddListener(() => onClick());

            Text text = CreateText(
                "Label",
                buttonObject.transform,
                label,
                13,
                TextAnchor.MiddleCenter,
                new Color(0.92f, 0.94f, 1f, 1f));
            Stretch(text.rectTransform);

            return button;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }
    }
}
