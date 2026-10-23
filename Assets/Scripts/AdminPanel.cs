using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using ScreamJam.Debugging;
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
    Coroutine outputRoutine;
    Coroutine verifyRoutine;
    bool verifying;
    bool accessGranted;
    int[] colorSequence;

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
            accessGranted = false;
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

        GenerateColorSequence();
        if (outputRoutine != null)
            StopCoroutine(outputRoutine);
        outputRoutine = StartCoroutine(StreamTerminal());
    }

    IEnumerator StreamTerminal()
    {
        if (terminalText == null)
            yield break;

        terminalText.text = string.Empty;
        for (int i = 0; i < TerminalLines.Length; i++)
        {
            if (i > 0)
                yield return new WaitForSecondsRealtime(UnityEngine.Random.Range(0.3f, 0.9f));

            if (i > 0)
                terminalText.text += "\n";
            terminalText.text += TerminalLines[i];
        }

        if (colorSequence != null && colorSequence.Length > 0)
            terminalText.text += "  " + ColorHintMarkup();
        outputRoutine = null;
    }

    void GenerateColorSequence()
    {
        // Classic minesweeper number colors: 1 blue, 2 green, 3 red.
        // Seed all three colors first, then fill and shuffle so every color
        // appears at least once in the six-square hint.
        colorSequence = new int[6];
        colorSequence[0] = 1;
        colorSequence[1] = 2;
        colorSequence[2] = 3;
        for (int i = 3; i < colorSequence.Length; i++)
        {
            int color = UnityEngine.Random.Range(0, 3);
            colorSequence[i] = color == 0 ? 3 : (color == 1 ? 2 : 1);
        }

        for (int i = colorSequence.Length - 1; i > 0; i--)
        {
            int swap = UnityEngine.Random.Range(0, i + 1);
            int value = colorSequence[i];
            colorSequence[i] = colorSequence[swap];
            colorSequence[swap] = value;
        }
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
        if (passwordInput == null || verifying || accessGranted)
            return;

        string entered = string.IsNullOrEmpty(raw) ? string.Empty : raw.Trim();
        if (string.IsNullOrEmpty(entered))
        {
            passwordInput.ActivateInputField();
            return;
        }

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
                    ? "<color=#2f8f57>[SUCCESS]</color> ACCESS GRANTED"
                    : "<color=#a33d3d>[WARN]</color> ACCESS DENIED");
        }

        verifying = false;
        verifyRoutine = null;
        accessGranted = accepted;
        if (!accepted && passwordInput != null)
            passwordInput.ActivateInputField();

        if (accepted)
            StartCoroutine(OpenConsoleAfterGrant());
    }

    IEnumerator OpenConsoleAfterGrant()
    {
        yield return new WaitForSecondsRealtime(0.9f);
        Hide();
        yield return new WaitForSecondsRealtime(0.2f);
        DebugConsole.OpenConsole();
        DebugConsole.WriteLine("Access granted");
        yield return new WaitForSecondsRealtime(0.5f);
        DebugConsole.WriteLine("Welcome user admin_OcOm!");
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

        terminalText = MakeText(
            "Output",
            terminal,
            string.Empty,
            12,
            TextAnchor.UpperLeft,
            TextColor);
        terminalText.supportRichText = true;
        terminalText.lineSpacing = 1.4375f;
        var terminalTextRect = terminalText.rectTransform;
        terminalTextRect.anchorMin = Vector2.zero;
        terminalTextRect.anchorMax = Vector2.one;
        terminalTextRect.offsetMin = new Vector2(20f, 52f);
        terminalTextRect.offsetMax = new Vector2(-20f, -12f);
        terminalText.raycastTarget = false;

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
        passwordInput.contentType = InputField.ContentType.Password;
        passwordInput.asteriskChar = '*';
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

    static string LocalIp()
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
