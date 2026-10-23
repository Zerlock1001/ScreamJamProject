using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using ScreamJam.Debugging;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Daily 5x5 minesweeper mini game. The cleared board contains only clue
/// numbers 1-3, with each count kept between 1 and 9. The admin password is
/// built from the counts selected by the colored squares in the admin panel.
/// </summary>
public class DailyMiniGame : MonoBehaviour
{
    public static bool Finished => instance != null && instance.gameFinished;
    public static bool HasGame => instance != null && instance.boardGenerated;

    public static int CountNumber(int number)
    {
        if (instance == null || !instance.boardGenerated || instance.adjacent == null
            || number < 1 || number > MaxClue)
            return 0;

        int count = 0;
        for (int i = 0; i < instance.adjacent.Length; i++)
        {
            if (!instance.mines[i] && instance.adjacent[i] == number)
                count++;
        }
        return count;
    }

    public static string PasswordFor(IList<int> numbers)
    {
        if (instance == null || !instance.boardGenerated || numbers == null || numbers.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        for (int i = 0; i < numbers.Count; i++)
            builder.Append(CountNumber(numbers[i]));
        return builder.ToString();
    }

    const int Size = 5;
    const int MineCount = 5;
    const int MaxClue = 3;
    const int CanvasOrder = 20000;
    const float RetrySeconds = 3f;
    const float PopupSlideSeconds = 0.34f;
    const float PopupShrinkSeconds = 0.18f;
    const float WindowOpenSeconds = 0.24f;
    const float WindowCloseSeconds = 0.18f;
    const float PopupBaseScale = 1.2f;
    const float StatusWidth = 252f;
    const float StatusHeight = 52f;
    const float GridSize = 210f;
    const float ContentGap = 8f;

    static DailyMiniGame instance;

    GameObject popup;
    GameObject window;
    RectTransform popupRect;
    RectTransform windowRect;
    CanvasGroup popupGroup;
    CanvasGroup windowGroup;
    Vector2 popupTargetPosition;
    readonly Vector2 windowHomePosition = new Vector2(-24f, -24f);
    Coroutine popupAnimRoutine;
    Coroutine windowAnimRoutine;
    Coroutine popupHoverRoutine;
    RectTransform popupVisualRect;
    Text popupTitle;
    RectTransform popupSmileyRect;
    Text statusText;
    Text flagsText;
    Image statusSmileyImage;
    RectTransform gridRoot;
    Coroutine retryRoutine;
    bool popupHovered;

    readonly MinesweeperCell[] cells = new MinesweeperCell[Size * Size];
    bool[] mines;
    bool[] revealed;
    bool[] flagged;
    int[] adjacent;

    bool boardGenerated;
    bool gameFinished;
    bool gameLost;
    bool inputLocked;
    bool hasStarted;
    bool windowTransitioning;
    int flagsPlaced;
    int revealedSafe;
    int explodedIndex = -1;

    static Sprite bevelSprite;
    static Sprite cellBevelSprite;
    static Sprite roundedSprite;
    static Sprite flatSprite;
    static Sprite flagSprite;
    static Sprite mineSprite;
    static Sprite smileySprite;
    static Sprite classicSmileySprite;
    static Sprite deadSmileySprite;

    static readonly int[] NeighborX = { -1, 0, 1, -1, 1, -1, 0, 1 };
    static readonly int[] NeighborY = { -1, -1, -1, 0, 0, 1, 1, 1 };

    public static void Trigger()
    {
        DebugConsole.CloseConsole();
        Ensure();
        if (instance == null)
            return;

        if (!instance.boardGenerated)
            instance.NewRound();

        instance.ShowDailyPopup();
    }

    static void Ensure()
    {
        if (instance != null)
            return;

        var host = new GameObject(
            "DailyMiniGameCanvas",
            typeof(RectTransform),
            typeof(Canvas),
            typeof(CanvasScaler),
            typeof(GraphicRaycaster),
            typeof(DailyMiniGame));
        var canvas = host.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = CanvasOrder;
        var scaler = host.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;

        instance = host.GetComponent<DailyMiniGame>();
        instance.Build(host.transform);
    }

    void Build(Transform canvas)
    {
        BuildPopup(canvas);
        BuildWindow(canvas);
        window.SetActive(false);
        popup.SetActive(false);
    }

    void BuildPopup(Transform canvas)
    {
        popup = new GameObject(
            "DailyMiniGamePopup",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Button),
            typeof(CanvasGroup));
        popup.transform.SetParent(canvas, false);
        popupRect = popup.GetComponent<RectTransform>();
        popupRect.anchorMin = new Vector2(1f, 1f);
        popupRect.anchorMax = new Vector2(1f, 1f);
        popupRect.pivot = new Vector2(1f, 1f);
        popupRect.anchoredPosition = new Vector2(-24f, -24f);
        popupRect.sizeDelta = new Vector2(300f, 64f);
        popupRect.localScale = Vector3.one * PopupBaseScale;
        popupTargetPosition = popupRect.anchoredPosition;
        popupGroup = popup.GetComponent<CanvasGroup>();

        var visual = new GameObject("Visual", typeof(RectTransform));
        visual.transform.SetParent(popup.transform, false);
        popupVisualRect = visual.GetComponent<RectTransform>();
        popupVisualRect.anchorMin = Vector2.zero;
        popupVisualRect.anchorMax = Vector2.one;
        popupVisualRect.offsetMin = Vector2.zero;
        popupVisualRect.offsetMax = Vector2.zero;
        popupVisualRect.pivot = new Vector2(0.5f, 0.5f);
        popupVisualRect.localScale = Vector3.one;

        var backgroundObject = new GameObject("Background", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        backgroundObject.transform.SetParent(popupVisualRect, false);
        var backgroundRect = backgroundObject.GetComponent<RectTransform>();
        backgroundRect.anchorMin = Vector2.zero;
        backgroundRect.anchorMax = Vector2.one;
        backgroundRect.offsetMin = Vector2.zero;
        backgroundRect.offsetMax = Vector2.zero;

        var background = backgroundObject.GetComponent<Image>();
        background.sprite = BevelSprite();
        background.type = Image.Type.Sliced;
        background.color = Color.white;
        background.raycastTarget = true;

        var button = popup.GetComponent<Button>();
        button.targetGraphic = background;
        button.transition = Selectable.Transition.None;
        button.onClick.AddListener(OpenWindow);

        popupTitle = MakeText("Title", popupVisualRect, "DAILY MINI GAME!", 17, TextAnchor.MiddleLeft, new Color(0.08f, 0.08f, 0.09f, 1f));
        popupTitle.fontStyle = FontStyle.Bold;
        var titleRect = popupTitle.rectTransform;
        titleRect.anchorMin = Vector2.zero;
        titleRect.anchorMax = Vector2.one;
        titleRect.offsetMin = new Vector2(14f, 0f);
        titleRect.offsetMax = new Vector2(-72f, 0f);
        popupTitle.raycastTarget = false;

        var smiley = new GameObject("Smiley", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        smiley.transform.SetParent(popupVisualRect, false);
        popupSmileyRect = smiley.GetComponent<RectTransform>();
        popupSmileyRect.anchorMin = new Vector2(1f, 0.5f);
        popupSmileyRect.anchorMax = new Vector2(1f, 0.5f);
        popupSmileyRect.pivot = new Vector2(1f, 0.5f);
        popupSmileyRect.anchoredPosition = new Vector2(-10f, -4f);
        popupSmileyRect.sizeDelta = new Vector2(40f, 40f);
        popupSmileyRect.localScale = Vector3.one;
        var smileyImage = smiley.GetComponent<Image>();
        smileyImage.sprite = SmileySprite();
        smileyImage.preserveAspect = true;
        smileyImage.raycastTarget = false;

        var hover = backgroundObject.AddComponent<PopupHoverRelay>();
        hover.Changed = SetPopupHover;
    }

    void BuildWindow(Transform canvas)
    {
        window = new GameObject(
            "MinesweeperWindow",
            typeof(RectTransform),
            typeof(CanvasRenderer),
            typeof(Image),
            typeof(CanvasGroup));
        window.transform.SetParent(canvas, false);
        windowRect = window.GetComponent<RectTransform>();
        windowRect.anchorMin = new Vector2(1f, 1f);
        windowRect.anchorMax = new Vector2(1f, 1f);
        windowRect.pivot = new Vector2(1f, 1f);
        windowRect.anchoredPosition = windowHomePosition;
        windowRect.sizeDelta = new Vector2(310.5f, 345f);
        windowGroup = window.GetComponent<CanvasGroup>();
        float contentHeight = StatusHeight + ContentGap + GridSize;
        float contentTop = (windowRect.sizeDelta.y - contentHeight) * 0.5f;

        var background = window.GetComponent<Image>();
        background.sprite = RoundedPanelSprite();
        background.type = Image.Type.Sliced;
        background.color = Color.white;
        background.raycastTarget = true;

        var statusBar = MakePanel("StatusBar", window.transform, BevelSprite(), Color.white);
        var statusImage = statusBar.GetComponent<Image>();
        statusImage.type = Image.Type.Sliced;
        var statusDrag = statusBar.gameObject.AddComponent<WindowDrag>();
        statusDrag.Target = windowRect;
        var statusRect = statusBar;
        statusRect.anchorMin = new Vector2(0.5f, 1f);
        statusRect.anchorMax = new Vector2(0.5f, 1f);
        statusRect.pivot = new Vector2(0.5f, 1f);
        statusRect.anchoredPosition = new Vector2(0f, -contentTop);
        statusRect.sizeDelta = new Vector2(StatusWidth, StatusHeight);

        var mineCounter = MakePanel("MineCounter", statusBar, FlatSprite(), new Color(0.055f, 0.055f, 0.06f, 1f));
        mineCounter.anchorMin = new Vector2(0f, 0.5f);
        mineCounter.anchorMax = new Vector2(0f, 0.5f);
        mineCounter.pivot = new Vector2(0f, 0.5f);
        mineCounter.anchoredPosition = new Vector2(10f, 0f);
        mineCounter.sizeDelta = new Vector2(58f, 34f);
        var mineOutline = mineCounter.gameObject.AddComponent<Outline>();
        mineOutline.effectColor = new Color(0.34f, 0.34f, 0.35f, 1f);
        mineOutline.effectDistance = new Vector2(1f, -1f);

        statusText = MakeText("Mines", mineCounter, "005", 20, TextAnchor.MiddleCenter, new Color(0.88f, 0.12f, 0.09f, 1f));
        statusText.fontStyle = FontStyle.Bold;
        Stretch(statusText.rectTransform, 2f);
        statusText.raycastTarget = false;

        var flagCounter = MakePanel("FlagCounter", statusBar, FlatSprite(), new Color(0.055f, 0.055f, 0.06f, 1f));
        flagCounter.anchorMin = new Vector2(1f, 0.5f);
        flagCounter.anchorMax = new Vector2(1f, 0.5f);
        flagCounter.pivot = new Vector2(1f, 0.5f);
        flagCounter.anchoredPosition = new Vector2(-38f, 0f);
        flagCounter.sizeDelta = new Vector2(58f, 34f);
        var flagOutline = flagCounter.gameObject.AddComponent<Outline>();
        flagOutline.effectColor = new Color(0.34f, 0.34f, 0.35f, 1f);
        flagOutline.effectDistance = new Vector2(1f, -1f);

        flagsText = MakeText("Flags", flagCounter, "000", 20, TextAnchor.MiddleCenter, new Color(0.88f, 0.12f, 0.09f, 1f));
        flagsText.fontStyle = FontStyle.Bold;
        Stretch(flagsText.rectTransform, 2f);
        flagsText.raycastTarget = false;

        var faceFrame = MakePanel("FaceFrame", statusBar, BevelSprite(), Color.white);
        var faceFrameImage = faceFrame.GetComponent<Image>();
        faceFrameImage.type = Image.Type.Sliced;
        faceFrameImage.raycastTarget = false;
        faceFrame.anchorMin = new Vector2(0.5f, 0.5f);
        faceFrame.anchorMax = new Vector2(0.5f, 0.5f);
        faceFrame.pivot = new Vector2(0.5f, 0.5f);
        faceFrame.anchoredPosition = Vector2.zero;
        faceFrame.sizeDelta = new Vector2(42f, 42f);

        var statusSmiley = new GameObject("Smiley", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        statusSmiley.transform.SetParent(faceFrame, false);
        var smileyRect = statusSmiley.GetComponent<RectTransform>();
        smileyRect.anchorMin = new Vector2(0.5f, 0.5f);
        smileyRect.anchorMax = new Vector2(0.5f, 0.5f);
        smileyRect.pivot = new Vector2(0.5f, 0.5f);
        smileyRect.anchoredPosition = Vector2.zero;
        smileyRect.sizeDelta = new Vector2(30f, 30f);
        statusSmileyImage = statusSmiley.GetComponent<Image>();
        statusSmileyImage.sprite = ClassicSmileySprite();
        statusSmileyImage.preserveAspect = true;
        statusSmileyImage.raycastTarget = false;

        var closeButton = MakeButton("Close", statusBar, new Color(0.84f, 0.84f, 0.85f, 1f), "X", 14);
        var closeRect = closeButton.GetComponent<RectTransform>();
        closeRect.anchorMin = new Vector2(1f, 0.5f);
        closeRect.anchorMax = new Vector2(1f, 0.5f);
        closeRect.pivot = new Vector2(1f, 0.5f);
        closeRect.anchoredPosition = new Vector2(-5f, 0f);
        closeRect.sizeDelta = new Vector2(22f, 22f);
        closeButton.onClick.AddListener(CloseWindow);

        var gridObject = new GameObject("Grid", typeof(RectTransform), typeof(Image), typeof(GridLayoutGroup));
        gridObject.transform.SetParent(window.transform, false);
        gridRoot = gridObject.GetComponent<RectTransform>();
        gridRoot.anchorMin = new Vector2(0.5f, 1f);
        gridRoot.anchorMax = new Vector2(0.5f, 1f);
        gridRoot.pivot = new Vector2(0.5f, 1f);
        gridRoot.anchoredPosition = new Vector2(0f, -(contentTop + StatusHeight + ContentGap));
        gridRoot.sizeDelta = new Vector2(GridSize, GridSize);
        var gridBackground = gridObject.GetComponent<Image>();
        gridBackground.sprite = FlatSprite();
        gridBackground.color = new Color(0.66f, 0.66f, 0.67f, 1f);
        gridBackground.raycastTarget = true;
        var grid = gridObject.GetComponent<GridLayoutGroup>();
        grid.cellSize = new Vector2(42f, 42f);
        grid.spacing = Vector2.zero;
        grid.padding = new RectOffset(0, 0, 0, 0);
        grid.childAlignment = TextAnchor.UpperLeft;

        for (int i = 0; i < Size * Size; i++)
            CreateCell(i);
    }

    void CreateCell(int index)
    {
        var cellObject = new GameObject("Cell", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Outline), typeof(MinesweeperCell));
        cellObject.transform.SetParent(gridRoot, false);
        var image = cellObject.GetComponent<Image>();
        image.sprite = CellBevelSprite();
        image.type = Image.Type.Sliced;
        image.color = Color.white;
        image.raycastTarget = true;

        var outline = cellObject.GetComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.18f);
        outline.effectDistance = new Vector2(1f, -1f);

        var flagObject = new GameObject("Flag", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        flagObject.transform.SetParent(cellObject.transform, false);
        var flagRect = flagObject.GetComponent<RectTransform>();
        flagRect.anchorMin = new Vector2(0.5f, 0.5f);
        flagRect.anchorMax = new Vector2(0.5f, 0.5f);
        flagRect.pivot = new Vector2(0.5f, 0.5f);
        flagRect.anchoredPosition = new Vector2(0f, -1f);
        flagRect.sizeDelta = new Vector2(24f, 24f);
        var flagImage = flagObject.GetComponent<Image>();
        flagImage.sprite = FlagSprite();
        flagImage.preserveAspect = true;
        flagImage.raycastTarget = false;
        flagObject.SetActive(false);

        var mineObject = new GameObject("Mine", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        mineObject.transform.SetParent(cellObject.transform, false);
        var mineRect = mineObject.GetComponent<RectTransform>();
        mineRect.anchorMin = new Vector2(0.5f, 0.5f);
        mineRect.anchorMax = new Vector2(0.5f, 0.5f);
        mineRect.pivot = new Vector2(0.5f, 0.5f);
        mineRect.anchoredPosition = new Vector2(0f, -1f);
        mineRect.sizeDelta = new Vector2(24f, 24f);
        var mineImage = mineObject.GetComponent<Image>();
        mineImage.sprite = MineSprite();
        mineImage.preserveAspect = true;
        mineImage.raycastTarget = false;
        mineObject.SetActive(false);

        var text = MakeText("Value", cellObject.transform, string.Empty, 22, TextAnchor.MiddleCenter, Color.black);
        text.fontStyle = FontStyle.Bold;
        Stretch(text.rectTransform, 2f);
        text.raycastTarget = false;

        var cell = cellObject.GetComponent<MinesweeperCell>();
        cell.Index = index;
        cell.Clicked = OnCellClicked;
        cell.Flag = flagImage;
        cell.Mine = mineImage;
        cells[index] = cell;
    }

    void ShowDailyPopup()
    {
        if (popup == null || window == null)
            return;

        if (window.activeSelf || windowTransitioning)
        {
            window.transform.SetAsLastSibling();
            return;
        }

        if (popup.activeSelf)
        {
            popup.transform.SetAsLastSibling();
            return;
        }

        if (popupAnimRoutine != null)
        {
            StopCoroutine(popupAnimRoutine);
            popupAnimRoutine = null;
        }

        popup.SetActive(true);
        popup.transform.SetAsLastSibling();
        popupGroup.alpha = 0f;
        popupGroup.interactable = true;
        popupGroup.blocksRaycasts = true;
        popupRect.localScale = Vector3.one * PopupBaseScale;
        popupRect.anchoredPosition = popupTargetPosition + new Vector2(0f, 170f);
        SetPopupHover(false);
        popupAnimRoutine = StartCoroutine(AnimatePopupSlideIn());
    }

    IEnumerator AnimatePopupSlideIn()
    {
        Vector2 start = popupRect.anchoredPosition;
        float elapsed = 0f;
        while (elapsed < PopupSlideSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / PopupSlideSeconds);
            float eased = 1f - Mathf.Pow(1f - t, 3f);
            popupRect.anchoredPosition = Vector2.LerpUnclamped(start, popupTargetPosition, eased);
            popupGroup.alpha = Mathf.Clamp01(t * 2.8f);
            yield return null;
        }

        popupRect.anchoredPosition = popupTargetPosition;
        popupGroup.alpha = 1f;
        popupAnimRoutine = null;
    }

    void HidePopupForGame()
    {
        if (popup == null || !popup.activeSelf)
            return;

        if (popupAnimRoutine != null)
        {
            StopCoroutine(popupAnimRoutine);
            popupAnimRoutine = null;
        }

        SetPopupHover(false);
        popupGroup.interactable = false;
        popupGroup.blocksRaycasts = false;
        popupAnimRoutine = StartCoroutine(AnimatePopupShrinkOut());
    }

    IEnumerator AnimatePopupShrinkOut()
    {
        Vector3 startScale = popupRect.localScale;
        float startAlpha = popupGroup.alpha;
        float elapsed = 0f;
        while (elapsed < PopupShrinkSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / PopupShrinkSeconds);
            popupRect.localScale = Vector3.LerpUnclamped(startScale, Vector3.one * (PopupBaseScale * 0.82f), t);
            popupGroup.alpha = Mathf.LerpUnclamped(startAlpha, 0f, t);
            yield return null;
        }

        popup.SetActive(false);
        popupRect.localScale = Vector3.one * PopupBaseScale;
        popupGroup.alpha = 1f;
        popupGroup.interactable = true;
        popupGroup.blocksRaycasts = true;
        popupAnimRoutine = null;
    }

    void SetPopupHover(bool hovered)
    {
        popupHovered = hovered;
        bool showFinished = hovered && gameFinished;
        if (popupTitle != null)
        {
            popupTitle.text = showFinished ? "FINISHED" : "DAILY MINI GAME!";
            popupTitle.color = showFinished
                ? new Color(0.12f, 0.38f, 0.20f, 1f)
                : new Color(0.08f, 0.08f, 0.09f, 1f);
        }

        if (popupSmileyRect == null || popupVisualRect == null)
            return;

        float popupTarget = hovered ? 1.05f : 1f;
        float iconTarget = hovered ? 1.10f : 1f;
        if (popupHoverRoutine != null)
            StopCoroutine(popupHoverRoutine);
        popupHoverRoutine = StartCoroutine(AnimatePopupHover(popupTarget, iconTarget));
    }

    IEnumerator AnimatePopupHover(float popupTarget, float iconTarget)
    {
        float popupStart = popupVisualRect.localScale.x;
        float iconStart = popupSmileyRect.localScale.x;
        float elapsed = 0f;
        while (elapsed < 0.12f)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / 0.12f);
            float eased = 1f - Mathf.Pow(1f - t, 3f);
            float popupScale = Mathf.LerpUnclamped(popupStart, popupTarget, eased);
            float iconScale = Mathf.LerpUnclamped(iconStart, iconTarget, eased);
            popupVisualRect.localScale = Vector3.one * popupScale;
            popupSmileyRect.localScale = Vector3.one * iconScale;
            yield return null;
        }

