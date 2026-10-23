using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Windows 独立构建：无边框小窗、背景透明、透明像素把点击交给后面的窗口。
/// 透明依赖关闭 DXGI Flip Model，并使用 D3D11。需要重新打包后才生效。
/// </summary>
[DefaultExecutionOrder(-500)]
public class DesktopPetWindow : MonoBehaviour
{
    [SerializeField] int windowWidth = 360;
    [SerializeField] int windowHeight = 360;
    [SerializeField] bool alwaysOnTop = true;
    // Uniform inset from the work-area edges. Values below MinScreenMargin are raised to it
    // so the borderless window never exactly covers the work area; Windows may treat that as fullscreen.
    [SerializeField] int screenMargin = 24;
    [SerializeField] bool showPlaceholder = true;
    [SerializeField] float clickAlphaThreshold = 0.2f;
    [SerializeField] float dragThresholdPixels = 36f;

    const int GwlStyle = -16;
    const int GwlExStyle = -20;
    const int GwlpWndProc = -4;
    const int MinScreenMargin = 2;

    const int WsCaption = 0x00C00000;
    const int WsThickFrame = 0x00040000;
    const int WsMinimizeBox = 0x00020000;
    const int WsMaximizeBox = 0x00010000;
    const int WsPopup = unchecked((int)0x80000000);

    const int WsExDlgModalFrame = 0x00000001;
    const int WsExWindowEdge = 0x00000100;
    const int WsExClientEdge = 0x00000200;
    const int WsExStaticEdge = 0x00020000;
    const int WsExTransparent = 0x00000020;
    const int WsExToolWindow = 0x00000080;
    const int WsExAppWindow = 0x00040000;

    const uint SwpNoSize = 0x0001;
    const uint SwpNoMove = 0x0002;
    const uint SwpFrameChanged = 0x0020;
    const uint SwpShowWindow = 0x0040;

    const int SwHide = 0;
    const int SwShowNoActivate = 4;

    const int WmNcLButtonDown = 0x00A1;
    const uint WmNcHitTest = 0x0084;
    const uint WmWindowPosChanging = 0x0046;
    const int HtTransparent = -1;
    const int HtCaption = 0x2;
    const uint MonitorDefaultToPrimary = 1;
    const uint MonitorDefaultToNearest = 2;

    static readonly IntPtr HwndTopMost = new IntPtr(-1);
    static readonly IntPtr HwndNoTopMost = new IntPtr(-2);

    Camera cam;
    Texture2D placeholderTexture;
    Sprite placeholderSprite;

#if !UNITY_EDITOR
    struct SpriteSample
    {
        public Matrix4x4 worldToLocal;
        public Vector3 planePoint;
        public Vector3 planeNormal;
        public Bounds bounds;
        public float colorAlpha;
        public bool flipX;
        public bool flipY;
        public bool readable;
        public int texWidth;
        public int texHeight;
        public Rect textureRect;
        public Color32[] pixels;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    struct ClientRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    static DesktopPetWindow instance;
    static WndProc procDelegate;
    static IntPtr procPtr;
    static EnumChildProc childEnum;
    static readonly Dictionary<IntPtr, IntPtr> prevByHwnd = new Dictionary<IntPtr, IntPtr>();
    static bool dragging;

    IntPtr hwnd;
    bool armDrag;
    Vector2 dragOrigin;
    bool petDragActive;
    Transform petDragTarget;
    Vector3 petDragLastWorld;
    bool ready;
    Vector3 camPosition;
    Quaternion camRotation;
    float camOrthoSize;
    float camAspect = 1f;
    int bufferWidth = 1;
    int bufferHeight = 1;
    int lastScreenWidth = -1;
    int lastScreenHeight = -1;
    int lastWorkLeft = int.MinValue;
    int lastWorkTop = int.MinValue;
    int lastWorkWidth = -1;
    int lastWorkHeight = -1;
    readonly List<SpriteSample> samples = new List<SpriteSample>();
    readonly Dictionary<int, Color32[]> pixelCache = new Dictionary<int, Color32[]>();
    readonly List<Rect> scratchUi = new List<Rect>();
    readonly List<Rect> scratchSprites = new List<Rect>();
    readonly List<ClientRect> scratchClient = new List<ClientRect>();
    readonly List<IntPtr> regionTargets = new List<IntPtr>();
    readonly List<IntPtr> pendingChildren = new List<IntPtr>();
    readonly Vector3[] uiCorners = new Vector3[4];
    SpriteSample[] sampleSnapshot = Array.Empty<SpriteSample>();
    Rect[] interfaceSnapshot = Array.Empty<Rect>();
    Rect[] spriteScreenSnapshot = Array.Empty<Rect>();
    ClientRect[] clientSnapshot = Array.Empty<ClientRect>();
    int appliedRegionHash = int.MinValue;
    bool regionDirty = true;
    bool spritesFullyMapped;
#endif

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void ConfigurePlayerEarly()
    {
#if !UNITY_EDITOR
        QualitySettings.antiAliasing = 0;
        Application.runInBackground = true;
#endif
    }

