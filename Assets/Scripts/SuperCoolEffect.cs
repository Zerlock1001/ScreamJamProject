using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using ScreamJam.Audio;
using ScreamJam.Debugging;
using ScreamJam.Utility;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Freezes a screenshot of the screen in a 12x6 glitch grid, then rearranges
/// the cells. Player input is blocked for the duration of the effect.
/// </summary>
public class SuperCoolEffect : MonoBehaviour
{
    const int CellCount = 12 * 6;
    const float FlashHz = 60f;

    static readonly int[] GridColumns = { 4, 6, 8, 12 };
    static readonly int[] GridRows = { 4, 6, 8, 6 };
    static readonly float[] GridWeightsEarly = { 0.70f, 0.22f, 0.07f, 0.01f };
    static readonly float[] GridWeightsLate = { 0.01f, 0.07f, 0.22f, 0.70f };
    static readonly Color[] PureBlockColors =
    {
        new Color(0f, 1f, 0f, 1f),
        new Color(1f, 0f, 0f, 1f),
        new Color(0f, 0f, 1f, 1f),
        new Color(1f, 1f, 0f, 1f),
        new Color(1f, 0.41f, 0.71f, 1f)
    };
    const float GridRampSeconds = 1.5f + 2f + 1f + 3f;

    static SuperCoolEffect instance;
    Action onFinished;

    static readonly string[] TerminalLines =
    {
        "root@node:~# scan --target 10.0.0.14 --full-range --service-detect --threads 8",
        "[*] initializing socket layer and resolving target route with 4 worker threads",
        "[*] host is reachable at 10.0.0.14, average round-trip time 12.4 ms",
        "[*] probing service ports across full TCP range, concurrency limit set to 256",
        "[+] 22/tcp open ssh OpenSSH 8.9p1 Ubuntu-3ubuntu0.6 protocol 2.0",
        "[+] 80/tcp open http nginx 1.24.0 serving static assets behind proxy",
        "[+] 443/tcp open https nginx 1.24.0 with TLS 1.3 and valid certificate",
        "[*] fingerprinting service banners, headers and TLS certificate metadata",
        "[*] building service map for 3 reachable endpoints and 2 filtered ports",
        "[*] loading payload module into memory, integrity checksum verification on",
        "[>] establishing encrypted tunnel to relay endpoint 10.0.0.21 port 443",
        "[+] credential accepted for account operator, temporary session token issued",
        "[+] privilege escalation complete, effective user id changed from 1000 to 0",
        "[*] collecting records from local databases and configuration directories",
        "[+] root shell opened, interactive session ready for remote commands",
        "root@node:~# nmap -sS -p- 10.0.0.14 --min-rate 2000 --open --reason",
        "[*] resolving target address and checking route through default gateway",
        "[*] target responded in 12 ms, packet loss 0 percent across 4 probes",
        "[*] scanning all 65535 ports with stealth SYN scan and service detection",
        "[+] 22/tcp open ssh syn-ack ttl 64 OpenSSH 8.9p1 Ubuntu 3ubuntu0.6",
        "[+] 80/tcp open http syn-ack ttl 64 nginx 1.24.0 Ubuntu Linux",
        "[+] 443/tcp open https syn-ack ttl 64 nginx 1.24.0 TLS 1.3 certificate",
        "[*] service fingerprint complete, three open ports and no filtered ports",
        "[*] searching public exploit database for matching service versions",
        "[*] no direct match found, continuing with manual validation checks",
        "[*] switching to credential audit and checking default account policies",
        "[>] opening secure tunnel with AES-256-GCM and ephemeral session keys",
        "[+] tunnel established, latency 18 ms, packet reordering disabled",
        "[*] uploading helper module to remote staging directory /tmp/.cache",
        "[*] module checksum verified against local manifest, signature accepted",
        "[*] reading local account list from system authentication database",
        "[+] account record recovered for operator and service maintenance user",
        "[*] reading active session tokens from local credential cache",
        "[+] session token recovered, expiry timestamp 2026-10-24 03:14:07 UTC",
        "[*] compressing collected data with balanced compression level and CRC32",
        "[*] encrypting transfer stream with rotating session-specific key material",
        "[>] sending records to relay 10.0.0.21 over established encrypted channel",
        "[+] transfer complete, 4.8 MB transmitted, average rate 2.1 MB per second",
        "[*] clearing temporary files and removing uploaded helper module",
        "[*] restoring service state and closing unused network descriptors",
        "root@node:~# logout --session operator --flush-cache --quiet",
        "[*] connection closed, relay link terminated and keys destroyed",
        "root@node:~# uname -a",
        "Linux node 6.8.0-generic x86_64 GNU/Linux SMP PREEMPT_DYNAMIC",
        "[*] enumerating local interfaces and reading link-layer configuration",
        "[+] eth0 10.0.0.14/24 mtu 1500 state up broadcast multicast",
        "[*] checking routing table and resolving default gateway address",
        "[+] default gateway 10.0.0.1 via eth0 metric 100, reachable",
        "[*] inspecting active sessions and listing connected user terminals",
        "[+] 2 sessions detected, 1 interactive shell and 1 background worker",
        "[*] dumping process tree and recording parent-child relationships",
        "[+] 184 processes collected, 12 services and 7 kernel threads found",
        "[*] archiving output into compressed container with metadata index",
        "[+] archive size 4.8 MB, compression ratio 38 percent, checksum ready",
        "[*] transfer channel encrypted, verifying remote endpoint identity",
        "[>] uploading archive to relay storage path /var/tmp/.incoming/session",
        "[+] upload complete, remote checksum verified, response code 200",
        "[*] verifying remote checksum against local manifest and metadata index",
        "[*] removing local traces and rotating session audit identifiers",
        "[*] closing socket and releasing worker threads, session terminated cleanly",
        "root@node:~# _"
    };

