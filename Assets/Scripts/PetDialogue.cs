using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 桌面上的一句对话：弹出气泡，显示文字，点击气泡后关闭。
/// </summary>
public class PetDialogue : MonoBehaviour
{
    public static PetDialogue Instance { get; private set; }

    [SerializeField] SpriteRenderer speaker;
    [SerializeField] bool playIntroOnStart = true;
    [SerializeField] string[] introLines = { "Hi, XX! I'm Blinky, your companion pets.", "Intro text 2" };
    [SerializeField] float clickSlopPixels = 18f;
    [SerializeField] int charactersPerLine = 10;
    [SerializeField] Vector2 bubbleSize = new Vector2(5.4f, 2.15f);
    [SerializeField] float gap = 0.22f;

    static readonly string[] FontCandidates =
    {
        "Microsoft YaHei",
        "微软雅黑",
        "SimHei",
        "黑体",
        "SimSun",
        "Arial"
    };

    Camera cam;
    GameObject bubbleObject;
    SpriteRenderer bubbleRenderer;
    TextMesh bodyText;
    MeshRenderer bodyRenderer;

    bool visible;
    bool pressedBubble;
    bool playingIntro;
    int introIndex;
    string[] sequence;
    int sequenceIndex;
    string clickUrl;
    Vector2 pressScreen;
    GameObject namePrompt;
    InputField nameInput;

    public static string PlayerName { get; private set; }
    public static bool HasPlayerName => !string.IsNullOrEmpty(PlayerName);
    public bool IsOpen => visible;

    public static void Say(string text)
    {
        if (Instance == null)
            return;

        Instance.clickUrl = null;
        Instance.Show(text);
    }

    public static void SayOnce(string text)
    {
        if (Instance == null)
            return;

        Instance.playingIntro = false;
        Instance.sequence = null;
        Instance.clickUrl = null;
        Instance.Show(text);
    }

    public static void SayLink(string text, string url)
    {
        if (Instance == null)
            return;

        Instance.playingIntro = false;
        Instance.sequence = null;
        Instance.clickUrl = url;
        Instance.Show(text);
    }

    public static bool HasPendingLink =>
        Instance != null && Instance.visible && !string.IsNullOrEmpty(Instance.clickUrl);

    public static void PlaySequence(string[] lines)
    {
        if (Instance != null)
            Instance.ShowNext(lines);
    }

    public static bool Blocks(Vector2 screenPoint)
    {
        return Instance != null && Instance.visible && Instance.HitsBubble(screenPoint);
    }

    void Awake()
    {
        Instance = this;
        if (speaker == null)
            speaker = GetComponent<SpriteRenderer>();
        cam = Camera.main;
        if (speaker != null)
            speaker.enabled = false;
        HoldIdle();
        CreateBubble();
        Hide();
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void Start()
    {
        if (speaker != null)
            speaker.enabled = false;
        HoldIdle();
        ShowNamePrompt();
    }

    void Update()
    {
        if (!visible || cam == null)
            return;

        if (Input.GetMouseButtonDown(0))
        {
            pressScreen = PointerScreen();
            pressedBubble = HitsBubble(pressScreen);
        }

        if (!Input.GetMouseButtonUp(0))
            return;

        Vector2 releaseScreen = PointerScreen();
        bool wasBubble = pressedBubble || HitsBubble(releaseScreen);
        pressedBubble = false;
        if (!wasBubble)
            return;

        float slop = Mathf.Max(1f, clickSlopPixels);
        if ((releaseScreen - pressScreen).sqrMagnitude > slop * slop)
            return;

        AdvanceIntroOrHide();
    }

    void BeginIntro()
    {
        if (introLines == null || introLines.Length == 0)
            return;

        playingIntro = true;
        introIndex = 0;
        clickUrl = null;
        Show(IntroText(0));
    }

    void ShowNext(string[] lines)
    {
        if (lines == null || lines.Length == 0)
            return;

        if (sequence == lines)
            sequenceIndex = (sequenceIndex + 1) % lines.Length;
        else
        {
            sequence = lines;
            sequenceIndex = 0;
        }

        playingIntro = false;
        clickUrl = null;
        Show(lines[sequenceIndex]);
    }

    void AdvanceIntroOrHide()
    {
        if (!string.IsNullOrEmpty(clickUrl))
        {
            string url = clickUrl;
            clickUrl = null;
            Hide();
            Application.OpenURL(url);
            return;
        }

        if (sequence != null)
        {
            Hide();
            return;
        }

        if (playingIntro && introLines != null && introIndex + 1 < introLines.Length)
        {
            introIndex++;
            Show(IntroText(introIndex));
            return;
        }

        if (playingIntro)
            playingIntro = false;

        Hide();
    }

    string IntroText(int index)
    {
        if (introLines == null || index < 0 || index >= introLines.Length)
            return string.Empty;

        string line = introLines[index];
        if (index != 0)
            return line;

        string name = PlayerName;
        if (string.IsNullOrWhiteSpace(name))
            name = "there";
        if (!string.IsNullOrEmpty(line) && line.Contains("XX"))
            return line.Replace("XX", name);
        return "Hi, " + name + "! I'm Blinky, your companion pets.";
    }

    void ShowNamePrompt()
    {
        var canvas = FindObjectOfType<Canvas>();
        if (canvas == null)
        {
            FinishNamePrompt("there");
            return;
        }

        namePrompt = new GameObject("NamePrompt", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        namePrompt.transform.SetParent(canvas.transform, false);
        var panelRect = namePrompt.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0.5f, 0.5f);
        panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        panelRect.pivot = new Vector2(0.5f, 0.5f);
        panelRect.sizeDelta = new Vector2(260f, 148f);
        var panelImage = namePrompt.GetComponent<Image>();
        panelImage.color = new Color(0.99f, 0.96f, 0.9f, 1f);
        panelImage.raycastTarget = true;

        Font font = BuiltinFont();
        CreatePromptLabel(namePrompt.transform, font);
        nameInput = CreatePromptInput(namePrompt.transform, font);
        CreatePromptButton(namePrompt.transform, font);
        StartCoroutine(FocusNameInput());
    }

    void CreatePromptLabel(Transform parent, Font font)
    {
        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        labelObject.transform.SetParent(parent, false);
        var rect = labelObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, -12f);
        rect.sizeDelta = new Vector2(-24f, 36f);
        var label = labelObject.GetComponent<Text>();
        label.font = font;
        label.fontSize = 15;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = new Color(0.24f, 0.16f, 0.12f, 1f);
        label.text = "Please enter your username";
        label.raycastTarget = false;
    }

