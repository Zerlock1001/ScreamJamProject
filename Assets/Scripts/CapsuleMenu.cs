using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Left-click the capsule to open a small pixel menu containing Journey and Quit.
/// </summary>
public class CapsuleMenu : MonoBehaviour
{
    [SerializeField] bool enableMenu = true;
    [SerializeField] float clickSlopPixels = 12f;
    [SerializeField] float menuGap = 0.10f;
    [SerializeField] float hoverScale = 1.18f;

    const float PanelWidth = 2.2f;
    const float PanelHeight = 1.2f;
    const float MenuScale = 1.2f;
    const float OptionRowY = 0.3f;
    const int GlyphWidth = 5;
    const int GlyphHeight = 7;
    const float TextPixelsPerUnit = 28f;

    static readonly string[] OptionLabels = { "journey", "quit" };
    static CapsuleMenu instance;

    public static void UseBody(SpriteRenderer body)
    {
        if (instance == null || body == null)
            return;

        instance.capsule = body;
        instance.hitRenderers = body.GetComponentsInChildren<SpriteRenderer>(true);
    }

    SpriteRenderer capsule;
    SpriteRenderer[] hitRenderers;
    Camera cam;
    SpriteRenderer panelRenderer;
    GameObject panelObject;
    SpriteRenderer highlightRenderer;
    SpriteRenderer[] optionRenderers;
    Sprite[] optionSprites;
    Texture2D[] optionTextures;
    Sprite highlightSprite;
    Texture2D highlightTexture;
    Sprite panelSprite;
    Texture2D panelTexture;

    bool menuVisible;
    bool pressedCapsule;
    int pressedOption = -1;
    int hoveredOption = -1;
    Vector2 pressScreen;

    void Awake()
    {
        instance = this;
        capsule = GetComponent<SpriteRenderer>();
        cam = Camera.main;
        HideLegacyHud();

        if (!enableMenu)
            return;

        CreateMenu();
        SetMenuVisible(false);
    }