    GameObject overlayRoot;
    Texture2D screenshot;
    WebCamTexture liveCamera;
    RawImage[] cells;
    Text[] terminalTexts;
    readonly List<int> flashIndices = new List<int>();
    bool running;
    bool finished;
    bool cursorWasVisible;
    CursorLockMode cursorPreviousLock;
    bool phoneIconWasVisible;
    bool phoneIconSuppressed;
#pragma warning disable 0169, 0414
    bool cursorPinned;
    bool nativeCursorHidden;
    int cursorPinX;
    int cursorPinY;
#pragma warning restore 0169, 0414
    Coroutine flashRoutine;
    bool flashing;
    bool flashOn;
    int currentColumns = 12;
    int currentRows = 6;
    int activeCellCount = CellCount;
    float gridRampElapsed;
    float[] terminalSpeeds = new float[CellCount];
    int[] terminalScroll = new int[CellCount];
    float[] terminalAdvance = new float[CellCount];

    public static void Trigger()
    {
        Trigger(null);
    }

    public static void Trigger(Action finished)
    {
        if (instance != null && instance.running)
        {
            if (finished != null)
                instance.onFinished += finished;
            return;
        }

        if (instance == null)
        {
            var host = new GameObject("SuperCoolEffectHost");
            instance = host.AddComponent<SuperCoolEffect>();
        }

        instance.onFinished = finished;
        instance.Begin();
    }

    void Begin()
    {
        if (running)
            return;

        running = true;
        StartCoroutine(RunEffect());
    }

    IEnumerator RunEffect()
    {
        finished = false;
        gridRampElapsed = 0f;
        cursorWasVisible = Cursor.visible;
        cursorPreviousLock = Cursor.lockState;
        Cursor.visible = false;
        Cursor.lockState = CursorLockMode.None;
        PetJourney.SetMoodBarVisible(false);
        phoneIconWasVisible = PetPhone.PhoneIconVisible;
        phoneIconSuppressed = true;
        PetPhone.SetPhoneIconVisible(false);

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        nativeCursorHidden = true;
        while (ShowCursor(false) >= 0)
        {
        }

        cursorPinned = GetCursorPos(out NativePoint point);
        if (cursorPinned)
        {
            cursorPinX = point.x;
            cursorPinY = point.y;
        }
#else
        cursorPinned = false;
#endif

        try
        {
            DebugConsole.CloseImmediate();
            yield return null;
            yield return new WaitForEndOfFrame();

            screenshot = DesktopScreenshotService.CaptureTexture(out _, out string error);
            if (screenshot == null)
            {
                Debug.LogWarning("SuperCoolEffect screenshot failed: " + error);
                yield break;
            }

            if (PetJourney.HasCameraPhoto || CameraCaptureService.HasCapturedPhoto)
                yield return PrepareLiveCamera();

            StartLiveMicrophone();
            BuildOverlay();
            ApplyGlitch();

            flashing = true;
            flashRoutine = StartCoroutine(FlashLoop());

            yield return GlitchPhase(1f, 8f, 1.5f);
            yield return GlitchPhase(15f, 30f, 2f);
            yield return GlitchPhase(60f, 60f, 1f);

            yield return GradientToBlack(3f);
            flashing = false;

            SetAllCells(Color.black);
            yield return new WaitForSecondsRealtime(3f);

            SetAllCells(Color.white);
            yield return new WaitForSecondsRealtime(0.03f);

            SetAllCells(Color.black);
            yield return new WaitForSecondsRealtime(0.03f);

            SetAllCells(Color.white);
            yield return new WaitForSecondsRealtime(0.05f);
        }
        finally
        {
            Finish();
        }
    }

