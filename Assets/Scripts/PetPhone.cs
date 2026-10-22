using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 手机：可拖动的聊天窗口。点图片会打开可拖动的查看窗，可以同时开多张。
/// </summary>
public class PetPhone : MonoBehaviour
{
    public static PetPhone Instance { get; private set; }

    RectTransform speechRoot;
    Text speechText;
    Text statusText;
    ScrollRect chatScroll;
    RectTransform chatContent;
    GameObject visual;
    Button launcher;
    int pictureWindows;
    GameObject nameRow;
    InputField nameInput;
    Action<string> nameSubmitted;

    const float PhoneWidth = 380f;
    const float PhoneHeight = 560f;
    const float ShellInset = 14f;
    const float ScreenPadding = 10f;
    const float ChatPadLeft = 4f;
    const float ChatPadRight = 12f;
    const float AvatarSize = 34f;
    const float RowGap = 6f;
    const float BubbleWidth = PhoneWidth - ShellInset * 2f - ScreenPadding * 2f - ChatPadLeft - ChatPadRight - AvatarSize - RowGap;
    const float BubblePadX = 10f;
    const float BubblePadY = 8f;
    const int MessageFontSize = 14;

    public Text SpeechText => speechText;
    public bool IsOpen => visual != null && visual.activeSelf;

