using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 远行：角色缩小离开，一段时间后回来，带回占位 meme。
/// 第一次离开 1 分钟，之后每次多 1 分钟。
/// </summary>
public class PetJourney : MonoBehaviour
{
    public static PetJourney Instance { get; private set; }
    public static bool PetIsAway => Instance != null && (Instance.away || Instance.departing);
    const string TripCountKey = "PetJourney.TripCount";
    const string ReturnAtKey = "PetJourney.ReturnAtTicks";
    const string MoodReadyKey = "PetJourney.MoodReadyAtTicks";
    const string NewsSeenKey = "PetJourney.NewsSeen";

    [SerializeField] SpriteRenderer pet;
    [SerializeField] Button journeyButton;
    [SerializeField] float shrinkSeconds = 1.2f;
    [SerializeField] float firstTripSeconds = 60f;
    [SerializeField] float extraSecondsPerTrip = 60f;
    [SerializeField] float moodRecoverSeconds = 60f;
    [SerializeField] float newsDelaySeconds = 30f;
    [SerializeField] string returnLine = "I brought something back for you.";
    [SerializeField] string tiredLine = "I'm a little tired. I need to rest.";
    [SerializeField] string newsLine = "Today's news";
    [SerializeField] string newsUrl = "https://itch.io";
    [SerializeField] int memeCount = 3;

    Vector3 homeScale = Vector3.one;
    GameObject memePopup;
    Image memeImage;
    Text memeLabel;
    int memeIndex;
    int tripCount;
    bool away;
    bool returning;
    bool departing;
    bool waitingForDialogue;
    bool newsShown;
    long returnAtTicks;
    long moodReadyAtTicks;
    Image moodFill;
    Sprite[] memeSprites;

    void Awake()
    {
        Instance = this;
        if (pet == null)
            pet = GetComponent<SpriteRenderer>();
        if (pet != null)
            homeScale = pet.transform.localScale;

        tripCount = PlayerPrefs.GetInt(TripCountKey, 0);
        returnAtTicks = ReadTicks(ReturnAtKey);
        moodReadyAtTicks = ReadTicks(MoodReadyKey);
        newsShown = PlayerPrefs.GetInt(NewsSeenKey, 0) != 0;
        BindJourneyButton();
        CreateMoodBar();
        CreateMemePopup();

        if (returnAtTicks > 0)
        {
            SetJourneyButtonVisible(false);
            if (pet != null)
                pet.enabled = false;
            away = true;
        }
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void Update()
    {
        RefreshMoodBar();
        TryShowNews();

        if (away && !returning && DateTime.UtcNow.Ticks >= returnAtTicks)
        {
            away = false;
            returning = true;
            ClearReturnAt();
            StartCoroutine(ReturnHome());
        }

        if (!waitingForDialogue)
            return;

        if (PetDialogue.Instance != null && PetDialogue.Instance.IsOpen)
            return;

        waitingForDialogue = false;
        ShowMeme(0);
    }

    void TryShowNews()
    {
        if (newsShown || tripCount != 1 || moodReadyAtTicks <= 0 || away)
            return;
        if (waitingForDialogue || (memePopup != null && memePopup.activeSelf))
            return;

        long newsAt = moodReadyAtTicks - SecondsToTicks(Mathf.Max(0f, moodRecoverSeconds - newsDelaySeconds));
        if (DateTime.UtcNow.Ticks < newsAt)
            return;

        newsShown = true;
        PlayerPrefs.SetInt(NewsSeenKey, 1);
        PlayerPrefs.Save();
        PetDialogue.SayLink(newsLine + "\n" + newsUrl, newsUrl);
    }

    static long SecondsToTicks(float seconds)
    {
        return (long)(Math.Max(0d, seconds) * TimeSpan.TicksPerSecond);
    }

    void BeginJourney()
    {
        if (away || returning || departing)
            return;

        if (CurrentMood() < 0.999f)
        {
            if (!PetDialogue.HasPendingLink)
                PetDialogue.SayOnce(tiredLine);
            return;
        }

        StartCoroutine(Depart());
    }

    IEnumerator Depart()
    {
        departing = true;
        SetJourneyButtonVisible(false);
        if (PetDialogue.Instance != null)
            PetDialogue.Instance.Hide();

        Vector3 start = pet != null ? pet.transform.localScale : homeScale;
        float elapsed = 0f;
        while (elapsed < shrinkSeconds)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / shrinkSeconds);
            if (pet != null)
                pet.transform.localScale = Vector3.Lerp(start, Vector3.zero, t);
            yield return null;
        }

