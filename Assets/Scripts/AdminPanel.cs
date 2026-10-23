using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using ScreamJam.Debugging;
using ScreamJam.Utility;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// In-game monochrome administrator panel. Opened from the phone chat with
/// "/adminpanel start". The avatar and display name are placeholders until
/// the final admin identity is supplied.
/// </summary>
public class AdminPanel : MonoBehaviour
{
    const string AdminName = "OcOm_Admin123";
    const string AdminRole = "ADMINISTRATOR // ROOT";
    const string ColorSequenceKey = "AdminPanel.ColorSequence";
    const float TerminalScrollWheelStep = 34f;

    static readonly Color PanelColor = new Color(0.035f, 0.035f, 0.04f, 0.98f);
    static readonly Color PanelDark = new Color(0.012f, 0.012f, 0.015f, 1f);
    static readonly Color BorderColor = new Color(0.78f, 0.78f, 0.80f, 0.7f);
    static readonly Color AccentColor = new Color(0.86f, 0.86f, 0.88f, 1f);
    static readonly Color TextColor = new Color(0.88f, 0.88f, 0.90f, 1f);
    static readonly Color Muted = new Color(0.48f, 0.48f, 0.50f, 1f);
    static readonly Color StatusColor = new Color(0.78f, 0.78f, 0.80f, 1f);
    static readonly string[] TerminalLines =
    {
        "admin@panel:~# access --admin",
        "<color=#2f8f57>[SUCCESS]</color> SECURE CHANNEL ESTABLISHED",
        "<color=#a33d3d>[WARN]</color> COULD NOT VERIFY IDENTITY",
        "<color=#a33d3d>[WARN]</color> SESSION TRUST IS LIMITED",
        "<color=#e6e6e8>//: ENTER ADMIN PASSWORD</color>"
    };

    static readonly string[] GrantedLines =
    {
        "admin@panel:~# access --admin",
        "<color=#2f8f57>[SUCCESS]</color> SECURE CHANNEL ESTABLISHED",
        "<color=#2f8f57>[SUCCESS]</color> ACCESS GRANTED / WELCOME USER ADMIN_OcOm!"
    };

    static AdminPanel instance;
    GameObject root;
    CanvasGroup canvasGroup;
    Coroutine animRoutine;
    Image avatarImage;
    InputField passwordInput;
    Text usernameText;
    Text roleText;
    Text statusText;
    Text terminalText;
    RectTransform terminalViewport;
    RectTransform terminalScrollTrack;
    RectTransform terminalScrollThumb;
    Image terminalScrollThumbImage;
    float terminalScrollY;
    float terminalScrollMax;
    bool terminalFollowBottom = true;
    Coroutine outputRoutine;
    Coroutine verifyRoutine;
    bool verifying;
    bool accessGranted;
    int[] colorSequence;
    static int[] sharedColorSequence;

    static Sprite avatarSprite;

    public static void Open()
    {
        if (instance == null)
        {
            var host = new GameObject(
                "AdminPanelCanvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster),
                typeof(AdminPanel));
            var canvas = host.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 31000;
            var scaler = host.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            instance = host.GetComponent<AdminPanel>();
            instance.Build(host.transform);
        }

        instance.Show();
    }

    public static void Close()
    {
        if (instance != null)
            instance.Hide();
    }

    public static bool IsOpen => instance != null && instance.root != null && instance.root.activeSelf;

    public static void HideForEffect()
    {
        if (instance == null || instance.root == null)
            return;

        if (instance.animRoutine != null)
        {
            instance.StopCoroutine(instance.animRoutine);
            instance.animRoutine = null;
        }

        instance.root.SetActive(false);
    }

    public static void ShowAfterEffect()
    {
        if (instance == null || instance.root == null)
            return;

        if (instance.animRoutine != null)
        {
            instance.StopCoroutine(instance.animRoutine);
            instance.animRoutine = null;
        }

        instance.root.SetActive(true);
        instance.root.transform.SetAsLastSibling();
        instance.root.transform.localScale = Vector3.one;
        if (instance.canvasGroup != null)
        {
            instance.canvasGroup.alpha = 1f;
            instance.canvasGroup.interactable = true;
            instance.canvasGroup.blocksRaycasts = true;
        }

        instance.StartCoroutine(instance.FocusPassword());
    }

    public static void ClearCachedSequence()
    {
        sharedColorSequence = null;
        if (instance != null)
            instance.colorSequence = null;
    }

    public static void SetIdentity(Sprite avatar, string username, string role = null)
    {
        if (instance == null)
            Open();

        if (instance.avatarImage != null && avatar != null)
            instance.avatarImage.sprite = avatar;
        if (instance.usernameText != null && !string.IsNullOrEmpty(username))
            instance.usernameText.text = username;
        if (instance.roleText != null && !string.IsNullOrEmpty(role))
            instance.roleText.text = role;
    }

    void Show()
    {
        if (root == null)
            return;

        bool wasActive = root.activeSelf && canvasGroup != null && canvasGroup.alpha > 0.99f;
        if (animRoutine != null)
        {
            StopCoroutine(animRoutine);
            animRoutine = null;
        }

        root.SetActive(true);
        root.transform.SetAsLastSibling();
        if (!wasActive)
        {
            accessGranted = AdminAccessUnlocked();
            verifying = false;
            if (passwordInput != null)
                passwordInput.text = string.Empty;
            animRoutine = StartCoroutine(AnimateOpen());
            StartTerminalOutput();
        }
        StartCoroutine(FocusPassword());
    }

    void Hide()
    {
        if (root == null || !root.activeSelf)
            return;

        if (animRoutine != null)
        {
            StopCoroutine(animRoutine);
            animRoutine = null;
        }

        if (outputRoutine != null)
        {
            StopCoroutine(outputRoutine);
            outputRoutine = null;
        }

        if (verifyRoutine != null)
        {
            StopCoroutine(verifyRoutine);
            verifyRoutine = null;
        }
        verifying = false;
        if (passwordInput != null)
            passwordInput.interactable = true;

        if (canvasGroup != null)
        {
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
        }

        animRoutine = StartCoroutine(AnimateClose());
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
    }