    IEnumerator PrepareLiveCamera()
    {
        if (!CameraCaptureService.TryStartLiveCamera(out liveCamera, out string error))
        {
            liveCamera = null;
            if (!string.IsNullOrEmpty(error))
                Debug.LogWarning("SuperCoolEffect live camera unavailable: " + error);
            yield break;
        }

        bool gotFrame = false;
        float deadline = Time.realtimeSinceStartup + 1f;
        while (liveCamera != null && Time.realtimeSinceStartup < deadline)
        {
            if (liveCamera.width > 16 && liveCamera.didUpdateThisFrame)
            {
                gotFrame = true;
                break;
            }

            yield return null;
        }

        if (liveCamera == null || !gotFrame)
        {
            Debug.LogWarning("SuperCoolEffect live camera produced no frame.");
            StopLiveCamera();
        }
    }

    void StopLiveCamera()
    {
        if (liveCamera == null)
            return;

        if (cells != null)
        {
            for (int i = 0; i < cells.Length; i++)
            {
                RawImage cell = cells[i];
                if (cell != null && cell.texture == liveCamera)
                {
                    cell.texture = Texture2D.whiteTexture;
                    cell.color = Color.black;
                    cell.uvRect = new Rect(0f, 0f, 1f, 1f);
                }
            }
        }

        CameraCaptureService.ReleaseLiveCamera(liveCamera);
        liveCamera = null;
    }

    void StartLiveMicrophone()
    {
        MicrophoneLevelService service = MicrophoneLevelService.Instance;
        if (service != null)
            service.StartLivePlayback();
    }

    void StopLiveMicrophone()
    {
        MicrophoneLevelService service = MicrophoneLevelService.Instance;
        if (service != null)
            service.StopLivePlayback();
    }

    void BuildOverlay()
    {
        var canvasObject = new GameObject(
            "SuperCoolEffectCanvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster));
        overlayRoot = canvasObject;

        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 40000;

        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;

        var contentObject = new GameObject("Overlay", typeof(RectTransform));
        contentObject.transform.SetParent(canvasObject.transform, false);
        var contentRect = contentObject.GetComponent<RectTransform>();
        contentRect.anchorMin = Vector2.zero;
        contentRect.anchorMax = Vector2.one;
        contentRect.offsetMin = Vector2.zero;
        contentRect.offsetMax = Vector2.zero;

        Font terminalFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (terminalFont == null)
            terminalFont = Resources.GetBuiltinResource<Font>("Arial.ttf");

        cells = new RawImage[CellCount];
        terminalTexts = new Text[CellCount];
        for (int i = 0; i < CellCount; i++)
        {
            int column = i % 12;
            int row = i / 12;

            var cellObject = new GameObject(
                "Cell_" + i,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(RawImage));
            cellObject.transform.SetParent(contentObject.transform, false);

            var rect = cellObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(
                (float)column / 12,
                1f - (float)(row + 1) / 6);
            rect.anchorMax = new Vector2(
                (float)(column + 1) / 12,
                1f - (float)row / 6);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var image = cellObject.GetComponent<RawImage>();
            image.texture = screenshot;
            image.raycastTarget = false;
            image.color = Color.white;
            cells[i] = image;

            var textObject = new GameObject(
                "TerminalText",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Text));
            textObject.transform.SetParent(cellObject.transform, false);
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(5f, 5f);
            textRect.offsetMax = new Vector2(-5f, -5f);

            var terminalText = textObject.GetComponent<Text>();
            terminalText.font = terminalFont;
            terminalText.fontSize = 11;
            terminalText.alignment = TextAnchor.UpperLeft;
            terminalText.color = new Color(0.35f, 1f, 0.55f, 1f);
            terminalText.horizontalOverflow = HorizontalWrapMode.Wrap;
            terminalText.verticalOverflow = VerticalWrapMode.Truncate;
            terminalText.supportRichText = true;
            terminalText.resizeTextForBestFit = true;
            terminalText.resizeTextMinSize = 6;
            terminalText.resizeTextMaxSize = 13;
            terminalText.raycastTarget = false;
            terminalText.gameObject.SetActive(false);
            terminalTexts[i] = terminalText;
        }

        Canvas.ForceUpdateCanvases();
    }

