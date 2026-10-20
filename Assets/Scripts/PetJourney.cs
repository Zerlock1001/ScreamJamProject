using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 远行：角色缩小离开，一段时间后回来，带回占位 meme。
/// 第一次离开 30 秒，之后每次多 1 分钟。
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
    [SerializeField] Image moodFill;
    [SerializeField] float shrinkSeconds = 1.2f;
    [SerializeField] float firstTripSeconds = 30f;
    [SerializeField] float extraSecondsPerTrip = 60f;
    [SerializeField] float moodRecoverSeconds = 60f;
    [SerializeField] float newsDelaySeconds = 30f;
    [SerializeField] string returnLine = "I brought something back for you.";
    [SerializeField] string tiredLine = "I'm a little tired. I need to rest.";
    [SerializeField] string newsLine = "Today's news";
    [SerializeField] string newsUrl = "https://itch.io";
    [SerializeField] int memeCount = 3;
    [SerializeField] bool keepProgressBetweenLaunches;

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
    float returnSecondsLeft = -1f;
    bool returnCountdownArmed;
    Sprite[] memeSprites;

    void Awake()
    {
        Instance = this;
        if (pet == null)
            pet = GetComponent<SpriteRenderer>();
        if (pet != null)
            homeScale = pet.transform.localScale;
        if (homeScale.sqrMagnitude < 0.0001f)
            homeScale = Vector3.one;

        tripCount = LoadInt(TripCountKey);
        returnAtTicks = LoadTicks(ReturnAtKey);
        moodReadyAtTicks = LoadTicks(MoodReadyKey);
        newsShown = LoadInt(NewsSeenKey) != 0;
        BindJourneyButton();
        BindMoodBar();
        CreateMemePopup();

        if (returnAtTicks > 0)
        {
            double remaining = (returnAtTicks - DateTime.UtcNow.Ticks) / (double)TimeSpan.TicksPerSecond;
            if (remaining > 1d)
            {
                ArmReturnCountdown((float)remaining);
                SetJourneyButtonVisible(false);
                if (pet != null)
                    pet.enabled = false;
                away = true;
            }
            else
            {
                returnAtTicks = 0;
                ClearReturnAt();
            }
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

        if (away && !returning)
        {
            if (returnCountdownArmed)
                returnSecondsLeft -= Time.unscaledDeltaTime;

            if (!departing && ReturnIsDue())
            {
                away = false;
                returning = true;
                returnCountdownArmed = false;
                returnAtTicks = 0;
                ClearReturnAt();
                StartCoroutine(ReturnHome());
            }
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
        SaveInt(NewsSeenKey, 1);
        PetDialogue.SayLink(newsLine + "\n" + newsUrl, newsUrl);
    }

    static long SecondsToTicks(float seconds)
    {
        return (long)(Math.Max(0d, seconds) * TimeSpan.TicksPerSecond);
    }

    void BeginJourney()
    {
        if (away || returning || departing || !PetDialogue.HasPlayerName)
            return;

        if (CurrentMood() < 0.999f)
        {
            if (!PetDialogue.HasPendingLink)
                PetDialogue.SayOnce(tiredLine);
            return;
        }

        float duration = Mathf.Max(1f, firstTripSeconds + tripCount * extraSecondsPerTrip);
        tripCount++;
        SaveInt(TripCountKey, tripCount);
        returnAtTicks = DateTime.UtcNow.AddSeconds(duration).Ticks;
        SaveString(ReturnAtKey, returnAtTicks.ToString());
        ArmReturnCountdown(duration);
        away = true;
        departing = true;
        returning = false;
        SetJourneyButtonVisible(false);
        StartCoroutine(Depart());
    }

    void ArmReturnCountdown(float seconds)
    {
        returnSecondsLeft = Mathf.Max(0f, seconds);
        returnCountdownArmed = true;
    }

    bool ReturnIsDue()
    {
        if (returnCountdownArmed && returnSecondsLeft <= 0f)
            return true;
        return returnAtTicks > 0 && DateTime.UtcNow.Ticks >= returnAtTicks;
    }

    IEnumerator Depart()
    {
        if (PetDialogue.Instance != null)
            PetDialogue.Instance.Hide();

        Vector3 start = pet != null ? pet.transform.localScale : homeScale;
        if (start.sqrMagnitude < 0.0001f)
            start = homeScale;
        float shrink = Mathf.Max(0.05f, shrinkSeconds);
        float elapsed = 0f;
        while (elapsed < shrink)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / shrink);
            if (pet != null)
                pet.transform.localScale = Vector3.Lerp(start, Vector3.zero, t);
            yield return null;
        }

        if (pet != null)
        {
            pet.enabled = false;
            pet.transform.localScale = homeScale;
        }

        departing = false;
    }

    IEnumerator ReturnHome()
    {
        BeginRest();

        if (pet != null)
        {
            pet.enabled = true;
            pet.transform.localScale = Vector3.zero;
            float shrink = Mathf.Max(0.05f, shrinkSeconds);
            float elapsed = 0f;
            while (elapsed < shrink)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / shrink);
                pet.transform.localScale = Vector3.Lerp(Vector3.zero, homeScale, t);
                yield return null;
            }

            pet.enabled = true;
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
        SaveString(MoodReadyKey, moodReadyAtTicks.ToString());
        RefreshMoodBar();
    }

    void RefreshMoodBar()
    {
        if (moodFill != null)
            moodFill.fillAmount = CurrentMood();
    }

    void BindMoodBar()
    {
        if (moodFill == null)
        {
            var fillObject = GameObject.Find("MoodFill");
            if (fillObject != null)
                moodFill = fillObject.GetComponent<Image>();
        }

        if (moodFill == null)
            return;

        moodFill.type = Image.Type.Filled;
        moodFill.fillMethod = Image.FillMethod.Horizontal;
        moodFill.fillOrigin = (int)Image.OriginHorizontal.Left;
        moodFill.raycastTarget = false;
        RefreshMoodBar();
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

    int LoadInt(string key, int fallback = 0)
    {
        if (!keepProgressBetweenLaunches)
            return fallback;
        return PlayerPrefs.GetInt(key, fallback);
    }

    long LoadTicks(string key)
    {
        if (!keepProgressBetweenLaunches)
            return 0L;
        string stored = PlayerPrefs.GetString(key, "0");
        return long.TryParse(stored, out long ticks) ? ticks : 0L;
    }

    void SaveInt(string key, int value)
    {
        if (!keepProgressBetweenLaunches)
            return;
        PlayerPrefs.SetInt(key, value);
        PlayerPrefs.Save();
    }

    void SaveString(string key, string value)
    {
        if (!keepProgressBetweenLaunches)
            return;
        PlayerPrefs.SetString(key, value);
        PlayerPrefs.Save();
    }

    void ClearReturnAt()
    {
        if (!keepProgressBetweenLaunches)
            return;
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