    IEnumerator AnimateOpen()
    {
        float duration = 0.24f;
        float elapsed = 0f;
        Vector3 fromScale = root.transform.localScale;
        if (fromScale.x < 0.01f)
            fromScale = Vector3.one * 0.86f;
        float fromAlpha = canvasGroup != null ? canvasGroup.alpha : 1f;

        if (canvasGroup != null)
        {
            canvasGroup.interactable = true;
            canvasGroup.blocksRaycasts = true;
        }

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float eased = EaseOutBack(t);
            root.transform.localScale = Vector3.LerpUnclamped(fromScale, Vector3.one, eased);
            if (canvasGroup != null)
                canvasGroup.alpha = Mathf.Lerp(fromAlpha, 1f, Mathf.Clamp01(eased));
            yield return null;
        }

        root.transform.localScale = Vector3.one;
        if (canvasGroup != null)
            canvasGroup.alpha = 1f;
        animRoutine = null;
    }

    IEnumerator AnimateClose()
    {
        float duration = 0.18f;
        float elapsed = 0f;
        Vector3 fromScale = root.transform.localScale;
        float fromAlpha = canvasGroup != null ? canvasGroup.alpha : 1f;

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float eased = Mathf.SmoothStep(0f, 1f, t);
            root.transform.localScale = Vector3.LerpUnclamped(fromScale, Vector3.one * 0.9f, eased);
            if (canvasGroup != null)
                canvasGroup.alpha = Mathf.Lerp(fromAlpha, 0f, eased);
            yield return null;
        }

        root.SetActive(false);
        animRoutine = null;
    }

    void StartTerminalOutput()
    {
        if (terminalText == null)
            return;

        terminalFollowBottom = true;
        terminalScrollY = 0f;
        terminalScrollMax = 0f;

        if (accessGranted)
            colorSequence = null;
        else if (colorSequence == null)
        {
            if (sharedColorSequence == null || sharedColorSequence.Length == 0)
                LoadSharedColorSequence();

            if (sharedColorSequence != null && sharedColorSequence.Length > 0)
                colorSequence = sharedColorSequence;
            else
                GenerateColorSequence();
        }
        if (outputRoutine != null)
            StopCoroutine(outputRoutine);
        outputRoutine = StartCoroutine(StreamTerminal());
    }

    static bool AdminAccessUnlocked()
    {
        PetStory.Phase phase = PetStory.Current;
        return phase == PetStory.Phase.StateII
            || phase == PetStory.Phase.StateIII
            || phase == PetStory.Phase.Final;
    }

    IEnumerator StreamTerminal()
    {
        if (terminalText == null)
            yield break;

        string[] lines = accessGranted ? GrantedLines : TerminalLines;
        terminalText.text = string.Empty;
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                yield return new WaitForSecondsRealtime(UnityEngine.Random.Range(0.3f, 0.9f));

            if (i > 0)
                terminalText.text += "\n";
            terminalText.text += lines[i];
            StartCoroutine(ScrollTerminalToBottom());
        }

        if (!accessGranted && colorSequence != null && colorSequence.Length > 0)
            terminalText.text += "  " + ColorHintMarkup();
        StartCoroutine(ScrollTerminalToBottom());
        outputRoutine = null;
    }

    IEnumerator ScrollTerminalToBottom()
    {
        yield return null;
        if (terminalText == null || terminalViewport == null)
            yield break;

        RecalculateTerminalScroll();
        if (terminalFollowBottom)
            terminalScrollY = terminalScrollMax;
        ApplyTerminalScroll();
    }

    void RecalculateTerminalScroll()
    {
        if (terminalText == null || terminalViewport == null)
            return;

        Canvas.ForceUpdateCanvases();
        RectTransform textRect = terminalText.rectTransform;
        float height = terminalText.preferredHeight;
        textRect.sizeDelta = new Vector2(0f, height);
        terminalScrollMax = Mathf.Max(0f, height - terminalViewport.rect.height);
        terminalScrollY = Mathf.Clamp(terminalScrollY, 0f, terminalScrollMax);
    }

    void ApplyTerminalScroll()
    {
        if (terminalText != null)
            terminalText.rectTransform.anchoredPosition = new Vector2(0f, terminalScrollY);
        UpdateTerminalScrollbar();
    }

    void SetTerminalScrollNormalized(float normalized)
    {
        RecalculateTerminalScroll();
        terminalScrollY = Mathf.Clamp01(normalized) * terminalScrollMax;
        terminalFollowBottom = terminalScrollMax <= 0.01f
            || terminalScrollY >= terminalScrollMax - 0.5f;
        ApplyTerminalScroll();
    }

    void ScrollTerminalByWheel(float wheel)
    {
        if (Mathf.Abs(wheel) < 0.001f)
            return;

        RecalculateTerminalScroll();
        terminalScrollY = Mathf.Clamp(
            terminalScrollY - wheel * TerminalScrollWheelStep,
            0f,
            terminalScrollMax);
        terminalFollowBottom = terminalScrollMax <= 0.01f
            || terminalScrollY >= terminalScrollMax - 0.5f;
        ApplyTerminalScroll();
    }

    void UpdateTerminalScrollbar()
    {
        if (terminalScrollTrack == null || terminalScrollThumb == null || terminalViewport == null)
            return;

        float trackHeight = terminalScrollTrack.rect.height;
        if (trackHeight <= 0f)
            return;

        float viewHeight = terminalViewport.rect.height;
        float contentHeight = terminalScrollMax + viewHeight;
        float thumbHeight = trackHeight;
        if (terminalScrollMax > 0.01f && contentHeight > 0.01f)
        {
            thumbHeight = Mathf.Clamp(
                trackHeight * (viewHeight / contentHeight),
                22f,
                trackHeight);
        }

        terminalScrollThumb.sizeDelta = new Vector2(0f, thumbHeight);
        float travel = Mathf.Max(0f, trackHeight - thumbHeight);
        float normalized = terminalScrollMax > 0.01f
            ? Mathf.Clamp01(terminalScrollY / terminalScrollMax)
            : 0f;
        terminalScrollThumb.anchoredPosition = new Vector2(0f, -normalized * travel);

        if (terminalScrollThumbImage != null)
        {
            terminalScrollThumbImage.color = terminalScrollMax > 0.01f
                ? new Color(0.72f, 0.72f, 0.75f, 0.90f)
                : new Color(0.40f, 0.40f, 0.42f, 0.55f);
        }
    }

    public static string CurrentPassword()
    {
        int[] sequence = instance != null ? instance.colorSequence : null;
        if (sequence == null || sequence.Length == 0)
            sequence = sharedColorSequence;
        if (sequence == null || sequence.Length == 0)
            sequence = LoadSharedColorSequence();

        if (sequence == null || sequence.Length == 0)
        {
            sequence = CreateColorSequence();
            sharedColorSequence = sequence;
            SaveSharedColorSequence();
            if (instance != null)
                instance.colorSequence = sequence;
        }
        else
        {
            sharedColorSequence = sequence;
        }

        return DailyMiniGame.PasswordFor(sequence);
    }

    void GenerateColorSequence()
    {
        colorSequence = CreateColorSequence();
        sharedColorSequence = colorSequence;
        SaveSharedColorSequence();
    }

    static int[] LoadSharedColorSequence()
    {
        string saved = PlayerPrefs.GetString(ColorSequenceKey, string.Empty);
        if (string.IsNullOrEmpty(saved) || saved.Length != 6)
            return null;

        var sequence = new int[6];
        for (int i = 0; i < saved.Length; i++)
        {
            if (saved[i] < '1' || saved[i] > '3')
                return null;
            sequence[i] = saved[i] - '0';
        }

        sharedColorSequence = sequence;
        return sequence;
    }

    static void SaveSharedColorSequence()
    {
        if (sharedColorSequence == null || sharedColorSequence.Length != 6)
            return;

        var saved = new char[6];
        for (int i = 0; i < sharedColorSequence.Length; i++)
            saved[i] = (char)('0' + Mathf.Clamp(sharedColorSequence[i], 1, 3));

        PlayerPrefs.SetString(ColorSequenceKey, new string(saved));
        PlayerPrefs.Save();
    }

    static int[] CreateColorSequence()
    {
        // Classic minesweeper number colors: 1 blue, 2 green, 3 red.
        // Seed all three colors first, then fill and shuffle so every color
        // appears at least once in the six-square hint.
        int[] sequence = new int[6];
        sequence[0] = 1;
        sequence[1] = 2;
        sequence[2] = 3;
        for (int i = 3; i < sequence.Length; i++)
        {
            int color = UnityEngine.Random.Range(0, 3);
            sequence[i] = color == 0 ? 3 : (color == 1 ? 2 : 1);
        }

        for (int i = sequence.Length - 1; i > 0; i--)
        {
            int swap = UnityEngine.Random.Range(0, i + 1);
            int value = sequence[i];
            sequence[i] = sequence[swap];
            sequence[swap] = value;
        }

        return sequence;
    }

    string ColorHintMarkup()
    {
        string result = string.Empty;
        for (int i = 0; i < colorSequence.Length; i++)
        {
            string color;
            switch (colorSequence[i])
            {
                case 3:
                    color = "#e05a5a";
                    break;
                case 2:
                    color = "#55c978";
                    break;
                default:
                    color = "#5f8dff";
                    break;
            }

            if (i > 0)
                result += " ";
            result += "<size=18><color=" + color + ">■</color></size>";
        }
        return result;
    }

    IEnumerator FocusPassword()
    {
        yield return null;
        if (passwordInput == null || root == null || !root.activeSelf)
            yield break;
        if (canvasGroup != null && !canvasGroup.interactable)
            yield break;
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(passwordInput.gameObject);
        passwordInput.ActivateInputField();
    }

    void SubmitAdminPassword(string raw)
    {
        if (passwordInput == null || verifying)
            return;

        string entered = string.IsNullOrEmpty(raw) ? string.Empty : raw.Trim();
        passwordInput.text = string.Empty;
        if (string.IsNullOrEmpty(entered))
        {
            passwordInput.ActivateInputField();
            return;
        }

        if (PetCampaign.IsCultCommand(entered))
        {
            passwordInput.text = string.Empty;
            PetCampaign.TryJoinCult();
            return;
        }

        if (accessGranted)
            return;

        if (outputRoutine != null)
        {
            StopCoroutine(outputRoutine);
            outputRoutine = null;
        }

        verifying = true;
        if (verifyRoutine != null)
            StopCoroutine(verifyRoutine);
        verifyRoutine = StartCoroutine(VerifyPassword(entered));
    }

    IEnumerator VerifyPassword(string entered)
    {
        string prefix = terminalText != null ? terminalText.text : string.Empty;
        if (!string.IsNullOrEmpty(prefix))
            prefix += "\n";

        const float duration = 3f;
        float elapsed = 0f;
        if (terminalText != null)
        {
            terminalText.text = prefix + "VERIFYING.";
            StartCoroutine(ScrollTerminalToBottom());
        }

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            int dots = 1 + Mathf.FloorToInt(elapsed / 0.4f) % 3;
            if (terminalText != null)
                terminalText.text = prefix + "VERIFYING" + new string('.', dots);
            yield return null;
        }

        string expected = DailyMiniGame.PasswordFor(colorSequence);
        bool accepted = !string.IsNullOrEmpty(expected)
            && string.Equals(entered, expected, StringComparison.Ordinal);
        if (terminalText != null)
        {
            terminalText.text = prefix + "VERIFYING...\n" +
                (accepted
                    ? "<color=#2f8f57>[SUCCESS]</color> ACCESS GRANTED\n" +
                      "<color=#2f8f57>WELCOME USER ADMIN_OcOm!</color>"
                    : "<color=#a33d3d>[WARN]</color> ACCESS DENIED");
            yield return ScrollTerminalToBottom();
        }

        if (!accepted)
        {
            yield return new WaitForSecondsRealtime(0.55f);
            verifying = false;
            verifyRoutine = null;
            if (passwordInput != null)
                passwordInput.ActivateInputField();
            PetCampaign.SayWrongPasswordHint();
            yield break;
        }

        yield return new WaitForSecondsRealtime(1f);

        verifying = false;
        verifyRoutine = null;
        accessGranted = true;
        if (passwordInput != null)
            passwordInput.DeactivateInputField();

        AdminAccessEffect.Play(PetCampaign.TryAgree);
    }

    static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float p = t - 1f;
        return 1f + c3 * p * p * p + c1 * p * p;
    }

    void Build(Transform parent)
    {
        root = new GameObject("AdminPanel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Outline), typeof(CanvasGroup));
        root.transform.SetParent(parent, false);
        var rect = root.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, 24f);
        rect.sizeDelta = new Vector2(560f, 380f);

        canvasGroup = root.GetComponent<CanvasGroup>();
        canvasGroup.alpha = 0f;

        var background = root.GetComponent<Image>();
        background.color = PanelColor;
        background.raycastTarget = true;
        var outline = root.GetComponent<Outline>();
        outline.effectColor = BorderColor;
        outline.effectDistance = new Vector2(1f, -1f);

        var drag = root.AddComponent<WindowDrag>();
        drag.Target = rect;

        var topBar = MakePanel("TopBar", root.transform, PanelDark);
        Stretch(topBar, 0f, 0f, 0f, 0f);
        topBar.anchorMin = new Vector2(0f, 1f);
        topBar.anchorMax = new Vector2(1f, 1f);
        topBar.pivot = new Vector2(0.5f, 1f);
        topBar.offsetMin = new Vector2(0f, -42f);
        topBar.offsetMax = Vector2.zero;
        topBar.gameObject.AddComponent<WindowDrag>().Target = rect;

        var accent = MakePanel("TopAccent", topBar, AccentColor);
        var accentRect = accent;
        accentRect.anchorMin = new Vector2(0f, 0f);
        accentRect.anchorMax = new Vector2(1f, 0f);
        accentRect.pivot = new Vector2(0.5f, 0f);
        accentRect.sizeDelta = new Vector2(0f, 2f);
        accentRect.anchoredPosition = Vector2.zero;

        var title = MakeText("Title", topBar, "ADMINPANEL", 16, TextAnchor.MiddleLeft, AccentColor);
        title.fontStyle = FontStyle.Bold;
        Stretch(title.rectTransform, 0f);
        title.rectTransform.offsetMin = new Vector2(20f, 0f);
        title.rectTransform.offsetMax = new Vector2(-52f, 0f);
        title.raycastTarget = false;

        var closeButton = MakeButton("Close", topBar, new Color(0.16f, 0.16f, 0.18f, 1f), "X", 15);
        var closeRect = closeButton.GetComponent<RectTransform>();
        closeRect.anchorMin = new Vector2(1f, 1f);
        closeRect.anchorMax = new Vector2(1f, 1f);
        closeRect.pivot = new Vector2(1f, 1f);
        closeRect.anchoredPosition = new Vector2(-8f, -7f);
        closeRect.sizeDelta = new Vector2(28f, 28f);
        closeButton.onClick.AddListener(Close);

        avatarImage = MakePanel("Avatar", root.transform, new Color(0.01f, 0.01f, 0.015f, 1f)).GetComponent<Image>();
        avatarImage.sprite = AvatarSprite();
        avatarImage.type = Image.Type.Simple;
        avatarImage.color = Color.white;
        var avatarRect = avatarImage.rectTransform;
        avatarRect.anchorMin = new Vector2(0f, 1f);
        avatarRect.anchorMax = new Vector2(0f, 1f);
        avatarRect.pivot = new Vector2(0f, 1f);
        avatarRect.anchoredPosition = new Vector2(22f, -58f);
        avatarRect.sizeDelta = new Vector2(54f, 54f);

        var avatarLetter = MakeText("AvatarLetter", avatarRect, "O", 24, TextAnchor.MiddleCenter, Color.white);
        avatarLetter.fontStyle = FontStyle.Bold;
        Stretch(avatarLetter.rectTransform, 0f);
        avatarLetter.raycastTarget = false;

        usernameText = MakeText("Username", root.transform, AdminName, 19, TextAnchor.MiddleLeft, TextColor);
        usernameText.fontStyle = FontStyle.Bold;
        var usernameRect = usernameText.rectTransform;
        usernameRect.anchorMin = new Vector2(0f, 1f);
        usernameRect.anchorMax = new Vector2(0f, 1f);
        usernameRect.pivot = new Vector2(0f, 1f);
        usernameRect.anchoredPosition = new Vector2(90f, -58f);
        usernameRect.sizeDelta = new Vector2(300f, 26f);
        usernameText.raycastTarget = false;

        roleText = MakeText("Role", root.transform, AdminRole, 11, TextAnchor.MiddleLeft, Muted);
        var roleRect = roleText.rectTransform;
        roleRect.anchorMin = new Vector2(0f, 1f);
        roleRect.anchorMax = new Vector2(0f, 1f);
        roleRect.pivot = new Vector2(0f, 1f);
        roleRect.anchoredPosition = new Vector2(90f, -84f);
        roleRect.sizeDelta = new Vector2(300f, 20f);
        roleText.raycastTarget = false;

        statusText = MakeText("Status", root.transform, "<color=#3fbf72>●</color> SESSION ACTIVE", 11, TextAnchor.MiddleRight, StatusColor);
        statusText.fontStyle = FontStyle.Bold;
        statusText.supportRichText = true;
        var statusRect = statusText.rectTransform;
        statusRect.anchorMin = new Vector2(1f, 1f);
        statusRect.anchorMax = new Vector2(1f, 1f);
        statusRect.pivot = new Vector2(1f, 1f);
        statusRect.anchoredPosition = new Vector2(-20f, -62f);
        statusRect.sizeDelta = new Vector2(220f, 20f);
        statusText.raycastTarget = false;

        var ipText = MakeText("IP", root.transform, "IP: " + LocalIp(), 11, TextAnchor.MiddleRight, Muted);
        var ipRect = ipText.rectTransform;
        ipRect.anchorMin = new Vector2(1f, 1f);
        ipRect.anchorMax = new Vector2(1f, 1f);
        ipRect.pivot = new Vector2(1f, 1f);
        ipRect.anchoredPosition = new Vector2(-20f, -84f);
        ipRect.sizeDelta = new Vector2(220f, 18f);
        ipText.raycastTarget = false;

        var separator = MakePanel("Separator", root.transform, new Color(0.70f, 0.70f, 0.72f, 0.28f));
        var separatorRect = separator;
        separatorRect.anchorMin = new Vector2(0f, 1f);
        separatorRect.anchorMax = new Vector2(1f, 1f);
        separatorRect.pivot = new Vector2(0.5f, 1f);
        separatorRect.offsetMin = new Vector2(20f, -122f);
        separatorRect.offsetMax = new Vector2(-20f, -121f);

        var terminal = MakePanel("Terminal", root.transform, PanelDark);
        var terminalRect = terminal;
        terminalRect.anchorMin = new Vector2(0f, 0f);
        terminalRect.anchorMax = new Vector2(1f, 1f);
        terminalRect.offsetMin = new Vector2(20f, 42f);
        terminalRect.offsetMax = new Vector2(-20f, -136f);
        var terminalOutline = terminal.gameObject.AddComponent<Outline>();
        terminalOutline.effectColor = new Color(0.72f, 0.72f, 0.74f, 0.26f);
        terminalOutline.effectDistance = new Vector2(1f, -1f);

        var viewportObject = new GameObject(
            "OutputViewport",
            typeof(RectTransform),
            typeof(RectMask2D));
        viewportObject.transform.SetParent(terminal, false);
        terminalViewport = viewportObject.GetComponent<RectTransform>();
        terminalViewport.anchorMin = Vector2.zero;
        terminalViewport.anchorMax = Vector2.one;
        terminalViewport.offsetMin = new Vector2(20f, 52f);
        terminalViewport.offsetMax = new Vector2(-30f, -12f);

        var contentObject = new GameObject(
            "Output",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Text));
        contentObject.transform.SetParent(terminalViewport, false);
        terminalText = contentObject.GetComponent<Text>();
        terminalText.font = BuiltinFont();
        terminalText.fontSize = 12;
        terminalText.alignment = TextAnchor.UpperLeft;
        terminalText.color = TextColor;
        terminalText.horizontalOverflow = HorizontalWrapMode.Wrap;
        terminalText.verticalOverflow = VerticalWrapMode.Overflow;
        terminalText.supportRichText = true;
        terminalText.lineSpacing = 1.4375f;
        terminalText.raycastTarget = false;

        var textRect = terminalText.rectTransform;
        textRect.anchorMin = new Vector2(0f, 1f);
        textRect.anchorMax = new Vector2(1f, 1f);
        textRect.pivot = new Vector2(0.5f, 1f);
        textRect.anchoredPosition = Vector2.zero;
        textRect.sizeDelta = Vector2.zero;

        var wheel = terminal.gameObject.AddComponent<AdminTerminalScrollWheel>();
        wheel.Scrolled = ScrollTerminalByWheel;

        terminalScrollTrack = MakePanel("Scrollbar", terminal, new Color(0.11f, 0.11f, 0.13f, 0.95f));
        terminalScrollTrack.anchorMin = new Vector2(1f, 0f);
        terminalScrollTrack.anchorMax = new Vector2(1f, 1f);
        terminalScrollTrack.pivot = new Vector2(1f, 1f);
        terminalScrollTrack.offsetMin = new Vector2(-20f, 52f);
        terminalScrollTrack.offsetMax = new Vector2(-10f, -12f);
        var trackImage = terminalScrollTrack.GetComponent<Image>();
        trackImage.raycastTarget = false;
        var trackOutline = terminalScrollTrack.gameObject.AddComponent<Outline>();
        trackOutline.effectColor = new Color(0.55f, 0.55f, 0.58f, 0.35f);
        trackOutline.effectDistance = new Vector2(1f, -1f);

        var thumbObject = new GameObject(
            "ScrollThumb",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(Outline));
        thumbObject.transform.SetParent(terminalScrollTrack, false);
        terminalScrollThumb = thumbObject.GetComponent<RectTransform>();
        terminalScrollThumb.anchorMin = new Vector2(0f, 1f);
        terminalScrollThumb.anchorMax = new Vector2(1f, 1f);
        terminalScrollThumb.pivot = new Vector2(0.5f, 1f);
        terminalScrollThumb.anchoredPosition = Vector2.zero;
        terminalScrollThumb.sizeDelta = new Vector2(0f, 40f);
        terminalScrollThumbImage = thumbObject.GetComponent<Image>();
        terminalScrollThumbImage.color = new Color(0.72f, 0.72f, 0.75f, 0.90f);
        terminalScrollThumbImage.raycastTarget = true;
        var thumbOutline = thumbObject.GetComponent<Outline>();
        thumbOutline.effectColor = new Color(0f, 0f, 0f, 0.55f);
        thumbOutline.effectDistance = new Vector2(1f, -1f);

        var thumbDrag = thumbObject.AddComponent<AdminPanelScrollbarDrag>();
        thumbDrag.Track = terminalScrollTrack;
        thumbDrag.Thumb = terminalScrollThumb;
        thumbDrag.Scrolled = SetTerminalScrollNormalized;

        var passwordObject = new GameObject("Password", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(InputField), typeof(Outline));
        passwordObject.transform.SetParent(terminal, false);
        var passwordRect = passwordObject.GetComponent<RectTransform>();
        passwordRect.anchorMin = new Vector2(0f, 0f);
        passwordRect.anchorMax = new Vector2(1f, 0f);
        passwordRect.pivot = new Vector2(0.5f, 0f);
        passwordRect.offsetMin = new Vector2(20f, 12f);
        passwordRect.offsetMax = new Vector2(-20f, 44f);

        var passwordImage = passwordObject.GetComponent<Image>();
        passwordImage.color = new Color(0.008f, 0.008f, 0.01f, 1f);
        passwordImage.raycastTarget = true;
        var passwordOutline = passwordObject.GetComponent<Outline>();
        passwordOutline.effectColor = BorderColor;
        passwordOutline.effectDistance = new Vector2(1f, -1f);

        var passwordText = MakeText("Text", passwordRect, string.Empty, 13, TextAnchor.MiddleLeft, Color.white);
        Stretch(passwordText.rectTransform, 8f);
        passwordText.supportRichText = false;

        passwordInput = passwordObject.GetComponent<InputField>();
        passwordInput.targetGraphic = passwordImage;
        passwordInput.textComponent = passwordText;
        passwordInput.contentType = InputField.ContentType.Standard;
        passwordInput.lineType = InputField.LineType.SingleLine;
        passwordInput.characterLimit = 64;
        passwordInput.caretColor = AccentColor;
        passwordInput.selectionColor = new Color(0.72f, 0.72f, 0.74f, 0.35f);
        passwordInput.onSubmit.AddListener(SubmitAdminPassword);

        var footer = MakeText("Footer", root.transform, "AUTHORIZED ACCESS ONLY    //    X TO CLOSE", 10, TextAnchor.MiddleCenter, Muted);
        var footerRect = footer.rectTransform;
        footerRect.anchorMin = new Vector2(0f, 0f);
        footerRect.anchorMax = new Vector2(1f, 0f);
        footerRect.pivot = new Vector2(0.5f, 0f);
        footerRect.sizeDelta = new Vector2(0f, 22f);
        footerRect.anchoredPosition = new Vector2(0f, 4f);
        footer.raycastTarget = false;

        root.transform.localScale = Vector3.one * 0.86f;
        canvasGroup.alpha = 0f;
        root.SetActive(false);
    }

    static RectTransform MakePanel(string name, Transform parent, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = true;
        return go.GetComponent<RectTransform>();
    }

    static Text MakeText(string name, Transform parent, string value, int size, TextAnchor anchor, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        go.transform.SetParent(parent, false);
        var text = go.GetComponent<Text>();
        text.font = BuiltinFont();
        text.fontSize = size;
        text.alignment = anchor;
        text.color = color;
        text.text = value;
        text.raycastTarget = false;
        return text;
    }

    static Button MakeButton(string name, Transform parent, Color color, string label, int fontSize)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = true;
        var button = go.GetComponent<Button>();
        button.targetGraphic = image;
        var text = MakeText("Label", go.transform, label, fontSize, TextAnchor.MiddleCenter, Color.white);
        text.fontStyle = FontStyle.Bold;
        Stretch(text.rectTransform, 0f);
        return button;
    }

    static void Stretch(RectTransform rect, float inset)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(inset, inset);
        rect.offsetMax = new Vector2(-inset, -inset);
    }

    static void Stretch(RectTransform rect, float left, float right, float top, float bottom)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(left, bottom);
        rect.offsetMax = new Vector2(-right, -top);
    }

    public static string LocalIp()
    {
        try
        {
            IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());
            for (int i = 0; i < host.AddressList.Length; i++)
            {
                IPAddress address = host.AddressList[i];
                if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                    return address.ToString();
            }
        }
        catch
        {
            // Fall back to a loopback address when no network adapter is available.
        }

        return "127.0.0.1";
    }

    static Sprite AvatarSprite()
    {
        if (avatarSprite != null)
            return avatarSprite;

        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[size * size];
        float center = (size - 1) * 0.5f;
        float radius = center - 2f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                if (distance > radius)
                    pixels[y * size + x] = new Color(0f, 0f, 0f, 0f);
                else if (distance > radius - 2f)
                    pixels[y * size + x] = new Color(0.82f, 0.82f, 0.84f, 1f);
                else
                    pixels[y * size + x] = new Color(0.015f, 0.015f, 0.02f, 1f);
            }
        }

        texture.SetPixels(pixels);
        texture.Apply(false, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        avatarSprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f);
        avatarSprite.hideFlags = HideFlags.HideAndDontSave;
        return avatarSprite;
    }

    static Font BuiltinFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }
}