    void HideLegacyHud()
    {
        var buttons = FindObjectsOfType<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            Button button = buttons[i];
            if (button == null)
                continue;

            if (button.gameObject.name == "Journey")
            {
                button.gameObject.SetActive(false);
            }
            else if (button.gameObject.name == "Quit")
            {
                button.interactable = false;
                var graphics = button.GetComponentsInChildren<Graphic>(true);
                for (int j = 0; j < graphics.Length; j++)
                {
                    graphics[j].raycastTarget = false;
                    graphics[j].enabled = false;
                }
            }
        }
    }

    void Update()
    {
        if (!enableMenu)
            return;

        if (cam == null)
            cam = Camera.main;
        if (capsule == null || !capsule.enabled || cam == null || !PetDialogue.HasPlayerName)
        {
            if (menuVisible)
                SetMenuVisible(false);
            return;
        }

        if (menuVisible)
        {
            PlaceMenu();
            RefreshAvailability();

            int hovered = OptionAt(PointerScreen());
            if (hovered != hoveredOption)
            {
                hoveredOption = hovered;
                ApplyHover();
            }
        }
        else if (hoveredOption != -1)
        {
            hoveredOption = -1;
            ApplyHover();
        }

        if (Input.GetMouseButtonDown(0))
        {
            pressScreen = PointerScreen();
            pressedCapsule = HitsCapsule(pressScreen);
            pressedOption = menuVisible ? OptionAt(pressScreen) : -1;
        }

        if (!Input.GetMouseButtonUp(0))
            return;

        Vector2 releaseScreen = PointerScreen();
        bool wasCapsule = pressedCapsule || HitsCapsule(releaseScreen);
        int option = pressedOption;
        pressedCapsule = false;
        pressedOption = -1;

        float slop = Mathf.Max(1f, clickSlopPixels);
        if ((releaseScreen - pressScreen).sqrMagnitude > slop * slop)
            return;

        if (option >= 0 && OptionAt(releaseScreen) == option)
        {
            ExecuteOption(option);
            return;
        }

        if (wasCapsule)
        {
            SetMenuVisible(!menuVisible);
            return;
        }

        if (menuVisible)
            SetMenuVisible(false);
    }

    void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus && menuVisible)
            SetMenuVisible(false);
    }

    void CreateMenu()
    {
        panelObject = new GameObject("CapsuleMenuPanel");
        panelObject.transform.SetParent(transform, false);
        panelObject.transform.localScale = Vector3.one * MenuScale;

        panelRenderer = panelObject.AddComponent<SpriteRenderer>();
        panelSprite = CreatePanelSprite();
        panelRenderer.sprite = panelSprite;
        panelRenderer.sortingOrder = 10;

        CreateHighlight();

        optionRenderers = new SpriteRenderer[OptionLabels.Length];
        optionSprites = new Sprite[OptionLabels.Length];
        optionTextures = new Texture2D[OptionLabels.Length];

        for (int i = 0; i < OptionLabels.Length; i++)
        {
            Sprite sprite = CreateTextSprite(OptionLabels[i], out Texture2D texture);
            optionSprites[i] = sprite;
            optionTextures[i] = texture;

            var labelObject = new GameObject("CapsuleMenuOption" + i);
            labelObject.transform.SetParent(panelObject.transform, false);
            labelObject.transform.localPosition = new Vector3(0f, OptionY(i), -0.01f);

            var renderer = labelObject.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.color = Color.white;
            renderer.sortingOrder = 13;
            optionRenderers[i] = renderer;
        }

        RefreshAvailability();
    }

    static float OptionY(int index)
    {
        return index == 0 ? OptionRowY : -OptionRowY;
    }

    void RefreshAvailability()
    {
        if (optionRenderers == null || optionRenderers.Length == 0 || optionRenderers[0] == null)
            return;

        bool joined = PetCampaign.Instance != null && PetCampaign.Instance.HasJoinedCult;
        bool available = !joined
            && PetJourney.Instance != null
            && PetJourney.Instance.JourneyAvailable
            && !PetCampaign.BlocksJourney;
        optionRenderers[0].color = available
            ? Color.white
            : new Color(0.42f, 0.42f, 0.44f, 1f);
    }

    void CreateHighlight()
    {
        highlightTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        highlightTexture.filterMode = FilterMode.Point;
        highlightTexture.SetPixel(0, 0, Color.white);
        highlightTexture.Apply();

        highlightSprite = Sprite.Create(
            highlightTexture,
            new Rect(0f, 0f, 1f, 1f),
            new Vector2(0.5f, 0.5f),
            1f);

        var highlightObject = new GameObject("CapsuleMenuHighlight");
        highlightObject.transform.SetParent(panelObject.transform, false);
        highlightObject.transform.localScale = new Vector3(
            PanelWidth - 0.12f,
            PanelHeight / 2f - 0.08f,
            1f);

        highlightRenderer = highlightObject.AddComponent<SpriteRenderer>();
        highlightRenderer.sprite = highlightSprite;
        highlightRenderer.color = new Color(1f, 1f, 1f, 0.22f);
        highlightRenderer.sortingOrder = 11;
        highlightObject.SetActive(false);
    }

    Sprite CreatePanelSprite()
    {
        const int width = 110;
        const int height = 60;
        const float pixelsPerUnit = 50f;

        panelTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        panelTexture.filterMode = FilterMode.Point;
        panelTexture.wrapMode = TextureWrapMode.Clamp;

        var fill = new Color(0.32f, 0.32f, 0.34f, 1f);
        var border = new Color(0.66f, 0.66f, 0.70f, 1f);
        var separator = new Color(0.20f, 0.20f, 0.22f, 1f);
        var pixels = new Color[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool edge = x < 2 || y < 2 || x >= width - 2 || y >= height - 2;
                bool line = y == 30;
                pixels[y * width + x] = edge ? border : (line ? separator : fill);
            }
        }

        panelTexture.SetPixels(pixels);
        panelTexture.Apply();

        return Sprite.Create(
            panelTexture,
            new Rect(0f, 0f, width, height),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit);
    }

    Sprite CreateTextSprite(string text, out Texture2D texture)
    {
        int width = text.Length * (GlyphWidth + 1) - 1;
        int height = GlyphHeight;

        texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;

        var pixels = new Color[width * height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color(1f, 1f, 1f, 0f);

        for (int i = 0; i < text.Length; i++)
        {
            string[] glyph = GetGlyph(text[i]);
            int originX = i * (GlyphWidth + 1);

            for (int y = 0; y < GlyphHeight; y++)
            {
                string row = glyph[y];
                for (int x = 0; x < GlyphWidth; x++)
                {
                    if (row[x] == '#')
                        pixels[(GlyphHeight - 1 - y) * width + originX + x] = Color.white;
                }
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();

        return Sprite.Create(
            texture,
            new Rect(0f, 0f, width, height),
            new Vector2(0.5f, 0.5f),
            TextPixelsPerUnit);
    }

    static string[] GetGlyph(char c)
    {
        switch (c)
        {
            case '?':
                return new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".....", "..#.." };
            case 's':
                return new[] { ".....", ".####", "#....", ".###.", "....#", "####.", "....." };
            case 'e':
                return new[] { ".....", ".###.", "#...#", "#####", "#....", ".###.", "....." };
            case 't':
                return new[] { "..#..", ".###.", "..#..", "..#..", "..#..", "..#..", "...##" };
            case 'i':
                return new[] { "..#..", ".....", ".##..", "..#..", "..#..", "..#..", ".###." };
            case 'n':
                return new[] { ".....", "#.##.", "##..#", "#...#", "#...#", "#...#", "....." };
            case 'g':
                return new[] { ".....", ".####", "#...#", "#...#", ".####", "....#", ".###." };
            case 'q':
                return new[] { ".....", ".####", "#...#", "#...#", ".####", "....#", "....#" };
            case 'u':
                return new[] { ".....", "#...#", "#...#", "#...#", "#..##", ".##.#", "....." };
            case 'j':
                return new[] { "...#.", ".....", "...#.", "...#.", "...#.", "#..#.", ".##.." };
            case 'o':
                return new[] { ".....", ".###.", "#...#", "#...#", "#...#", ".###.", "....." };
            case 'r':
                return new[] { ".....", "#.##.", "##..#", "#....", "#....", "#....", "....." };
            case 'y':
                return new[] { ".....", "#...#", "#...#", ".####", "....#", ".###.", "....." };
            default:
                return new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." };
        }
    }

    void SetMenuVisible(bool visible)
    {
        menuVisible = visible;

        if (panelObject != null)
            panelObject.SetActive(visible);

        if (!visible)
        {
            hoveredOption = -1;
            ApplyHover();
            return;
        }

        PlaceMenu();
        RefreshAvailability();
        hoveredOption = -1;
        ApplyHover();
    }

    void ApplyHover()
    {
        if (optionRenderers != null)
        {
            for (int i = 0; i < optionRenderers.Length; i++)
            {
                if (optionRenderers[i] == null)
                    continue;

                float scale = i == hoveredOption ? hoverScale : 1f;
                optionRenderers[i].transform.localScale = new Vector3(scale, scale, 1f);
            }
        }

        if (highlightRenderer == null)
            return;

        bool visible = hoveredOption >= 0 && hoveredOption < OptionLabels.Length;
        highlightRenderer.gameObject.SetActive(visible);

        if (visible)
        {
            highlightRenderer.transform.localPosition = new Vector3(
                0f,
                OptionY(hoveredOption),
                -0.005f);
        }
    }

    void PlaceMenu()
    {
        if (capsule == null || panelObject == null)
            return;

        Bounds bounds = HitBounds();
        float menuWorldScale = Mathf.Abs(panelObject.transform.lossyScale.x);
        float menuHalfWidth = PanelWidth * 0.5f * menuWorldScale;
        if (menuHalfWidth < 0.001f)
            menuHalfWidth = PanelWidth * 0.5f * MenuScale;
        float x = bounds.min.x - menuGap - menuHalfWidth;

        if (cam != null && cam.orthographic)
        {
            float halfWidth = cam.orthographicSize * Mathf.Max(0.01f, cam.aspect);
            float leftLimit = cam.transform.position.x - halfWidth + 0.05f;
            if (x - menuHalfWidth < leftLimit)
                x = bounds.max.x + menuGap + menuHalfWidth;
        }

        panelObject.transform.position = new Vector3(x, bounds.center.y, bounds.center.z);
    }

    public static bool Blocks(Vector2 screenPoint)
    {
        return instance != null && instance.MenuContains(screenPoint);
    }

    bool MenuContains(Vector2 screenPoint)
    {
        if (!menuVisible || panelRenderer == null || cam == null)
            return false;

        Bounds bounds = panelRenderer.bounds;
        float depth = Mathf.Abs(cam.transform.position.z - bounds.center.z);
        Vector3 world = cam.ScreenToWorldPoint(new Vector3(screenPoint.x, screenPoint.y, depth));
        world.z = bounds.center.z;
        return bounds.Contains(world);
    }

    void ExecuteOption(int index)
    {
        switch (index)
        {
            case 0:
                if (PetCampaign.Ensure().HasJoinedCult)
                {
                    SetMenuVisible(false);
                    PetDialogue.Say("We don't need to limit you this way anymore.");
                    return;
                }

                if (PetJourney.Instance == null
                    || !PetJourney.Instance.JourneyAvailable
                    || PetCampaign.BlocksJourney)
                    return;

                SetMenuVisible(false);
                PetJourney.Instance.StartJourney();
                break;

            case 1:
                SetMenuVisible(false);
                QuitRefusal.RequestQuit();
                break;
        }
    }

    bool HitsCapsule(Vector2 screenPoint)
    {
        if (capsule == null || cam == null || capsule.sprite == null || !capsule.enabled)
            return false;

        Bounds bounds = HitBounds();
        float depth = Mathf.Abs(cam.transform.position.z - bounds.center.z);
        Vector3 world = cam.ScreenToWorldPoint(new Vector3(screenPoint.x, screenPoint.y, depth));
        world.z = bounds.center.z;
        return bounds.Contains(world);
    }

    Bounds HitBounds()
    {
        if (hitRenderers == null || hitRenderers.Length == 0)
            return capsule.bounds;

        bool any = false;
        Bounds bounds = default;
        for (int i = 0; i < hitRenderers.Length; i++)
        {
            SpriteRenderer renderer = hitRenderers[i];
            if (renderer == null || !renderer.enabled || renderer.sprite == null)
                continue;

            if (!any)
            {
                bounds = renderer.bounds;
                any = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds);
            }
        }

        return any ? bounds : capsule.bounds;
    }

    int OptionAt(Vector2 screenPoint)
    {
        if (!menuVisible || panelRenderer == null || cam == null)
            return -1;

        Bounds bounds = panelRenderer.bounds;
        float depth = Mathf.Abs(cam.transform.position.z - bounds.center.z);
        Vector3 world = cam.ScreenToWorldPoint(new Vector3(screenPoint.x, screenPoint.y, depth));
        world.z = bounds.center.z;

        if (!bounds.Contains(world))
            return -1;

        float fromTop = (bounds.max.y - world.y) / Mathf.Max(0.0001f, bounds.size.y);
        int index = Mathf.Clamp((int)(fromTop * 2f), 0, 1);
        return index;
    }

    Vector2 PointerScreen()
    {
#if !UNITY_EDITOR
        if (TryGetUnityScreenPoint(out Vector2 screen))
            return screen;
#endif
        return Input.mousePosition;
    }

    void OnDestroy()
    {
        if (instance == this)
            instance = null;

        if (optionSprites != null)
        {
            for (int i = 0; i < optionSprites.Length; i++)
            {
                if (optionSprites[i] != null)
                    Destroy(optionSprites[i]);
            }
        }

        if (optionTextures != null)
        {
            for (int i = 0; i < optionTextures.Length; i++)
            {
                if (optionTextures[i] != null)
                    Destroy(optionTextures[i]);
            }
        }

        if (panelSprite != null)
            Destroy(panelSprite);

        if (panelTexture != null)
            Destroy(panelTexture);

        if (highlightSprite != null)
            Destroy(highlightSprite);

        if (highlightTexture != null)
            Destroy(highlightTexture);
    }

#if !UNITY_EDITOR
    bool TryGetUnityScreenPoint(out Vector2 screen)
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
