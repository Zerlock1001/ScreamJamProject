using System;
using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// 桌面上的一句对话：弹出气泡，显示文字，点击气泡后关闭。
/// </summary>
public class PetDialogue : MonoBehaviour
{
    public static PetDialogue Instance { get; private set; }

    [SerializeField] SpriteRenderer speaker;
    [SerializeField] bool playIntroOnStart = true;
    [SerializeField] string[] introLines = { "介绍文本1", "介绍文本2" };
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
    const string IntroSeenKey = "PetDialogue.IntroSeen";

    bool visible;
    bool pressedBubble;
    bool playingIntro;
    int introIndex;
    Vector2 pressScreen;

    public bool IsOpen => visible;

    public static void Say(string text)
    {
        if (Instance != null)
            Instance.Show(text);
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
        if (playIntroOnStart && PlayerPrefs.GetInt(IntroSeenKey, 0) == 0)
            BeginIntro();
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
        Show(introLines[0]);
    }

    void AdvanceIntroOrHide()
    {
        if (playingIntro && introLines != null && introIndex + 1 < introLines.Length)
        {
            introIndex++;
            Show(introLines[introIndex]);
            return;
        }

        if (playingIntro)
        {
            playingIntro = false;
            PlayerPrefs.SetInt(IntroSeenKey, 1);
            PlayerPrefs.Save();
        }

        Hide();
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
        if (string.IsNullOrEmpty(text) || text.Length <= perLine)
            return text;

        var wrapped = new System.Text.StringBuilder(text.Length + 8);
        int column = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char character = text[i];
            if (character == '\n')
            {
                wrapped.Append('\n');
                column = 0;
                continue;
            }

            if (column >= perLine)
            {
                wrapped.Append('\n');
                column = 0;
            }

            wrapped.Append(character);
            column++;
        }

        return wrapped.ToString();
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