        popupVisualRect.localScale = Vector3.one * popupTarget;
        popupSmileyRect.localScale = Vector3.one * iconTarget;
        popupHoverRoutine = null;
    }

    void RefreshPopupHoverPresentation()
    {
        SetPopupHover(popupHovered);
    }

    void OpenWindow()
    {
        if (!boardGenerated)
            NewRound();

        if (window.activeSelf)
        {
            window.transform.SetAsLastSibling();
            return;
        }

        if (windowAnimRoutine != null)
        {
            StopCoroutine(windowAnimRoutine);
            windowAnimRoutine = null;
        }

        windowTransitioning = false;
        windowRect.anchoredPosition = windowHomePosition;
        window.SetActive(true);
        window.transform.SetAsLastSibling();
        windowGroup.alpha = 0f;
        windowGroup.interactable = false;
        windowGroup.blocksRaycasts = false;
        windowRect.localScale = Vector3.one * 0.86f;
        windowAnimRoutine = StartCoroutine(AnimateWindowOpen());
        HidePopupForGame();
        UpdateStatus();
    }

    IEnumerator AnimateWindowOpen()
    {
        float elapsed = 0f;
        while (elapsed < WindowOpenSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / WindowOpenSeconds);
            float eased = 1f - Mathf.Pow(1f - t, 3f);
            windowRect.localScale = Vector3.LerpUnclamped(Vector3.one * 0.86f, Vector3.one, eased);
            windowGroup.alpha = eased;
            yield return null;
        }

        windowRect.localScale = Vector3.one;
        windowGroup.alpha = 1f;
        windowGroup.interactable = true;
        windowGroup.blocksRaycasts = true;
        windowAnimRoutine = null;
    }

    void CloseWindow()
    {
        if (!window.activeSelf || windowTransitioning)
            return;

        windowTransitioning = true;
        if (windowAnimRoutine != null)
        {
            StopCoroutine(windowAnimRoutine);
            windowAnimRoutine = null;
        }

        windowGroup.interactable = false;
        windowGroup.blocksRaycasts = false;
        windowAnimRoutine = StartCoroutine(AnimateWindowClose());
    }

    IEnumerator AnimateWindowClose()
    {
        Vector3 startScale = windowRect.localScale;
        float startAlpha = windowGroup.alpha;
        float elapsed = 0f;
        while (elapsed < WindowCloseSeconds)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / WindowCloseSeconds);
            windowRect.localScale = Vector3.LerpUnclamped(startScale, Vector3.one * 0.86f, t);
            windowGroup.alpha = Mathf.LerpUnclamped(startAlpha, 0f, t);
            yield return null;
        }

        window.SetActive(false);
        windowRect.localScale = Vector3.one;
        windowGroup.alpha = 1f;
        windowGroup.interactable = true;
        windowGroup.blocksRaycasts = true;
        windowTransitioning = false;
        windowAnimRoutine = null;
        ShowDailyPopup();
    }

    void NewRound()
    {
        if (retryRoutine != null)
        {
            StopCoroutine(retryRoutine);
            retryRoutine = null;
        }

        mines = new bool[Size * Size];
        revealed = new bool[Size * Size];
        flagged = new bool[Size * Size];
        adjacent = new int[Size * Size];

        GenerateValidMines();
        boardGenerated = true;
        gameFinished = false;
        gameLost = false;
        inputLocked = false;
        hasStarted = false;
        flagsPlaced = 0;
        revealedSafe = 0;
        explodedIndex = -1;

        if (statusSmileyImage != null)
        {
            statusSmileyImage.sprite = ClassicSmileySprite();
            statusSmileyImage.color = Color.white;
        }
        RefreshPopupHoverPresentation();
        for (int i = 0; i < cells.Length; i++)
            UpdateCell(i);
        UpdateStatus();
    }

    void GenerateValidMines()
    {
        for (int attempt = 0; attempt < 4000; attempt++)
        {
            var available = new List<int>(Size * Size);
            for (int i = 0; i < Size * Size; i++)
            {
                mines[i] = false;
                available.Add(i);
            }

            for (int i = 0; i < MineCount; i++)
            {
                int pick = UnityEngine.Random.Range(0, available.Count);
                mines[available[pick]] = true;
                available.RemoveAt(pick);
            }

            ComputeAdjacent();
            if (BoardIsValid())
                return;
        }
    }

    bool BoardIsValid()
    {
        if (!CluesWithinLimit())
            return false;

        int ones = 0;
        int twos = 0;
        int threes = 0;
        for (int i = 0; i < Size * Size; i++)
        {
            if (mines[i])
                continue;

            switch (adjacent[i])
            {
                case 1: ones++; break;
                case 2: twos++; break;
                case 3: threes++; break;
            }
        }

        return ones >= 1 && ones <= 9
            && twos >= 1 && twos <= 9
            && threes >= 1 && threes <= 9;
    }

    bool CluesWithinLimit()
    {
        for (int i = 0; i < Size * Size; i++)
        {
            if (!mines[i] && adjacent[i] > MaxClue)
                return false;
        }
        return true;
    }

    void ComputeAdjacent()
    {
        for (int i = 0; i < Size * Size; i++)
        {
            if (mines[i])
            {
                adjacent[i] = 0;
                continue;
            }

            int x = i % Size;
            int y = i / Size;
            int count = 0;
            for (int n = 0; n < NeighborX.Length; n++)
            {
                int nx = x + NeighborX[n];
                int ny = y + NeighborY[n];
                if (nx < 0 || nx >= Size || ny < 0 || ny >= Size)
                    continue;
                if (mines[ny * Size + nx])
                    count++;
            }
            adjacent[i] = count;
        }
    }

    void OnCellClicked(int index, PointerEventData.InputButton button)
    {
        if (inputLocked || index < 0 || index >= cells.Length)
            return;

        if (button == PointerEventData.InputButton.Right)
            ToggleFlag(index);
        else if (button == PointerEventData.InputButton.Left)
            Reveal(index);
    }

    void Reveal(int index)
    {
        if (inputLocked || revealed[index] || flagged[index])
            return;

        if (!hasStarted)
        {
            hasStarted = true;
            if (mines[index])
                RelocateMine(index);
        }

        if (mines[index])
        {
            Lose(index);
            return;
        }

        var stack = new Stack<int>();
        stack.Push(index);

        while (stack.Count > 0)
        {
            int current = stack.Pop();
            if (revealed[current] || flagged[current])
                continue;

            revealed[current] = true;
            revealedSafe++;
            UpdateCell(current);

            if (adjacent[current] != 0)
                continue;

            int x = current % Size;
            int y = current / Size;
            for (int n = 0; n < NeighborX.Length; n++)
            {
                int nx = x + NeighborX[n];
                int ny = y + NeighborY[n];
                if (nx < 0 || nx >= Size || ny < 0 || ny >= Size)
                    continue;

                int neighbor = ny * Size + nx;
                if (!mines[neighbor] && !revealed[neighbor] && !flagged[neighbor])
                    stack.Push(neighbor);
            }
        }

        if (revealedSafe >= Size * Size - MineCount)
            Win();
        else
            UpdateStatus();
    }

    void RelocateMine(int safeIndex)
    {
        mines[safeIndex] = false;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            int candidate = UnityEngine.Random.Range(0, Size * Size);
            if (candidate == safeIndex || mines[candidate])
                continue;

            mines[candidate] = true;
            ComputeAdjacent();
            if (BoardIsValid())
                return;

            mines[candidate] = false;
        }

        ComputeAdjacent();
    }

    void ToggleFlag(int index)
    {
        if (inputLocked || revealed[index])
            return;

        flagged[index] = !flagged[index];
        flagsPlaced += flagged[index] ? 1 : -1;
        UpdateCell(index);
        UpdateStatus();
    }

    void Lose(int index)
    {
        gameLost = true;
        inputLocked = true;
        explodedIndex = index;
        if (statusSmileyImage != null)
        {
            statusSmileyImage.sprite = DeadSmileySprite();
            statusSmileyImage.color = Color.white;
        }
        UpdateAllCells();
        UpdateStatus();

        if (retryRoutine != null)
            StopCoroutine(retryRoutine);
        retryRoutine = StartCoroutine(RetryAfterDelay());
    }

    IEnumerator RetryAfterDelay()
    {
        yield return new WaitForSecondsRealtime(RetrySeconds);
        retryRoutine = null;
        if (!gameFinished)
            NewRound();
    }

    void Win()
    {
        gameFinished = true;
        inputLocked = true;
        if (statusSmileyImage != null)
            statusSmileyImage.sprite = SmileySprite();
        for (int i = 0; i < Size * Size; i++)
        {
            if (mines[i] && !flagged[i])
            {
                flagged[i] = true;
                flagsPlaced++;
            }
        }

        UpdateAllCells();
        UpdateStatus();
        RefreshPopupHoverPresentation();
    }

    void UpdateAllCells()
    {
        for (int i = 0; i < cells.Length; i++)
            UpdateCell(i);
    }

    void UpdateCell(int index)
    {
        if (index < 0 || index >= cells.Length || cells[index] == null)
            return;

        var cell = cells[index];
        var image = cell.GetComponent<Image>();
        var text = cell.transform.Find("Value")?.GetComponent<Text>();
        if (image == null || text == null)
            return;

        bool showMine = mines[index] && (revealed[index] || gameLost);
        if (revealed[index] && !mines[index])
        {
            image.sprite = FlatSprite();
            image.type = Image.Type.Simple;
            image.color = index == explodedIndex
                ? new Color(0.82f, 0.25f, 0.25f, 1f)
                : new Color(0.76f, 0.76f, 0.76f, 1f);
        }
        else
        {
            image.sprite = CellBevelSprite();
            image.type = Image.Type.Sliced;
            image.color = Color.white;
        }

        bool showFlag = flagged[index] && !revealed[index];
        bool showMineVisual = showMine && !showFlag;
        if (cell.Flag != null)
            cell.Flag.gameObject.SetActive(showFlag);
        if (cell.Mine != null)
            cell.Mine.gameObject.SetActive(showMineVisual);
        if (showFlag)
        {
            text.text = string.Empty;
            return;
        }

        if (showMineVisual)
        {
            text.text = string.Empty;
            if (index == explodedIndex)
                image.color = new Color(0.82f, 0.25f, 0.25f, 1f);
            return;
        }

        if (!revealed[index])
        {
            text.text = string.Empty;
            return;
        }

        int value = adjacent[index];
        text.text = value > 0 ? value.ToString() : string.Empty;
        text.color = NumberColor(value);
    }

    static Color NumberColor(int value)
    {
        switch (value)
        {
            case 1: return new Color(0.02f, 0.08f, 0.86f, 1f);
            case 2: return new Color(0.04f, 0.48f, 0.08f, 1f);
            case 3: return new Color(0.84f, 0.04f, 0.04f, 1f);
            case 4: return new Color(0.04f, 0.04f, 0.46f, 1f);
            case 5: return new Color(0.48f, 0.04f, 0.04f, 1f);
            case 6: return new Color(0.04f, 0.44f, 0.44f, 1f);
            case 7: return new Color(0.06f, 0.06f, 0.06f, 1f);
            default: return new Color(0.28f, 0.28f, 0.28f, 1f);
        }
    }

    void UpdateStatus()
    {
        if (statusText != null)
            statusText.text = Mathf.Max(0, MineCount - flagsPlaced).ToString("000");
        if (flagsText != null)
            flagsText.text = flagsPlaced.ToString("000");
    }

    static RectTransform MakePanel(string name, Transform parent, Sprite sprite, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.sprite = sprite;
        image.type = Image.Type.Simple;
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
        image.sprite = FlatSprite();
        image.type = Image.Type.Simple;
        image.color = color;
        image.raycastTarget = true;
        var button = go.GetComponent<Button>();
        button.targetGraphic = image;
        var text = MakeText("Label", go.transform, label, fontSize, TextAnchor.MiddleCenter, Color.black);
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

    static Sprite BevelSprite()
    {
        if (bevelSprite == null)
        {
            bevelSprite = CreateBevelSprite(2, false);
            bevelSprite.hideFlags = HideFlags.HideAndDontSave;
        }
        return bevelSprite;
    }

    static Sprite CellBevelSprite()
    {
        if (cellBevelSprite == null)
        {
            cellBevelSprite = CreateBevelSprite(3, true);
            cellBevelSprite.hideFlags = HideFlags.HideAndDontSave;
        }
        return cellBevelSprite;
    }

    static Sprite CreateBevelSprite(int thickness, bool stronger)
    {
        const int size = 16;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[size * size];
        var border = new Color(0.45f, 0.45f, 0.46f, 1f);
        var light = new Color(1f, 1f, 1f, 1f);
        var dark = stronger
            ? new Color(0.38f, 0.38f, 0.39f, 1f)
            : new Color(0.47f, 0.47f, 0.48f, 1f);
        var middle = new Color(0.75f, 0.75f, 0.75f, 1f);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool edge = x == 0 || y == 0 || x == size - 1 || y == size - 1;
                bool topLeft = !edge && (x <= thickness || y >= size - 1 - thickness);
                bool bottomRight = !edge && (x >= size - 1 - thickness || y <= thickness);
                pixels[y * size + x] = edge ? border : (topLeft ? light : (bottomRight ? dark : middle));
            }
        }

        texture.SetPixels(pixels);
        texture.Apply(false, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        return Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f,
            0,
            SpriteMeshType.FullRect,
            new Vector4(5f, 5f, 5f, 5f));
    }

    static Sprite RoundedPanelSprite()
    {
        if (roundedSprite != null)
            return roundedSprite;

        const int size = 32;
        const int radius = 10;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[size * size];
        var fill = new Color(0.78f, 0.78f, 0.79f, 1f);
        var edge = new Color(0.34f, 0.34f, 0.35f, 1f);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (!InsideRoundedRect(x, y, size, radius))
                {
                    pixels[y * size + x] = new Color(0f, 0f, 0f, 0f);
                    continue;
                }

                bool boundary = !InsideRoundedRect(x - 1, y, size, radius)
                    || !InsideRoundedRect(x + 1, y, size, radius)
                    || !InsideRoundedRect(x, y - 1, size, radius)
                    || !InsideRoundedRect(x, y + 1, size, radius)
                    || !InsideRoundedRect(x - 2, y, size, radius)
                    || !InsideRoundedRect(x + 2, y, size, radius)
                    || !InsideRoundedRect(x, y - 2, size, radius)
                    || !InsideRoundedRect(x, y + 2, size, radius);
                pixels[y * size + x] = boundary ? edge : fill;
            }
        }

        texture.SetPixels(pixels);
        texture.Apply(false, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        roundedSprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f,
            0,
            SpriteMeshType.FullRect,
            new Vector4(12f, 12f, 12f, 12f));
        roundedSprite.hideFlags = HideFlags.HideAndDontSave;
        return roundedSprite;
    }

    static bool InsideRoundedRect(int x, int y, int size, int radius)
    {
        if (x < 0 || y < 0 || x >= size || y >= size)
            return false;

        int cornerX = Mathf.Clamp(x, radius, size - 1 - radius);
        int cornerY = Mathf.Clamp(y, radius, size - 1 - radius);
        int dx = x - cornerX;
        int dy = y - cornerY;
        return dx * dx + dy * dy <= radius * radius;
    }

    static Sprite FlagSprite()
    {
        if (flagSprite != null)
            return flagSprite;

        const int size = 16;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[size * size];
        var clear = new Color(0f, 0f, 0f, 0f);
        var ink = new Color(0.05f, 0.05f, 0.06f, 1f);
        var red = new Color(0.82f, 0.08f, 0.07f, 1f);
        var darkRed = new Color(0.48f, 0.04f, 0.04f, 1f);
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = clear;

        for (int y = 2; y <= 13; y++)
        {
            pixels[y * size + 4] = ink;
            pixels[y * size + 5] = ink;
        }
        for (int x = 2; x <= 7; x++)
        {
            pixels[1 * size + x] = ink;
            pixels[2 * size + x] = ink;
        }

        for (int y = 8; y <= 13; y++)
        {
            int right = 5 + (y - 8);
            for (int x = 6; x <= right; x++)
            {
                bool edgePixel = y == 13 || x == right;
                pixels[y * size + x] = edgePixel ? darkRed : red;
            }
        }

        texture.SetPixels(pixels);
        texture.Apply(false, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        flagSprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f);
        flagSprite.hideFlags = HideFlags.HideAndDontSave;
        return flagSprite;
    }

    static Sprite MineSprite()
    {
        if (mineSprite != null)
            return mineSprite;

        const int size = 16;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[size * size];
        var clear = new Color(0f, 0f, 0f, 0f);
        var ink = new Color(0.03f, 0.03f, 0.04f, 1f);
        var highlight = new Color(0.92f, 0.92f, 0.93f, 1f);
        float center = (size - 1) * 0.5f;
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = clear;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);

                bool horizontalSpike = Mathf.Abs(dy) < 0.8f
                    && Mathf.Abs(dx) >= 4f && Mathf.Abs(dx) <= 7.2f;
                bool verticalSpike = Mathf.Abs(dx) < 0.8f
                    && Mathf.Abs(dy) >= 4f && Mathf.Abs(dy) <= 7.2f;
                bool diagonalSpike = Mathf.Abs(Mathf.Abs(dx) - Mathf.Abs(dy)) < 0.8f
                    && Mathf.Abs(dx) >= 3.8f && Mathf.Abs(dx) <= 5.6f;

                if (distance <= 5.2f || horizontalSpike || verticalSpike || diagonalSpike)
                    pixels[y * size + x] = ink;

                if (x >= 4 && x <= 5 && y >= 10 && y <= 11)
                    pixels[y * size + x] = highlight;
            }
        }

        texture.SetPixels(pixels);
        texture.Apply(false, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        mineSprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f);
        mineSprite.hideFlags = HideFlags.HideAndDontSave;
        return mineSprite;
    }

    static Sprite FlatSprite()
    {
        if (flatSprite != null)
            return flatSprite;

        var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        var pixels = new Color[16];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = Color.white;
        texture.SetPixels(pixels);
        texture.Apply(false, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        flatSprite = Sprite.Create(texture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 100f);
        flatSprite.hideFlags = HideFlags.HideAndDontSave;
        return flatSprite;
    }

    static Sprite SmileySprite()
    {
        if (smileySprite == null)
        {
            smileySprite = CreateSmiley(true);
            smileySprite.hideFlags = HideFlags.HideAndDontSave;
        }
        return smileySprite;
    }

    static Sprite ClassicSmileySprite()
    {
        if (classicSmileySprite == null)
        {
            classicSmileySprite = CreateSmiley(false);
            classicSmileySprite.hideFlags = HideFlags.HideAndDontSave;
        }
        return classicSmileySprite;
    }

    static Sprite DeadSmileySprite()
    {
        if (deadSmileySprite != null)
            return deadSmileySprite;

        const int size = 32;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[size * size];
        float center = (size - 1) * 0.5f;
        var ink = new Color(0.06f, 0.06f, 0.07f, 1f);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                if (distance > 15.2f)
                {
                    pixels[y * size + x] = new Color(0f, 0f, 0f, 0f);
                    continue;
                }

                if (distance > 14.2f)
                    pixels[y * size + x] = new Color(0.08f, 0.08f, 0.09f, 1f);
                else
                    pixels[y * size + x] = new Color(0.94f, 0.75f, 0.20f, 1f);

                bool leftEye = Mathf.Abs(x - 10) <= 2 && Mathf.Abs(y - 18) <= 2
                    && Mathf.Abs(x - 10) == Mathf.Abs(y - 18);
                bool rightEye = Mathf.Abs(x - 21) <= 2 && Mathf.Abs(y - 18) <= 2
                    && Mathf.Abs(x - 21) == Mathf.Abs(y - 18);
                if (leftEye || rightEye)
                    pixels[y * size + x] = ink;

                int mouthY = 12 - (int)(Mathf.Abs(dx) * 0.25f);
                if (Mathf.Abs(dx) <= 8f && (y == mouthY || y == mouthY + 1))
                    pixels[y * size + x] = ink;
            }
        }

        texture.SetPixels(pixels);
        texture.Apply(false, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        deadSmileySprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f);
        deadSmileySprite.hideFlags = HideFlags.HideAndDontSave;
        return deadSmileySprite;
    }

    static Sprite CreateSmiley(bool sunglasses)
    {
        const int size = 32;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[size * size];
        float center = (size - 1) * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);
                if (distance > 15.2f)
                {
                    pixels[y * size + x] = new Color(0f, 0f, 0f, 0f);
                    continue;
                }

                if (distance > 14.2f)
                    pixels[y * size + x] = new Color(0.08f, 0.08f, 0.09f, 1f);
                else
                    pixels[y * size + x] = new Color(0.94f, 0.75f, 0.20f, 1f);

                if (sunglasses)
                {
                    bool leftLens = x >= 5 && x <= 14 && y >= 17 && y <= 23;
                    bool rightLens = x >= 18 && x <= 27 && y >= 17 && y <= 23;
                    bool bridge = x >= 15 && x <= 17 && y >= 20 && y <= 21;
                    bool leftArm = x >= 3 && x <= 4 && y >= 20 && y <= 21;
                    bool rightArm = x >= 28 && x <= 29 && y >= 20 && y <= 21;
                    if (leftLens || rightLens || bridge || leftArm || rightArm)
                        pixels[y * size + x] = new Color(0.04f, 0.04f, 0.05f, 1f);

                    bool leftGlint = (x == 7 || x == 8) && (y == 21 || y == 22);
                    bool rightGlint = (x == 20 || x == 21) && (y == 21 || y == 22);
                    if (leftGlint || rightGlint)
                        pixels[y * size + x] = new Color(0.92f, 0.92f, 0.93f, 1f);
                }
                else
                {
                    if (x >= 9 && x <= 12 && y >= 17 && y <= 20)
                        pixels[y * size + x] = new Color(0.06f, 0.06f, 0.07f, 1f);
                    if (x >= 19 && x <= 22 && y >= 17 && y <= 20)
                        pixels[y * size + x] = new Color(0.06f, 0.06f, 0.07f, 1f);
                }

                int mouthY = 8 + (int)(Mathf.Abs(dx) * 0.25f);
                if (Mathf.Abs(dx) <= 8f && (y == mouthY || y == mouthY + 1))
                    pixels[y * size + x] = new Color(0.06f, 0.06f, 0.07f, 1f);
            }
        }

        texture.SetPixels(pixels);
        texture.Apply(false, false);
        texture.hideFlags = HideFlags.HideAndDontSave;
        return Sprite.Create(
            texture,
            new Rect(0f, 0f, size, size),
            new Vector2(0.5f, 0.5f),
            100f);
    }

    static Font BuiltinFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }
}

public class MinesweeperCell : MonoBehaviour, IPointerClickHandler
{
    public int Index;
    public Image Flag;
    public Image Mine;
    public Action<int, PointerEventData.InputButton> Clicked;

    public void OnPointerClick(PointerEventData eventData)
    {
        if (Clicked != null)
            Clicked(Index, eventData.button);
    }
}

public class PopupHoverRelay : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public Action<bool> Changed;

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (Changed != null)
            Changed(true);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (Changed != null)
            Changed(false);
    }
}