public class AdminPanelScrollbarDrag : MonoBehaviour, IBeginDragHandler, IDragHandler
{
    public RectTransform Track;
    public RectTransform Thumb;
    public Action<float> Scrolled;

    float dragStartLocalY;
    float dragStartNormalized;

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (!TryGetLocalY(eventData, out dragStartLocalY))
            return;

        float travel = Travel;
        dragStartNormalized = travel > 0.01f
            ? Mathf.Clamp01(-Thumb.anchoredPosition.y / travel)
            : 0f;
    }

    public void OnDrag(PointerEventData eventData)
    {
        float localY;
        if (!TryGetLocalY(eventData, out localY))
            return;

        float travel = Travel;
        if (travel <= 0.01f)
            return;

        float normalized = Mathf.Clamp01(
            dragStartNormalized - (localY - dragStartLocalY) / travel);
        if (Scrolled != null)
            Scrolled(normalized);
    }

    float Travel
    {
        get
        {
            if (Track == null || Thumb == null)
                return 0f;
            return Mathf.Max(0f, Track.rect.height - Thumb.rect.height);
        }
    }

    bool TryGetLocalY(PointerEventData eventData, out float localY)
    {
        localY = 0f;
        if (Track == null)
            return false;

        Vector2 local;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                Track,
                eventData.position,
                eventData.pressEventCamera,
                out local))
            return false;

        localY = local.y;
        return true;
    }
}