        if (pet != null)
        {
            pet.enabled = false;
            pet.transform.localScale = homeScale;
        }

        float duration = firstTripSeconds + tripCount * extraSecondsPerTrip;
        tripCount++;
        PlayerPrefs.SetInt(TripCountKey, tripCount);
        returnAtTicks = DateTime.UtcNow.AddSeconds(duration).Ticks;
        PlayerPrefs.SetString(ReturnAtKey, returnAtTicks.ToString());
        PlayerPrefs.Save();
        departing = false;
        away = true;
        returning = false;
    }

    IEnumerator ReturnHome()
    {
        BeginRest();

        if (pet != null)
        {
            pet.enabled = true;
            pet.transform.localScale = Vector3.zero;
            float elapsed = 0f;
            while (elapsed < shrinkSeconds)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / shrinkSeconds);
                pet.transform.localScale = Vector3.Lerp(Vector3.zero, homeScale, t);
                yield return null;
            }

            pet.transform.localScale = homeScale;
        }

        PetDialogue.SayOnce(returnLine);
        waitingForDialogue = true;
    }

    void ShowMeme(int index)
    {
        memeIndex = index;
        if (memePopup == null)
            return;

        EnsureMemeSprites();
        memePopup.SetActive(true);
        memePopup.transform.SetAsLastSibling();
        if (memeImage != null && index >= 0 && index < memeSprites.Length)
            memeImage.sprite = memeSprites[index];
        if (memeLabel != null)
            memeLabel.text = "Meme " + (index + 1);
    }

    void OnMemeClicked()
    {
        int next = memeIndex + 1;
        if (next < memeCount)
        {
            ShowMeme(next);
            return;
        }

        if (memePopup != null)
            memePopup.SetActive(false);
        returning = false;
        SetJourneyButtonVisible(true);
    }

    float CurrentMood()
    {
        if (moodReadyAtTicks <= 0)
            return 1f;

        long now = DateTime.UtcNow.Ticks;
        if (now >= moodReadyAtTicks)
            return 1f;

        double remaining = (moodReadyAtTicks - now) / (double)TimeSpan.TicksPerSecond;
        float recover = Mathf.Max(0.01f, moodRecoverSeconds);
        return Mathf.Clamp01(1f - (float)(remaining / recover));
    }

    void BeginRest()
    {
        moodReadyAtTicks = DateTime.UtcNow.AddSeconds(moodRecoverSeconds).Ticks;
        PlayerPrefs.SetString(MoodReadyKey, moodReadyAtTicks.ToString());
        PlayerPrefs.Save();
        RefreshMoodBar();
    }

    void RefreshMoodBar()
    {
        if (moodFill != null)
            moodFill.fillAmount = CurrentMood();
    }

    void CreateMoodBar()
    {
        Transform parent = journeyButton != null ? journeyButton.transform.parent : null;
        if (parent == null)
        {
            var quitObject = GameObject.Find("Quit");
            if (quitObject != null)
                parent = quitObject.transform.parent;
        }

        if (parent == null)
            return;

        var root = new GameObject("Mood", typeof(RectTransform));
        root.transform.SetParent(parent, false);
        var rootRect = root.GetComponent<RectTransform>();
        rootRect.anchorMin = Vector2.zero;
        rootRect.anchorMax = Vector2.zero;
        rootRect.pivot = Vector2.zero;
        rootRect.anchoredPosition = new Vector2(12f, 12f);
        rootRect.sizeDelta = new Vector2(116f, 24f);

        var heartObject = new GameObject("Heart", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        heartObject.transform.SetParent(root.transform, false);
        var heartRect = heartObject.GetComponent<RectTransform>();
        heartRect.anchorMin = new Vector2(0f, 0.5f);
        heartRect.anchorMax = new Vector2(0f, 0.5f);
        heartRect.pivot = new Vector2(0f, 0.5f);
        heartRect.anchoredPosition = Vector2.zero;
        heartRect.sizeDelta = new Vector2(22f, 22f);
        var heart = heartObject.GetComponent<Image>();
        heart.sprite = CreateHeartSprite();
        heart.preserveAspect = true;
        heart.raycastTarget = false;

        var trackObject = new GameObject("Track", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        trackObject.transform.SetParent(root.transform, false);
        var trackRect = trackObject.GetComponent<RectTransform>();
        trackRect.anchorMin = new Vector2(0f, 0.5f);
        trackRect.anchorMax = new Vector2(0f, 0.5f);
        trackRect.pivot = new Vector2(0f, 0.5f);
        trackRect.anchoredPosition = new Vector2(28f, 0f);
        trackRect.sizeDelta = new Vector2(88f, 10f);
        var track = trackObject.GetComponent<Image>();
        track.sprite = WhiteSprite();
        track.color = new Color(0.42f, 0.28f, 0.3f, 1f);
        track.raycastTarget = false;

        var fillObject = new GameObject("Fill", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        fillObject.transform.SetParent(trackObject.transform, false);
        var fillRect = fillObject.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = new Vector2(1f, 1f);
        fillRect.offsetMax = new Vector2(-1f, -1f);
        moodFill = fillObject.GetComponent<Image>();
        moodFill.sprite = WhiteSprite();
        moodFill.type = Image.Type.Filled;
        moodFill.fillMethod = Image.FillMethod.Horizontal;
        moodFill.fillOrigin = (int)Image.OriginHorizontal.Left;
        moodFill.color = new Color(0.93f, 0.32f, 0.42f, 1f);
        moodFill.raycastTarget = false;
        RefreshMoodBar();
    }

    static Sprite CreateHeartSprite()
    {
        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Bilinear;
        var pixels = new Color[size * size];
        var fill = new Color(0.93f, 0.28f, 0.4f, 1f);
        float center = (size - 1) * 0.5f;
        float radius = size * 0.34f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float nx = (x - center) / radius;
                float ny = (y - center) / radius + 0.15f;
                float a = nx * nx + ny * ny - 1f;
                bool inside = a * a * a - nx * nx * ny * ny * ny <= 0f;
                pixels[y * size + x] = inside ? fill : new Color(0f, 0f, 0f, 0f);
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    static Sprite WhiteSprite()
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        texture.SetPixel(0, 0, Color.white);
        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 100f);
    }

    void BindJourneyButton()
    {
        if (journeyButton == null)
            journeyButton = FindJourneyButton();
        if (journeyButton != null)
            journeyButton.onClick.AddListener(BeginJourney);
    }

    static Button FindJourneyButton()
    {
        var buttons = FindObjectsOfType<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i].gameObject.name == "Journey")
                return buttons[i];
        }

        return null;
    }

    void CreateMemePopup()
    {
        var quitObject = GameObject.Find("Quit");
        if (quitObject == null)
            return;

        memePopup = new GameObject("MemePopup", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        memePopup.transform.SetParent(quitObject.transform.parent, false);
        var rect = memePopup.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(180f, 210f);
        rect.anchoredPosition = Vector2.zero;

        var background = memePopup.GetComponent<Image>();
        background.color = new Color(0.99f, 0.96f, 0.9f, 1f);
        var quitImage = quitObject.GetComponent<Image>();
        if (quitImage != null && quitImage.sprite != null)
        {
            background.sprite = quitImage.sprite;
            background.type = Image.Type.Sliced;
        }

        var button = memePopup.GetComponent<Button>();
        button.targetGraphic = background;
        button.onClick.AddListener(OnMemeClicked);

        var pictureObject = new GameObject("Picture", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        pictureObject.transform.SetParent(memePopup.transform, false);
        var pictureRect = pictureObject.GetComponent<RectTransform>();
        pictureRect.anchorMin = new Vector2(0.5f, 0.5f);
        pictureRect.anchorMax = new Vector2(0.5f, 0.5f);
        pictureRect.pivot = new Vector2(0.5f, 0.5f);
        pictureRect.sizeDelta = new Vector2(140f, 140f);
        pictureRect.anchoredPosition = new Vector2(0f, 16f);
        memeImage = pictureObject.GetComponent<Image>();
        memeImage.raycastTarget = false;

        var labelObject = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        labelObject.transform.SetParent(memePopup.transform, false);
        var labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = new Vector2(0f, 0f);
        labelRect.anchorMax = new Vector2(1f, 0f);
        labelRect.pivot = new Vector2(0.5f, 0f);
        labelRect.sizeDelta = new Vector2(0f, 36f);
        labelRect.anchoredPosition = new Vector2(0f, 8f);
        memeLabel = labelObject.GetComponent<Text>();
        memeLabel.alignment = TextAnchor.MiddleCenter;
        memeLabel.color = new Color(0.24f, 0.16f, 0.12f, 1f);
        memeLabel.fontSize = 16;
        memeLabel.font = BuiltinFont();
        memeLabel.raycastTarget = false;

        memePopup.SetActive(false);
    }

    void EnsureMemeSprites()
    {
        if (memeSprites != null)
            return;

        memeSprites = new Sprite[Mathf.Max(1, memeCount)];
        var colors = new[]
        {
            new Color(0.95f, 0.55f, 0.28f, 1f),
            new Color(0.35f, 0.62f, 0.9f, 1f),
            new Color(0.45f, 0.75f, 0.42f, 1f)
        };

        for (int i = 0; i < memeSprites.Length; i++)
        {
            Color color = colors[i % colors.Length];
            memeSprites[i] = CreatePlaceholderSprite(color, i);
        }
    }

    static Sprite CreatePlaceholderSprite(Color color, int pattern)
    {
        const int size = 128;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Bilinear;
        var pixels = new Color[size * size];
        var paper = new Color(0.98f, 0.94f, 0.86f, 1f);
        float center = (size - 1) * 0.5f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - center;
                float dy = y - center;
                bool mark = false;
                if (pattern % 3 == 0)
                    mark = dx * dx + dy * dy <= 28f * 28f;
                else if (pattern % 3 == 1)
                    mark = Mathf.Abs(dx) <= 26f && Mathf.Abs(dy) <= 26f;
                else
                    mark = dy > -30f && dy < 34f && Mathf.Abs(dx) < (34f - dy) * 0.7f;

                Color pixel = mark ? color : paper;
                pixel.r = Mathf.Max(pixel.r, 0.05f);
                pixel.g = Mathf.Max(pixel.g, 0.05f);
                pixel.b = Mathf.Max(pixel.b, 0.05f);
                pixels[y * size + x] = pixel;
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    void SetJourneyButtonVisible(bool visible)
    {
        if (journeyButton != null)
            journeyButton.gameObject.SetActive(visible);
    }

    static long ReadTicks(string key)
    {
        string stored = PlayerPrefs.GetString(key, "0");
        return long.TryParse(stored, out long ticks) ? ticks : 0L;
    }

    static void ClearReturnAt()
    {
        PlayerPrefs.DeleteKey(ReturnAtKey);
        PlayerPrefs.Save();
    }

    static Font BuiltinFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }
}