    void ApplyGlitch(float blackOverride = -1f)
    {
        if (cells == null || screenshot == null)
            return;

        int grid = ChooseGridIndex(Mathf.Clamp01(gridRampElapsed / GridRampSeconds));
        currentColumns = GridColumns[grid];
        currentRows = GridRows[grid];
        activeCellCount = currentColumns * currentRows;

        for (int i = 0; i < CellCount; i++)
        {
            bool active = i < activeCellCount;
            if (cells[i] != null)
                cells[i].gameObject.SetActive(active);
            if (terminalTexts != null && terminalTexts[i] != null)
                terminalTexts[i].gameObject.SetActive(false);

            if (!active || cells[i] == null)
                continue;

            int column = i % currentColumns;
            int row = i / currentColumns;
            var rect = cells[i].rectTransform;
            rect.anchorMin = new Vector2(
                (float)column / currentColumns,
                1f - (float)(row + 1) / currentRows);
            rect.anchorMax = new Vector2(
                (float)(column + 1) / currentColumns,
                1f - (float)row / currentRows);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        int whiteCount;
        int blackCount;
        int flashCount;
        int terminalCount;
        int pureColorCount;
        int cameraCount;
        if (blackOverride >= 0f)
        {
            blackCount = Mathf.RoundToInt(activeCellCount * Mathf.Clamp01(blackOverride));
            int otherCount = Mathf.Max(0, activeCellCount - blackCount);
            whiteCount = Mathf.RoundToInt(otherCount * (0.1f / 0.7f));
            flashCount = Mathf.RoundToInt(otherCount * (0.1f / 0.7f));
            terminalCount = Mathf.RoundToInt(otherCount * (0.05f / 0.7f));
            pureColorCount = RandomRound(otherCount * (0.02f / 0.7f));
            cameraCount = liveCamera != null
                ? RandomRound(otherCount * (0.03f / 0.7f))
                : 0;
        }
        else
        {
            whiteCount = Mathf.RoundToInt(activeCellCount * 0.1f);
            blackCount = Mathf.RoundToInt(activeCellCount * 0.3f);
            flashCount = Mathf.RoundToInt(activeCellCount * 0.1f);
            terminalCount = Mathf.RoundToInt(activeCellCount * 0.05f);
            pureColorCount = RandomRound(activeCellCount * 0.02f);
            cameraCount = liveCamera != null
                ? RandomRound(activeCellCount * 0.03f)
                : 0;
        }

        int[] special = new int[activeCellCount];
        var order = new List<int>(activeCellCount);
        for (int i = 0; i < activeCellCount; i++)
            order.Add(i);
        Shuffle(order);

        int specialIndex = 0;
        for (int i = 0; i < whiteCount; i++)
            special[order[specialIndex++]] = 1;
        for (int i = 0; i < blackCount; i++)
            special[order[specialIndex++]] = 2;
        for (int i = 0; i < flashCount; i++)
            special[order[specialIndex++]] = 3;
        for (int i = 0; i < terminalCount; i++)
            special[order[specialIndex++]] = 4;
        for (int i = 0; i < pureColorCount; i++)
            special[order[specialIndex++]] = 5;
        for (int i = 0; i < cameraCount; i++)
            special[order[specialIndex++]] = 6;

        flashIndices.Clear();
        var sources = new List<int>(activeCellCount);
        for (int i = 0; i < activeCellCount; i++)
            sources.Add(i);
        Shuffle(sources);

        int sourceIndex = 0;
        for (int i = 0; i < activeCellCount; i++)
        {
            RawImage cell = cells[i];
            if (special[i] == 1)
            {
                cell.texture = Texture2D.whiteTexture;
                cell.color = Color.white;
                cell.uvRect = new Rect(0f, 0f, 1f, 1f);
                continue;
            }

            if (special[i] == 2 || special[i] == 4)
            {
                cell.texture = Texture2D.whiteTexture;
                cell.color = Color.black;
                cell.uvRect = new Rect(0f, 0f, 1f, 1f);
                if (special[i] == 4 && terminalTexts[i] != null)
                {
                    terminalSpeeds[i] = UnityEngine.Random.Range(5f, 30f);
                    terminalScroll[i] = UnityEngine.Random.Range(0, TerminalLines.Length);
                    terminalAdvance[i] = 0f;
                    terminalTexts[i].gameObject.SetActive(true);
                    UpdateTerminalText(i);
                }
                continue;
            }

            if (special[i] == 3)
            {
                cell.texture = Texture2D.whiteTexture;
                cell.color = flashOn ? Color.white : Color.black;
                cell.uvRect = new Rect(0f, 0f, 1f, 1f);
                flashIndices.Add(i);
                continue;
            }

            if (special[i] == 5)
            {
                cell.texture = Texture2D.whiteTexture;
                cell.color = PureBlockColors[
                    UnityEngine.Random.Range(0, PureBlockColors.Length)];
                cell.uvRect = new Rect(0f, 0f, 1f, 1f);
                continue;
            }

            if (special[i] == 6 && liveCamera != null)
            {
                cell.texture = liveCamera;
                cell.color = Color.white;
                cell.uvRect = liveCamera.videoVerticallyMirrored
                    ? new Rect(0f, 1f, 1f, -1f)
                    : new Rect(0f, 0f, 1f, 1f);
                continue;
            }

            int source = sources[sourceIndex++];
            int sourceColumn = source % currentColumns;
            int sourceRow = source / currentColumns;
            cell.texture = screenshot;
            cell.color = Color.white;
            cell.uvRect = new Rect(
                (float)sourceColumn / currentColumns,
                1f - (float)(sourceRow + 1) / currentRows,
                1f / currentColumns,
                1f / currentRows);
        }
    }

    IEnumerator GlitchPhase(float fromFrequency, float toFrequency, float duration)
    {
        if (duration <= 0f)
            yield break;

        float elapsed = 0f;
        float timer = 0f;
        while (running && elapsed < duration)
        {
            float dt = Time.unscaledDeltaTime;
            elapsed += dt;
            gridRampElapsed += dt;
            timer += dt;

            float t = Mathf.Clamp01(elapsed / duration);
            float frequency = Mathf.Lerp(fromFrequency, toFrequency, t);
            if (frequency > 0f && timer >= 1f / frequency)
            {
                timer -= 1f / frequency;
                ApplyGlitch();
            }

            yield return null;
        }
    }

    IEnumerator GradientToBlack(float duration)
    {
        float elapsed = 0f;
        float timer = 0f;
        while (running && elapsed < duration)
        {
            float dt = Time.unscaledDeltaTime;
            elapsed += dt;
            gridRampElapsed += dt;
            timer += dt;

            float t = Mathf.Clamp01(elapsed / duration);
            if (timer >= 1f / 60f)
            {
                timer -= 1f / 60f;
                ApplyGlitch(Mathf.Lerp(0.3f, 1f, t));
            }

            yield return null;
        }

        ApplyGlitch(1f);
    }

    IEnumerator FlashLoop()
    {
        float timer = 0f;
        while (running)
        {
            if (flashing)
            {
                timer += Time.unscaledDeltaTime;
                float interval = 1f / FlashHz;
                if (timer >= interval)
                {
                    timer -= interval;
                    flashOn = !flashOn;
                    ApplyFlash();
                }
            }
            else
            {
                timer = 0f;
            }

            yield return null;
        }
    }

    void ApplyFlash()
    {
        for (int i = 0; i < flashIndices.Count; i++)
        {
            int index = flashIndices[i];
            if (cells == null || index < 0 || index >= cells.Length || cells[index] == null)
                continue;

            cells[index].texture = Texture2D.whiteTexture;
            cells[index].color = flashOn ? Color.white : Color.black;
            cells[index].uvRect = new Rect(0f, 0f, 1f, 1f);
        }
    }

    void UpdateTerminalAnimations()
    {
        if (terminalTexts == null)
            return;

        for (int i = 0; i < activeCellCount && i < terminalTexts.Length; i++)
        {
            Text text = terminalTexts[i];
            if (text == null || !text.gameObject.activeSelf)
                continue;

            terminalAdvance[i] += Time.unscaledDeltaTime * terminalSpeeds[i];
            int lines = Mathf.FloorToInt(terminalAdvance[i]);
            if (lines <= 0)
                continue;

            terminalAdvance[i] -= lines;
            terminalScroll[i] = (terminalScroll[i] + lines) % TerminalLines.Length;
            UpdateTerminalText(i);
        }
    }

    void UpdateTerminalText(int index)
    {
        if (terminalTexts == null || index < 0 || index >= terminalTexts.Length)
            return;

        Text text = terminalTexts[index];
        if (text == null || !text.gameObject.activeSelf)
            return;

        const int visibleLines = 6;
        int start = terminalScroll[index] % TerminalLines.Length;
        var builder = new StringBuilder();
        for (int line = 0; line < visibleLines; line++)
        {
            if (line > 0)
                builder.Append('\n');
            builder.Append(TerminalLines[(start + line) % TerminalLines.Length]);
        }
        text.text = builder.ToString();
    }

    void SetAllCells(Color color)
    {
        if (cells == null)
            return;

        for (int i = 0; i < cells.Length; i++)
        {
            RawImage cell = cells[i];
            if (cell == null)
                continue;

            cell.texture = Texture2D.whiteTexture;
            cell.color = color;
            cell.uvRect = new Rect(0f, 0f, 1f, 1f);
            if (terminalTexts != null && terminalTexts[i] != null)
                terminalTexts[i].gameObject.SetActive(false);
        }
    }

    static int RandomRound(float value)
    {
        int lower = Mathf.FloorToInt(value);
        float fraction = value - lower;
        return lower + (UnityEngine.Random.value < fraction ? 1 : 0);
    }

    static int ChooseGridIndex(float progress)
    {
        float totalWeight = 0f;
        for (int i = 0; i < GridColumns.Length; i++)
            totalWeight += Mathf.Lerp(GridWeightsEarly[i], GridWeightsLate[i], progress);

        if (totalWeight <= 0f)
            return 0;

        float roll = UnityEngine.Random.value * totalWeight;
        for (int i = 0; i < GridColumns.Length; i++)
        {
            float weight = Mathf.Lerp(GridWeightsEarly[i], GridWeightsLate[i], progress);
            if (roll < weight)
                return i;
            roll -= weight;
        }

        return GridColumns.Length - 1;
    }

    static void Shuffle(List<int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int swap = UnityEngine.Random.Range(0, i + 1);
            int value = list[i];
            list[i] = list[swap];
            list[swap] = value;
        }
    }