public class AdminTerminalScrollWheel : MonoBehaviour, IScrollHandler
{
    public Action<float> Scrolled;

    public void OnScroll(PointerEventData eventData)
    {
        if (Scrolled != null)
            Scrolled(eventData.scrollDelta.y);
    }
}

public class AdminAccessEffect : MonoBehaviour
{
    const int GridSize = 3;
    const float BlockGap = 1.5f;
    const float TileDisplaySeconds = 3f;
    const float TileShuffleStartRate = 3f;
    const float TileShuffleEndRate = 24f;
    const int TileSnowCount = 3;
    const int TileBlackCount = 1;
    const float NoiseRefreshSeconds = 1f / 60f;
    const int NoiseSize = 96;

    Action onFinished;
    GameObject canvasRoot;
    GameObject tilesRoot;
    GameObject flashObject;
    Image flashImage;
    Texture2D screenshot;
    Texture2D noiseTexture;
    Color32[] noisePixels;
    RawImage[] tileImages;
    RectTransform[] tileRects;
    bool moodHidden;
    bool effectUiHidden;
    bool phoneIconWasVisible;
    bool adminWasOpen;
    bool previewMode;

    public static void Play(Action finished)
    {
        var host = new GameObject("AdminAccessEffect");
        var effect = host.AddComponent<AdminAccessEffect>();
        effect.onFinished = finished;
        effect.previewMode = finished == null;
        effect.StartCoroutine(effect.Run());
    }