    void Awake()
    {
        cam = GetComponent<Camera>();
        if (cam == null)
            cam = Camera.main;
        ConfigureCamera();
    }

    void Start()
    {
        ConfigureCamera();
        if (showPlaceholder)
            CreatePlaceholder();

#if !UNITY_EDITOR
        instance = this;
        StartCoroutine(SetupWindow());
#endif
    }

    void Update()
    {
#if !UNITY_EDITOR
        if (!ready || hwnd == IntPtr.Zero)
            return;

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            QuitRefusal.Play();
            return;
        }

        if (Screen.fullScreenMode != FullScreenMode.Windowed)
            Screen.fullScreenMode = FullScreenMode.Windowed;

        if (Screen.fullScreen || Screen.width != lastScreenWidth || Screen.height != lastScreenHeight)
            ApplyAdaptiveWindow();

        TrackCurrentWindow();
        EnsureNotCoveringMonitor();
        ConfigureCamera();
        CacheHitData();
        EnsureHook();
        UpdateClickThrough();
        ApplyHitRegion();

        bool overMenu = TryGetCursorScreenPoint(out Vector2 cursorScreen) && CapsuleMenu.Blocks(cursorScreen);
        bool overVisible = IsCursorOverVisiblePixel() && !IsCursorOverInterface() && !overMenu;
        if (Input.GetMouseButtonDown(0) && overVisible)
        {
            armDrag = true;
            dragOrigin = Input.mousePosition;
        }

        if (armDrag && Input.GetMouseButton(0))
        {
            float threshold = Mathf.Max(1f, dragThresholdPixels);
            if ((Input.mousePosition - (Vector3)dragOrigin).sqrMagnitude >= threshold * threshold)
            {
                armDrag = false;
                if (!TryBeginPetDrag())
                    BeginWindowDrag();
            }
        }
        else if (petDragActive && Input.GetMouseButton(0))
        {
            UpdatePetDrag();
        }

        if (Input.GetMouseButtonUp(0))
            armDrag = false;

        if (petDragActive && !Input.GetMouseButton(0))
            EndPetDrag();
#endif
    }

#if !UNITY_EDITOR
    // The adaptive window covers almost the whole work area, so moving the native
    // window is clamped. Drag the pet inside the transparent overlay instead.
    // Returns true when the drag was handled (even if blocked by a modal prompt).
    bool TryBeginPetDrag()
    {
        PetDialogue pet = PetDialogue.Instance;
        if (pet == null)
            return false;

        if (!pet.CanDragPet || cam == null)
            return true;

        petDragTarget = pet.PetRoot;
        if (petDragTarget == null)
            return true;

        petDragActive = true;
        dragging = true;
        petDragLastWorld = ScreenToWorldOnPetPlane(Input.mousePosition);
        regionDirty = true;
        ApplyHitRegion();
        return true;
    }

    void BeginWindowDrag()
    {
        dragging = true;
        ReleaseCapture();
        SendMessage(hwnd, WmNcLButtonDown, HtCaption, 0);
        dragging = false;
    }

    void UpdatePetDrag()
    {
        if (petDragTarget == null)
        {
            EndPetDrag();
            return;
        }

        Vector3 world = ScreenToWorldOnPetPlane(Input.mousePosition);
        Vector3 delta = world - petDragLastWorld;
        if (delta.sqrMagnitude > 0f)
        {
            petDragTarget.position += new Vector3(delta.x, delta.y, 0f);
            ClampPetToView();
        }

        petDragLastWorld = world;
    }

    void EndPetDrag()
    {
        petDragActive = false;
        petDragTarget = null;
        dragging = false;
        regionDirty = true;
    }

    Vector3 ScreenToWorldOnPetPlane(Vector2 screenPoint)
    {
        if (cam == null)
            return Vector3.zero;

        float depth = 10f;
        if (petDragTarget != null)
        {
            Vector3 projected = cam.WorldToScreenPoint(petDragTarget.position);
            if (projected.z > 0.01f)
                depth = projected.z;
        }

        return cam.ScreenToWorldPoint(new Vector3(screenPoint.x, screenPoint.y, depth));
    }

    void ClampPetToView()
    {
        if (cam == null || petDragTarget == null || !cam.orthographic)
            return;

        Vector3 position = petDragTarget.position;
        float halfHeight = Mathf.Max(0.01f, cam.orthographicSize);
        float halfWidth = halfHeight * Mathf.Max(0.01f, cam.aspect);
        Vector3 center = cam.transform.position;
        const float margin = 1.2f;
        position.x = Mathf.Clamp(position.x, center.x - halfWidth + margin, center.x + halfWidth - margin);
        position.y = Mathf.Clamp(position.y, center.y - halfHeight + margin, center.y + halfHeight - margin);
        petDragTarget.position = position;
    }
#endif

    void OnDestroy()
    {
#if !UNITY_EDITOR
        if (instance == this)
        {
            RestoreHook();
            instance = null;
        }
#endif
        if (placeholderSprite != null)
            Destroy(placeholderSprite);
        if (placeholderTexture != null)
            Destroy(placeholderTexture);
    }

