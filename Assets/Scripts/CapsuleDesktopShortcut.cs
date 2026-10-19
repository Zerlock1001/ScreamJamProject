using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

/// <summary>
/// 轻点胶囊体时，在当前用户看到的桌面上创建指向本游戏 exe 的快捷方式。
/// </summary>
public class CapsuleDesktopShortcut : MonoBehaviour
{
    static readonly Guid DesktopFolderId = new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");

    [SerializeField] SpriteRenderer capsule;
    [SerializeField] float clickSlopPixels = 18f;
    [SerializeField] string shortcutName = "";

    Camera cam;
    bool pressedCapsule;
    Vector2 pressScreen;
    TextMesh statusText;
    MeshRenderer statusRenderer;

    void Awake()
    {
        if (capsule == null)
            capsule = GetComponent<SpriteRenderer>();
        cam = Camera.main;
        CreateStatus();
    }

    void Update()
    {
        if (cam == null)
            cam = Camera.main;
        if (capsule == null || cam == null)
            return;

        if (Input.GetMouseButtonDown(0))
        {
            pressScreen = PointerScreen();
            pressedCapsule = HitsCapsule(pressScreen);
        }

        if (!Input.GetMouseButtonUp(0))
            return;

        Vector2 releaseScreen = PointerScreen();
        bool wasCapsule = pressedCapsule || HitsCapsule(releaseScreen);
        pressedCapsule = false;
        if (!wasCapsule)
            return;

        float slop = Mathf.Max(1f, clickSlopPixels);
        if ((releaseScreen - pressScreen).sqrMagnitude > slop * slop)
            return;

        CreateDesktopShortcut();
    }

    void CreateDesktopShortcut()
    {
#if UNITY_EDITOR
        ShowStatus("请打包后运行再点击");
        UnityEngine.Debug.LogWarning("桌面快捷方式只在打包后的游戏里创建，编辑器播放不会指向 Unity。");
#else
        try
        {
            string exePath = ResolveExecutablePath();
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                ShowStatus("没找到游戏本体");
                return;
            }

            string desktop = GetDesktopFolder();
            if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop))
            {
                ShowStatus("没找到桌面");
                return;
            }

            string name = string.IsNullOrWhiteSpace(shortcutName) ? Application.productName : shortcutName.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            if (string.IsNullOrEmpty(name))
                name = "Game";

            string lnkPath = Path.Combine(desktop, name + ".lnk");
            WriteShortcutWithScript(lnkPath, exePath);
            if (!File.Exists(lnkPath))
            {
                ShowStatus("快捷方式没有写入");
                return;
            }

            NotifyShell(lnkPath);
            ShowStatus("已创建桌面快捷方式");
            UnityEngine.Debug.Log("已在桌面创建快捷方式：" + lnkPath);
        }
        catch (Exception exception)
        {
            ShowStatus("创建失败");
            UnityEngine.Debug.LogWarning("创建桌面快捷方式失败：" + exception.Message);
        }
#endif
    }

    void CreateStatus()
    {
        var textObject = new GameObject("ShortcutStatus");
        textObject.transform.SetParent(transform, false);
        statusText = textObject.AddComponent<TextMesh>();
        statusText.anchor = TextAnchor.MiddleCenter;
        statusText.alignment = TextAlignment.Center;
        statusText.fontSize = 32;
        statusText.characterSize = 0.08f;
        statusText.color = new Color(0.98f, 0.94f, 0.86f, 1f);
        statusText.text = string.Empty;

        Font font = Font.CreateDynamicFontFromOSFont(
            new[] { "Microsoft YaHei", "微软雅黑", "SimHei", "黑体", "SimSun", "Arial" },
            statusText.fontSize);
        if (font != null)
            statusText.font = font;

        statusRenderer = statusText.GetComponent<MeshRenderer>();
        statusRenderer.sortingOrder = 30;
        if (statusText.font != null)
            statusRenderer.material = statusText.font.material;
        statusRenderer.enabled = false;
    }

    void ShowStatus(string message)
    {
        if (statusText == null)
            return;

        if (statusText.font != null)
            statusText.font.RequestCharactersInTexture(message, statusText.fontSize, FontStyle.Normal);
        statusText.text = message;
        statusRenderer.enabled = true;
        PlaceStatus();
        StartCoroutine(FitStatus());
    }

    System.Collections.IEnumerator FitStatus()
    {
        yield return null;
        if (statusText == null || statusRenderer == null || string.IsNullOrEmpty(statusText.text))
            yield break;

        statusText.transform.localScale = Vector3.one;
        float width = statusRenderer.bounds.size.x;
        float height = statusRenderer.bounds.size.y;
        if (width < 0.001f || height < 0.001f)
            yield break;

        const float maxWidth = 3.1f;
        const float maxHeight = 0.55f;
        float scale = Mathf.Min(maxWidth / width, maxHeight / height);
        statusText.transform.localScale = new Vector3(scale, scale, 1f);
        PlaceStatus();
    }

    void PlaceStatus()
    {
        if (capsule == null || statusText == null)
            return;

        Bounds bounds = capsule.bounds;
        float textHeight = statusRenderer != null ? statusRenderer.bounds.size.y : 0.4f;
        statusText.transform.position = new Vector3(
            bounds.center.x,
            bounds.max.y + 0.2f + textHeight * 0.5f,
            capsule.transform.position.z);
    }

    Vector2 PointerScreen()
    {
#if !UNITY_EDITOR
        if (TryGetUnityScreenPoint(out Vector2 screen))
            return screen;
#endif
        return Input.mousePosition;
    }

    bool HitsCapsule(Vector2 screenPoint)
    {
        if (capsule == null || !capsule.enabled || capsule.sprite == null || cam == null)
            return false;

        float depth = Mathf.Abs(cam.transform.position.z - capsule.transform.position.z);
        Vector3 world = cam.ScreenToWorldPoint(new Vector3(screenPoint.x, screenPoint.y, depth));
        world.z = capsule.bounds.center.z;
        return capsule.bounds.Contains(world);
    }