    IEnumerator Run()
    {
        effectUiHidden = true;
        moodHidden = true;
        adminWasOpen = AdminPanel.IsOpen;
        phoneIconWasVisible = PetPhone.PhoneIconVisible;

        DebugConsole.CloseImmediate();
        DailyMiniGame.HideForEffect();
        PetJourney.SetMoodBarVisible(false, true);
        PetPhone.SetPhoneIconVisible(false);
        PetPhone.SetPhoneUiHidden(true);
        PetJourney.SetGalleryHidden(true);
        if (adminWasOpen)
            AdminPanel.HideForEffect();

        DesktopPetWindow.SetFullWindowRegion(true);
        yield return null;

        screenshot = DesktopScreenshotService.CaptureTexture(out _, out string error);
        if (screenshot == null)
            Debug.LogWarning("AdminAccessEffect screenshot failed: " + error);

        BuildOverlay();
        ShowFlash(Color.black);
        yield return new WaitForSecondsRealtime(1f);

        if (tilesRoot != null)
            tilesRoot.SetActive(true);
        if (flashObject != null)
            flashObject.SetActive(false);
        yield return DisplayTiles();

        ShowFlash(Color.black);
        yield return new WaitForSecondsRealtime(1f);

        ShowFlash(Color.white);
        yield return new WaitForSecondsRealtime(0.03f);

        ShowFlash(Color.black);
        yield return new WaitForSecondsRealtime(0.05f);

        Finish();
    }

