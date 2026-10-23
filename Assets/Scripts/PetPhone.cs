using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 手机：可拖动的聊天窗口。点图片只打开这张图，右上角的 X 用来关闭。
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
    CanvasGroup phoneGroup;
    GameObject phoneIcon;
    RectTransform phoneIconRect;
    GameObject unreadBadge;
    Text unreadBadgeText;
    Vector2 phoneIconBasePosition;
    int unreadCount;
    Coroutine phoneShakeRoutine;
    Text emptyChatText;
    InputField chatInput;
    Button launcher;
    int pictureWindows;
    GameObject nameRow;
    InputField nameInput;
    Action<string> nameSubmitted;
    bool phoneOpen;
    Coroutine phoneAnimRoutine;

    const float PhoneWidth = 380f;
    const float PhoneHeight = 560f;
    const float PhoneClosedScale = 0.88f;
    const float PhoneOpenSeconds = 0.24f;
    const float PhoneCloseSeconds = 0.18f;
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
    public bool IsOpen => phoneOpen && visual != null;

    public static PetPhone Ensure()
    {
        if (Instance != null)
            return Instance;

        Canvas canvas = PetDialogue.OverlayCanvas();
        if (canvas == null)
            return null;

        var root = new GameObject("PhoneHost", typeof(RectTransform));
        root.transform.SetParent(canvas.transform, false);
        var phone = root.AddComponent<PetPhone>();
        phone.ActivateHost();
        return phone;
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

    public static void ShowPicture(Sprite picture)
    {
        Ensure();
        if (Instance != null)
            Instance.OpenPicture(picture);
    }

    public static void ShowButton()
    {
        Ensure();
        if (Instance == null)
            return;

        Instance.ActivateHost();
    }

    public void Toggle()
    {
        SetOpen(!IsOpen);
    }

    // 协程挂在 PhoneHost 上。这里只保证宿主在游戏 Overlay 画布下并且是激活的，不打开聊天面板。
    void ActivateHost()
    {
        if (!IsOnGameOverlay())
        {
            Canvas canvas = PetDialogue.OverlayCanvas();
            if (canvas != null && canvas.transform != transform)
                transform.SetParent(canvas.transform, false);
        }

        if (!gameObject.activeInHierarchy)
        {
            Transform node = transform.parent;
            while (node != null && !gameObject.activeInHierarchy)
            {
                if (IsDebugCanvasObject(node.gameObject))
                    break;
                if (node.GetComponent<Canvas>() != null && !node.gameObject.activeSelf)
                    node.gameObject.SetActive(true);
                node = node.parent;
            }
        }

        if (!gameObject.activeSelf)
            gameObject.SetActive(true);

        if (!enabled)
            enabled = true;
    }

    bool IsOnGameOverlay()
    {
        Transform node = transform.parent;
        while (node != null)
        {
            var canvas = node.GetComponent<Canvas>();
            if (canvas != null)
            {
                if (!canvas.gameObject.activeInHierarchy)
                    return false;
                if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    return false;
                if (IsDebugCanvasObject(canvas.gameObject))
                    return false;
                return true;
            }

            node = node.parent;
        }

        return false;
    }

    static bool IsDebugCanvasObject(GameObject go)
    {
        if (go == null)
            return false;
        if (go.name == "DebugConsoleWindow" || go.name == "[DebugConsole]")
            return true;
        var canvas = go.GetComponent<Canvas>();
        return canvas != null && canvas.sortingOrder >= 32000;
    }

    public void Reveal()
    {
        if (phoneOpen && visual != null && visual.activeSelf)
            return;

        phoneOpen = false;
        SetOpen(true);
    }

    public void SetOpen(bool open)
    {
        ActivateHost();
        if (visual == null || open == phoneOpen)
            return;

        phoneOpen = open;

        if (phoneOpen)
        {
            ClearUnread();
            StopIconShake();
        }

        if (phoneAnimRoutine != null)
        {
            StopCoroutine(phoneAnimRoutine);
            phoneAnimRoutine = null;
        }

        if (phoneOpen)
        {
            visual.SetActive(true);
            if (phoneGroup != null)
            {
                phoneGroup.interactable = true;
                phoneGroup.blocksRaycasts = true;
            }
            phoneAnimRoutine = StartCoroutine(AnimatePhoneOpen());
            StartCoroutine(ScrollToBottom());
        }
        else
        {
            phoneAnimRoutine = StartCoroutine(AnimatePhoneClose());
        }

        if (PetDialogue.Instance != null)
            PetDialogue.Instance.RefreshPlacement();
    }

    IEnumerator AnimatePhoneOpen()
    {
        var panel = visual != null ? visual.GetComponent<RectTransform>() : null;
        if (panel == null)
            yield break;

        Vector3 fromScale = panel.localScale;
        if (fromScale.x < 0.01f)
            fromScale = Vector3.one * PhoneClosedScale;
        float fromAlpha = phoneGroup != null ? phoneGroup.alpha : 1f;
        float elapsed = 0f;

        while (elapsed < PhoneOpenSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / PhoneOpenSeconds);
            float eased = EaseOutBack(t);

            panel.localScale = Vector3.LerpUnclamped(fromScale, Vector3.one, eased);
            if (phoneGroup != null)
                phoneGroup.alpha = Mathf.Lerp(fromAlpha, 1f, Mathf.Clamp01(eased));

            yield return null;
        }

        panel.localScale = Vector3.one;
        if (phoneGroup != null)
            phoneGroup.alpha = 1f;
        phoneAnimRoutine = null;
    }

    IEnumerator AnimatePhoneClose()
    {
        var panel = visual != null ? visual.GetComponent<RectTransform>() : null;
        if (panel == null)
            yield break;

        Vector3 fromScale = panel.localScale;
        float fromAlpha = phoneGroup != null ? phoneGroup.alpha : 1f;
        float elapsed = 0f;

        while (elapsed < PhoneCloseSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / PhoneCloseSeconds);
            float eased = Mathf.SmoothStep(0f, 1f, t);

            panel.localScale = Vector3.LerpUnclamped(fromScale, Vector3.one * PhoneClosedScale, eased);
            if (phoneGroup != null)
                phoneGroup.alpha = Mathf.Lerp(fromAlpha, 0f, eased);

            yield return null;
        }

        panel.localScale = Vector3.one * PhoneClosedScale;
        if (phoneGroup != null)
            phoneGroup.alpha = 0f;
        visual.SetActive(false);
        phoneAnimRoutine = null;
    }

    static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float p = t - 1f;
        return 1f + c3 * p * p * p + c1 * p * p;
    }

    public void SetAway(bool away)
    {
        if (statusText == null)
            return;

        statusText.text = away ? "Blinky · Online" : "Blinky · Away";
        statusText.color = away
            ? new Color(0.22f, 0.42f, 0.28f, 1f)
            : new Color(0.62f, 0.36f, 0.16f, 1f);
    }

    public void BeginNameEntry(Action<string> onSubmit)
    {
        ActivateHost();
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
        ActivateHost();
        if (chatContent == null || string.IsNullOrEmpty(text))
            return;

        if (emptyChatText != null)
            emptyChatText.gameObject.SetActive(false);

        var label = CreateText("Label", transform, text, MessageFontSize, TextAnchor.UpperLeft, new Color(0.12f, 0.13f, 0.16f, 1f));
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        float textWidth = BubbleWidth - BubblePadX * 2f;
        float bubbleHeight = PreferredTextHeight(label, textWidth) + BubblePadY * 2f;

        RectTransform row = BeginChatRow("MessageRow", BubbleWidth, bubbleHeight);
        var bubble = new GameObject("Message", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement), typeof(Shadow));
        bubble.transform.SetParent(row, false);
        label.transform.SetParent(bubble.transform, false);
        var image = bubble.GetComponent<Image>();
        image.sprite = BubbleSprite();
        image.type = Image.Type.Sliced;
        image.color = new Color(0.90f, 0.93f, 0.97f, 1f);
        image.raycastTarget = false;
        var shadow = bubble.GetComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.12f);
        shadow.effectDistance = new Vector2(0f, -1.5f);
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
        NotifyNewMessage();
    }

    public void AddLinkMessage(string text, string linkLabel, string url)
    {
        ActivateHost();
        if (chatContent == null || string.IsNullOrEmpty(text))
            return;

        float textWidth = BubbleWidth - BubblePadX * 2f;
        var measure = CreateText("Measure", transform, text, MessageFontSize, TextAnchor.UpperLeft, Color.black);
        float textHeight = PreferredTextHeight(measure, textWidth);
        measure.text = string.IsNullOrEmpty(linkLabel) ? " " : linkLabel;
        float linkHeight = PreferredTextHeight(measure, textWidth);
        DestroyImmediate(measure.gameObject);

        float bubbleHeight = BubblePadY + textHeight + 4f + linkHeight + 2f + BubblePadY;
        RectTransform row = BeginChatRow("MessageRow", BubbleWidth, bubbleHeight);
        var bubble = new GameObject("Message", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(LayoutElement));
        bubble.transform.SetParent(row, false);
        var image = bubble.GetComponent<Image>();
        image.sprite = Skin();
        image.type = Image.Type.Simple;
        image.color = new Color(0.86f, 0.88f, 0.9f, 1f);
        image.raycastTarget = false;
        var stack = bubble.GetComponent<VerticalLayoutGroup>();
        stack.padding = new RectOffset((int)BubblePadX, (int)BubblePadX, (int)BubblePadY, (int)BubblePadY);
        stack.spacing = 4f;
        stack.childAlignment = TextAnchor.UpperLeft;
        stack.childControlWidth = true;
        stack.childControlHeight = true;
        stack.childForceExpandWidth = true;
        stack.childForceExpandHeight = false;
        var fitter = bubble.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var bubbleLayout = bubble.GetComponent<LayoutElement>();
        bubbleLayout.preferredWidth = BubbleWidth;
        bubbleLayout.minWidth = BubbleWidth;
        bubbleLayout.preferredHeight = bubbleHeight;

        var body = CreateText("Label", bubble.transform, text, MessageFontSize, TextAnchor.UpperLeft, new Color(0.16f, 0.16f, 0.18f, 1f));
        body.horizontalOverflow = HorizontalWrapMode.Wrap;
        body.verticalOverflow = VerticalWrapMode.Overflow;
        var bodyLayout = body.gameObject.AddComponent<LayoutElement>();
        bodyLayout.preferredWidth = textWidth;
        bodyLayout.preferredHeight = textHeight;

        var linkBlue = new Color(0f, 0f, 0.933f, 1f);
        var linkBlock = new GameObject("LinkBlock", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        linkBlock.transform.SetParent(bubble.transform, false);
        var linkStack = linkBlock.GetComponent<VerticalLayoutGroup>();
        linkStack.spacing = 1f;
        linkStack.childAlignment = TextAnchor.UpperLeft;
        linkStack.childControlWidth = true;
        linkStack.childControlHeight = true;
        linkStack.childForceExpandWidth = false;
        linkStack.childForceExpandHeight = false;
        var linkBlockLayout = linkBlock.GetComponent<LayoutElement>();
        linkBlockLayout.preferredWidth = textWidth;
        linkBlockLayout.preferredHeight = linkHeight + 2f;

        var link = CreateText("Link", linkBlock.transform, linkLabel, MessageFontSize, TextAnchor.UpperLeft, linkBlue);
        link.horizontalOverflow = HorizontalWrapMode.Wrap;
        link.verticalOverflow = VerticalWrapMode.Overflow;
        link.raycastTarget = true;
        float linkWidth = PreferredTextWidth(link, textWidth);
        var linkLayout = link.gameObject.AddComponent<LayoutElement>();
        linkLayout.preferredWidth = Mathf.Min(textWidth, linkWidth);
        linkLayout.preferredHeight = linkHeight;

        var lineObject = new GameObject("Underline", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        lineObject.transform.SetParent(linkBlock.transform, false);
        var line = lineObject.GetComponent<Image>();
        line.sprite = Skin();
        line.type = Image.Type.Simple;
        line.color = linkBlue;
        line.raycastTarget = true;
        var lineLayout = lineObject.GetComponent<LayoutElement>();
        lineLayout.preferredWidth = Mathf.Min(textWidth, linkWidth);
        lineLayout.preferredHeight = 1f;
        lineLayout.minHeight = 1f;

        var button = linkBlock.AddComponent<Button>();
        button.targetGraphic = link;
        button.transition = Selectable.Transition.None;
        string open = url;
        button.onClick.AddListener(() =>
        {
            if (!string.IsNullOrEmpty(open))
                Application.OpenURL(open);
        });

        StartCoroutine(ScrollToBottom());
        NotifyNewMessage();
    }

    public void AddAudioMessage(string text, string playLabel, Action onPlay)
    {
        ActivateHost();
        if (chatContent == null || string.IsNullOrEmpty(text))
            return;

        float textWidth = BubbleWidth - BubblePadX * 2f;
        var measure = CreateText("Measure", transform, text, MessageFontSize, TextAnchor.UpperLeft, Color.black);
        float textHeight = PreferredTextHeight(measure, textWidth);
        measure.text = string.IsNullOrEmpty(playLabel) ? "Play" : playLabel;
        float linkHeight = PreferredTextHeight(measure, textWidth);
        DestroyImmediate(measure.gameObject);

        float bubbleHeight = BubblePadY + textHeight + 4f + linkHeight + 2f + BubblePadY;
        RectTransform row = BeginChatRow("MessageRow", BubbleWidth, bubbleHeight);
        var bubble = new GameObject("Message", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(LayoutElement));
        bubble.transform.SetParent(row, false);
        var image = bubble.GetComponent<Image>();
        image.sprite = Skin();
        image.type = Image.Type.Simple;
        image.color = new Color(0.86f, 0.88f, 0.9f, 1f);
        image.raycastTarget = false;
        var stack = bubble.GetComponent<VerticalLayoutGroup>();
        stack.padding = new RectOffset((int)BubblePadX, (int)BubblePadX, (int)BubblePadY, (int)BubblePadY);
        stack.spacing = 4f;
        stack.childAlignment = TextAnchor.UpperLeft;
        stack.childControlWidth = true;
        stack.childControlHeight = true;
        stack.childForceExpandWidth = true;
        stack.childForceExpandHeight = false;
        var fitter = bubble.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var bubbleLayout = bubble.GetComponent<LayoutElement>();
        bubbleLayout.preferredWidth = BubbleWidth;
        bubbleLayout.minWidth = BubbleWidth;
        bubbleLayout.preferredHeight = bubbleHeight;

        var body = CreateText("Label", bubble.transform, text, MessageFontSize, TextAnchor.UpperLeft, new Color(0.16f, 0.16f, 0.18f, 1f));
        body.horizontalOverflow = HorizontalWrapMode.Wrap;
        body.verticalOverflow = VerticalWrapMode.Overflow;
        var bodyLayout = body.gameObject.AddComponent<LayoutElement>();
        bodyLayout.preferredWidth = textWidth;
        bodyLayout.preferredHeight = textHeight;

        var linkBlue = new Color(0f, 0f, 0.933f, 1f);
        var linkBlock = new GameObject("PlayBlock", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        linkBlock.transform.SetParent(bubble.transform, false);
        var linkStack = linkBlock.GetComponent<VerticalLayoutGroup>();
        linkStack.spacing = 1f;
        linkStack.childAlignment = TextAnchor.UpperLeft;
        linkStack.childControlWidth = true;
        linkStack.childControlHeight = true;
        linkStack.childForceExpandWidth = false;
        linkStack.childForceExpandHeight = false;
        var linkBlockLayout = linkBlock.GetComponent<LayoutElement>();
        linkBlockLayout.preferredWidth = textWidth;
        linkBlockLayout.preferredHeight = linkHeight + 2f;

        var link = CreateText("Play", linkBlock.transform, string.IsNullOrEmpty(playLabel) ? "Play" : playLabel, MessageFontSize, TextAnchor.UpperLeft, linkBlue);
        link.horizontalOverflow = HorizontalWrapMode.Wrap;
        link.verticalOverflow = VerticalWrapMode.Overflow;
        link.raycastTarget = true;
        float linkWidth = PreferredTextWidth(link, textWidth);
        var linkLayout = link.gameObject.AddComponent<LayoutElement>();
        linkLayout.preferredWidth = Mathf.Min(textWidth, linkWidth);
        linkLayout.preferredHeight = linkHeight;

        var lineObject = new GameObject("Underline", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        lineObject.transform.SetParent(linkBlock.transform, false);
        var line = lineObject.GetComponent<Image>();
        line.sprite = Skin();
        line.type = Image.Type.Simple;
        line.color = linkBlue;
        line.raycastTarget = true;
        var lineLayout = lineObject.GetComponent<LayoutElement>();
        lineLayout.preferredWidth = Mathf.Min(textWidth, linkWidth);
        lineLayout.preferredHeight = 1f;
        lineLayout.minHeight = 1f;

        var button = linkBlock.AddComponent<Button>();
        button.targetGraphic = link;
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(() => onPlay?.Invoke());

        StartCoroutine(ScrollToBottom());
        NotifyNewMessage();
    }

    public void RemoveLastPostcard()
    {
        if (chatContent == null)
            return;

        for (int i = chatContent.childCount - 1; i >= 0; i--)
        {
            Transform row = chatContent.GetChild(i);
            if (row.name != "PostcardRow")
                continue;

            Destroy(row.gameObject);
            return;
        }
    }

    public void AddPostcard(Sprite picture, string caption, GifClip animation = null)
    {
        ActivateHost();
        if (chatContent == null)
            return;

        if (emptyChatText != null)
            emptyChatText.gameObject.SetActive(false);

        const float cardWidth = 180f;
        RectTransform row = BeginChatRow("PostcardRow", cardWidth, 34f);
        var card = new GameObject("Postcard", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter), typeof(LayoutElement));
        card.transform.SetParent(row, false);
        card.GetComponent<LayoutElement>().preferredWidth = cardWidth;
        var cardImage = card.GetComponent<Image>();
        cardImage.sprite = BubbleSprite();
        cardImage.type = Image.Type.Sliced;
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
            if (animation != null && animation.Frames != null && animation.Frames.Length > 1)
                pictureObject.AddComponent<GifPlayer>().Play(animation);
            GifClip shown = animation;
            pictureButton.onClick.AddListener(() => OpenPicture(picture, shown));
        }

        if (caption != null)
        {
        var captionObject = new GameObject("Caption", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(LayoutElement));
        captionObject.transform.SetParent(card.transform, false);
        var captionText = captionObject.GetComponent<Text>();
        captionText.font = BuiltinFont();
        captionText.fontSize = 14;
        captionText.alignment = TextAnchor.UpperLeft;
        captionText.horizontalOverflow = HorizontalWrapMode.Wrap;
        captionText.verticalOverflow = VerticalWrapMode.Overflow;
        captionText.color = new Color(0.2f, 0.2f, 0.22f, 1f);
        captionText.raycastTarget = false;
        captionText.text = string.IsNullOrEmpty(caption) ? "Postcard" : caption;
        float captionWidth = cardWidth - layout.padding.left - layout.padding.right;
        float captionHeight = Mathf.Max(20f, PreferredTextHeight(captionText, captionWidth) + 4f);
        var captionLayout = captionObject.GetComponent<LayoutElement>();
        captionLayout.preferredWidth = captionWidth;
        captionLayout.preferredHeight = captionHeight;
        captionLayout.minHeight = captionHeight;
        }

        StartCoroutine(ScrollToBottom());
        NotifyNewMessage();
    }

    public void AddGifReply(GifClip clip)
    {
        if (clip == null || clip.First == null)
        {
            AddMessage("...");
            return;
        }

        AddPostcard(clip.First, null, clip);
    }

    void SubmitChat(string raw)
    {
        string message = string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim();

        if (chatInput != null)
            chatInput.text = string.Empty;

        if (string.IsNullOrEmpty(message))
        {
            if (chatInput != null)
                chatInput.ActivateInputField();
            return;
        }

        if (string.Equals(message, "/adminpanel start", StringComparison.OrdinalIgnoreCase))
        {
            AdminPanel.Open();
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);
            return;
        }

        AddOutgoingMessage(message);
        if (PetCampaign.Ensure().HasJoinedCult)
            AddGifReply(PetJourney.Instance != null ? PetJourney.Instance.PlaceholderGif() : null);

        if (chatInput != null)
            chatInput.ActivateInputField();
    }

    void AddOutgoingMessage(string text)
    {
        if (chatContent == null || string.IsNullOrEmpty(text))
            return;

        if (emptyChatText != null)
            emptyChatText.gameObject.SetActive(false);

        var label = CreateText("Label", transform, text, MessageFontSize, TextAnchor.UpperRight, new Color(0.20f, 0.10f, 0.06f, 1f));
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        float preferredTextWidth = PreferredTextWidth(label);
        float bubbleWidth = Mathf.Clamp(preferredTextWidth + BubblePadX * 2f, 64f, BubbleWidth);
        float textWidth = bubbleWidth - BubblePadX * 2f;
        float bubbleHeight = PreferredTextHeight(label, textWidth) + BubblePadY * 2f;

        float rowWidth = AvatarSize + RowGap + BubbleWidth;
        var rowObject = new GameObject("SentRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        rowObject.transform.SetParent(chatContent, false);
        var row = rowObject.GetComponent<RectTransform>();
        row.sizeDelta = new Vector2(rowWidth, Mathf.Max(AvatarSize, bubbleHeight));
        var rowLayout = rowObject.GetComponent<LayoutElement>();
        rowLayout.preferredWidth = rowWidth;
        rowLayout.preferredHeight = Mathf.Max(AvatarSize, bubbleHeight);
        var horizontal = rowObject.GetComponent<HorizontalLayoutGroup>();
        horizontal.spacing = RowGap;
        horizontal.childAlignment = TextAnchor.MiddleRight;
        horizontal.childControlWidth = false;
        horizontal.childControlHeight = false;
        horizontal.childForceExpandWidth = false;
        horizontal.childForceExpandHeight = false;

        var bubble = new GameObject("Message", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement), typeof(Shadow));
        bubble.transform.SetParent(row, false);
        label.transform.SetParent(bubble.transform, false);
        var image = bubble.GetComponent<Image>();
        image.sprite = BubbleSprite();
        image.type = Image.Type.Sliced;
        image.color = new Color(0.96f, 0.59f, 0.40f, 1f);
        image.raycastTarget = false;
        var shadow = bubble.GetComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.10f);
        shadow.effectDistance = new Vector2(0f, -1.5f);
        var bubbleRect = bubble.GetComponent<RectTransform>();
        bubbleRect.sizeDelta = new Vector2(bubbleWidth, bubbleHeight);
        var bubbleLayout = bubble.GetComponent<LayoutElement>();
        bubbleLayout.preferredWidth = bubbleWidth;
        bubbleLayout.preferredHeight = bubbleHeight;
        bubbleLayout.minWidth = bubbleWidth;
        bubbleLayout.minHeight = bubbleHeight;

        var labelRect = label.rectTransform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = new Vector2(BubblePadX, BubblePadY);
        labelRect.offsetMax = new Vector2(-BubblePadX, -BubblePadY);

        CreatePlayerAvatar(row, AvatarSize);
        StartCoroutine(ScrollToBottom());
    }

    static float PreferredTextWidth(Text label)
    {
        TextGenerationSettings settings = label.GetGenerationSettings(new Vector2(10000f, 10000f));
        settings.horizontalOverflow = HorizontalWrapMode.Overflow;
        settings.verticalOverflow = VerticalWrapMode.Overflow;
        float unit = label.pixelsPerUnit > 0f ? label.pixelsPerUnit : 1f;
        return label.cachedTextGeneratorForLayout.GetPreferredWidth(label.text, settings) / unit;
    }

    static float PreferredTextHeight(Text label, float width)
    {
        TextGenerationSettings settings = label.GetGenerationSettings(new Vector2(width, 10000f));
        settings.horizontalOverflow = HorizontalWrapMode.Wrap;
        settings.verticalOverflow = VerticalWrapMode.Overflow;
        float unit = label.pixelsPerUnit > 0f ? label.pixelsPerUnit : 1f;
        return label.cachedTextGeneratorForLayout.GetPreferredHeight(label.text, settings) / unit;
    }

    static float PreferredTextWidth(Text label, float width)
    {
        TextGenerationSettings settings = label.GetGenerationSettings(new Vector2(width, 10000f));
        settings.horizontalOverflow = HorizontalWrapMode.Wrap;
        settings.verticalOverflow = VerticalWrapMode.Overflow;
        float unit = label.pixelsPerUnit > 0f ? label.pixelsPerUnit : 1f;
        return label.cachedTextGeneratorForLayout.GetPreferredWidth(label.text, settings) / unit;
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
        frame.sprite = BubbleSprite();
        frame.type = Image.Type.Sliced;
        frame.color = new Color(0.90f, 0.89f, 0.86f, 1f);
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

    void CreatePlayerAvatar(Transform parent, float size)
    {
        var avatarObject = new GameObject("PlayerAvatar", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        avatarObject.transform.SetParent(parent, false);
        var image = avatarObject.GetComponent<Image>();
        image.sprite = PlayerAvatarSprite();
        image.preserveAspect = true;
        image.raycastTarget = false;
        var rect = avatarObject.GetComponent<RectTransform>();
        rect.sizeDelta = new Vector2(size, size);
        var layout = avatarObject.GetComponent<LayoutElement>();
        layout.preferredWidth = size;
        layout.preferredHeight = size;
        layout.minWidth = size;
        layout.minHeight = size;
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

        // The old text button is replaced by the phone icon at the bottom-right.
        if (launcher != null)
            launcher.gameObject.SetActive(false);
    }

    void CreatePhoneIcon()
    {
        if (phoneIcon != null)
            return;

        Canvas canvas = GetComponentInParent<Canvas>();
        if (canvas == null)
            return;

        phoneIcon = new GameObject(
            "PhoneIcon",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Shadow));
        phoneIcon.transform.SetParent(canvas.transform, false);
        phoneIconRect = phoneIcon.GetComponent<RectTransform>();
        phoneIconRect.anchorMin = new Vector2(1f, 0f);
        phoneIconRect.anchorMax = new Vector2(1f, 0f);
        phoneIconRect.pivot = new Vector2(1f, 0f);
        phoneIconRect.anchoredPosition = new Vector2(-24f, 24f);
        phoneIconRect.sizeDelta = new Vector2(62.1f, 93.15f);
        phoneIconBasePosition = phoneIconRect.anchoredPosition;

        var body = phoneIcon.GetComponent<Image>();
        body.sprite = PixelPhoneSprite();
        body.type = Image.Type.Simple;
        body.preserveAspect = true;
        body.color = Color.white;
        body.raycastTarget = true;

        var shadow = phoneIcon.GetComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.25f);
        shadow.effectDistance = new Vector2(0f, -3f);

        CreateUnreadBadge();

        var drag = phoneIcon.AddComponent<PhoneIconDrag>();
        drag.Phone = this;
        drag.Target = phoneIconRect;

        phoneIcon.transform.SetAsLastSibling();
    }

    void CreateUnreadBadge()
    {
        unreadBadge = new GameObject("UnreadBadge", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        unreadBadge.transform.SetParent(phoneIcon.transform, false);
        var rect = unreadBadge.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(1f, 1f);
        rect.sizeDelta = new Vector2(23f, 23f);

        var image = unreadBadge.GetComponent<Image>();
        image.sprite = BadgeSprite();
        image.type = Image.Type.Simple;
        image.color = Color.white;
        image.raycastTarget = false;

        var textObject = new GameObject("Count", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        textObject.transform.SetParent(unreadBadge.transform, false);
        var textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        unreadBadgeText = textObject.GetComponent<Text>();
        unreadBadgeText.font = BuiltinFont();
        unreadBadgeText.fontSize = 12;
        unreadBadgeText.fontStyle = FontStyle.Bold;
        unreadBadgeText.alignment = TextAnchor.MiddleCenter;
        unreadBadgeText.color = Color.white;
        unreadBadgeText.raycastTarget = false;
        unreadBadgeText.horizontalOverflow = HorizontalWrapMode.Overflow;
        unreadBadgeText.verticalOverflow = VerticalWrapMode.Overflow;
        unreadBadgeText.text = string.Empty;
        unreadBadge.SetActive(false);
    }

    void NotifyNewMessage()
    {
        if (phoneOpen || phoneIcon == null || unreadBadge == null)
            return;

        unreadCount++;
        UpdateUnreadBadge();
        if (phoneShakeRoutine != null)
            StopCoroutine(phoneShakeRoutine);
        phoneShakeRoutine = StartCoroutine(ShakePhoneIcon());
    }

    void UpdateUnreadBadge()
    {
        if (unreadBadge == null)
            return;

        bool show = unreadCount > 0;
        unreadBadge.SetActive(show);
        if (show && unreadBadgeText != null)
            unreadBadgeText.text = unreadCount > 99 ? "99+" : unreadCount.ToString();
    }

    void ClearUnread()
    {
        unreadCount = 0;
        UpdateUnreadBadge();
    }

    void StopIconShake()
    {
        CancelIconShake();
        if (phoneIconRect != null)
            phoneIconRect.anchoredPosition = phoneIconBasePosition;
    }

    public void CancelIconShake()
    {
        if (phoneShakeRoutine != null)
        {
            StopCoroutine(phoneShakeRoutine);
            phoneShakeRoutine = null;
        }
    }

    public void SetIconBasePosition(Vector2 position)
    {
        phoneIconBasePosition = position;
    }

    IEnumerator ShakePhoneIcon()
    {
        if (phoneIconRect == null)
            yield break;

        const float duration = 0.42f;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float damping = 1f - t;
            float offset = Mathf.Sin(t * Mathf.PI * 6f) * 5f * damping;
            phoneIconRect.anchoredPosition = phoneIconBasePosition + new Vector2(offset, 0f);
            yield return null;
        }

        phoneIconRect.anchoredPosition = phoneIconBasePosition;
        phoneShakeRoutine = null;
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
        root.localScale = Vector3.one * PhoneClosedScale;
        phoneGroup = panelObject.AddComponent<CanvasGroup>();
        phoneGroup.alpha = 0f;

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

        statusText = CreateText("Status", titleBar, "Blinky · Away", 12, TextAnchor.MiddleCenter, new Color(0.62f, 0.36f, 0.16f, 1f));
        statusText.gameObject.AddComponent<LayoutElement>().preferredHeight = 20f;
        var header = CreateText("Title", titleBar, "Chat", 21, TextAnchor.MiddleCenter, new Color(0.12f, 0.12f, 0.14f, 1f));
        header.fontStyle = FontStyle.Bold;
        header.gameObject.AddComponent<LayoutElement>().preferredHeight = 28f;

        var viewport = CreatePanel("Chat", screen, null, new Color(0.96f, 0.96f, 0.95f, 1f));
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
        contentLayout.padding = new RectOffset((int)ChatPadLeft, (int)ChatPadRight, 8, 8);
        contentLayout.spacing = 10f;
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

        emptyChatText = CreateText("EmptyChat", viewport, "No messages yet.\nSay hi to Blinky.", 14, TextAnchor.MiddleCenter, new Color(0.48f, 0.47f, 0.45f, 1f));
        var emptyRect = emptyChatText.rectTransform;
        emptyRect.anchorMin = new Vector2(0.5f, 0.5f);
        emptyRect.anchorMax = new Vector2(0.5f, 0.5f);
        emptyRect.pivot = new Vector2(0.5f, 0.5f);
        emptyRect.anchoredPosition = Vector2.zero;
        emptyRect.sizeDelta = new Vector2(220f, 52f);
        emptyChatText.fontStyle = FontStyle.Italic;
        emptyChatText.raycastTarget = false;

        var divider = new GameObject("Divider", typeof(RectTransform), typeof(LayoutElement));
        divider.transform.SetParent(screen, false);
        divider.GetComponent<LayoutElement>().preferredHeight = 48f;

        var line = CreatePanel("Line", divider.transform, Skin(), new Color(0.78f, 0.78f, 0.82f, 1f));
        line.anchorMin = new Vector2(0f, 0.5f);
        line.anchorMax = new Vector2(1f, 0.5f);
        line.offsetMin = new Vector2(48f, -0.5f);
        line.offsetMax = new Vector2(0f, 0.5f);
        line.GetComponent<Image>().raycastTarget = false;

        var pictureObject = new GameObject("CollectionsButton", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        pictureObject.transform.SetParent(divider.transform, false);
        var pictureRect = pictureObject.GetComponent<RectTransform>();
        pictureRect.anchorMin = new Vector2(0f, 0.5f);
        pictureRect.anchorMax = new Vector2(0f, 0.5f);
        pictureRect.pivot = new Vector2(0f, 0.5f);
        pictureRect.anchoredPosition = Vector2.zero;
        pictureRect.sizeDelta = new Vector2(40f, 40f);
        var pictureImage = pictureObject.GetComponent<Image>();
        pictureImage.sprite = Skin();
        pictureImage.type = Image.Type.Simple;
        pictureImage.color = new Color(0.22f, 0.22f, 0.24f, 1f);
        var pictureButton = pictureObject.GetComponent<Button>();
        pictureButton.targetGraphic = pictureImage;
        pictureButton.onClick.AddListener(() =>
        {
            if (PetJourney.Instance != null)
                PetJourney.Instance.OpenGallery();
        });
        var iconObject = new GameObject("Icon", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        iconObject.transform.SetParent(pictureObject.transform, false);
        var icon = iconObject.GetComponent<Image>();
        icon.sprite = PictureIcon();
        icon.preserveAspect = true;
        icon.raycastTarget = false;
        Stretch(icon.rectTransform, 7f);

        var compose = new GameObject("Compose", typeof(RectTransform), typeof(LayoutElement));
        compose.transform.SetParent(screen, false);
        compose.GetComponent<LayoutElement>().preferredHeight = 50f;
        var composeLayout = compose.AddComponent<HorizontalLayoutGroup>();
        composeLayout.padding = new RectOffset(0, 0, 2, 2);
        composeLayout.spacing = 6f;
        composeLayout.childAlignment = TextAnchor.MiddleLeft;
        composeLayout.childControlWidth = true;
        composeLayout.childControlHeight = true;
        composeLayout.childForceExpandWidth = false;
        composeLayout.childForceExpandHeight = false;

        var inputObject = new GameObject("ChatInput", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(InputField), typeof(LayoutElement));
        inputObject.transform.SetParent(compose.transform, false);
        var inputImage = inputObject.GetComponent<Image>();
        inputImage.sprite = BubbleSprite();
        inputImage.type = Image.Type.Sliced;
        inputImage.color = new Color(0.98f, 0.98f, 0.99f, 1f);
        inputImage.raycastTarget = true;
        inputObject.GetComponent<LayoutElement>().flexibleWidth = 1f;
        inputObject.GetComponent<LayoutElement>().preferredHeight = 36f;

        var inputText = CreateText("Text", inputObject.transform, string.Empty, 14, TextAnchor.MiddleLeft, new Color(0.12f, 0.13f, 0.16f, 1f));
        Stretch(inputText.rectTransform, 10f);
        inputText.supportRichText = false;
        var inputPlaceholder = CreateText("Placeholder", inputObject.transform, "Message Blinky...", 14, TextAnchor.MiddleLeft, new Color(0.48f, 0.47f, 0.45f, 1f));
        Stretch(inputPlaceholder.rectTransform, 10f);
        inputPlaceholder.fontStyle = FontStyle.Italic;

        chatInput = inputObject.GetComponent<InputField>();
        chatInput.targetGraphic = inputImage;
        chatInput.textComponent = inputText;
        chatInput.placeholder = inputPlaceholder;
        chatInput.lineType = InputField.LineType.SingleLine;
        chatInput.characterLimit = 200;
        chatInput.caretColor = new Color(0.24f, 0.16f, 0.12f, 1f);
        chatInput.selectionColor = new Color(0.93f, 0.55f, 0.42f, 0.35f);
        chatInput.onSubmit.AddListener(SubmitChat);

        var sendObject = new GameObject("Send", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button), typeof(LayoutElement));
        sendObject.transform.SetParent(compose.transform, false);
        var sendImage = sendObject.GetComponent<Image>();
        sendImage.sprite = BubbleSprite();
        sendImage.type = Image.Type.Sliced;
        sendImage.color = new Color(0.93f, 0.55f, 0.42f, 1f);
        sendImage.raycastTarget = true;
        var sendLayout = sendObject.GetComponent<LayoutElement>();
        sendLayout.preferredWidth = 60f;
        sendLayout.preferredHeight = 36f;
        var sendButton = sendObject.GetComponent<Button>();
        sendButton.targetGraphic = sendImage;
        sendButton.onClick.AddListener(() => SubmitChat(chatInput != null ? chatInput.text : string.Empty));
        var sendLabel = CreateText("Label", sendObject.transform, "Send", 14, TextAnchor.MiddleCenter, new Color(0.24f, 0.14f, 0.10f, 1f));
        Stretch(sendLabel.rectTransform, 0f);
        sendLabel.fontStyle = FontStyle.Bold;

        CreateCloseButton(shell, () => SetOpen(false));
        shell.gameObject.AddComponent<WindowDrag>().Target = root;
        SetAway(false);
        visual.SetActive(false);
        BindLauncher();
        CreatePhoneIcon();
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
    static Sprite bubbleSprite;
    static Sprite pixelPhoneSprite;
    static Sprite badgeSprite;
    static Sprite playerAvatarSprite;
    static Sprite pictureIcon;

    static Sprite PlayerAvatarSprite()
    {
        if (playerAvatarSprite != null)
            return playerAvatarSprite;

        const int size = 24;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[size * size];
        var clear = new Color(0f, 0f, 0f, 0f);
        var back = new Color(0.94f, 0.94f, 0.95f, 1f);
        var border = new Color(0.74f, 0.74f, 0.76f, 1f);
        var silhouette = new Color(0.56f, 0.56f, 0.59f, 1f);
        float center = (size - 1) * 0.5f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                if (distance > 11.4f)
                {
                    pixels[y * size + x] = clear;
                    continue;
                }

                Color color = distance > 10.2f ? border : back;

                float headDx = x - 12f;
                float headDy = y - 15f;
                if (headDx * headDx + headDy * headDy <= 21.2f)
                    color = silhouette;

                if (y >= 2 && y <= 10)
                {
                    float halfWidth = 5.6f + (10 - y) * 0.55f;
                    if (Mathf.Abs(x - center) <= halfWidth)
                        color = silhouette;
                }

                pixels[y * size + x] = color;
            }
        }

        texture.SetPixels(pixels);
        texture.Apply(false, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        playerAvatarSprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f);
        playerAvatarSprite.hideFlags = HideFlags.HideAndDontSave;
        return playerAvatarSprite;
    }

    static Sprite PixelPhoneSprite()
    {
        if (pixelPhoneSprite != null)
            return pixelPhoneSprite;

        const int width = 16;
        const int height = 24;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[width * height];
        var clear = new Color(0f, 0f, 0f, 0f);
        var casing = new Color(0.13f, 0.14f, 0.18f, 1f);
        var edge = new Color(0.55f, 0.58f, 0.64f, 1f);
        var screen = new Color(0.55f, 0.78f, 0.92f, 1f);
        var screenLight = new Color(0.78f, 0.91f, 0.98f, 1f);
        var buttonColor = new Color(0.68f, 0.72f, 0.78f, 1f);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Color color = clear;
                bool outside = x < 1 || x > width - 2 || y < 1 || y > height - 2;
                bool corner = (x == 1 || x == width - 2) && (y == 1 || y == height - 2);
                if (!outside && !corner)
                {
                    color = casing;
                    if (x == 1 || x == width - 2 || y == 1 || y == height - 2)
                        color = edge;

                    if (x >= 2 && x <= width - 3 && y >= 4 && y <= height - 5)
                        color = edge;
                    if (x >= 3 && x <= width - 4 && y >= 5 && y <= height - 6)
                        color = screen;
                    if (x >= 4 && x <= width - 5 && y >= height - 7 && y <= height - 6)
                        color = screenLight;
                }

                if (x >= 6 && x <= 9 && y >= height - 4 && y <= height - 3)
                    color = buttonColor;
                if (x >= 6 && x <= 9 && y >= 2 && y <= 3)
                    color = buttonColor;
                if (x == width - 1 && y >= 9 && y <= 13)
                    color = edge;

                pixels[y * width + x] = color;
            }
        }

        texture.SetPixels(pixels);
        texture.Apply(false, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        pixelPhoneSprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, width, height),
            new Vector2(0.5f, 0.5f),
            100f);
        pixelPhoneSprite.hideFlags = HideFlags.HideAndDontSave;
        return pixelPhoneSprite;
    }

    static Sprite BadgeSprite()
    {
        if (badgeSprite != null)
            return badgeSprite;

        const int size = 16;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[size * size];
        float center = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                if (distance > 7.5f)
                    pixels[y * size + x] = new Color(1f, 1f, 1f, 0f);
                else if (distance > 6.4f)
                    pixels[y * size + x] = Color.white;
                else
                    pixels[y * size + x] = new Color(0.86f, 0.20f, 0.23f, 1f);
            }
        }

        texture.SetPixels(pixels);
        texture.Apply(false, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        badgeSprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f);
        badgeSprite.hideFlags = HideFlags.HideAndDontSave;
        return badgeSprite;
    }

    static Sprite BubbleSprite()
    {
        if (bubbleSprite != null)
            return bubbleSprite;

        const int size = 32;
        const int radius = 10;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int cx = x < radius ? radius - x - 1 : (x >= size - radius ? x - (size - radius) : 0);
                int cy = y < radius ? radius - y - 1 : (y >= size - radius ? y - (size - radius) : 0);
                bool outside = cx > 0 && cy > 0 && cx * cx + cy * cy > radius * radius;
                pixels[y * size + x] = outside
                    ? new Color(1f, 1f, 1f, 0f)
                    : Color.white;
            }
        }

        texture.SetPixels(pixels);
        texture.Apply(false, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        bubbleSprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f,
            0,
            SpriteMeshType.FullRect,
            new Vector4(12f, 12f, 12f, 12f));
        bubbleSprite.hideFlags = HideFlags.HideAndDontSave;
        return bubbleSprite;
    }

    static Sprite PictureIcon()
    {
        if (pictureIcon != null)
            return pictureIcon;

        const int size = 32;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        var pixels = new Color[size * size];
        var clear = new Color(0f, 0f, 0f, 0f);
        var frame = new Color(0.95f, 0.95f, 0.97f, 1f);
        var sky = new Color(0.62f, 0.78f, 0.84f, 1f);
        var sun = new Color(0.96f, 0.84f, 0.46f, 1f);
        var hill = new Color(0.46f, 0.66f, 0.5f, 1f);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool border = x < 2 || y < 2 || x >= size - 2 || y >= size - 2;
                bool inner = x >= 5 && x < size - 5 && y >= 5 && y < size - 5;
                Color pixel = clear;
                if (border)
                    pixel = frame;
                else if (inner)
                {
                    int dx = x - 22;
                    int dy = y - 22;
                    bool sunDot = dx * dx + dy * dy <= 9;
                    int hillTop = 8 + (x < 16 ? (16 - x) / 2 : (x - 16) / 3);
                    pixel = sunDot ? sun : (y <= hillTop ? hill : sky);
                }

                pixels[y * size + x] = pixel;
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();
        texture.hideFlags = HideFlags.HideAndDontSave;
        pictureIcon = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        pictureIcon.hideFlags = HideFlags.HideAndDontSave;
        return pictureIcon;
    }

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

    void OpenPicture(Sprite picture, GifClip animation = null)
    {
        if (picture == null)
            return;

        Canvas canvas = GetComponentInParent<Canvas>();
        if (canvas == null)
            canvas = PetDialogue.OverlayCanvas();
        if (canvas == null)
            return;

        float width = Mathf.Max(1f, picture.rect.width);
        float height = Mathf.Max(1f, picture.rect.height);
        float scale = 480f / Mathf.Max(width, height);
        var windowObject = new GameObject("PictureView", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        windowObject.transform.SetParent(canvas.transform, false);
        var window = windowObject.GetComponent<RectTransform>();
        window.anchorMin = new Vector2(0.5f, 0.5f);
        window.anchorMax = new Vector2(0.5f, 0.5f);
        window.pivot = new Vector2(0.5f, 0.5f);
        window.sizeDelta = new Vector2(width * scale, height * scale);
        float shift = 26f * (pictureWindows % 8);
        pictureWindows++;
        window.anchoredPosition = new Vector2(shift, -shift);
        var image = windowObject.GetComponent<Image>();
        image.sprite = picture;
        image.preserveAspect = true;
        image.color = Color.white;
        image.raycastTarget = true;
        if (animation != null && animation.Frames != null && animation.Frames.Length > 1)
            windowObject.AddComponent<GifPlayer>().Play(animation);

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

public class PhoneIconDrag : MonoBehaviour, IPointerDownHandler, IPointerClickHandler, IBeginDragHandler, IDragHandler
{
    public PetPhone Phone;
    public RectTransform Target;
    bool dragged;

    public void OnPointerDown(PointerEventData eventData)
    {
        dragged = false;
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        dragged = true;
        if (Phone != null)
            Phone.CancelIconShake();
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
        ClampToCanvas(canvas);
        if (Phone != null)
            Phone.SetIconBasePosition(Target.anchoredPosition);
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (!dragged && Phone != null)
            Phone.Toggle();
        dragged = false;
    }

    void ClampToCanvas(Canvas canvas)
    {
        if (canvas == null || Target == null)
            return;

        var canvasRect = canvas.transform as RectTransform;
        if (canvasRect == null)
            return;

        Rect canvasBounds = canvasRect.rect;
        Rect iconBounds = Target.rect;
        Vector2 position = Target.anchoredPosition;
        position.x = Mathf.Clamp(position.x, iconBounds.width - canvasBounds.width, 0f);
        position.y = Mathf.Clamp(position.y, 0f, canvasBounds.height - iconBounds.height);
        Target.anchoredPosition = position;
    }
}