    void ConfigureCamera()
    {
        if (cam == null)
            return;

        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.allowHDR = false;
        cam.allowMSAA = false;
    }

    void CreatePlaceholder()
    {
        if (FindObjectOfType<SpriteRenderer>() != null)
            return;

        const int size = 256;
        placeholderTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        placeholderTexture.filterMode = FilterMode.Bilinear;
        placeholderTexture.wrapMode = TextureWrapMode.Clamp;

        var pixels = new Color[size * size];
        float center = (size - 1) * 0.5f;
        float radius = center - 4f;
        var fill = new Color(0.96f, 0.62f, 0.38f, 1f);
        var outline = new Color(0.45f, 0.22f, 0.16f, 1f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float edge = radius - Mathf.Sqrt(dx * dx + dy * dy);
                if (edge < 0f)
                    continue;

                float alpha = Mathf.Clamp01(edge / 2.5f);
                Color color = edge < 12f ? outline : fill;
                color.a *= alpha;
                pixels[y * size + x] = color;
            }
        }

        placeholderTexture.SetPixels(pixels);
        placeholderTexture.Apply();

        placeholderSprite = Sprite.Create(
            placeholderTexture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            48f);

        var go = new GameObject("PetPlaceholder");
        var renderer = go.AddComponent<SpriteRenderer>();
        renderer.sprite = placeholderSprite;
    }