    void BuildOverlay()
    {
        canvasRoot = new GameObject(
            "AdminAccessEffectCanvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster));
        var canvas = canvasRoot.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 50000;

        var scaler = canvasRoot.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;

        var backdropObject = new GameObject(
            "Backdrop",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image));
        backdropObject.transform.SetParent(canvasRoot.transform, false);
        var backdropRect = backdropObject.GetComponent<RectTransform>();
        backdropRect.anchorMin = Vector2.zero;
        backdropRect.anchorMax = Vector2.one;
        backdropRect.offsetMin = Vector2.zero;
        backdropRect.offsetMax = Vector2.zero;
        var backdrop = backdropObject.GetComponent<Image>();
        backdrop.color = Color.black;
        backdrop.raycastTarget = true;

        tilesRoot = new GameObject("Tiles", typeof(RectTransform));
        tilesRoot.transform.SetParent(canvasRoot.transform, false);
        var tilesRect = tilesRoot.GetComponent<RectTransform>();
        tilesRect.anchorMin = Vector2.zero;
        tilesRect.anchorMax = Vector2.one;
        tilesRect.offsetMin = Vector2.zero;
        tilesRect.offsetMax = Vector2.zero;

        tileImages = new RawImage[GridSize * GridSize];
        tileRects = new RectTransform[GridSize * GridSize];
        noisePixels = new Color32[NoiseSize * NoiseSize];
        noiseTexture = new Texture2D(NoiseSize, NoiseSize, TextureFormat.RGBA32, false);
        noiseTexture.filterMode = FilterMode.Point;
        noiseTexture.wrapMode = TextureWrapMode.Repeat;
        noiseTexture.hideFlags = HideFlags.HideAndDontSave;
        RefreshNoiseTexture();

        for (int row = 0; row < GridSize; row++)
        {
            for (int column = 0; column < GridSize; column++)
            {
                var tileObject = new GameObject(
                    "Tile_" + row + "_" + column,
                    typeof(RectTransform),
                    typeof(CanvasRenderer),
                    typeof(RawImage));
                tileObject.transform.SetParent(tilesRoot.transform, false);
                var tileRect = tileObject.GetComponent<RectTransform>();
                tileRect.anchorMin = new Vector2(
                    (float)column / GridSize,
                    1f - (float)(row + 1) / GridSize);
                tileRect.anchorMax = new Vector2(
                    (float)(column + 1) / GridSize,
                    1f - (float)row / GridSize);
                tileRect.offsetMin = new Vector2(BlockGap, BlockGap);
                tileRect.offsetMax = new Vector2(-BlockGap, -BlockGap);

                tileRect.localScale = Vector3.one;
                tileRect.localRotation = Quaternion.identity;

                var tile = tileObject.GetComponent<RawImage>();
                tile.texture = screenshot != null ? screenshot : Texture2D.blackTexture;
                tile.color = Color.white;
                tile.uvRect = new Rect(0f, 0f, 1f, 1f);
                tile.raycastTarget = false;

                int index = row * GridSize + column;
                tileImages[index] = tile;
                tileRects[index] = tileRect;
            }
        }

        tilesRoot.SetActive(false);

        flashObject = new GameObject(
            "Flash",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image));
        flashObject.transform.SetParent(canvasRoot.transform, false);
        var flashRect = flashObject.GetComponent<RectTransform>();
        flashRect.anchorMin = Vector2.zero;
        flashRect.anchorMax = Vector2.one;
        flashRect.offsetMin = Vector2.zero;
        flashRect.offsetMax = Vector2.zero;
        flashImage = flashObject.GetComponent<Image>();
        flashImage.color = Color.black;
        flashImage.raycastTarget = true;
    }

    IEnumerator DisplayTiles()
    {
        int snowCount = Mathf.Clamp(TileSnowCount, 0, tileImages.Length);
        int blackCount = Mathf.Clamp(TileBlackCount, 0, tileImages.Length - snowCount);
        var order = new List<int>(tileImages.Length);
        for (int i = 0; i < tileImages.Length; i++)
            order.Add(i);

        for (int i = order.Count - 1; i > 0; i--)
        {
            int swap = UnityEngine.Random.Range(0, i + 1);
            int held = order[i];
            order[i] = order[swap];
            order[swap] = held;
        }

        var snowTiles = new bool[tileImages.Length];
        var blackTiles = new bool[tileImages.Length];
        for (int i = 0; i < snowCount; i++)
            snowTiles[order[i]] = true;
        for (int i = 0; i < blackCount; i++)
            blackTiles[order[snowCount + i]] = true;

        for (int i = 0; i < tileImages.Length; i++)
        {
            RawImage tile = tileImages[i];
            if (tile == null)
                continue;

            if (snowTiles[i])
            {
                tile.texture = noiseTexture;
                tile.color = Color.white;
            }
            else if (blackTiles[i])
            {
                tile.texture = Texture2D.whiteTexture;
                tile.color = Color.black;
            }
            else
            {
                tile.texture = screenshot != null ? screenshot : Texture2D.blackTexture;
                tile.color = Color.white;
            }
        }

        ShuffleTilePositions();

        float startTime = Time.unscaledTime;
        float nextShuffle = startTime + 1f / TileShuffleStartRate;
        float nextNoiseRefresh = 0f;

        while (true)
        {
            float elapsed = Time.unscaledTime - startTime;
            if (elapsed >= TileDisplaySeconds)
                break;

            while (Time.unscaledTime >= nextShuffle)
            {
                ShuffleTilePositions();
                float progress = Mathf.Clamp01((nextShuffle - startTime) / TileDisplaySeconds);
                float rate = Mathf.Lerp(TileShuffleStartRate, TileShuffleEndRate, progress);
                nextShuffle += 1f / Mathf.Max(0.01f, rate);
            }

            if (Time.unscaledTime >= nextNoiseRefresh)
            {
                RefreshNoiseTexture();
                nextNoiseRefresh = Time.unscaledTime + NoiseRefreshSeconds;
            }

            yield return null;
        }
    }

    void ShuffleTilePositions()
    {
        if (tileRects == null || tileRects.Length == 0)
            return;

        var slots = new List<int>(tileRects.Length);
        for (int i = 0; i < tileRects.Length; i++)
            slots.Add(i);

        for (int i = slots.Count - 1; i > 0; i--)
        {
            int swap = UnityEngine.Random.Range(0, i + 1);
            int held = slots[i];
            slots[i] = slots[swap];
            slots[swap] = held;
        }

        for (int i = 0; i < tileRects.Length; i++)
        {
            RectTransform rect = tileRects[i];
            if (rect == null)
                continue;

            int slot = slots[i];
            int row = slot / GridSize;
            int column = slot % GridSize;
            rect.anchorMin = new Vector2(
                (float)column / GridSize,
                1f - (float)(row + 1) / GridSize);
            rect.anchorMax = new Vector2(
                (float)(column + 1) / GridSize,
                1f - (float)row / GridSize);
            rect.offsetMin = new Vector2(BlockGap, BlockGap);
            rect.offsetMax = new Vector2(-BlockGap, -BlockGap);
        }
    }

    void RefreshNoiseTexture()
    {
        if (noiseTexture == null || noisePixels == null)
            return;

        for (int i = 0; i < noisePixels.Length; i++)
        {
            byte value = (byte)UnityEngine.Random.Range(0, 256);
            noisePixels[i] = new Color32(value, value, value, 255);
        }

        noiseTexture.SetPixels32(noisePixels);
        noiseTexture.Apply(false, false);
    }

    void ShowFlash(Color color)
    {
        if (flashObject != null)
            flashObject.SetActive(true);
        if (flashImage != null)
            flashImage.color = color;
    }

    void Finish()
    {
        if (canvasRoot != null)
        {
            canvasRoot.SetActive(false);
            Destroy(canvasRoot);
            canvasRoot = null;
        }

        if (screenshot != null)
        {
            Destroy(screenshot);
            screenshot = null;
        }

        if (noiseTexture != null)
        {
            Destroy(noiseTexture);
            noiseTexture = null;
        }

        DesktopPetWindow.SetFullWindowRegion(false);
        RestoreEffectUi();

        Action done = onFinished;
        onFinished = null;
        Destroy(gameObject);
        if (done != null)
            done();
    }

    void RestoreEffectUi()
    {
        if (!effectUiHidden)
            return;

        effectUiHidden = false;
        RestoreMood();
        if (previewMode)
            DailyMiniGame.ShowAfterEffect();
        else
            DailyMiniGame.ForgetEffectHide();
        PetPhone.SetPhoneUiHidden(false);
        PetJourney.SetGalleryHidden(false);
        PetPhone.SetPhoneIconVisible(phoneIconWasVisible);
        if (adminWasOpen)
        {
            adminWasOpen = false;
            AdminPanel.ShowAfterEffect();
        }
    }

    void RestoreMood()
    {
        if (!moodHidden)
            return;

        moodHidden = false;
        PetJourney.SetMoodBarVisible(true);
    }

    void OnDestroy()
    {
        DesktopPetWindow.SetFullWindowRegion(false);
        RestoreEffectUi();
    }
}
