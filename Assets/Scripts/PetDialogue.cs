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
    [SerializeField] string journeyIntroLine = "Press Journey when you want me to go out and bring something back.";
    [SerializeField] float journeyIntroDelay = 3f;
    [SerializeField] float clickSlopPixels = 18f;
    [SerializeField] RectTransform dialogueBox;
    [SerializeField] Text dialogueText;

    static readonly Color StateIColor = new Color(0.36f, 0.20f, 0.18f, 1f);
    static readonly Color StateIIColor = new Color(0.20f, 0.24f, 0.40f, 1f);

    Camera cam;
    Font defaultFont;
    Color defaultColor;
    bool styleCached;

    bool visible;
    bool pressedBubble;
    bool playingIntro;
    int introIndex;
    enum OpeningBeat
    {
        None,
        AskName,
        Greet,
        Stay,
        TeachSend,
        TeachClick
    }

    OpeningBeat openingBeat;
    string[] sequence;
    int sequenceIndex;
    string[] followupLines;
    int followupIndex;
    string clickUrl;
    string currentLine;
    Text sceneDialogueText;

    public static Sprite PortraitSprite { get; private set; }
    Vector2 pressScreen;
    GameObject namePrompt;
    InputField nameInput;
    Transform headBubble;
    Vector3 headLocalOffset;
    bool headPoseCached;

    public static string PlayerName { get; private set; }
    public static bool HasPlayerName => !string.IsNullOrEmpty(PlayerName);
    public bool IsOpen => visible;

    public static void Say(string text)
    {
        if (Instance == null)
            return;

        Instance.clickUrl = null;
        Instance.followupLines = null;
        Instance.Show(text);
    }

    public static void SayLines(params string[] lines)
    {
        if (Instance == null || lines == null || lines.Length == 0)
            return;

        Instance.playingIntro = false;
        Instance.sequence = null;
        Instance.clickUrl = null;
        Instance.followupLines = lines;
        Instance.followupIndex = 0;
        Instance.Show(lines[0]);
    }

    public static void SayOnce(string text)
    {
        if (Instance == null)
            return;

        Instance.playingIntro = false;
        Instance.sequence = null;
        Instance.clickUrl = null;
        Instance.followupLines = null;
        Instance.Show(text);
    }

    public static void SayLink(string text, string url)
    {
        if (Instance == null)
            return;

        Instance.playingIntro = false;
        Instance.sequence = null;
        Instance.clickUrl = url;
        Instance.followupLines = null;
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
        if (speaker != null)
            PortraitSprite = speaker.sprite;
        cam = Camera.main;
        if (speaker != null)
            speaker.enabled = false;
        HoldIdle();
        PetPhone.Ensure();
        BindDialogue();
        Hide();
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void Start()
    {
        StartCoroutine(AppearInCenter());
    }

    void LateUpdate()
    {
        if (headBubble == null)
            return;

        if (!headPoseCached)
        {
            headLocalOffset = headBubble.localPosition;
            headPoseCached = true;
        }

        Vector3 scale = transform.lossyScale;
        headBubble.position = transform.position + Vector3.Scale(headLocalOffset, scale);
        headBubble.rotation = Quaternion.identity;
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
        {
            StartCoroutine(ShowJourneyIntroAfterDelay());
            return;
        }

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
        if (openingBeat == OpeningBeat.AskName)
        {
            openingBeat = OpeningBeat.None;
            Hide();
            ShowNamePrompt();
            return;
        }

        if (openingBeat == OpeningBeat.Greet)
        {
            openingBeat = OpeningBeat.None;
            Hide();
            StartCoroutine(MoveToCorner());
            return;
        }

        if (openingBeat == OpeningBeat.Stay)
        {
            openingBeat = OpeningBeat.None;
            Hide();
            StartCoroutine(ShowJourneyIntroAfterDelay());
            return;
        }

        if (openingBeat == OpeningBeat.TeachSend)
        {
            openingBeat = OpeningBeat.TeachClick;
            PetJourney.ShowJourneyButton();
            Show("See the Journey button on the right? Click it, and I'll head out on a trip.");
            return;
        }

        if (openingBeat == OpeningBeat.TeachClick)
        {
            openingBeat = OpeningBeat.None;
            Hide();
            return;
        }

        if (!string.IsNullOrEmpty(clickUrl))
        {
            string url = clickUrl;
            clickUrl = null;
            Hide();
            Application.OpenURL(url);
            return;
        }

        if (followupLines != null && followupIndex + 1 < followupLines.Length)
        {
            followupIndex++;
            Show(followupLines[followupIndex]);
            return;
        }

        if (followupLines != null)
            followupLines = null;

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
        {
            playingIntro = false;
            Hide();
            StartCoroutine(ShowJourneyIntroAfterDelay());
            return;
        }

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

    public static Canvas OverlayCanvas()
    {
        var canvases = FindObjectsOfType<Canvas>();
        for (int i = 0; i < canvases.Length; i++)
        {
            Canvas canvas = canvases[i];
            if (canvas == null || !canvas.isRootCanvas)
                continue;
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                continue;
            return canvas;
        }

        return null;
    }

    void ShowNamePrompt()
    {
        Canvas canvas = OverlayCanvas();
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
        label.text = "Your name";
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
        if (PetPhone.Instance != null)
            PetPhone.Instance.EndNameEntry();

        if (speaker != null && !PetJourney.PetIsAway)
            speaker.enabled = true;
        openingBeat = OpeningBeat.Greet;
        Show("Hello, " + PlayerName + ".");
    }

    IEnumerator AppearInCenter()
    {
        transform.localPosition = Vector3.zero;
        transform.localRotation = Quaternion.identity;
        transform.localScale = Vector3.zero;
        if (speaker != null)
            speaker.enabled = true;

        yield return StartCoroutine(PlayClipAndWait("JourneyEnd"));
        transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
        if (speaker != null)
            speaker.enabled = true;
        openingBeat = OpeningBeat.AskName;
        Show("Hi, I'm Blinky, your companion pet. What should I call you?");
    }

    IEnumerator MoveToCorner()
    {
        yield return StartCoroutine(PlayClipAndWait("Enter"));
        openingBeat = OpeningBeat.Stay;
        Show("I think I'll just stay here.");
    }

    IEnumerator PlayClipAndWait(string stateName)
    {
        var animator = GetComponent<Animator>();
        if (animator == null || animator.runtimeAnimatorController == null)
        {
            if (transform.localScale.sqrMagnitude < 0.0001f)
                transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
            yield break;
        }

        animator.Play(stateName, 0, 0f);
        animator.Update(0f);

        float length = ClipLength(animator, stateName);
        float timeout = Mathf.Max(length, 0.1f) + 0.35f;
        float elapsed = 0f;
        bool finished = false;
        while (elapsed < timeout)
        {
            yield return null;
            elapsed += animator.updateMode == AnimatorUpdateMode.UnscaledTime
                ? Time.unscaledDeltaTime
                : Time.deltaTime;

            AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
            if (info.IsName(stateName) && !animator.IsInTransition(0) && info.normalizedTime >= 1f)
            {
                finished = true;
                break;
            }

            // Enter already has an exit into Idle. Leaving the clip is the ending, not a restart.
            if (!info.IsName(stateName) && elapsed >= length)
            {
                finished = true;
                break;
            }

            // Only push the state back on if Play has not been accepted yet.
            if (!info.IsName(stateName) && elapsed < 0.15f)
                animator.Play(stateName, 0, 0f);
        }

        if (!finished && animator.GetCurrentAnimatorStateInfo(0).IsName(stateName))
        {
            animator.Play(stateName, 0, 1f);
            animator.Update(0f);
        }

        if (stateName == "JourneyEnd")
            yield break;

        AnimatorStateInfo end = animator.GetCurrentAnimatorStateInfo(0);
        if (!end.IsName("Idle"))
        {
            animator.Play("Idle", 0, 0f);
            animator.Update(0f);
        }
    }

    static float ClipLength(Animator animator, string stateName)
    {
        AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
        for (int i = 0; i < clips.Length; i++)
        {
            if (clips[i] != null && clips[i].name == stateName)
                return clips[i].length;
        }

        return 0f;
    }

    public void CloseForJourney()
    {
        openingBeat = OpeningBeat.None;
        if (visible)
            Hide();
    }

    IEnumerator ShowJourneyIntroAfterDelay()
    {
        yield return new WaitForSecondsRealtime(Mathf.Max(0f, journeyIntroDelay));
        openingBeat = OpeningBeat.TeachSend;
        string name = string.IsNullOrWhiteSpace(PlayerName) ? "there" : PlayerName;
        Show("Alright, " + name + ". It's time I showed you how to send me out to search for memes.");
    }

    void HoldIdle()
    {
        var animator = GetComponent<Animator>();
        if (animator == null || animator.runtimeAnimatorController == null)
            return;

        animator.Play("Idle", 0, 0f);
        animator.Update(0f);
    }

    void PlayPose(string stateName)
    {
        var animator = GetComponent<Animator>();
        if (animator == null || animator.runtimeAnimatorController == null)
            return;

        AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
        if (info.IsName(stateName))
            return;
        if (info.IsName("JourneyStart"))
            return;
        if (info.IsName("JourneyEnd") && info.normalizedTime < 1f)
            return;

        animator.Play(stateName, 0, 0f);
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
        currentLine = string.IsNullOrEmpty(text) ? "……" : text;
        PlaceDialogue(currentLine);
        visible = true;
        PlayPose("Talking");
    }

    public void RefreshPlacement()
    {
        if (!visible)
            return;

        PlaceDialogue(currentLine);
    }

    void PlaceDialogue(string content)
    {
        BindDialogue();
        if (sceneDialogueText != null)
            dialogueText = sceneDialogueText;
        if (dialogueBox == null || dialogueText == null)
            return;

        ApplyStoryStyle();
        dialogueText.text = content;
        if (headBubble != null)
            headBubble.gameObject.SetActive(true);
        else
            dialogueBox.gameObject.SetActive(true);
        AssignDialogueCamera();
    }

    public void Hide()
    {
        visible = false;
        pressedBubble = false;
        clickUrl = null;
        followupLines = null;
        if (PetPhone.Instance != null)
            PetPhone.Instance.HideSpeech();
        if (headBubble != null)
            headBubble.gameObject.SetActive(false);
        else if (dialogueBox != null)
            dialogueBox.gameObject.SetActive(false);
        PlayPose("Idle");
    }

    void BindDialogue()
    {
        if (dialogueBox == null && !BindHeadDialogue())
        {
            var images = FindObjectsOfType<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                if (images[i].gameObject.name != "Image")
                    continue;
                if (images[i].GetComponentInChildren<Text>(true) == null)
                    continue;
                Canvas imageCanvas = images[i].GetComponentInParent<Canvas>();
                if (imageCanvas != null && imageCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    continue;
                dialogueBox = images[i].rectTransform;
                break;
            }
        }

        if (sceneDialogueText == null && dialogueBox != null)
            sceneDialogueText = dialogueBox.GetComponentInChildren<Text>(true);
        if (dialogueText == null)
            dialogueText = sceneDialogueText;
        CacheDefaultStyle();
        AssignDialogueCamera();
    }

    bool BindHeadDialogue()
    {
        var canvases = GetComponentsInChildren<Canvas>(true);
        for (int i = 0; i < canvases.Length; i++)
        {
            Canvas canvas = canvases[i];
            if (canvas == null || canvas.renderMode != RenderMode.WorldSpace)
                continue;

            Text text = canvas.GetComponentInChildren<Text>(true);
            if (text == null)
                continue;

            dialogueBox = canvas.GetComponent<RectTransform>();
            sceneDialogueText = text;
            dialogueText = text;
            Transform parent = canvas.transform.parent;
            headBubble = parent != null && parent != transform ? parent : canvas.transform;
            return true;
        }

        return false;
    }

    void AssignDialogueCamera()
    {
        if (dialogueBox == null)
            return;

        Canvas canvas = dialogueBox.GetComponentInParent<Canvas>();
        if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            return;
        if (cam == null)
            cam = Camera.main;
        if (cam != null && canvas.worldCamera == null)
            canvas.worldCamera = cam;
    }

    void CacheDefaultStyle()
    {
        if (styleCached || sceneDialogueText == null)
            return;

        defaultFont = sceneDialogueText.font;
        defaultColor = sceneDialogueText.color;
        styleCached = true;
    }

    void ApplyStoryStyle()
    {
        CacheDefaultStyle();
        if (dialogueText == null)
            return;

        PetStory.Phase phase = PetStory.Current;
        if (phase == PetStory.Phase.StateI)
        {
            dialogueText.font = LoadStoryFont("Fonts/SourceSerif4-Regular");
            dialogueText.color = StateIColor;
            return;
        }

        if (phase == PetStory.Phase.StateII)
        {
            dialogueText.font = LoadStoryFont("Fonts/IBMPlexSerif-Regular");
            dialogueText.color = StateIIColor;
            return;
        }

        if (defaultFont != null)
            dialogueText.font = defaultFont;
        dialogueText.color = defaultColor;
    }

    Font LoadStoryFont(string resourcePath)
    {
        Font font = Resources.Load<Font>(resourcePath);
        return font != null ? font : defaultFont;
    }

    bool HitsBubble(Vector2 screenPoint)
    {
        if (!visible || dialogueBox == null)
            return false;

        Camera eventCamera = cam;
        Canvas canvas = dialogueBox.GetComponentInParent<Canvas>();
        if (canvas != null && canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            eventCamera = null;
        else if (canvas != null && canvas.worldCamera != null)
            eventCamera = canvas.worldCamera;

        if (RectTransformUtility.RectangleContainsScreenPoint(dialogueBox, screenPoint, eventCamera))
            return true;

        SpriteRenderer bubble = headBubble != null ? headBubble.GetComponent<SpriteRenderer>() : null;
        if (bubble == null || bubble.sprite == null || cam == null)
            return false;

        Bounds bounds = bubble.bounds;
        Vector3 min = cam.WorldToScreenPoint(new Vector3(bounds.min.x, bounds.min.y, bounds.center.z));
        Vector3 max = cam.WorldToScreenPoint(new Vector3(bounds.max.x, bounds.max.y, bounds.center.z));
        var screenRect = Rect.MinMaxRect(
            Mathf.Min(min.x, max.x),
            Mathf.Min(min.y, max.y),
            Mathf.Max(min.x, max.x),
            Mathf.Max(min.y, max.y));
        return screenRect.Contains(screenPoint);
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