    void Update()
    {
        if (!running)
            return;

        UpdateTerminalAnimations();

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (cursorPinned)
            SetCursorPos(cursorPinX, cursorPinY);
#endif
    }

    void RestorePhoneIcon()
    {
        if (!phoneIconSuppressed)
            return;

        phoneIconSuppressed = false;
        PetPhone.SetPhoneIconVisible(phoneIconWasVisible);
    }

    void Finish()
    {
        if (finished)
            return;
        finished = true;
        running = false;
        cursorPinned = false;

        if (flashRoutine != null)
        {
            StopCoroutine(flashRoutine);
            flashRoutine = null;
        }

        StopLiveCamera();
        StopLiveMicrophone();

        if (overlayRoot != null)
        {
            Destroy(overlayRoot);
            overlayRoot = null;
        }

        if (screenshot != null)
        {
            Destroy(screenshot);
            screenshot = null;
        }

        cells = null;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (nativeCursorHidden)
        {
            while (ShowCursor(true) < 0)
            {
            }
            nativeCursorHidden = false;
        }
#endif
        Cursor.visible = cursorWasVisible;
        Cursor.lockState = cursorPreviousLock;
        PetJourney.SetMoodBarVisible(true);
        RestorePhoneIcon();

        Action done = onFinished;
        onFinished = null;
        if (instance == this)
            instance = null;
        Destroy(gameObject);
        if (done != null)
            done();
    }

    void OnDestroy()
    {
        cursorPinned = false;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        if (nativeCursorHidden)
        {
            while (ShowCursor(true) < 0)
            {
            }
            nativeCursorHidden = false;
        }
#endif
        Cursor.visible = cursorWasVisible;
        Cursor.lockState = cursorPreviousLock;
        PetJourney.SetMoodBarVisible(true);
        RestorePhoneIcon();
        StopLiveCamera();
        StopLiveMicrophone();

        if (overlayRoot != null)
        {
            Destroy(overlayRoot);
            overlayRoot = null;
        }

        if (screenshot != null)
        {
            Destroy(screenshot);
            screenshot = null;
        }

        if (instance == this)
            instance = null;
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    static extern int ShowCursor(bool show);
#endif
}
