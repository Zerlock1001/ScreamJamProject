using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ScreamJam.Debugging
{
    /// <summary>
    /// Lightweight in-game log console. Press `~` to toggle.
    /// It bootstraps itself at runtime, so no scene setup is required.
    /// </summary>
    public sealed class DebugConsole : MonoBehaviour
    {
        private const int MaxEntries = 500;
        private const int MaxDrainPerFrame = 64;
        private const int MaxMessageLength = 8000;
        private const string InputControlName = "DebugConsoleInput";

        private const int CameraRequestedWidth = 1280;
        private const int CameraRequestedHeight = 720;
        private const int CameraRequestedFps = 30;
        private const float CameraStartTimeoutSeconds = 8f;
        private const float CameraFrameTimeoutSeconds = 3f;

        /// <summary>Default delay between starting the camera and capturing, in seconds.</summary>
        public static float CameraCaptureDelaySeconds = 2f;

        private static DebugConsole _instance;
        private static readonly ConcurrentQueue<LogEntry> Pending = new ConcurrentQueue<LogEntry>();
        private static readonly Dictionary<string, ConsoleCommand> Commands =
            new Dictionary<string, ConsoleCommand>(StringComparer.OrdinalIgnoreCase);

        private readonly List<LogEntry> _entries = new List<LogEntry>(MaxEntries);

        private bool _open;
        private Vector2 _scroll;
        private bool _autoScroll = true;
        private bool _showStackTraces;
        private bool _scrollToBottom;
        private string _input = string.Empty;
        private bool _focusInput;
        private int _openFrame;

        private bool _cameraBusy;
        private WebCamTexture _cameraTexture;
        private Texture2D _photoTexture;
        private bool _showPhoto;

        private bool _previousCursorVisible;
        private CursorLockMode _previousCursorLock;

        private Font _font;
        private GUIStyle _logStyle;
        private GUIStyle _stackStyle;
        private GUIStyle _titleStyle;
        private GUIStyle _hintStyle;
        private GUIStyle _inputStyle;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;

            var go = new GameObject("[DebugConsole]");
            go.hideFlags = HideFlags.DontSave;
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<DebugConsole>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            Application.logMessageReceivedThreaded += OnLogMessage;
            _font = CreateFont();

            RegisterCommand("help", "/help", "List all available commands.", _ => PrintHelp());
            RegisterCommand("camera", "/camera [seconds]", "Open the camera, capture a photo after a delay, then close it.", BeginCameraCapture);
            RegisterCommand("photo", "/photo", "Show the last photo taken with /camera.", _ => ShowLastPhoto());
        }

        private void OnDestroy()
        {
            Application.logMessageReceivedThreaded -= OnLogMessage;

            if (_instance == this)
                _instance = null;

            if (_open)
            {
                Cursor.visible = _previousCursorVisible;
                Cursor.lockState = _previousCursorLock;
            }

            StopCamera();

            if (_photoTexture != null)
            {
                Destroy(_photoTexture);
                _photoTexture = null;
            }

            if (_font != null)
            {
                Destroy(_font);
                _font = null;
            }
        }

        private void Update()
        {
            // KeyCode.BackQuote is the `~` key on a US keyboard layout.
            if (Input.GetKeyDown(KeyCode.BackQuote))
                SetOpen(!_open);

            if (_open)
                HandleConsoleKeys();

            DrainPending();
        }

        private void HandleConsoleKeys()
        {
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            if (ctrl && Input.GetKeyDown(KeyCode.L))
                Clear();

            if (ctrl && Input.GetKeyDown(KeyCode.C))
                CopyAll();

            // Keyboard scrolling keeps the console usable when the pet window
            // click-through logic is active in a standalone build.
            if (Input.GetKeyDown(KeyCode.PageDown))
            {
                _autoScroll = false;
                _scroll.y += 240f;
            }

            if (Input.GetKeyDown(KeyCode.PageUp))
            {
                _autoScroll = false;
                _scroll.y -= 240f;
            }

            if (Input.GetKeyDown(KeyCode.End))
            {
                _autoScroll = true;
                _scrollToBottom = true;
            }

            if (Input.GetKeyDown(KeyCode.Home))
            {
                _autoScroll = false;
                _scroll.y = 0f;
            }
        }

        private void SetOpen(bool open)
        {
            if (_open == open) return;

            _open = open;

            if (_open)
            {
                _previousCursorVisible = Cursor.visible;
                _previousCursorLock = Cursor.lockState;
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                _scrollToBottom = true;
                _input = string.Empty;
                _focusInput = true;
                _openFrame = Time.frameCount;
            }
            else
            {
                Cursor.visible = _previousCursorVisible;
                Cursor.lockState = _previousCursorLock;
                _input = string.Empty;
                _focusInput = false;
                _showPhoto = false;
            }
        }

        private void DrainPending()
        {
            int drained = 0;
            while (drained < MaxDrainPerFrame && Pending.TryDequeue(out var entry))
            {
                _entries.Add(entry);
                drained++;
            }

            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);

            if (drained > 0 && _open && _autoScroll)
                _scrollToBottom = true;
        }

        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (condition == null)
                condition = string.Empty;

            if (condition.Length > MaxMessageLength)
                condition = condition.Substring(0, MaxMessageLength) + "\n... (truncated)";

            if (stackTrace != null && stackTrace.Length > MaxMessageLength)
                stackTrace = stackTrace.Substring(0, MaxMessageLength) + "\n... (truncated)";

            Pending.Enqueue(new LogEntry
            {
                Time = DateTime.Now,
                Message = condition,
                StackTrace = stackTrace,
                Type = type
            });
        }

        private void OnGUI()
        {
            if (!_open) return;

            // Handle Enter before the text field consumes the key event.
            if (Event.current.type == EventType.KeyDown &&
                (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
            {
                SubmitInput();
                Event.current.Use();
                return;
            }

            EnsureStyles();

            int previousDepth = GUI.depth;
            Color previousContentColor = GUI.contentColor;
            GUI.contentColor = Color.white;
            GUI.depth = -1000;

            float panelHeight = Mathf.Min(Screen.height * 0.6f, 720f);

            Color previousColor = GUI.color;
            GUI.color = new Color(0.04f, 0.04f, 0.06f, 0.93f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, panelHeight), Texture2D.whiteTexture);
            GUI.color = previousColor;

            GUILayout.BeginArea(new Rect(8f, 6f, Screen.width - 16f, panelHeight - 12f));

            GUILayout.BeginHorizontal();

            GUILayout.Label("Debug Console (" + _entries.Count + ")", _titleStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label("~ to close", _hintStyle, GUILayout.Width(80f));

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Clear", GUILayout.Width(55f), GUILayout.Height(20f)))
                Clear();

            if (GUILayout.Button("Copy", GUILayout.Width(55f), GUILayout.Height(20f)))
                CopyAll();

            _autoScroll = GUILayout.Toggle(_autoScroll, "Auto-scroll", GUILayout.Width(90f));
            _showStackTraces = GUILayout.Toggle(_showStackTraces, "Stack", GUILayout.Width(60f));

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();

            GUILayout.Label(">", _titleStyle, GUILayout.Width(12f));
            GUI.SetNextControlName(InputControlName);
            _input = GUILayout.TextField(_input, _inputStyle, GUILayout.Height(20f));

            if (_focusInput && Time.frameCount > _openFrame)
            {
                GUI.FocusControl(InputControlName);
                _focusInput = false;
            }

            if (GUILayout.Button("Run", GUILayout.Width(45f), GUILayout.Height(20f)))
                SubmitInput();

            GUILayout.Label("/help", _hintStyle, GUILayout.Width(40f));

            GUILayout.EndHorizontal();

            if (_showPhoto && _photoTexture != null)
                DrawPhotoPreview();

            if (_scrollToBottom)
            {
                _scroll.y = float.MaxValue;
                _scrollToBottom = false;
            }

            _scroll = GUILayout.BeginScrollView(
                _scroll, false, true, GUILayout.ExpandHeight(true));

            for (int i = 0; i < _entries.Count; i++)
            {
                LogEntry entry = _entries[i];

                GUI.contentColor = ColorFor(entry);
                GUILayout.Label(
                    entry.Time.ToString("HH:mm:ss.fff") + " [" + TagFor(entry) + "] " + entry.Message,
                    _logStyle);
                GUI.contentColor = Color.white;

                if (_showStackTraces && !string.IsNullOrEmpty(entry.StackTrace))
                    GUILayout.Label(entry.StackTrace, _stackStyle);
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();

            GUI.contentColor = previousContentColor;
            GUI.depth = previousDepth;
        }

        private void Clear()
        {
            _entries.Clear();
            while (Pending.TryDequeue(out _)) { }
            _scroll = Vector2.zero;
        }

        private void CopyAll()
        {
            var sb = new StringBuilder();

            for (int i = 0; i < _entries.Count; i++)
            {
                LogEntry entry = _entries[i];

                sb.Append(entry.Time.ToString("HH:mm:ss.fff"))
                  .Append(" [").Append(TagFor(entry)).Append("] ")
                  .Append(entry.Message)
                  .Append('\n');

                if (_showStackTraces && !string.IsNullOrEmpty(entry.StackTrace))
                    sb.Append(entry.StackTrace).Append('\n');
            }

            GUIUtility.systemCopyBuffer = sb.ToString();
        }

        private void SubmitInput()
        {
            string raw = _input.Trim();
            _input = string.Empty;
            _focusInput = true;

            if (raw.Length == 0)
                return;

            ExecuteCommand(raw);
        }

        private void ExecuteCommand(string raw)
        {
            AddCommandLine("> " + raw);

            string[] parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            string name = NormalizeCommandName(parts[0]);

            if (!Commands.TryGetValue(name, out ConsoleCommand command))
            {
                AddCommandLine("Unknown command: " + parts[0] + ". Type /help for a list of commands.", LogType.Warning);
                return;
            }

            string[] args = new string[parts.Length - 1];
            Array.Copy(parts, 1, args, 0, args.Length);

            try
            {
                command.Handler?.Invoke(args);
            }
            catch (Exception ex)
            {
                AddCommandLine("Command failed: " + ex.Message, LogType.Exception);
            }
        }

        /// <summary>
        /// Registers a console command. Names may be written with or without a leading '/'.
        /// Other scripts can call this from Awake/Start.
        /// </summary>
        public static void RegisterCommand(string name, string usage, string description, Action<string[]> handler)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Command name cannot be empty.", nameof(name));

            string key = NormalizeCommandName(name);
            if (key.Length == 0)
                throw new ArgumentException("Command name cannot be empty.", nameof(name));

            Commands[key] = new ConsoleCommand
            {
                Name = key,
                Usage = string.IsNullOrEmpty(usage) ? "/" + key : usage,
                Description = description ?? string.Empty,
                Handler = handler
            };
        }

        private void PrintHelp()
        {
            AddCommandLine("Available commands (" + Commands.Count + "):");

            var names = new List<string>(Commands.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < names.Count; i++)
            {
                ConsoleCommand command = Commands[names[i]];
                AddCommandLine("  " + command.Usage + "  -  " + command.Description);
            }

            AddCommandLine("Press Enter to run a command. Commands are case-insensitive.");
        }

        private void AddCommandLine(string message, LogType type = LogType.Log)
        {
            AddEntry(new LogEntry
            {
                Time = DateTime.Now,
                Message = message,
                StackTrace = null,
                Type = type,
                IsCommand = true
            });
        }

        private void AddEntry(LogEntry entry)
        {
            _entries.Add(entry);

            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);

            if (_open && _autoScroll)
                _scrollToBottom = true;
        }

        private static string NormalizeCommandName(string name)
        {
            name = name.Trim();

            while (name.StartsWith("/", StringComparison.Ordinal))
                name = name.Substring(1).TrimStart();

            return name.ToLowerInvariant();
        }

        private void BeginCameraCapture(string[] args)
        {
            if (_cameraBusy)
            {
                AddCommandLine("Camera capture is already in progress.", LogType.Warning);
                return;
            }

            float delay = Mathf.Clamp(CameraCaptureDelaySeconds, 0f, 60f);

            if (args.Length > 0)
            {
                if (float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) &&
                    !float.IsNaN(parsed) && !float.IsInfinity(parsed))
                    delay = Mathf.Clamp(parsed, 0f, 60f);
                else
                    AddCommandLine("Invalid delay. Using " + delay.ToString("0.##", CultureInfo.InvariantCulture) + "s.", LogType.Warning);
            }

            _cameraBusy = true;

            try
            {
                StartCoroutine(CameraCaptureRoutine(delay));
            }
            catch (Exception ex)
            {
                _cameraBusy = false;
                AddCommandLine("Failed to start capture: " + ex.Message, LogType.Error);
            }
        }

        private IEnumerator CameraCaptureRoutine(float delay)
        {
            AddCommandLine("Opening camera...");

            try
            {
                string device = PickCameraDevice();
                if (string.IsNullOrEmpty(device))
                {
                    AddCommandLine("No camera found.", LogType.Error);
                    yield break;
                }

                _cameraTexture = CreateCamera(device);
                if (_cameraTexture == null)
                {
                    AddCommandLine("Failed to create camera.", LogType.Error);
                    yield break;
                }

                if (!TryPlayCamera(_cameraTexture, out string playError))
                {
                    AddCommandLine("Failed to start camera: " + playError, LogType.Error);
                    yield break;
                }

                AddCommandLine("Camera on. Capturing in " + delay.ToString("0.##", CultureInfo.InvariantCulture) + "s...");

                float captureTime = Time.realtimeSinceStartup + delay;
                while (Time.realtimeSinceStartup < captureTime)
                    yield return null;

                float readyDeadline = Time.realtimeSinceStartup + CameraStartTimeoutSeconds;
                while (_cameraTexture.width <= 16 || !_cameraTexture.didUpdateThisFrame)
                {
                    if (Time.realtimeSinceStartup > readyDeadline)
                    {
                        AddCommandLine("Camera failed to start (timeout).", LogType.Error);
                        yield break;
                    }

                    yield return null;
                }

                bool freshFrame = false;
                float frameDeadline = Time.realtimeSinceStartup + CameraFrameTimeoutSeconds;
                while (Time.realtimeSinceStartup < frameDeadline)
                {
                    if (_cameraTexture.didUpdateThisFrame)
                    {
                        freshFrame = true;
                        break;
                    }

                    yield return null;
                }

                if (!freshFrame)
                {
                    AddCommandLine("Camera stopped producing frames.", LogType.Error);
                    yield break;
                }

                Texture2D photo = GrabFrame(_cameraTexture);
                if (photo == null)
                {
                    AddCommandLine("Failed to capture photo.", LogType.Error);
                    yield break;
                }

                byte[] png = EncodePhoto(photo);
                Destroy(photo);

                if (png == null || png.Length == 0)
                {
                    AddCommandLine("Failed to encode photo.", LogType.Error);
                    yield break;
                }

                if (TrySavePhoto(png, out string saveError))
                    AddCommandLine("Photo captured and saved.");
                else
                    AddCommandLine("Failed to save photo: " + saveError, LogType.Error);
            }
            finally
            {
                StopCamera();
                _cameraBusy = false;
            }
        }

        private void StopCamera()
        {
            if (_cameraTexture == null)
                return;

            try
            {
                _cameraTexture.Stop();
            }
            catch
            {
                // Ignore shutdown errors from a removed/busy device.
            }

            Destroy(_cameraTexture);
            _cameraTexture = null;
        }

        private static byte[] EncodePhoto(Texture2D texture)
        {
            try
            {
                return texture.EncodeToPNG();
            }
            catch
            {
                return null;
            }
        }

        private void ShowLastPhoto()
        {
            if (!File.Exists(PhotoPath))
            {
                _showPhoto = false;
                AddCommandLine("No photo found. Run /camera first.", LogType.Warning);
                return;
            }

            byte[] bytes;

            try
            {
                bytes = File.ReadAllBytes(PhotoPath);
            }
            catch (Exception ex)
            {
                _showPhoto = false;
                AddCommandLine("Failed to read photo: " + ex.Message, LogType.Error);
                return;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
            bool loaded;

            try
            {
                loaded = texture.LoadImage(bytes);
            }
            catch
            {
                loaded = false;
            }

            if (!loaded)
            {
                Destroy(texture);
                _showPhoto = false;
                AddCommandLine("Failed to load photo.", LogType.Error);
                return;
            }

            if (_photoTexture != null)
                Destroy(_photoTexture);

            _photoTexture = texture;
            _showPhoto = true;
            AddCommandLine("Showing last photo.");
        }

        private void DrawPhotoPreview()
        {
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Last photo", _titleStyle);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Hide", GUILayout.Width(50f), GUILayout.Height(20f)))
                _showPhoto = false;

            GUILayout.EndHorizontal();

            float maxWidth = Mathf.Max(64f, Screen.width - 48f);
            float maxHeight = Mathf.Max(48f, Mathf.Min(Screen.height * 0.28f, 320f));
            float aspect = (float)_photoTexture.width / Mathf.Max(1, _photoTexture.height);
            float width = maxWidth;
            float height = width / Mathf.Max(0.0001f, aspect);

            if (height > maxHeight)
            {
                height = maxHeight;
                width = height * aspect;
            }

            Rect photoRect = GUILayoutUtility.GetRect(width, height, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(photoRect, _photoTexture, ScaleMode.ScaleToFit, true);

            GUILayout.EndVertical();
        }

        private static string PickCameraDevice()
        {
            WebCamDevice[] devices = WebCamTexture.devices;
            if (devices == null || devices.Length == 0)
                return null;

            for (int i = 0; i < devices.Length; i++)
            {
                string deviceName = devices[i].name;
                if (string.IsNullOrEmpty(deviceName))
                    continue;

                if (deviceName.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                return deviceName;
            }

            return devices[0].name;
        }

        private static WebCamTexture CreateCamera(string deviceName)
        {
            try
            {
                return new WebCamTexture(
                    deviceName,
                    CameraRequestedWidth,
                    CameraRequestedHeight,
                    CameraRequestedFps);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryPlayCamera(WebCamTexture camera, out string error)
        {
            try
            {
                camera.Play();
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static Texture2D GrabFrame(WebCamTexture source)
        {
            int width = source.width;
            int height = source.height;

            if (width <= 0 || height <= 0)
                return null;

            var pixels = new Color32[width * height];

            try
            {
                source.GetPixels32(pixels);
            }
            catch
            {
                return null;
            }

            if (source.videoVerticallyMirrored)
                FlipVertically(pixels, width, height);

            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static void FlipVertically(Color32[] pixels, int width, int height)
        {
            for (int y = 0; y < height / 2; y++)
            {
                int top = y * width;
                int bottom = (height - 1 - y) * width;

                for (int x = 0; x < width; x++)
                {
                    Color32 temp = pixels[top + x];
                    pixels[top + x] = pixels[bottom + x];
                    pixels[bottom + x] = temp;
                }
            }
        }

        private static bool TrySavePhoto(byte[] png, out string error)
        {
            try
            {
                Directory.CreateDirectory(PhotoDirectory);
                File.WriteAllBytes(PhotoPath, png);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string PhotoDirectory
        {
            get { return Path.Combine(Application.persistentDataPath, "Photos"); }
        }

        private static string PhotoPath
        {
            get { return Path.Combine(PhotoDirectory, "last_photo.png"); }
        }

        private void EnsureStyles()
        {
            if (_logStyle != null) return;

            _logStyle = new GUIStyle(GUI.skin.label)
            {
                wordWrap = true,
                richText = false,
                fontSize = 12,
                padding = new RectOffset(2, 2, 1, 1)
            };
            _logStyle.normal.textColor = Color.white;

            _stackStyle = new GUIStyle(_logStyle)
            {
                fontSize = 11,
                padding = new RectOffset(14, 2, 0, 3)
            };
            _stackStyle.normal.textColor = new Color(0.62f, 0.62f, 0.66f, 1f);

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 13
            };
            _titleStyle.normal.textColor = new Color(0.85f, 0.88f, 0.95f, 1f);

            _hintStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleRight,
                fontSize = 11
            };
            _hintStyle.normal.textColor = new Color(0.6f, 0.6f, 0.65f, 1f);

            _inputStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 12,
                padding = new RectOffset(4, 4, 1, 1)
            };

            if (_font != null)
            {
                _logStyle.font = _font;
                _stackStyle.font = _font;
                _titleStyle.font = _font;
                _hintStyle.font = _font;
                _inputStyle.font = _font;
            }
        }

        private static Font CreateFont()
        {
            try
            {
                return Font.CreateDynamicFontFromOSFont(
                    new[]
                    {
                        "Microsoft YaHei UI",
                        "Microsoft YaHei",
                        "SimHei",
                        "PingFang SC",
                        "Noto Sans CJK SC",
                        "Arial"
                    },
                    13);
            }
            catch
            {
                return null;
            }
        }

        private static Color ColorFor(LogEntry entry)
        {
            switch (entry.Type)
            {
                case LogType.Error:
                case LogType.Assert:
                case LogType.Exception:
                    return new Color(1f, 0.42f, 0.42f, 1f);

                case LogType.Warning:
                    return new Color(1f, 0.85f, 0.35f, 1f);
            }

            if (entry.IsCommand)
                return new Color(0.55f, 0.85f, 1f, 1f);

            return new Color(0.86f, 0.88f, 0.9f, 1f);
        }

        private static string TagFor(LogEntry entry)
        {
            switch (entry.Type)
            {
                case LogType.Error: return "ERROR";
                case LogType.Assert: return "ASSERT";
                case LogType.Warning: return "WARN";
                case LogType.Exception: return "EXCEPTION";
            }

            return entry.IsCommand ? "CMD" : "LOG";
        }

        private sealed class LogEntry
        {
            public DateTime Time;
            public string Message;
            public string StackTrace;
            public LogType Type;
            public bool IsCommand;
        }

        private sealed class ConsoleCommand
        {
            public string Name;
            public string Usage;
            public string Description;
            public Action<string[]> Handler;
        }
    }
}