#if !UNITY_EDITOR
    IEnumerator SetupWindow()
    {
        Screen.fullScreenMode = FullScreenMode.Windowed;
        ComputeAdaptiveWindow(out int targetWidth, out int targetHeight, out _, out _);
        windowWidth = targetWidth;
        windowHeight = targetHeight;
        if (Screen.fullScreen || Screen.width != windowWidth || Screen.height != windowHeight)
            Screen.SetResolution(windowWidth, windowHeight, FullScreenMode.Windowed);

        for (int i = 0; i < 15; i++)
        {
            hwnd = ResolveWindow();
            bool sized = Screen.width == windowWidth && Screen.height == windowHeight && !Screen.fullScreen;
            if (hwnd != IntPtr.Zero && sized)
                break;
            yield return null;
        }

        if (hwnd == IntPtr.Zero)
            hwnd = ResolveWindow();
        if (hwnd == IntPtr.Zero)
            yield break;

        ApplyBorderless(placeOnScreen: true);
        EnsureHook();
        ready = true;
        RememberLayout();

        for (int i = 0; i < 30; i++)
        {
            yield return null;
            TrackCurrentWindow();
        }
    }

    void TrackCurrentWindow()
    {
        IntPtr current = ResolveWindow();
        if (current == IntPtr.Zero)
            return;
        if (current == hwnd)
        {
            EnsureHook();
            return;
        }

        RestoreHook();
        hwnd = current;
        ApplyBorderless(placeOnScreen: false);
        ApplyAdaptiveWindow();
        EnsureHook();
    }

    void ApplyBorderless(bool placeOnScreen)
    {
        if (hwnd == IntPtr.Zero)
            return;

        int style = GetWindowLong(hwnd, GwlStyle);
        style &= ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox);
        style |= WsPopup;
        SetWindowLong(hwnd, GwlStyle, style);

        int exStyle = GetWindowLong(hwnd, GwlExStyle);
        exStyle &= ~(WsExDlgModalFrame | WsExWindowEdge | WsExClientEdge | WsExStaticEdge | WsExAppWindow);
        exStyle |= WsExToolWindow;
        SetWindowLong(hwnd, GwlExStyle, exStyle);

        var margins = new Margins
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1
        };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        if (placeOnScreen)
        {
            // Force the shell to refresh the taskbar button after applying the tool-window style.
            ShowWindow(hwnd, SwHide);
            ShowWindow(hwnd, SwShowNoActivate);
        }

        IntPtr zOrder = alwaysOnTop ? HwndTopMost : HwndNoTopMost;
        if (!placeOnScreen)
        {
            SetWindowPos(hwnd, zOrder, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpFrameChanged | SwpShowWindow);
            return;
        }

        ComputeAdaptiveWindow(out int width, out int height, out int x, out int y);
        windowWidth = width;
        windowHeight = height;
        SetWindowPos(hwnd, zOrder, x, y, width, height, SwpFrameChanged | SwpShowWindow);
        RememberLayout();
    }

    void EnsureNotCoveringMonitor()
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out NativeRect window))
            return;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf(typeof(MonitorInfo)) };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return;

        int workLeft = info.rcWork.left;
        int workTop = info.rcWork.top;
        int workWidth = Mathf.Max(1, info.rcWork.right - info.rcWork.left);
        int workHeight = Mathf.Max(1, info.rcWork.bottom - info.rcWork.top);

        if (workLeft != lastWorkLeft || workTop != lastWorkTop ||
            workWidth != lastWorkWidth || workHeight != lastWorkHeight)
        {
            ApplyAdaptiveWindow();
            return;
        }

        int margin = EffectiveScreenMargin;
        int maxWidth = Mathf.Max(1, workWidth - margin * 2);
        int maxHeight = Mathf.Max(1, workHeight - margin * 2);

        int width = window.right - window.left;
        int height = window.bottom - window.top;
        int newWidth = Mathf.Min(width, maxWidth);
        int newHeight = Mathf.Min(height, maxHeight);
        int x = Mathf.Clamp(window.left, workLeft, Mathf.Max(workLeft, info.rcWork.right - newWidth));
        int y = Mathf.Clamp(window.top, workTop, Mathf.Max(workTop, info.rcWork.bottom - newHeight));

        if (newWidth == width && newHeight == height && x == window.left && y == window.top)
            return;

        IntPtr zOrder = alwaysOnTop ? HwndTopMost : HwndNoTopMost;
        SetWindowPos(hwnd, zOrder, x, y, newWidth, newHeight, SwpFrameChanged | SwpShowWindow);
        windowWidth = newWidth;
        windowHeight = newHeight;
        regionDirty = true;
        RememberLayout();
    }

    int EffectiveScreenMargin => Mathf.Max(MinScreenMargin, screenMargin);

    void ComputeAdaptiveWindow(out int width, out int height, out int x, out int y)
    {
        int margin = EffectiveScreenMargin;
        int workLeft;
        int workTop;
        int workRight;
        int workBottom;

        var monitor = MonitorFromWindow(
            hwnd,
            hwnd == IntPtr.Zero ? MonitorDefaultToPrimary : MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf(typeof(MonitorInfo)) };

        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            workLeft = info.rcWork.left;
            workTop = info.rcWork.top;
            workRight = info.rcWork.right;
            workBottom = info.rcWork.bottom;
        }
        else
        {
            workLeft = 0;
            workTop = 0;
            workRight = Mathf.Max(1, Screen.width);
            workBottom = Mathf.Max(1, Screen.height);
        }

        int workWidth = Mathf.Max(1, workRight - workLeft);
        int workHeight = Mathf.Max(1, workBottom - workTop);
        width = Mathf.Max(1, workWidth - margin * 2);
        height = Mathf.Max(1, workHeight - margin * 2);
        x = Mathf.Clamp(
            workRight - width - margin,
            workLeft + margin,
            Mathf.Max(workLeft + margin, workRight - width - margin));
        y = Mathf.Clamp(
            workBottom - height - margin,
            workTop + margin,
            Mathf.Max(workTop + margin, workBottom - height - margin));
    }

    void ApplyAdaptiveWindow()
    {
        if (hwnd == IntPtr.Zero)
            return;

        ComputeAdaptiveWindow(out int width, out int height, out int x, out int y);
        if (!GetWindowRect(hwnd, out NativeRect current)
            || current.left != x
            || current.top != y
            || current.right - current.left != width
            || current.bottom - current.top != height)
        {
            IntPtr zOrder = alwaysOnTop ? HwndTopMost : HwndNoTopMost;
            SetWindowPos(hwnd, zOrder, x, y, width, height, SwpFrameChanged | SwpShowWindow);
        }

        windowWidth = width;
        windowHeight = height;
        regionDirty = true;
        RememberLayout();
    }

    void RememberLayout()
    {
        lastScreenWidth = Screen.width;
        lastScreenHeight = Screen.height;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf(typeof(MonitorInfo)) };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return;

        lastWorkLeft = info.rcWork.left;
        lastWorkTop = info.rcWork.top;
        lastWorkWidth = info.rcWork.right - info.rcWork.left;
        lastWorkHeight = info.rcWork.bottom - info.rcWork.top;
    }

    void UpdateClickThrough()
    {
        if (hwnd == IntPtr.Zero)
            return;

        bool solid = dragging || IsCursorOverVisiblePixel();
        bool changed = false;
        for (int i = 0; i < regionTargets.Count; i++)
            changed |= SetPassThrough(regionTargets[i], !solid);
        if (regionTargets.Count == 0)
            changed |= SetPassThrough(hwnd, !solid);
        if (changed)
            regionDirty = true;
    }

    bool SetPassThrough(IntPtr target, bool passThrough)
    {
        if (target == IntPtr.Zero)
            return false;

        int exStyle = GetWindowLong(target, GwlExStyle);
        bool current = (exStyle & WsExTransparent) != 0;
        if (current == passThrough)
            return false;

        if (passThrough)
            exStyle |= WsExTransparent;
        else
            exStyle &= ~WsExTransparent;
        SetWindowLong(target, GwlExStyle, exStyle);
        IntPtr zOrder = alwaysOnTop ? HwndTopMost : HwndNoTopMost;
        SetWindowPos(target, zOrder, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpFrameChanged | SwpShowWindow);
        return true;
    }

    void CacheHitData()
    {
        if (cam == null)
            cam = GetComponent<Camera>();
        if (cam == null)
            cam = Camera.main;
        if (cam == null)
            return;

        camPosition = cam.transform.position;
        camRotation = cam.transform.rotation;
        camOrthoSize = cam.orthographic ? cam.orthographicSize : 5f;
        camAspect = cam.aspect > 0.01f ? cam.aspect : 1f;
        bufferWidth = Mathf.Max(1, Screen.width);
        bufferHeight = Mathf.Max(1, Screen.height);

        samples.Clear();
        scratchUi.Clear();
        scratchSprites.Clear();
        scratchClient.Clear();
        var renderers = FindObjectsOfType<SpriteRenderer>();
        for (int i = 0; i < renderers.Length; i++)
        {
            SpriteRenderer renderer = renderers[i];
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                continue;
            if (renderer.sprite == null || renderer.color.a <= clickAlphaThreshold)
                continue;

            var sample = new SpriteSample
            {
                worldToLocal = renderer.transform.worldToLocalMatrix,
                planePoint = renderer.transform.position,
                planeNormal = renderer.transform.forward,
                bounds = renderer.sprite.bounds,
                colorAlpha = renderer.color.a,
                flipX = renderer.flipX,
                flipY = renderer.flipY,
                textureRect = renderer.sprite.textureRect
            };

            Texture2D texture = renderer.sprite.texture;
            if (texture != null && texture.isReadable)
            {
                int id = texture.GetInstanceID();
                if (!pixelCache.TryGetValue(id, out Color32[] pixels) || pixels == null || pixels.Length != texture.width * texture.height)
                {
                    pixels = texture.GetPixels32();
                    pixelCache[id] = pixels;
                }

                sample.readable = true;
                sample.texWidth = texture.width;
                sample.texHeight = texture.height;
                sample.pixels = pixels;
            }

            samples.Add(sample);
            if (TryGetSpriteScreenRect(sample, out Rect spriteRect))
            {
                scratchSprites.Add(spriteRect);
                AddClientRect(spriteRect);
            }
        }

        CollectGraphics();
        sampleSnapshot = samples.ToArray();
        interfaceSnapshot = scratchUi.ToArray();
        spriteScreenSnapshot = scratchSprites.ToArray();
        clientSnapshot = scratchClient.ToArray();
        spritesFullyMapped = scratchSprites.Count == samples.Count;
    }

    void CollectGraphics()
    {
        var graphics = FindObjectsOfType<Graphic>();
        for (int i = 0; i < graphics.Length; i++)
        {
            Graphic graphic = graphics[i];
            if (graphic == null || !graphic.enabled || !graphic.gameObject.activeInHierarchy)
                continue;
            if (graphic.color.a <= 0.04f)
                continue;
            if (!TryGetScreenRect(graphic, out Rect screenRect))
                continue;
            if (screenRect.width >= bufferWidth * 0.92f && screenRect.height >= bufferHeight * 0.92f)
                continue;

            AddClientRect(screenRect);
            if (graphic.raycastTarget && graphic.color.a > clickAlphaThreshold)
                scratchUi.Add(screenRect);
        }
    }

    bool TryGetSpriteScreenRect(SpriteSample sample, out Rect screenRect)
    {
        screenRect = default;
        if (cam == null)
            return false;

        Matrix4x4 localToWorld = sample.worldToLocal.inverse;
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        float z = (sample.bounds.min.z + sample.bounds.max.z) * 0.5f;
        Vector3 min = sample.bounds.min;
        Vector3 max = sample.bounds.max;
        Vector3[] local =
        {
            new Vector3(min.x, min.y, z),
            new Vector3(min.x, max.y, z),
            new Vector3(max.x, min.y, z),
            new Vector3(max.x, max.y, z)
        };

        for (int i = 0; i < 4; i++)
        {
            Vector3 screen = cam.WorldToScreenPoint(localToWorld.MultiplyPoint3x4(local[i]));
            if (screen.z < 0f || float.IsNaN(screen.x) || float.IsNaN(screen.y) || float.IsInfinity(screen.x) || float.IsInfinity(screen.y))
                return false;
            minX = Mathf.Min(minX, screen.x);
            minY = Mathf.Min(minY, screen.y);
            maxX = Mathf.Max(maxX, screen.x);
            maxY = Mathf.Max(maxY, screen.y);
        }

        if (maxX - minX < 1f || maxY - minY < 1f)
            return false;

        screenRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
        return true;
    }

    void AddClientRect(Rect screen)
    {
        if (!GetClientRect(hwnd, out NativeRect area))
            return;

        int clientWidth = area.right - area.left;
        int clientHeight = area.bottom - area.top;
        if (clientWidth <= 1 || clientHeight <= 1 || bufferWidth <= 1 || bufferHeight <= 1)
            return;
        if (float.IsNaN(screen.xMin) || float.IsNaN(screen.yMin) || float.IsInfinity(screen.xMax) || float.IsInfinity(screen.yMax))
            return;

        float x0 = screen.xMin / bufferWidth * clientWidth;
        float x1 = screen.xMax / bufferWidth * clientWidth;
        float y0 = (bufferHeight - screen.yMax) / bufferHeight * clientHeight;
        float y1 = (bufferHeight - screen.yMin) / bufferHeight * clientHeight;
        var client = new ClientRect
        {
            left = Mathf.FloorToInt(Mathf.Min(x0, x1)) - 8,
            right = Mathf.CeilToInt(Mathf.Max(x0, x1)) + 8,
            top = Mathf.FloorToInt(Mathf.Min(y0, y1)) - 8,
            bottom = Mathf.CeilToInt(Mathf.Max(y0, y1)) + 8
        };
        if (client.right <= client.left || client.bottom <= client.top)
            return;
        scratchClient.Add(client);
    }

    bool IsCursorOverVisiblePixel()
    {
        if (!GetCursorPos(out NativePoint cursor))
            return false;
        return IsScreenPixelVisible(cursor.x, cursor.y);
    }

    bool IsCursorOverInterface()
    {
        if (!TryGetCursorScreenPoint(out Vector2 point))
            return false;
        return ContainsPoint(interfaceSnapshot, point);
    }

    bool TryGetCursorScreenPoint(out Vector2 screenPoint)
    {
        screenPoint = default;
        if (hwnd == IntPtr.Zero || !GetCursorPos(out NativePoint cursor))
            return false;
        if (!ScreenToClient(hwnd, ref cursor) || !GetClientRect(hwnd, out NativeRect client))
            return false;

        int clientWidth = Mathf.Max(1, client.right - client.left);
        int clientHeight = Mathf.Max(1, client.bottom - client.top);
        if (cursor.x < 0 || cursor.y < 0 || cursor.x >= clientWidth || cursor.y >= clientHeight)
            return false;

        screenPoint = new Vector2(
            (cursor.x + 0.5f) * bufferWidth / clientWidth,
            (clientHeight - cursor.y - 0.5f) * bufferHeight / clientHeight);
        return true;
    }

    bool IsScreenPixelVisible(int screenX, int screenY)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        var point = new NativePoint { x = screenX, y = screenY };
        if (!ScreenToClient(hwnd, ref point))
            return false;
        if (point.x < 0 || point.y < 0)
            return false;

        float screenPx = (point.x + 0.5f) * bufferWidth / Mathf.Max(1, windowWidth);
        float screenPy = (windowHeight - point.y - 0.5f) * bufferHeight / Mathf.Max(1, windowHeight);
        if (!GetClientRect(hwnd, out NativeRect client))
            return HitWorld(ScreenPointToWorld(screenPx, screenPy));

        int clientWidth = Mathf.Max(1, client.right - client.left);
        int clientHeight = Mathf.Max(1, client.bottom - client.top);
        if (point.x >= clientWidth || point.y >= clientHeight)
            return false;

        screenPx = (point.x + 0.5f) * bufferWidth / clientWidth;
        screenPy = (clientHeight - point.y - 0.5f) * bufferHeight / clientHeight;
        var cursor = new Vector2(screenPx, screenPy);
        if (ContainsPoint(interfaceSnapshot, cursor))
            return true;
        if (!ContainsPoint(spriteScreenSnapshot, cursor))
            return false;
        return HitWorld(ScreenPointToWorld(screenPx, screenPy));
    }

    static bool ContainsPoint(Rect[] rects, Vector2 point)
    {
        for (int i = 0; i < rects.Length; i++)
        {
            if (rects[i].Contains(point))
                return true;
        }

        return false;
    }

    bool TryGetScreenRect(UnityEngine.UI.Graphic graphic, out Rect screenRect)
    {
        screenRect = default;
        if (graphic.rectTransform == null)
            return false;

        graphic.rectTransform.GetWorldCorners(uiCorners);
        Camera eventCamera = null;
        Canvas canvas = graphic.canvas;
        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            eventCamera = canvas.worldCamera != null ? canvas.worldCamera : cam;

        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        for (int i = 0; i < 4; i++)
        {
            Vector3 screen = eventCamera != null ? eventCamera.WorldToScreenPoint(uiCorners[i]) : uiCorners[i];
            if (float.IsNaN(screen.x) || float.IsNaN(screen.y) || float.IsInfinity(screen.x) || float.IsInfinity(screen.y))
                return false;
            if (screen.z < 0f && eventCamera != null)
                return false;
            minX = Mathf.Min(minX, screen.x);
            minY = Mathf.Min(minY, screen.y);
            maxX = Mathf.Max(maxX, screen.x);
            maxY = Mathf.Max(maxY, screen.y);
        }

        if (maxX - minX < 1f || maxY - minY < 1f)
            return false;

        screenRect = Rect.MinMaxRect(minX, minY, maxX, maxY);
        return true;
    }

    Vector3 ScreenPointToWorld(float screenX, float screenY)
    {
        float ndcX = screenX / bufferWidth * 2f - 1f;
        float ndcY = screenY / bufferHeight * 2f - 1f;
        Vector3 right = camRotation * Vector3.right;
        Vector3 up = camRotation * Vector3.up;
        float halfWidth = camOrthoSize * camAspect;
        return camPosition + right * (ndcX * halfWidth) + up * (ndcY * camOrthoSize);
    }

    bool HitWorld(Vector3 worldOnCameraPlane)
    {
        Vector3 direction = camRotation * Vector3.forward;
        for (int i = 0; i < sampleSnapshot.Length; i++)
        {
            SpriteSample sample = sampleSnapshot[i];
            float denom = Vector3.Dot(direction, sample.planeNormal);
            Vector3 world = worldOnCameraPlane;
            if (Mathf.Abs(denom) > 0.0001f)
            {
                float distance = Vector3.Dot(sample.planePoint - worldOnCameraPlane, sample.planeNormal) / denom;
                world = worldOnCameraPlane + direction * distance;
            }

            Vector3 local = sample.worldToLocal.MultiplyPoint3x4(world);
            Bounds bounds = sample.bounds;
            if (float.IsNaN(bounds.min.x) || float.IsNaN(bounds.max.x) || float.IsNaN(bounds.min.y) || float.IsNaN(bounds.max.y))
                continue;
            if (bounds.max.x - bounds.min.x > 40f || bounds.max.y - bounds.min.y > 40f)
                continue;
            if (local.x < bounds.min.x || local.x > bounds.max.x || local.y < bounds.min.y || local.y > bounds.max.y)
                continue;
            if (bounds.size.x <= 0.0001f || bounds.size.y <= 0.0001f)
                continue;

            if (!sample.readable || sample.pixels == null)
                return sample.colorAlpha > clickAlphaThreshold;

            float u = Mathf.InverseLerp(bounds.min.x, bounds.max.x, local.x);
            float v = Mathf.InverseLerp(bounds.min.y, bounds.max.y, local.y);
            if (sample.flipX)
                u = 1f - u;
            if (sample.flipY)
                v = 1f - v;

            float texX = sample.textureRect.x + u * sample.textureRect.width;
            float texY = sample.textureRect.y + v * sample.textureRect.height;
            int ix = Mathf.Clamp(Mathf.FloorToInt(texX), 0, sample.texWidth - 1);
            int iy = Mathf.Clamp(Mathf.FloorToInt(texY), 0, sample.texHeight - 1);
            byte alpha = sample.pixels[iy * sample.texWidth + ix].a;
            if (alpha / 255f * sample.colorAlpha > clickAlphaThreshold)
                return true;
        }

        return false;
    }

    void EnsureHook()
    {
        if (hwnd == IntPtr.Zero)
            return;

        if (procDelegate == null)
        {
            procDelegate = StaticWndProc;
            procPtr = Marshal.GetFunctionPointerForDelegate(procDelegate);
        }

        if (childEnum == null)
            childEnum = OnChildWindow;

        var wanted = new List<IntPtr> { hwnd };
        pendingChildren.Clear();
        EnumChildWindows(hwnd, childEnum, IntPtr.Zero);
        for (int i = 0; i < pendingChildren.Count; i++)
        {
            IntPtr child = pendingChildren[i];
            if (child != IntPtr.Zero && !wanted.Contains(child))
                wanted.Add(child);
        }
        pendingChildren.Clear();

        var stale = new List<IntPtr>();
        foreach (KeyValuePair<IntPtr, IntPtr> pair in prevByHwnd)
        {
            if (!wanted.Contains(pair.Key))
                stale.Add(pair.Key);
        }

        for (int i = 0; i < stale.Count; i++)
            Unhook(stale[i]);
        for (int i = 0; i < wanted.Count; i++)
            HookOne(wanted[i]);

        regionTargets.Clear();
        regionTargets.AddRange(wanted);
    }

    static bool OnChildWindow(IntPtr child, IntPtr unused)
    {
        if (instance == null || child == IntPtr.Zero)
            return true;
        if (!GetClientRect(instance.hwnd, out NativeRect parent) || !GetClientRect(child, out NativeRect childRect))
            return true;

        int parentWidth = parent.right - parent.left;
        int parentHeight = parent.bottom - parent.top;
        int childWidth = childRect.right - childRect.left;
        int childHeight = childRect.bottom - childRect.top;
        if (parentWidth <= 0 || parentHeight <= 0)
            return true;
        if (childWidth * 2 < parentWidth || childHeight * 2 < parentHeight)
            return true;

        instance.pendingChildren.Add(child);
        return true;
    }

    void HookOne(IntPtr target)
    {
        if (target == IntPtr.Zero || procPtr == IntPtr.Zero)
            return;

        IntPtr current = GetWindowLongPtr(target, GwlpWndProc);
        if (current == procPtr || current == IntPtr.Zero)
            return;

        prevByHwnd[target] = current;
        SetWindowLongPtr(target, GwlpWndProc, procPtr);
    }

    void Unhook(IntPtr target)
    {
        if (!prevByHwnd.TryGetValue(target, out IntPtr previous))
            return;

        IntPtr current = GetWindowLongPtr(target, GwlpWndProc);
        if (current == procPtr && previous != IntPtr.Zero)
            SetWindowLongPtr(target, GwlpWndProc, previous);
        prevByHwnd.Remove(target);
        SetWindowRgn(target, IntPtr.Zero, false);
    }

    void RestoreHook()
    {
        var hooked = new List<IntPtr>(prevByHwnd.Keys);
        for (int i = 0; i < hooked.Count; i++)
            Unhook(hooked[i]);
        regionTargets.Clear();
        appliedRegionHash = int.MinValue;
        regionDirty = true;
    }

    void ApplyHitRegion()
    {
        if (hwnd == IntPtr.Zero)
            return;

        int hash = 23 + regionTargets.Count;
        for (int i = 0; i < clientSnapshot.Length; i++)
        {
            ClientRect rect = clientSnapshot[i];
            hash = hash * 31 + rect.left;
            hash = hash * 31 + rect.top;
            hash = hash * 31 + rect.right;
            hash = hash * 31 + rect.bottom;
        }

        if (!regionDirty && hash == appliedRegionHash)
            return;

        bool keepFullWindow = dragging || (sampleSnapshot.Length > 0 && !spritesFullyMapped);
        var targets = regionTargets.Count > 0 ? regionTargets : new List<IntPtr> { hwnd };
        for (int i = 0; i < targets.Count; i++)
        {
            IntPtr target = targets[i];
            if (target == IntPtr.Zero)
                continue;
            if (keepFullWindow)
            {
                SetWindowRgn(target, IntPtr.Zero, false);
                continue;
            }

            IntPtr region = BuildRegionFor(target);
            if (region == IntPtr.Zero)
                continue;
            if (SetWindowRgn(target, region, false) == 0)
                DeleteObject(region);
        }

        appliedRegionHash = hash;
        regionDirty = false;
    }

    IntPtr BuildRegionFor(IntPtr target)
    {
        int dx = 0;
        int dy = 0;
        if (target != hwnd)
        {
            var rootOrigin = new NativePoint();
            var targetOrigin = new NativePoint();
            ClientToScreen(hwnd, ref rootOrigin);
            ClientToScreen(target, ref targetOrigin);
            dx = rootOrigin.x - targetOrigin.x;
            dy = rootOrigin.y - targetOrigin.y;
        }

        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        for (int i = 0; i < clientSnapshot.Length; i++)
        {
            ClientRect rect = clientSnapshot[i];
            IntPtr piece = CreateRectRgn(rect.left + dx, rect.top + dy, rect.right + dx, rect.bottom + dy);
            CombineRgn(region, region, piece, 2);
            DeleteObject(piece);
        }

        return region;
    }

    static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WmWindowPosChanging && instance != null && hWnd == instance.hwnd)
                instance.ClampWindowPos(lParam);

            if (!dragging && msg == WmNcHitTest && instance != null)
            {
                if (!GetCursorPos(out NativePoint cursor) || !instance.IsScreenPixelVisible(cursor.x, cursor.y))
                    return new IntPtr(HtTransparent);
            }
        }
        catch (Exception)
        {
            if (!dragging && msg == WmNcHitTest)
                return new IntPtr(HtTransparent);
        }

        if (!prevByHwnd.TryGetValue(hWnd, out IntPtr previous) || previous == IntPtr.Zero)
            return DefWindowProc(hWnd, msg, wParam, lParam);
        return CallWindowProc(previous, hWnd, msg, wParam, lParam);
    }

    void ClampWindowPos(IntPtr lParam)
    {
        var pos = Marshal.PtrToStructure<WindowPos>(lParam);
        if ((pos.flags & SwpNoSize) != 0 && (pos.flags & SwpNoMove) != 0)
            return;

        IntPtr monitor = MonitorFromWindow(pos.hwnd != IntPtr.Zero ? pos.hwnd : hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf(typeof(MonitorInfo)) };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
            return;

        int x = pos.x;
        int y = pos.y;
        int width = pos.cx;
        int height = pos.cy;
        if ((pos.flags & SwpNoMove) != 0 || (pos.flags & SwpNoSize) != 0)
        {
            if (!GetWindowRect(pos.hwnd != IntPtr.Zero ? pos.hwnd : hwnd, out NativeRect current))
                return;
            if ((pos.flags & SwpNoMove) != 0)
            {
                x = current.left;
                y = current.top;
            }
            if ((pos.flags & SwpNoSize) != 0)
            {
                width = current.right - current.left;
                height = current.bottom - current.top;
            }
        }

        int maxWidth = info.rcMonitor.right - info.rcMonitor.left - 1;
        int maxHeight = info.rcMonitor.bottom - info.rcMonitor.top - 1;
        if (maxWidth < 1 || maxHeight < 1)
            return;

        bool changed = false;
        if (width > maxWidth)
        {
            width = maxWidth;
            changed = true;
        }
        if (height > maxHeight)
        {
            height = maxHeight;
            changed = true;
        }
        if (x <= info.rcMonitor.left && x + width >= info.rcMonitor.right)
        {
            width = Mathf.Min(width, maxWidth);
            changed = true;
        }
        if (y <= info.rcMonitor.top && y + height >= info.rcMonitor.bottom)
        {
            height = Mathf.Min(height, maxHeight);
            changed = true;
        }
        if (!changed)
            return;

        pos.flags &= ~SwpNoSize;
        pos.cx = width;
        pos.cy = height;
        Marshal.StructureToPtr(pos, lParam, false);
    }

    static IntPtr ResolveWindow()
    {
        IntPtr found = FindWindow("UnityWndClass", Application.productName);
        if (found == IntPtr.Zero)
            found = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Margins
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WindowPos
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
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

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint lpPoint);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("dwmapi.dll")]
    static extern uint DwmExtendFrameIntoClientArea(IntPtr hWnd, ref Margins margins);
#endif
}
