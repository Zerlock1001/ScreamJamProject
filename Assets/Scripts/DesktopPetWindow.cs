using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.EventSystems;

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
    [SerializeField] int screenMargin = 24;
    [SerializeField] bool showPlaceholder = true;
    [SerializeField] float clickAlphaThreshold = 0.2f;
    [SerializeField] float dragThresholdPixels = 36f;

    const int GwlStyle = -16;
    const int GwlExStyle = -20;
    const int GwlpWndProc = -4;

    const int WsCaption = 0x00C00000;
    const int WsThickFrame = 0x00040000;
    const int WsMinimizeBox = 0x00020000;
    const int WsMaximizeBox = 0x00010000;
    const int WsPopup = unchecked((int)0x80000000);

    const int WsExDlgModalFrame = 0x00000001;
    const int WsExWindowEdge = 0x00000100;
    const int WsExClientEdge = 0x00000200;
    const int WsExStaticEdge = 0x00020000;

    const uint SwpNoSize = 0x0001;
    const uint SwpNoMove = 0x0002;
    const uint SwpFrameChanged = 0x0020;
    const uint SwpShowWindow = 0x0040;

    const int WmNcLButtonDown = 0x00A1;
    const uint WmNcHitTest = 0x0084;
    const int HtTransparent = -1;
    const int HtCaption = 0x2;
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

    static DesktopPetWindow instance;
    static WndProc procDelegate;
    static IntPtr procPtr;
    static IntPtr prevProc;
    static bool dragging;

    IntPtr hwnd;
    bool armDrag;
    Vector2 dragOrigin;
    bool ready;
    Vector3 camPosition;
    Quaternion camRotation;
    float camOrthoSize;
    float camAspect = 1f;
    int bufferWidth = 1;
    int bufferHeight = 1;
    readonly List<SpriteSample> samples = new List<SpriteSample>();
    readonly Dictionary<int, Color32[]> pixelCache = new Dictionary<int, Color32[]>();
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
            Application.Quit();
            return;
        }

        ConfigureCamera();
        CacheHitData();
        EnsureHook();

        bool overVisible = IsCursorOverVisiblePixel() && !IsPointerOverUi();
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
                dragging = true;
                ReleaseCapture();
                SendMessage(hwnd, WmNcLButtonDown, HtCaption, 0);
                dragging = false;
            }
        }

        if (Input.GetMouseButtonUp(0))
            armDrag = false;
#endif
    }

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

        for (int i = 0; i < 5; i++)
        {
            yield return null;
            IntPtr current = ResolveWindow();
            if (current != IntPtr.Zero && current != hwnd)
            {
                RestoreHook();
                hwnd = current;
            }

            ApplyBorderless(placeOnScreen: false);
            EnsureHook();
        }
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
        exStyle &= ~(WsExDlgModalFrame | WsExWindowEdge | WsExClientEdge | WsExStaticEdge);
        SetWindowLong(hwnd, GwlExStyle, exStyle);

        var margins = new Margins
        {
            cxLeftWidth = -1,
            cxRightWidth = -1,
            cyTopHeight = -1,
            cyBottomHeight = -1
        };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        IntPtr zOrder = alwaysOnTop ? HwndTopMost : HwndNoTopMost;
        if (!placeOnScreen)
        {
            SetWindowPos(hwnd, zOrder, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpFrameChanged | SwpShowWindow);
            return;
        }

        int x = screenMargin;
        int y = screenMargin;
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf(typeof(MonitorInfo)) };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            x = info.rcWork.right - windowWidth - screenMargin;
            y = info.rcWork.bottom - windowHeight - screenMargin;
        }

        SetWindowPos(hwnd, zOrder, x, y, windowWidth, windowHeight, SwpFrameChanged | SwpShowWindow);
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
        }
    }

    bool IsCursorOverVisiblePixel()
    {
        if (!GetCursorPos(out NativePoint cursor))
            return false;
        return IsScreenPixelVisible(cursor.x, cursor.y);
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
        return HitWorld(ScreenPointToWorld(screenPx, screenPy));
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
        for (int i = 0; i < samples.Count; i++)
        {
            SpriteSample sample = samples[i];
            float denom = Vector3.Dot(direction, sample.planeNormal);
            Vector3 world = worldOnCameraPlane;
            if (Mathf.Abs(denom) > 0.0001f)
            {
                float distance = Vector3.Dot(sample.planePoint - worldOnCameraPlane, sample.planeNormal) / denom;
                world = worldOnCameraPlane + direction * distance;
            }

            Vector3 local = sample.worldToLocal.MultiplyPoint3x4(world);
            Bounds bounds = sample.bounds;
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

        IntPtr current = GetWindowLongPtr(hwnd, GwlpWndProc);
        if (current == procPtr || current == IntPtr.Zero)
            return;

        prevProc = current;
        SetWindowLongPtr(hwnd, GwlpWndProc, procPtr);
    }

    void RestoreHook()
    {
        if (hwnd == IntPtr.Zero || procPtr == IntPtr.Zero || prevProc == IntPtr.Zero)
            return;

        IntPtr current = GetWindowLongPtr(hwnd, GwlpWndProc);
        if (current == procPtr)
            SetWindowLongPtr(hwnd, GwlpWndProc, prevProc);
        prevProc = IntPtr.Zero;
    }

    static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (!dragging && msg == WmNcHitTest && instance != null)
            {
                long packed = lParam.ToInt64();
                int screenX = unchecked((short)(packed & 0xFFFF));
                int screenY = unchecked((short)((packed >> 16) & 0xFFFF));
                if (!instance.IsScreenPixelVisible(screenX, screenY))
                    return new IntPtr(HtTransparent);
            }
        }
        catch (Exception)
        {
        }

        if (prevProc == IntPtr.Zero)
            return DefWindowProc(hWnd, msg, wParam, lParam);
        return CallWindowProc(prevProc, hWnd, msg, wParam, lParam);
    }

    static IntPtr ResolveWindow()
    {
        IntPtr found = FindWindow("UnityWndClass", Application.productName);
        if (found == IntPtr.Zero)
            found = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
        return found;
    }

    static bool IsPointerOverUi()
    {
        return EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
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
    static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("dwmapi.dll")]
    static extern uint DwmExtendFrameIntoClientArea(IntPtr hWnd, ref Margins margins);
#endif
}