    InputField CreatePromptInput(Transform parent, Font font)
    {
        var inputObject = new GameObject("Input", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(InputField));
        inputObject.transform.SetParent(parent, false);
        var rect = inputObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, 4f);
        rect.sizeDelta = new Vector2(220f, 32f);
        var image = inputObject.GetComponent<Image>();
        image.color = new Color(1f, 1f, 1f, 1f);

        var text = CreateInputText(inputObject.transform, font, "Text", new Color(0.2f, 0.14f, 0.1f, 1f), FontStyle.Normal);
        text.text = string.Empty;
        var placeholder = CreateInputText(inputObject.transform, font, "Placeholder", new Color(0.55f, 0.48f, 0.42f, 1f), FontStyle.Italic);
        placeholder.text = "Username";

        var input = inputObject.GetComponent<InputField>();
        input.textComponent = text;
        input.placeholder = placeholder;
        input.lineType = InputField.LineType.SingleLine;
        input.characterLimit = 24;
        input.onSubmit.AddListener(FinishNamePrompt);
        return input;
    }

    static Text CreateInputText(Transform parent, Font font, string name, Color color, FontStyle style)
    {
        var textObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        textObject.transform.SetParent(parent, false);
        var rect = textObject.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(8f, 4f);
        rect.offsetMax = new Vector2(-8f, -4f);
        var text = textObject.GetComponent<Text>();
        text.font = font;
        text.fontSize = 15;
        text.fontStyle = style;
        text.alignment = TextAnchor.MiddleLeft;
        text.color = color;
        text.supportRichText = false;
        text.raycastTarget = false;
        return text;
    }

    void CreatePromptButton(Transform parent, Font font)
    {
        var buttonObject = new GameObject("OK", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        var rect = buttonObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0f);
        rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.anchoredPosition = new Vector2(0f, 14f);
        rect.sizeDelta = new Vector2(88f, 28f);
        var image = buttonObject.GetComponent<Image>();
        image.color = new Color(0.93f, 0.55f, 0.42f, 1f);
        var button = buttonObject.GetComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(() => FinishNamePrompt(nameInput != null ? nameInput.text : string.Empty));

        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        labelObject.transform.SetParent(buttonObject.transform, false);
        var labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;
        var label = labelObject.GetComponent<Text>();
        label.font = font;
        label.fontSize = 14;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = new Color(0.24f, 0.14f, 0.1f, 1f);
        label.text = "OK";
        label.raycastTarget = false;
    }

    IEnumerator FocusNameInput()
    {
        yield return null;
        if (nameInput == null)
            yield break;
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(nameInput.gameObject);
        nameInput.ActivateInputField();
    }

    void FinishNamePrompt(string raw)
    {
        if (HasPlayerName)
            return;

        string name = string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim();
        if (string.IsNullOrEmpty(name))
            return;

        PlayerName = name;
        if (namePrompt != null)
            Destroy(namePrompt);
        namePrompt = null;
        nameInput = null;

        if (speaker != null && !PetJourney.PetIsAway)
        {
            speaker.enabled = true;
            PlayEnter();
        }
        if (playIntroOnStart)
            StartCoroutine(BeginIntroAfterEnter());
    }

    IEnumerator BeginIntroAfterEnter()
    {
        float enterLength = EnterClipLength();
        if (enterLength > 0f)
            yield return new WaitForSecondsRealtime(enterLength);

        yield return new WaitForSecondsRealtime(1f);
        BeginIntro();
    }

    float EnterClipLength()
    {
        var animator = GetComponent<Animator>();
        if (animator == null || animator.runtimeAnimatorController == null)
            return 0f;

        AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
        for (int i = 0; i < clips.Length; i++)
        {
            if (clips[i] != null && clips[i].name == "Enter")
                return clips[i].length;
        }

        return 0f;
    }

    void HoldIdle()
    {
        var animator = GetComponent<Animator>();
        if (animator == null || animator.runtimeAnimatorController == null)
            return;

        animator.Play("Idle", 0, 0f);
        animator.Update(0f);
    }

    void PlayEnter()
    {
        var animator = GetComponent<Animator>();
        if (animator == null || animator.runtimeAnimatorController == null)
            return;

        animator.Play("Enter", 0, 0f);
    }

    static Font BuiltinFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }

    public void Show(string text)
    {
        if (cam == null)
            cam = Camera.main;

        string content = string.IsNullOrEmpty(text) ? "……" : text;
        if (bodyText.font != null)
            bodyText.font.RequestCharactersInTexture(content, bodyText.fontSize, FontStyle.Normal);
        bodyText.text = Wrap(content, Mathf.Max(4, charactersPerLine));
        bubbleObject.SetActive(true);
        visible = true;
        PlaceAboveSpeaker();
        StartCoroutine(FitText());
    }

    public void Hide()
    {
        visible = false;
        pressedBubble = false;
        clickUrl = null;
        if (bubbleObject != null)
            bubbleObject.SetActive(false);
    }

    void CreateBubble()
    {
        bubbleObject = new GameObject("DialogueBubble");
        bubbleObject.transform.SetParent(transform, false);
        bubbleRenderer = bubbleObject.AddComponent<SpriteRenderer>();
        bubbleRenderer.sprite = CreateBubbleSprite();
        bubbleRenderer.sortingOrder = 25;

        var textObject = new GameObject("DialogueText");
        textObject.transform.SetParent(bubbleObject.transform, false);
        float tailShare = 42f / 250f;
        float bubbleHeight = bubbleRenderer.sprite.bounds.size.y;
        textObject.transform.localPosition = new Vector3(0f, bubbleHeight * tailShare * 0.5f, -0.01f);

        bodyText = textObject.AddComponent<TextMesh>();
        bodyText.anchor = TextAnchor.MiddleCenter;
        bodyText.alignment = TextAlignment.Center;
        bodyText.fontSize = 32;
        bodyText.characterSize = 0.08f;
        bodyText.color = new Color(0.24f, 0.16f, 0.12f, 1f);

        Font font = Font.CreateDynamicFontFromOSFont(FontCandidates, bodyText.fontSize);
        if (font != null)
            bodyText.font = font;

        bodyRenderer = bodyText.GetComponent<MeshRenderer>();
        bodyRenderer.sortingOrder = 26;
        if (bodyText.font != null)
            bodyRenderer.material = bodyText.font.material;
    }

    Sprite CreateBubbleSprite()
    {
        const int width = 520;
        const int height = 250;
        const int tailHeight = 42;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;

        var fill = new Color(0.99f, 0.96f, 0.9f, 1f);
        var border = new Color(0.42f, 0.26f, 0.18f, 1f);
        var pixels = new Color[width * height];
        float radius = 40f;
        int bodyHeight = height - tailHeight;
        int center = width / 2;
        int tailHalf = 34;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool inside;
                bool edge;
                if (y >= tailHeight)
                {
                    float outside = RoundedOutside(x, y - tailHeight, width, bodyHeight, radius);
                    inside = outside <= radius;
                    edge = inside && outside > radius - 7f;
                }
                else
                {
                    float t = tailHeight <= 1 ? 1f : y / (float)(tailHeight - 1);
                    float half = Mathf.Lerp(2f, tailHalf, t);
                    float dist = Mathf.Abs(x - center);
                    inside = dist <= half;
                    edge = inside && dist >= half - 6f;
                }

                if (!inside)
                    continue;

                Color color = edge ? border : fill;
                color.r = Mathf.Max(color.r, 0.05f);
                color.g = Mathf.Max(color.g, 0.05f);
                color.b = Mathf.Max(color.b, 0.05f);
                pixels[y * width + x] = color;
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();
        float pixelsPerUnit = width / Mathf.Max(0.1f, bubbleSize.x);
        return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), pixelsPerUnit);
    }

    static float RoundedOutside(int x, int y, int width, int height, float radius)
    {
        float left = radius;
        float right = width - 1 - radius;
        float bottom = radius;
        float top = height - 1 - radius;
        float px = Mathf.Clamp(x, left, right);
        float py = Mathf.Clamp(y, bottom, top);
        float dx = x - px;
        float dy = y - py;
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    static string Wrap(string text, int perLine)
    {
        if (string.IsNullOrEmpty(text) || perLine < 1 || text.Length <= perLine)
            return text;

        var wrapped = new System.Text.StringBuilder(text.Length + 8);
        int index = 0;
        while (index < text.Length)
        {
            if (text[index] == '\n')
            {
                wrapped.Append('\n');
                index++;
                continue;
            }

            int remaining = text.Length - index;
            if (remaining <= perLine)
            {
                wrapped.Append(text, index, remaining);
                break;
            }

            int end = index + perLine - 1;
            int breakAt = text.LastIndexOf(' ', end, perLine);
            if (breakAt <= index)
                breakAt = index + perLine;

            wrapped.Append(text, index, breakAt - index);
            wrapped.Append('\n');
            index = breakAt < text.Length && text[breakAt] == ' ' ? breakAt + 1 : breakAt;
        }

        return wrapped.ToString().TrimEnd('\n');
    }

    IEnumerator FitText()
    {
        yield return null;
        if (!visible || bodyText == null || bodyRenderer == null || bubbleRenderer == null)
            yield break;

        bodyText.transform.localScale = Vector3.one;
        float textWidth = bodyRenderer.bounds.size.x;
        float textHeight = bodyRenderer.bounds.size.y;
        float bubbleWidth = bubbleRenderer.bounds.size.x;
        float bubbleHeight = bubbleRenderer.bounds.size.y;
        if (textWidth < 0.001f || textHeight < 0.001f || bubbleWidth < 0.001f || bubbleHeight < 0.001f)
            yield break;

        float innerWidth = bubbleWidth * 0.78f;
        float innerHeight = bubbleHeight * 0.48f;
        float scale = Mathf.Min(innerWidth / textWidth, innerHeight / textHeight);
        bodyText.transform.localScale = new Vector3(scale, scale, 1f);
    }

    void PlaceAboveSpeaker()
    {
        if (speaker == null || bubbleRenderer == null || bubbleRenderer.sprite == null)
            return;

        float height = bubbleRenderer.sprite.bounds.size.y * bubbleRenderer.transform.lossyScale.y;
        Bounds speakerBounds = speaker.bounds;
        float y = speakerBounds.max.y + gap + height * 0.5f;
        float limit = 4.55f;
        if (y + height * 0.5f > limit)
            y = limit - height * 0.5f;

        bubbleObject.transform.position = new Vector3(speakerBounds.center.x, y, speaker.transform.position.z);
    }

    bool HitsBubble(Vector2 screenPoint)
    {
        if (!visible || bubbleRenderer == null || bubbleRenderer.sprite == null || cam == null)
            return false;

        float depth = Mathf.Abs(cam.transform.position.z - bubbleRenderer.transform.position.z);
        Vector3 world = cam.ScreenToWorldPoint(new Vector3(screenPoint.x, screenPoint.y, depth));
        world.z = bubbleRenderer.bounds.center.z;
        return bubbleRenderer.bounds.Contains(world);
    }

    Vector2 PointerScreen()
    {
#if !UNITY_EDITOR
        if (TryGetUnityScreenPoint(out Vector2 screen))
            return screen;
#endif
        return Input.mousePosition;
    }

#if !UNITY_EDITOR
    static bool TryGetUnityScreenPoint(out Vector2 screen)
    {
        screen = Input.mousePosition;
        IntPtr hwnd = FindWindow("UnityWndClass", Application.productName);
        if (hwnd == IntPtr.Zero)
            hwnd = Process.GetCurrentProcess().MainWindowHandle;
        if (hwnd == IntPtr.Zero || !GetCursorPos(out NativePoint cursor))
            return false;
        if (!ScreenToClient(hwnd, ref cursor) || !GetClientRect(hwnd, out NativeRect client))
            return false;

        int clientWidth = Mathf.Max(1, client.right - client.left);
        int clientHeight = Mathf.Max(1, client.bottom - client.top);
        if (cursor.x < 0 || cursor.y < 0 || cursor.x >= clientWidth || cursor.y >= clientHeight)
            return false;

        screen = new Vector2(
            (cursor.x + 0.5f) * Screen.width / clientWidth,
            (clientHeight - cursor.y - 0.5f) * Screen.height / clientHeight);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    static extern bool ScreenToClient(IntPtr hwnd, ref NativePoint point);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
#endif
}
