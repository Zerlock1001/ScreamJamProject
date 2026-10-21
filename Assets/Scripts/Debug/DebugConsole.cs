using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ScreamJam.Audio;
using ScreamJam.Utility;
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
        private const int MotionGridWidth = 32;
        private const int MotionGridHeight = 18;
        private const float CameraFrameWaitTimeoutSeconds = 1f;

        /// <summary>Default delay between starting the camera and capturing, in seconds.</summary>
        public static float CameraCaptureDelaySeconds = 2f;

        /// <summary>Warm-up time after the first valid camera frame before taking the reference sample.</summary>
        public static float CameraMotionWarmupSeconds = 1f;

        /// <summary>Delay between the reference sample and the comparison sample, in seconds.</summary>
        public static float CameraMotionCheckDelaySeconds = 0.3f;

        /// <summary>Mean absolute luma difference (0-255) required to consider the camera live.</summary>
        public static float CameraMotionThreshold = 0.5f;

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
        private int _lastToggleFrame = -1;
        private float _lastToggleTime = -1f;

        private bool _cameraBusy;
        private WebCamTexture _cameraTexture;
        private Color32[] _cameraFrameBuffer;
        private float[] _cameraMotionBaseline;
        private bool _showMicLevel;
        private MicrophoneLevelService.MicStartPhase _lastMicStartPhase = MicrophoneLevelService.MicStartPhase.Idle;

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
            RegisterCommand("camera", "/camera [seconds]", "Open the camera, verify it produces changing frames, capture a photo after a delay, then close it.", BeginCameraCapture);
            RegisterCommand("photo", "/photo", "Show the last photo taken with /camera.", _ => ShowLastPhoto());
            RegisterCommand("miclevel", "/miclevel", "Toggle the live microphone level display.", _ => ToggleMicLevelDisplay());
            RegisterCommand("micstart", "/micstart [stop]", "Record a 15s baseline, then detect a sustained loud level.", BeginMicStart);
            RegisterCommand("clipboard", "/clipboard", "Print the current clipboard text.", _ => PrintClipboard());
            RegisterCommand("screenshot", "/screenshot [show]", "Capture the full desktop, or show the last screenshot.", HandleScreenshot);
            RegisterCommand("record", "/record [play]", "Record 10s of microphone audio, or play the last recording.", HandleRecord);
            RegisterCommand("tempclear", "/tempclear", "Delete cached photos, screenshots, and recordings.", _ => ClearCachedFiles());
            RegisterCommand("quit", "/quit", "Quit the game.", _ => QuitGame());
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
            DestroyAllImageEntries();

            if (_font != null)
            {
                Destroy(_font);
                _font = null;
            }
        }

        private void Update()
        {
            // KeyCode.BackQuote is the `~` key on a US keyboard layout.
            // OnGUI also checks this key, because a focused text field can
            // swallow the event in some situations. ToggleConsole de-dupes
            // the two paths so one key press always means one toggle.
            if (Input.GetKeyDown(KeyCode.BackQuote))
                ToggleConsole();

            if (_open)
                HandleConsoleKeys();

            CheckMicStartVerdict();
            DrainPending();
        }

        private void ToggleConsole()
        {
            float now = Time.unscaledTime;

            if (_lastToggleFrame == Time.frameCount || now - _lastToggleTime < 0.15f)
                return;

            _lastToggleFrame = Time.frameCount;
            _lastToggleTime = now;
            SetOpen(!_open);
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
            {
                int excess = _entries.Count - MaxEntries;
                for (int i = 0; i < excess; i++)
                    DestroyImage(_entries[i]);
                _entries.RemoveRange(0, excess);
            }

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
            // Catch `~` before the command text field can consume it.
            if (Event.current.type == EventType.KeyDown &&
                Event.current.keyCode == KeyCode.BackQuote)
            {
                ToggleConsole();
                Event.current.Use();
                return;
            }

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

            if (_showMicLevel)
                DrawMicLevel();

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

                if (entry.Texture != null)
                    DrawInlineImage(entry.Texture);

                if (_showStackTraces && !string.IsNullOrEmpty(entry.StackTrace))
                    GUILayout.Label(entry.StackTrace, _stackStyle);
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();

            GUI.contentColor = previousContentColor;
            GUI.depth = previousDepth;
        }

        private void DrawInlineImage(Texture2D texture)
        {
            float maxWidth = Mathf.Max(64f, Screen.width - 48f);
            float maxHeight = Mathf.Max(64f, Mathf.Min(Screen.height * 0.30f, 360f));
            float aspect = (float)texture.width / Mathf.Max(1, texture.height);
            float width = maxWidth;
            float height = width / Mathf.Max(0.0001f, aspect);

            if (height > maxHeight)
            {
                height = maxHeight;
                width = height * aspect;
            }

            Rect rect = GUILayoutUtility.GetRect(width, height, GUILayout.ExpandWidth(false));
            Color previous = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, true);
            GUI.color = previous;
        }

        private void Clear()
        {
            DestroyAllImageEntries();
            _entries.Clear();
            while (Pending.TryDequeue(out _)) { }
            _scroll = Vector2.zero;
        }

        private void DestroyAllImageEntries()
        {
            for (int i = 0; i < _entries.Count; i++)
                DestroyImage(_entries[i]);
        }

        private void RemoveImageEntries()
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Texture == null)
                    continue;

                DestroyImage(_entries[i]);
                _entries.RemoveAt(i);
            }
        }

        private void DestroyImage(LogEntry entry)
        {
            if (entry.Texture == null)
                return;

            Destroy(entry.Texture);
            entry.Texture = null;
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

        public static void WriteLine(string message)
        {
            if (_instance == null || string.IsNullOrEmpty(message))
                return;

            _instance.AddCommandLine(message);
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

        private void PrintClipboard()
        {
            string text;

            try
            {
                text = GUIUtility.systemCopyBuffer;
            }
            catch (Exception ex)
            {
                AddCommandLine("Failed to read clipboard: " + ex.Message, LogType.Error);
                return;
            }

            if (string.IsNullOrEmpty(text))
            {
                AddCommandLine("Clipboard is empty.", LogType.Warning);
                return;
            }

            int length = text.Length;
            const int maxLength = 8000;

            if (text.Length > maxLength)
            {
                text = text.Substring(0, maxLength) +
                       "\n... (truncated, " + length + " chars total)";
            }

            AddCommandLine("Clipboard (" + length + " chars):");
            AddCommandLine(text);
        }

        private void CheckMicStartVerdict()
        {
            MicrophoneLevelService service = MicrophoneLevelService.Instance;
            if (service == null)
                return;

            MicrophoneLevelService.MicStartPhase phase = service.CurrentMicStartPhase;
            if (phase == _lastMicStartPhase)
                return;

            _lastMicStartPhase = phase;

            if (phase == MicrophoneLevelService.MicStartPhase.Loud)
            {
                AddCommandLine(
                    "MICSTART verdict: LOUD (current " +
                    service.RmsDb.ToString("0.0", CultureInfo.InvariantCulture) +
                    " dBFS, baseline " +
                    service.MicStartBaselineDb.ToString("0.0", CultureInfo.InvariantCulture) +
                    " dBFS).");
            }
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
            {
                int excess = _entries.Count - MaxEntries;
                for (int i = 0; i < excess; i++)
                    DestroyImage(_entries[i]);
                _entries.RemoveRange(0, excess);
            }

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

                int width = _cameraTexture.width;
                int height = _cameraTexture.height;

                if (_cameraFrameBuffer == null || _cameraFrameBuffer.Length != width * height)
                    _cameraFrameBuffer = new Color32[width * height];

                float warmup = Mathf.Clamp(CameraMotionWarmupSeconds, 0f, 10f);
                float warmupEnd = Time.realtimeSinceStartup + warmup;
                while (Time.realtimeSinceStartup < warmupEnd)
                    yield return null;

                bool gotReferenceFrame = false;
                float referenceDeadline = Time.realtimeSinceStartup + CameraFrameWaitTimeoutSeconds;
                while (Time.realtimeSinceStartup < referenceDeadline)
                {
                    if (_cameraTexture.didUpdateThisFrame)
                    {
                        gotReferenceFrame = true;
                        break;
                    }

                    yield return null;
                }

                if (!gotReferenceFrame)
                {
                    AddCommandLine(
                        "Camera invalid: no frame after " +
                        warmup.ToString("0.##", CultureInfo.InvariantCulture) +
                        "s warm-up (timeout " + CameraFrameWaitTimeoutSeconds.ToString("0.##", CultureInfo.InvariantCulture) + "s).",
                        LogType.Error);

                    yield break;
                }

                if (!TryReadFrame(_cameraTexture, _cameraFrameBuffer, out string baselineError))
                {
                    AddCommandLine("Failed to read camera frame: " + baselineError, LogType.Error);
                    yield break;
                }

                _cameraMotionBaseline = new float[MotionGridWidth * MotionGridHeight];
                SampleLumaGrid(_cameraFrameBuffer, width, height, _cameraMotionBaseline);

                float checkDelay = Mathf.Clamp(CameraMotionCheckDelaySeconds, 0.05f, 5f);
                float motionThreshold = Mathf.Max(0.01f, CameraMotionThreshold);

                // Compare the reference sample with a frame captured after the delay.
                float checkTime = Time.realtimeSinceStartup + checkDelay;
                while (Time.realtimeSinceStartup < checkTime)
                    yield return null;

                bool gotComparisonFrame = false;
                float comparisonDeadline = Time.realtimeSinceStartup + CameraFrameWaitTimeoutSeconds;
                while (Time.realtimeSinceStartup < comparisonDeadline)
                {
                    if (_cameraTexture.didUpdateThisFrame)
                    {
                        gotComparisonFrame = true;
                        break;
                    }

                    yield return null;
                }

                if (!gotComparisonFrame)
                {
                    AddCommandLine(
                        "Camera invalid: no new frame after " +
                        checkDelay.ToString("0.##", CultureInfo.InvariantCulture) +
                        "s (timeout " + CameraFrameWaitTimeoutSeconds.ToString("0.##", CultureInfo.InvariantCulture) + "s).",
                        LogType.Error);

                    yield break;
                }

                if (!TryReadFrame(_cameraTexture, _cameraFrameBuffer, out string sampleError))
                {
                    AddCommandLine("Failed to read camera frame: " + sampleError, LogType.Error);
                    yield break;
                }

                float difference = ComputeLumaDifference(
                    _cameraFrameBuffer,
                    width,
                    height,
                    _cameraMotionBaseline);

                if (difference < motionThreshold)
                {
                    AddCommandLine(
                        "Camera invalid: no pixel change detected in " +
                        checkDelay.ToString("0.##", CultureInfo.InvariantCulture) +
                        "s (diff " + difference.ToString("0.###", CultureInfo.InvariantCulture) +
                        ", threshold " + motionThreshold.ToString("0.###", CultureInfo.InvariantCulture) + ").",
                        LogType.Error);

                    yield break;
                }

                AddCommandLine(
                    "Camera valid (pixel diff " +
                    difference.ToString("0.###", CultureInfo.InvariantCulture) +
                    ", threshold " + motionThreshold.ToString("0.###", CultureInfo.InvariantCulture) + ").");

                while (Time.realtimeSinceStartup < captureTime)
                    yield return null;

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
                _cameraFrameBuffer = null;
                _cameraMotionBaseline = null;
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

        private static bool TryReadFrame(WebCamTexture source, Color32[] buffer, out string error)
        {
            try
            {
                source.GetPixels32(buffer);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void SampleLumaGrid(Color32[] pixels, int width, int height, float[] target)
        {
            for (int gy = 0; gy < MotionGridHeight; gy++)
            {
                int y = MotionGridHeight <= 1
                    ? 0
                    : (gy * (height - 1)) / (MotionGridHeight - 1);

                for (int gx = 0; gx < MotionGridWidth; gx++)
                {
                    int x = MotionGridWidth <= 1
                        ? 0
                        : (gx * (width - 1)) / (MotionGridWidth - 1);

                    target[gy * MotionGridWidth + gx] = Luma(pixels[y * width + x]);
                }
            }
        }

        private static float ComputeLumaDifference(
            Color32[] pixels,
            int width,
            int height,
            float[] baseline)
        {
            float sum = 0f;

            for (int gy = 0; gy < MotionGridHeight; gy++)
            {
                int y = MotionGridHeight <= 1
                    ? 0
                    : (gy * (height - 1)) / (MotionGridHeight - 1);

                for (int gx = 0; gx < MotionGridWidth; gx++)
                {
                    int x = MotionGridWidth <= 1
                        ? 0
                        : (gx * (width - 1)) / (MotionGridWidth - 1);

                    float difference = Luma(pixels[y * width + x]) - baseline[gy * MotionGridWidth + gx];
                    sum += Mathf.Abs(difference);
                }
            }

            return sum / (MotionGridWidth * MotionGridHeight);
        }

        private static float Luma(Color32 color)
        {
            return 0.299f * color.r + 0.587f * color.g + 0.114f * color.b;
        }

        private void ShowLastPhoto()
        {
            ShowImageInConsole(
                PhotoPath,
                "Last photo",
                "No photo found. Run /camera first.");
        }

        private void ShowLastScreenshot()
        {
            ShowImageInConsole(
                DesktopScreenshotService.ScreenshotPath,
                "Last screenshot",
                "No screenshot found. Run /screenshot first.");
        }

        private void ShowImageInConsole(string path, string title, string missingMessage)
        {
            if (!File.Exists(path))
            {
                AddCommandLine(missingMessage, LogType.Warning);
                return;
            }

            byte[] bytes;

            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                AddCommandLine("Failed to read image: " + ex.Message, LogType.Error);
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
                AddCommandLine("Failed to load image.", LogType.Error);
                return;
            }

            AddImageEntry(title, texture);
        }

        private void AddImageEntry(string title, Texture2D texture)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (_entries[i].Texture == null)
                    continue;

                DestroyImage(_entries[i]);
                _entries.RemoveAt(i);
            }

            AddEntry(new LogEntry
            {
                Time = DateTime.Now,
                Message = title,
                StackTrace = null,
                Type = LogType.Log,
                IsCommand = true,
                Texture = texture
            });

            _scrollToBottom = true;
        }

        private void HandleScreenshot(string[] args)
        {
            if (args.Length > 0)
            {
                if (string.Equals(args[0], "show", StringComparison.OrdinalIgnoreCase))
                {
                    ShowLastScreenshot();
                    return;
                }

                AddCommandLine("Usage: /screenshot [show]", LogType.Warning);
                return;
            }

            if (DesktopScreenshotService.TryCaptureAndSave(out bool fullDesktop, out string error))
            {
                AddCommandLine(fullDesktop
                    ? "Screenshot captured and saved."
                    : "Screenshot captured (game view only).");
            }
            else
            {
                AddCommandLine("Failed to capture screenshot: " + error, LogType.Error);
            }
        }

        private void HandleRecord(string[] args)
        {
            MicrophoneLevelService service = MicrophoneLevelService.Instance;
            if (service == null)
            {
                AddCommandLine("Microphone service unavailable.", LogType.Error);
                return;
            }

            if (args.Length > 0)
            {
                if (string.Equals(args[0], "play", StringComparison.OrdinalIgnoreCase))
                {
                    if (service.IsRecording)
                    {
                        AddCommandLine("Recording is in progress.", LogType.Warning);
                        return;
                    }

                    if (!service.HasRecording)
                    {
                        AddCommandLine("No recording found. Run /record first.", LogType.Warning);
                        return;
                    }

                    if (service.TryPlayLastRecording(out string error))
                        AddCommandLine("Playing last recording.");
                    else
                        AddCommandLine("Failed to play recording: " + error, LogType.Error);

                    return;
                }

                AddCommandLine("Usage: /record [play]", LogType.Warning);
                return;
            }

            if (!service.IsRunning)
            {
                AddCommandLine("Microphone unavailable: " + service.Status, LogType.Error);
                return;
            }

            if (!service.StartRecording(MicrophoneLevelService.RecordSeconds))
            {
                AddCommandLine(
                    service.IsRecording
                        ? "Recording is already in progress."
                        : "Failed to start recording.",
                    LogType.Warning);
                return;
            }

            AddCommandLine(
                "Recording started. Capturing " +
                MicrophoneLevelService.RecordSeconds.ToString("0.#", CultureInfo.InvariantCulture) +
                "s...");
        }

        private void ClearCachedFiles()
        {
            RemoveImageEntries();

            MicrophoneLevelService mic = MicrophoneLevelService.Instance;
            if (mic != null)
                mic.ClearRecordingCache();

            string[] directories =
            {
                Path.Combine(Application.persistentDataPath, "Photos"),
                DesktopScreenshotService.ScreenshotDirectory,
                MicrophoneLevelService.RecordingsDirectory
            };

            bool clearedAny = false;
            string error = null;

            for (int i = 0; i < directories.Length; i++)
                clearedAny |= TryDeleteCacheDirectory(directories[i], ref error);

            if (!string.IsNullOrEmpty(error))
                AddCommandLine("Cache clear warning: " + error, LogType.Warning);

            if (clearedAny)
                AddCommandLine("Cache cleared.");
            else
                AddCommandLine("No cached files found.", LogType.Warning);
        }

        private void QuitGame()
        {
            AddCommandLine("Quitting...");

#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private static bool TryDeleteCacheDirectory(string directory, ref string error)
        {
            try
            {
                if (!Directory.Exists(directory))
                    return false;

                Directory.Delete(directory, true);
                return true;
            }
            catch (Exception ex)
            {
                error = string.IsNullOrEmpty(error)
                    ? ex.Message
                    : error + "; " + ex.Message;
                return false;
            }
        }

        private void ToggleMicLevelDisplay()
        {
            _showMicLevel = !_showMicLevel;
            AddCommandLine(_showMicLevel
                ? "Microphone level display on."
                : "Microphone level display off.");
        }

        private void BeginMicStart(string[] args)
        {
            MicrophoneLevelService service = MicrophoneLevelService.Instance;
            if (service == null)
            {
                AddCommandLine("Microphone service unavailable.", LogType.Error);
                return;
            }

            if (args.Length > 0 && string.Equals(args[0], "stop", StringComparison.OrdinalIgnoreCase))
            {
                service.StopMicStartEvent();
                AddCommandLine("MICSTART stopped.");
                return;
            }

            if (!service.IsRunning)
            {
                AddCommandLine("Microphone unavailable: " + service.Status, LogType.Error);
                return;
            }

            if (!service.StartMicStartEvent())
            {
                AddCommandLine("MICSTART is already running. Use /micstart stop to cancel.", LogType.Warning);
                return;
            }

            AddCommandLine(
                "MICSTART started. Recording a " +
                MicrophoneLevelService.MicStartBaselineSeconds.ToString("0.#", CultureInfo.InvariantCulture) +
                "s baseline (limit " +
                MicrophoneLevelService.MicStartTimeoutSeconds.ToString("0.#", CultureInfo.InvariantCulture) +
                "s)...");
        }

        private void DrawMicLevel()
        {
            MicrophoneLevelService service = MicrophoneLevelService.Instance;
            string text;
            Color color;

            if (service == null)
            {
                text = "Mic: service unavailable.";
                color = new Color(1f, 0.45f, 0.45f, 1f);
            }
            else if (!service.IsRunning)
            {
                text = "Mic: " + service.Status;
                color = new Color(1f, 0.45f, 0.45f, 1f);
            }
            else if (!service.HasSample)
            {
                text = "Mic: starting... (" + service.DeviceName + ")";
                color = new Color(1f, 0.85f, 0.35f, 1f);
            }
            else
            {
                const int barCount = 24;
                float db = Mathf.Clamp(service.RmsDb, -60f, 0f);
                int filled = Mathf.Clamp(
                    Mathf.RoundToInt(Mathf.InverseLerp(-60f, 0f, db) * barCount),
                    0,
                    barCount);
                string bar = new string('#', filled) + new string('.', barCount - filled);

                text = "Mic [" + bar + "] " +
                       service.RmsDb.ToString("0.0", CultureInfo.InvariantCulture) + " dBFS  peak " +
                       service.PeakDb.ToString("0.0", CultureInfo.InvariantCulture) + " dB  " +
                       service.DeviceName;

                color = new Color(0.55f, 0.9f, 1f, 1f);
            }

            Color previous = GUI.contentColor;
            GUI.contentColor = color;
            GUILayout.Label(text, _logStyle);
            GUI.contentColor = previous;
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
            public Texture2D Texture;
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