    public static PetPhone Ensure()
    {
        if (Instance != null)
            return Instance;

        Canvas canvas = PetDialogue.OverlayCanvas();
        if (canvas == null)
            return null;

        var root = new GameObject("PhoneHost", typeof(RectTransform));
        root.transform.SetParent(canvas.transform, false);
        return root.AddComponent<PetPhone>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        Build();
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    public static void ShowButton()
    {
        Ensure();
        if (Instance == null)
            return;

        Instance.BindLauncher();
        if (Instance.launcher != null)
            Instance.launcher.gameObject.SetActive(true);
    }

    public void Toggle()
    {
        SetOpen(!IsOpen);
    }

    public void SetOpen(bool open)
    {
        if (visual != null)
            visual.SetActive(open);
        if (open)
            StartCoroutine(ScrollToBottom());
        if (PetDialogue.Instance != null)
            PetDialogue.Instance.RefreshPlacement();
    }

    public void SetAway(bool away)
    {
        if (statusText == null)
            return;

        statusText.text = away ? "Away" : "Home";
        statusText.color = away
            ? new Color(0.62f, 0.36f, 0.16f, 1f)
            : new Color(0.22f, 0.42f, 0.28f, 1f);
    }

    public void BeginNameEntry(Action<string> onSubmit)
    {
        nameSubmitted = onSubmit;
        AddMessage("Please enter your username");
        if (speechText != null)
            speechText.gameObject.SetActive(false);
        if (nameRow == null)
            BuildNameRow();
        nameRow.SetActive(true);
        StartCoroutine(FocusName());
    }

    public void EndNameEntry()
    {
        nameSubmitted = null;
        if (nameRow != null)
            nameRow.SetActive(false);
        if (speechText != null)
            speechText.gameObject.SetActive(true);
    }

    public void ShowSpeech(string text)
    {
        if (speechText == null)
            return;

        speechText.text = text;
    }

    public void HideSpeech()
    {
        if (speechText != null)
            speechText.text = string.Empty;
    }

    public bool HitsSpeech(Vector2 screenPoint)
    {
        if (speechRoot == null || nameRow != null && nameRow.activeSelf)
            return false;

        Canvas canvas = speechRoot.GetComponentInParent<Canvas>();
        Camera eventCamera = null;
        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            eventCamera = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
        return RectTransformUtility.RectangleContainsScreenPoint(speechRoot, screenPoint, eventCamera);
    }

    public void AddMessage(string text)
    {
        if (chatContent == null || string.IsNullOrEmpty(text))
            return;

        var label = CreateText("Label", transform, text, MessageFontSize, TextAnchor.UpperLeft, new Color(0.16f, 0.16f, 0.18f, 1f));
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        float textWidth = BubbleWidth - BubblePadX * 2f;
        float bubbleHeight = PreferredTextHeight(label, textWidth) + BubblePadY * 2f;

        RectTransform row = BeginChatRow("MessageRow", BubbleWidth, bubbleHeight);
        var bubble = new GameObject("Message", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        bubble.transform.SetParent(row, false);
        label.transform.SetParent(bubble.transform, false);
        var image = bubble.GetComponent<Image>();
        image.sprite = Skin();
        image.type = Image.Type.Simple;
        image.color = new Color(0.86f, 0.88f, 0.9f, 1f);
        image.raycastTarget = false;
        var bubbleRect = bubble.GetComponent<RectTransform>();
        bubbleRect.sizeDelta = new Vector2(BubbleWidth, bubbleHeight);
        var layout = bubble.GetComponent<LayoutElement>();
        layout.preferredWidth = BubbleWidth;
        layout.preferredHeight = bubbleHeight;
        layout.minWidth = BubbleWidth;
        layout.minHeight = bubbleHeight;

        var labelRect = label.rectTransform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(BubblePadX, BubblePadY);
        labelRect.offsetMax = new Vector2(-BubblePadX, -BubblePadY);
        StartCoroutine(ScrollToBottom());
    }

    public void AddPostcard(Sprite picture, string caption)
    {
        if (chatContent == null)
            return;

        const float cardWidth = 180f;
        RectTransform row = BeginChatRow("PostcardRow", cardWidth, 34f);
        var card = new GameObject("Postcard", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(LayoutElement));
        card.transform.SetParent(row, false);
        card.GetComponent<LayoutElement>().preferredWidth = cardWidth;
        var cardImage = card.GetComponent<Image>();
        cardImage.sprite = Skin();
        cardImage.type = Image.Type.Simple;
        cardImage.color = Color.white;
        cardImage.raycastTarget = false;
        var layout = card.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(8, 8, 8, 8);
        layout.spacing = 6f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        var fitter = card.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var cardRect = card.GetComponent<RectTransform>();
        cardRect.sizeDelta = new Vector2(cardWidth, 0f);

        if (picture != null)
        {
            var pictureObject = new GameObject("Picture", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
            pictureObject.transform.SetParent(card.transform, false);
            var pictureImage = pictureObject.GetComponent<Image>();
            pictureImage.sprite = picture;
            pictureImage.preserveAspect = true;
            pictureImage.raycastTarget = true;
            var pictureLayout = pictureObject.GetComponent<LayoutElement>();
            pictureLayout.preferredHeight = 96f;
            pictureLayout.preferredWidth = 160f;
            var pictureButton = pictureObject.AddComponent<Button>();
            pictureButton.targetGraphic = pictureImage;
            pictureButton.onClick.AddListener(() => OpenPicture(picture));
        }

        var captionObject = new GameObject("Caption", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(LayoutElement));
        captionObject.transform.SetParent(card.transform, false);
        var captionText = captionObject.GetComponent<Text>();
        captionText.font = BuiltinFont();
        captionText.fontSize = 14;
        captionText.alignment = TextAnchor.MiddleLeft;
        captionText.color = new Color(0.2f, 0.2f, 0.22f, 1f);
        captionText.raycastTarget = false;
        captionText.text = string.IsNullOrEmpty(caption) ? "Postcard" : caption;
        captionObject.GetComponent<LayoutElement>().preferredHeight = 22f;

        StartCoroutine(ScrollToBottom());
    }

    static float PreferredTextHeight(Text label, float width)
    {
        TextGenerationSettings settings = label.GetGenerationSettings(new Vector2(width, 10000f));
        settings.horizontalOverflow = HorizontalWrapMode.Wrap;
        settings.verticalOverflow = VerticalWrapMode.Overflow;
        float unit = label.pixelsPerUnit > 0f ? label.pixelsPerUnit : 1f;
        return label.cachedTextGeneratorForLayout.GetPreferredHeight(label.text, settings) / unit;
    }

    RectTransform BeginChatRow(string name, float bodyWidth, float bodyHeight)
    {
        var rowObject = new GameObject(name, typeof(RectTransform), typeof(HorizontalLayoutGroup));
        rowObject.transform.SetParent(chatContent, false);
        var row = rowObject.GetComponent<RectTransform>();
        row.sizeDelta = new Vector2(AvatarSize + RowGap + bodyWidth, Mathf.Max(AvatarSize, bodyHeight));
        var layout = rowObject.GetComponent<HorizontalLayoutGroup>();
        layout.spacing = RowGap;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = false;
        layout.childForceExpandHeight = false;
        CreateAvatar(row, AvatarSize);
        return row;
    }

    void CreateAvatar(Transform parent, float size)
    {
        var frameObject = new GameObject("Avatar", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        frameObject.transform.SetParent(parent, false);
        var frame = frameObject.GetComponent<Image>();
        frame.sprite = Skin();
        frame.type = Image.Type.Simple;
        frame.color = new Color(0.86f, 0.84f, 0.8f, 1f);
        frame.raycastTarget = false;
        var frameRect = frameObject.GetComponent<RectTransform>();
        frameRect.sizeDelta = new Vector2(size, size);
        var layout = frameObject.GetComponent<LayoutElement>();
        layout.preferredWidth = size;
        layout.preferredHeight = size;
        layout.minWidth = size;
        layout.minHeight = size;

        var faceObject = new GameObject("Face", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        faceObject.transform.SetParent(frameObject.transform, false);
        var face = faceObject.GetComponent<Image>();
        face.preserveAspect = true;
        face.raycastTarget = false;
        face.sprite = PetDialogue.PortraitSprite;
        if (face.sprite == null)
        {
            var speaker = FindObjectOfType<SpriteRenderer>();
            if (speaker != null)
                face.sprite = speaker.sprite;
        }
        Stretch(face.rectTransform, 2f);
    }

    IEnumerator ScrollToBottom()
    {
        yield return null;
        if (chatContent != null)
            LayoutRebuilder.ForceRebuildLayoutImmediate(chatContent);
        if (chatScroll != null)
            chatScroll.verticalNormalizedPosition = 0f;
    }

    void BuildNameRow()
    {
        nameRow = new GameObject("NameEntry", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        nameRow.transform.SetParent(speechRoot, false);
        Stretch(nameRow.GetComponent<RectTransform>(), 6f);
        var row = nameRow.GetComponent<HorizontalLayoutGroup>();
        row.spacing = 6f;
        row.childAlignment = TextAnchor.MiddleCenter;
        row.childControlWidth = true;
        row.childControlHeight = true;
        row.childForceExpandWidth = false;
        row.childForceExpandHeight = true;

        var inputObject = new GameObject("Input", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(InputField), typeof(LayoutElement));
        inputObject.transform.SetParent(nameRow.transform, false);
        var inputImage = inputObject.GetComponent<Image>();
        inputImage.sprite = Skin();
        inputImage.type = Image.Type.Simple;
        inputImage.color = new Color(0.97f, 0.97f, 0.98f, 1f);
        var inputLayout = inputObject.GetComponent<LayoutElement>();
        inputLayout.flexibleWidth = 1f;
        inputLayout.preferredHeight = 36f;

        Font font = BuiltinFont();
        var text = CreateText("Text", inputObject.transform, string.Empty, 14, TextAnchor.MiddleLeft, new Color(0.16f, 0.14f, 0.12f, 1f));
        Stretch(text.rectTransform, 6f);
        text.supportRichText = false;
        var placeholder = CreateText("Placeholder", inputObject.transform, "Username", 14, TextAnchor.MiddleLeft, new Color(0.55f, 0.52f, 0.48f, 1f));
        Stretch(placeholder.rectTransform, 6f);
        placeholder.fontStyle = FontStyle.Italic;

        nameInput = inputObject.GetComponent<InputField>();
        nameInput.textComponent = text;
        nameInput.placeholder = placeholder;
        nameInput.lineType = InputField.LineType.SingleLine;
        nameInput.characterLimit = 24;
        nameInput.onSubmit.AddListener(SubmitName);

        var okObject = new GameObject("OK", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(LayoutElement));
        okObject.transform.SetParent(nameRow.transform, false);
        var okImage = okObject.GetComponent<Image>();
        okImage.sprite = Skin();
        okImage.type = Image.Type.Simple;
        okImage.color = new Color(0.93f, 0.55f, 0.42f, 1f);
        okObject.GetComponent<LayoutElement>().preferredWidth = 52f;
        var okButton = okObject.GetComponent<Button>();
        okButton.targetGraphic = okImage;
        okButton.onClick.AddListener(() => SubmitName(nameInput != null ? nameInput.text : string.Empty));
        var okLabel = CreateText("Label", okObject.transform, "OK", 14, TextAnchor.MiddleCenter, new Color(0.24f, 0.14f, 0.1f, 1f));
        Stretch(okLabel.rectTransform, 0f);
        okLabel.raycastTarget = false;
    }

    IEnumerator FocusName()
    {
        yield return null;
        if (nameInput == null)
            yield break;
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(nameInput.gameObject);
        nameInput.ActivateInputField();
    }

    void SubmitName(string raw)
    {
        if (nameSubmitted != null)
            nameSubmitted.Invoke(raw);
    }

    void BindLauncher()
    {
        if (launcher != null)
            return;

        var buttons = FindObjectsOfType<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i].gameObject.name != "Phone")
                continue;
            if (buttons[i].transform.IsChildOf(transform))
                continue;
            launcher = buttons[i];
            break;
        }

        if (launcher == null)
            return;

        launcher.onClick.AddListener(Toggle);
        launcher.gameObject.SetActive(false);
    }

    void Build()
    {
        var panelObject = new GameObject("PhonePanel", typeof(RectTransform));
        panelObject.transform.SetParent(transform, false);
        visual = panelObject;
        var root = panelObject.GetComponent<RectTransform>();
        root.anchorMin = new Vector2(0f, 0.5f);
        root.anchorMax = new Vector2(0f, 0.5f);
        root.pivot = new Vector2(0f, 0.5f);
        root.anchoredPosition = new Vector2(28f, 0f);
        root.sizeDelta = new Vector2(PhoneWidth, PhoneHeight);

        var shell = CreatePanel("Shell", root, Skin(), new Color(0.16f, 0.16f, 0.18f, 1f));
        Stretch(shell, 0f);
        var screen = CreatePanel("Screen", shell, Skin(), new Color(0.94f, 0.94f, 0.95f, 1f));
        Stretch(screen, ShellInset);
        screen.gameObject.AddComponent<WindowDrag>().Target = root;

        var screenLayout = screen.gameObject.AddComponent<VerticalLayoutGroup>();
        screenLayout.padding = new RectOffset((int)ScreenPadding, (int)ScreenPadding, (int)ScreenPadding, (int)ScreenPadding);
        screenLayout.spacing = 8f;
        screenLayout.childAlignment = TextAnchor.UpperCenter;
        screenLayout.childControlWidth = true;
        screenLayout.childControlHeight = true;
        screenLayout.childForceExpandWidth = true;
        screenLayout.childForceExpandHeight = false;

        var titleBar = CreatePanel("TitleBar", screen, Skin(), new Color(0.9f, 0.9f, 0.92f, 1f));
        titleBar.gameObject.AddComponent<LayoutElement>().preferredHeight = 58f;
        var titleLayout = titleBar.gameObject.AddComponent<VerticalLayoutGroup>();
        titleLayout.padding = new RectOffset(8, 36, 4, 4);
        titleLayout.childAlignment = TextAnchor.MiddleCenter;
        titleLayout.childControlWidth = true;
        titleLayout.childControlHeight = true;
        titleLayout.childForceExpandWidth = true;
        titleLayout.childForceExpandHeight = false;
        titleBar.gameObject.AddComponent<WindowDrag>().Target = root;

        statusText = CreateText("Status", titleBar, "Home", 13, TextAnchor.MiddleCenter, new Color(0.25f, 0.25f, 0.28f, 1f));
        statusText.gameObject.AddComponent<LayoutElement>().preferredHeight = 20f;
        var header = CreateText("Name", titleBar, "Blinky", 20, TextAnchor.MiddleCenter, new Color(0.12f, 0.12f, 0.14f, 1f));
        header.gameObject.AddComponent<LayoutElement>().preferredHeight = 26f;

        var viewport = CreatePanel("Chat", screen, null, new Color(1f, 1f, 1f, 0.01f));
        var viewportLayout = viewport.gameObject.AddComponent<LayoutElement>();
        viewportLayout.flexibleHeight = 1f;
        viewportLayout.preferredHeight = 360f;
        viewport.gameObject.AddComponent<RectMask2D>();
        chatScroll = viewport.gameObject.AddComponent<ScrollRect>();
        chatScroll.horizontal = false;
        chatScroll.movementType = ScrollRect.MovementType.Clamped;
        chatScroll.scrollSensitivity = 24f;

        var contentObject = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        contentObject.transform.SetParent(viewport, false);
        chatContent = contentObject.GetComponent<RectTransform>();
        chatContent.anchorMin = new Vector2(0f, 1f);
        chatContent.anchorMax = new Vector2(1f, 1f);
        chatContent.pivot = new Vector2(0.5f, 1f);
        chatContent.offsetMin = Vector2.zero;
        chatContent.offsetMax = Vector2.zero;
        var contentLayout = contentObject.GetComponent<VerticalLayoutGroup>();
        contentLayout.padding = new RectOffset((int)ChatPadLeft, (int)ChatPadRight, 4, 4);
        contentLayout.spacing = 8f;
        contentLayout.childAlignment = TextAnchor.UpperLeft;
        contentLayout.childControlWidth = false;
        contentLayout.childControlHeight = true;
        contentLayout.childForceExpandWidth = false;
        contentLayout.childForceExpandHeight = false;
        var contentFitter = contentObject.GetComponent<ContentSizeFitter>();
        contentFitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        chatScroll.viewport = viewport;
        chatScroll.content = chatContent;

        var galleryBar = new GameObject("GalleryBar", typeof(RectTransform), typeof(LayoutElement));
        galleryBar.transform.SetParent(screen, false);
        galleryBar.GetComponent<LayoutElement>().preferredHeight = 44f;
        var galleryObject = new GameObject("GalleryButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        galleryObject.transform.SetParent(galleryBar.transform, false);
        var galleryRect = galleryObject.GetComponent<RectTransform>();
        galleryRect.anchorMin = new Vector2(1f, 0.5f);
        galleryRect.anchorMax = new Vector2(1f, 0.5f);
        galleryRect.pivot = new Vector2(1f, 0.5f);
        galleryRect.anchoredPosition = Vector2.zero;
        galleryRect.sizeDelta = new Vector2(44f, 44f);
        var galleryImage = galleryObject.GetComponent<Image>();
        galleryImage.sprite = Skin();
        galleryImage.type = Image.Type.Simple;
        galleryImage.color = new Color(0.22f, 0.22f, 0.24f, 1f);
        var galleryButton = galleryObject.GetComponent<Button>();
        galleryButton.targetGraphic = galleryImage;
        galleryButton.interactable = false;
        var galleryLabel = CreateText("Icon", galleryObject.transform, "▦", 18, TextAnchor.MiddleCenter, Color.white);
        Stretch(galleryLabel.rectTransform, 0f);
        galleryLabel.raycastTarget = false;

        CreateCloseButton(shell, () => SetOpen(false));
        shell.gameObject.AddComponent<WindowDrag>().Target = root;
        SetAway(false);
        visual.SetActive(false);
        BindLauncher();
    }

    static RectTransform CreatePanel(string name, Transform parent, Sprite sprite, Color color)
    {
        var panel = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        panel.transform.SetParent(parent, false);
        var image = panel.GetComponent<Image>();
        image.sprite = sprite;
        image.type = Image.Type.Simple;
        image.color = color;
        image.raycastTarget = true;
        return panel.GetComponent<RectTransform>();
    }

    static Text CreateText(string name, Transform parent, string value, int size, TextAnchor anchor, Color color)
    {
        var textObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        textObject.transform.SetParent(parent, false);
        var text = textObject.GetComponent<Text>();
        text.font = BuiltinFont();
        text.fontSize = size;
        text.alignment = anchor;
        text.color = color;
        text.text = value;
        text.raycastTarget = false;
        return text;
    }

    static void Stretch(RectTransform rect, float inset)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(inset, inset);
        rect.offsetMax = new Vector2(-inset, -inset);
    }

    static Sprite skinSprite;

    static Sprite Skin()
    {
        if (skinSprite != null)
            return skinSprite;

        var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        var pixels = new Color[16];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = Color.white;
        texture.SetPixels(pixels);
        texture.Apply();
        texture.hideFlags = HideFlags.HideAndDontSave;
        skinSprite = Sprite.Create(texture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 100f);
        skinSprite.hideFlags = HideFlags.HideAndDontSave;
        return skinSprite;
    }

    static Font BuiltinFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }

    void OpenPicture(Sprite picture)
    {
        if (picture == null)
            return;

        Canvas canvas = GetComponentInParent<Canvas>();
        if (canvas == null)
            return;

        var windowObject = new GameObject("PictureView", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        windowObject.transform.SetParent(canvas.transform, false);
        var window = windowObject.GetComponent<RectTransform>();
        window.anchorMin = new Vector2(0.5f, 0.5f);
        window.anchorMax = new Vector2(0.5f, 0.5f);
        window.pivot = new Vector2(0.5f, 0.5f);
        window.sizeDelta = new Vector2(420f, 460f);
        float shift = 26f * (pictureWindows % 8);
        pictureWindows++;
        window.anchoredPosition = new Vector2(shift, -shift);
        var background = windowObject.GetComponent<Image>();
        background.sprite = Skin();
        background.type = Image.Type.Simple;
        background.color = new Color(0.16f, 0.16f, 0.18f, 1f);
        background.raycastTarget = true;
        windowObject.AddComponent<WindowDrag>().Target = window;

        var imageObject = new GameObject("Photo", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        imageObject.transform.SetParent(window, false);
        var imageRect = imageObject.GetComponent<RectTransform>();
        imageRect.anchorMin = Vector2.zero;
        imageRect.anchorMax = Vector2.one;
        imageRect.offsetMin = new Vector2(16f, 16f);
        imageRect.offsetMax = new Vector2(-16f, -48f);
        var image = imageObject.GetComponent<Image>();
        image.sprite = picture;
        image.preserveAspect = true;
        image.raycastTarget = true;
        imageObject.AddComponent<WindowDrag>().Target = window;

        CreateCloseButton(window, () => Destroy(windowObject));
        window.SetAsLastSibling();
    }

    void CreateCloseButton(Transform parent, Action onClick)
    {
        var buttonObject = new GameObject("Close", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        var rect = buttonObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = new Vector2(-8f, -8f);
        rect.sizeDelta = new Vector2(28f, 28f);
        var image = buttonObject.GetComponent<Image>();
        image.sprite = Skin();
        image.type = Image.Type.Simple;
        image.color = new Color(0.86f, 0.22f, 0.18f, 1f);
        var button = buttonObject.GetComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(() => onClick());
        var label = CreateText("X", buttonObject.transform, "X", 16, TextAnchor.MiddleCenter, Color.white);
        Stretch(label.rectTransform, 0f);
        label.raycastTarget = false;
        label.fontStyle = FontStyle.Bold;
    }
}

public class WindowDrag : MonoBehaviour, IBeginDragHandler, IDragHandler
{
    public RectTransform Target;

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (Target != null)
            Target.SetAsLastSibling();
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (Target == null)
            return;

        Canvas canvas = Target.GetComponentInParent<Canvas>();
        float scale = canvas != null ? canvas.scaleFactor : 1f;
        if (scale < 0.01f)
            scale = 1f;
        Target.anchoredPosition += eventData.delta / scale;
    }
}