#if !UNITY_EDITOR
    static string ResolveExecutablePath()
    {
        string dataPath = Application.dataPath;
        const string dataSuffix = "_Data";
        if (dataPath.EndsWith(dataSuffix, StringComparison.OrdinalIgnoreCase))
        {
            string besideData = dataPath.Substring(0, dataPath.Length - dataSuffix.Length) + ".exe";
            if (File.Exists(besideData))
                return besideData;
        }

        try
        {
            string mainModule = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(mainModule) && File.Exists(mainModule))
                return mainModule;
        }
        catch (Exception)
        {
        }

        return null;
    }

    static string GetDesktopFolder()
    {
        Guid folderId = DesktopFolderId;
        if (SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out IntPtr pathPtr) == 0 && pathPtr != IntPtr.Zero)
        {
            string known = Marshal.PtrToStringUni(pathPtr);
            Marshal.FreeCoTaskMem(pathPtr);
            if (!string.IsNullOrEmpty(known))
                return known;
        }

        string directory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(directory))
            return directory;
        return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }

    static void WriteShortcutWithScript(string lnkPath, string targetPath)
    {
        string scriptPath = Path.Combine(Path.GetTempPath(), "pet-shortcut-" + Process.GetCurrentProcess().Id + ".vbs");
        string workDir = Path.GetDirectoryName(targetPath) ?? "";
        string script =
            "Set shell = CreateObject(\"WScript.Shell\")\r\n" +
            "Set sc = shell.CreateShortcut(\"" + Vbs(lnkPath) + "\")\r\n" +
            "sc.TargetPath = \"" + Vbs(targetPath) + "\"\r\n" +
            "sc.WorkingDirectory = \"" + Vbs(workDir) + "\"\r\n" +
            "sc.IconLocation = \"" + Vbs(targetPath) + ",0\"\r\n" +
            "sc.Description = \"" + Vbs(Application.productName) + "\"\r\n" +
            "sc.Save\r\n";

        File.WriteAllText(scriptPath, script, Encoding.Unicode);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "wscript.exe"),
                Arguments = "//B //Nologo \"" + scriptPath + "\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using (Process process = Process.Start(start))
            {
                if (process == null)
                    throw new InvalidOperationException("无法启动 wscript。");
                if (!process.WaitForExit(8000))
                {
                    process.Kill();
                    throw new TimeoutException("创建快捷方式超时。");
                }

                if (process.ExitCode != 0)
                    throw new InvalidOperationException("wscript 退出码 " + process.ExitCode);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(scriptPath))
                    File.Delete(scriptPath);
            }
            catch (Exception)
            {
            }
        }
    }

    static string Vbs(string value)
    {
        return (value ?? string.Empty).Replace("\"", "\"\"");
    }

    static void NotifyShell(string lnkPath)
    {
        IntPtr path = Marshal.StringToHGlobalUni(lnkPath);
        try
        {
            SHChangeNotify(0x00000002, 0x0005, path, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(path);
        }
    }

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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

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
