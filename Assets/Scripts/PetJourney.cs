using System;
using System.Collections;
using System.Collections.Generic;
using ScreamJam.Debugging;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 远行：播放 JourneyStart 后离开，途中把明信片发到手机。回来后说一句，不再展示带回了什么。
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
    [SerializeField] string returnLine = "I'm back! I brought you quite a few photos. Remember to check your phone.";
    [SerializeField] string restLine = "I need to rest for a bit now.";
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
    bool homeScriptPending;
    bool arrivalLineQueued;
    bool newsShown;
    Coroutine moodShake;
    RectTransform moodWidget;
    Vector2 moodRestPosition;
    bool moodRestCached;
    long returnAtTicks;
    long moodReadyAtTicks;
    float returnSecondsLeft = -1f;
    bool returnCountdownArmed;
    Coroutine postcardRoutine;
    Sprite[] memeSprites;
    Button galleryButton;
    GameObject galleryPanel;
    readonly List<Sprite> collectedMemes = new List<Sprite>();
    bool browsingGallery;

    void Awake()
    {
        Instance = this;
        if (pet == null)
            pet = GetComponent<SpriteRenderer>();
        if (pet != null)
            homeScale = pet.transform.localScale;
        if (homeScale.sqrMagnitude < 0.0001f)
            homeScale = Vector3.one;

        DebugConsole.RegisterCommand("skip", "/skip", "End the current journey and bring Blinky back now.", _ => SkipJourney());
        DebugConsole.RegisterCommand("skipto", "/skipto X", "Set the number of completed journeys to X.", SkipToCommand);
        DebugConsole.RegisterCommand("recover", "/recover mood", "Fill the mood bar immediately.", RecoverMoodCommand);

        tripCount = LoadInt(TripCountKey);
        returnAtTicks = LoadTicks(ReturnAtKey);
        moodReadyAtTicks = LoadTicks(MoodReadyKey);
        newsShown = LoadInt(NewsSeenKey) != 0;
        BindJourneyButton();
        BindGalleryButton();
        BindMoodBar();
        CreateMemePopup();
        CreateGallery();
        SetJourneyButtonVisible(false);
        SetGalleryButtonVisible(false);

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
        if (waitingForDialogue && PetDialogue.Instance != null && !PetDialogue.Instance.IsOpen)
        {
            waitingForDialogue = false;
            if (homeScriptPending)
            {
                homeScriptPending = false;
                PetCampaign.Ensure().BeginAfterFirstReturn();
            }
        }
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
                if (postcardRoutine != null)
                {
                    StopCoroutine(postcardRoutine);
                    postcardRoutine = null;
                }
                StartCoroutine(ReturnHome());
            }
        }
    }

    void TryShowNews()
    {
        if (newsShown || tripCount != 1 || moodReadyAtTicks <= 0 || away || arrivalLineQueued)
            return;
        if (waitingForDialogue || (memePopup != null && memePopup.activeSelf))
            return;

        long newsAt = moodReadyAtTicks - SecondsToTicks(Mathf.Max(0f, moodRecoverSeconds - newsDelaySeconds));
        if (DateTime.UtcNow.Ticks < newsAt)
            return;

        newsShown = true;
        SaveInt(NewsSeenKey, 1);
        PetDialogue.SayLink(newsLine + "\nTrump ties $5,000 checks to a Republican win", NewsPageUrl());
    }

    string NewsPageUrl()
    {
        string path = System.IO.Path.Combine(Application.streamingAssetsPath, "News", "trump-greenville-rally.html");
        if (System.IO.File.Exists(path))
            return new System.Uri(path).AbsoluteUri;
        return newsUrl;
    }

    static long SecondsToTicks(float seconds)
    {
        return (long)(Math.Max(0d, seconds) * TimeSpan.TicksPerSecond);
    }

    void BeginJourney()
    {
        if (away || returning || departing || !PetDialogue.HasPlayerName)
            return;

        if (PetCampaign.BlocksJourney)
            return;

        if (CurrentMood() < 0.999f)
        {
            if (!PetDialogue.HasPendingLink)
                PetDialogue.SayOnce(tiredLine);
            ShakeMood();
            return;
        }

        float duration = Mathf.Max(1f, firstTripSeconds + tripCount * extraSecondsPerTrip);
        tripCount++;
        SaveInt(TripCountKey, tripCount);
        returnAtTicks = DateTime.UtcNow.AddSeconds(duration).Ticks;
        SaveString(ReturnAtKey, returnAtTicks.ToString());
        ArmReturnCountdown(duration);
        away = true;
        PetPhone.Ensure();
        if (PetPhone.Instance != null)
            PetPhone.Instance.SetAway(true);
        if (postcardRoutine != null)
            StopCoroutine(postcardRoutine);
        postcardRoutine = StartCoroutine(SendPostcards(duration));
        departing = true;
        returning = false;
        PetCampaign.Ensure().OnTripStarted(tripCount, duration);
        browsingGallery = false;
        if (galleryPanel != null)
            galleryPanel.SetActive(false);
        if (memePopup != null)
            memePopup.SetActive(false);
        SetJourneyButtonVisible(false);
        if (PetDialogue.Instance != null)
            PetDialogue.Instance.CloseForJourney();
        StartCoroutine(Depart());
    }

    static void RecoverMoodCommand(string[] args)
    {
        if (args == null || args.Length != 1 || !string.Equals(args[0], "mood", StringComparison.OrdinalIgnoreCase))
        {
            DebugConsole.WriteLine("Usage: /recover mood");
            return;
        }

        if (Instance == null)
            return;

        Instance.RecoverMood();
        DebugConsole.WriteLine("Mood restored.");
    }

    public void RecoverMood()
    {
        moodReadyAtTicks = 0;
        if (keepProgressBetweenLaunches)
        {
            PlayerPrefs.DeleteKey(MoodReadyKey);
            PlayerPrefs.Save();
        }

        RefreshMoodBar();
    }

    public void SetBodyVisible(bool visible)
    {
        if (pet != null)
            pet.enabled = visible;
    }

    public void ShowCollections()
    {
        SetGalleryButtonVisible(true);
    }

    public void SetJourneyAvailable(bool visible)
    {
        SetJourneyButtonVisible(visible);
    }

    public string PageUrl()
    {
        return NewsPageUrl();
    }

    public bool HitsPet(Vector2 screenPoint)
    {
        if (pet == null || !pet.enabled)
            return false;

        Camera cam = Camera.main;
        if (cam == null)
            return false;

        Bounds bounds = pet.bounds;
        float depth = Mathf.Abs(cam.transform.position.z - bounds.center.z);
        Vector3 world = cam.ScreenToWorldPoint(new Vector3(screenPoint.x, screenPoint.y, depth));
        return bounds.Contains(new Vector3(world.x, world.y, bounds.center.z));
    }

    static void SkipToCommand(string[] args)
    {
        if (Instance == null)
            return;

        int count;
        if (args == null || args.Length != 1 || !int.TryParse(args[0], out count) || count < 0)
        {
            DebugConsole.WriteLine("Usage: /skipto X");
            return;
        }

        Instance.SkipToCompleted(count);
        DebugConsole.WriteLine("Completed journeys: " + count + ". Next trip is " + (count + 1) + ". " + PetStory.PhaseLabel(PetStory.Current) + ".");
    }

    public void SkipToCompleted(int count)
    {
        StopAllCoroutines();
        postcardRoutine = null;
        tripCount = Mathf.Max(0, count);
        SaveInt(TripCountKey, tripCount);
        away = false;
        returning = false;
        departing = false;
        homeScriptPending = false;
        arrivalLineQueued = false;
        returnCountdownArmed = false;
        returnSecondsLeft = -1f;
        returnAtTicks = 0;
        ClearReturnAt();
        if (tripCount >= 1)
        {
            newsShown = true;
            SaveInt(NewsSeenKey, 1);
        }

        RecoverMood();
        if (pet != null)
        {
            pet.enabled = true;
            if (homeScale.sqrMagnitude > 0.0001f)
                pet.transform.localScale = homeScale;
        }

        Animator animator = PetAnimator();
        if (animator != null)
        {
            animator.Play("Idle", 0, 0f);
            animator.Update(0f);
        }

        if (PetPhone.Instance != null)
            PetPhone.Instance.SetAway(false);
        if (tripCount >= 1)
        {
            PetPhone.ShowButton();
            ShowCollections();
            SetJourneyAvailable(true);
        }

        if (galleryPanel != null)
            galleryPanel.SetActive(false);
        if (memePopup != null)
            memePopup.SetActive(false);

        PetCampaign.Ensure().SnapToCompleted(tripCount);
        if (PetDialogue.Instance != null && PetDialogue.Instance.IsOpen)
            PetDialogue.Instance.Hide();
    }

    static void SkipJourney()
    {
        if (Instance == null || !Instance.away)
        {
            DebugConsole.WriteLine("Blinky is not out on a journey.");
            return;
        }

        if (Instance.returning)
        {
            DebugConsole.WriteLine("Blinky is already on the way back.");
            return;
        }

        Instance.returnSecondsLeft = 0f;
        Instance.returnCountdownArmed = true;
        Instance.returnAtTicks = DateTime.UtcNow.Ticks;
        DebugConsole.WriteLine(Instance.departing
            ? "Trip skipped. Blinky will head back once the leave animation finishes."
            : "Trip skipped. Blinky is heading back.");
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

        if (PetAnimator() != null)
            yield return PlayState("JourneyStart");
        else
            yield return ScalePet(CurrentScale(), Vector3.zero);

        if (pet != null)
            pet.enabled = false;

        departing = false;
        if (tripCount == 1)
            yield return IntroducePhone();
    }

    IEnumerator IntroducePhone()
    {
        yield return new WaitForSecondsRealtime(2f);
        PetPhone.ShowButton();
        if (PetPhone.Instance == null)
            yield break;

        PetPhone.Instance.SetOpen(true);
        string name = PetDialogue.HasPlayerName ? PetDialogue.PlayerName : "there";
        PetPhone.Instance.AddMessage("Hi, " + name + ". If I run into anything interesting while I'm out, I'll send it to you through this. Remember to check.");
    }

    IEnumerator ReturnHome()
    {
        BeginRest();
        arrivalLineQueued = true;
        PetCampaign.Ensure().FlushTripLines();

        if (pet != null)
            pet.enabled = true;

        if (PetAnimator() != null)
            yield return PlayState("JourneyEnd");
        else
            yield return ScalePet(Vector3.zero, homeScale);

        if (PetPhone.Instance != null)
            PetPhone.Instance.SetAway(false);
        returning = false;
        SetJourneyButtonVisible(true);
        arrivalLineQueued = false;
        if (tripCount <= 1 && PetDialogue.Instance != null)
        {
            PetDialogue.SayLines(returnLine, restLine);
            waitingForDialogue = true;
            homeScriptPending = true;
        }
        else
            PetCampaign.Ensure().OnReturned(tripCount);
    }

    Animator PetAnimator()
    {
        if (pet != null)
        {
            Animator onPet = pet.GetComponent<Animator>();
            if (onPet != null && onPet.runtimeAnimatorController != null)
                return onPet;
        }

        Animator animator = GetComponent<Animator>();
        if (animator != null && animator.runtimeAnimatorController != null)
            return animator;
        return null;
    }

    IEnumerator PlayState(string stateName)
    {
        Animator animator = PetAnimator();
        if (animator == null)
            yield break;

        animator.Play(stateName, 0, 0f);
        animator.Update(0f);

        float length = ClipLength(animator, stateName);
        float timeout = Mathf.Max(length, 0.05f) + 0.25f;
        float elapsed = 0f;
        bool seen = false;
        while (elapsed < timeout)
        {
            AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
            bool playing = info.IsName(stateName) && info.normalizedTime < 1f;
            if (playing)
                seen = true;
            else if (seen)
                break;

            yield return null;
            elapsed += Time.unscaledDeltaTime;
        }

        animator.Play("Idle", 0, 0f);
        animator.Update(0f);
    }

    static float ClipLength(Animator animator, string clipName)
    {
        AnimationClip[] clips = animator.runtimeAnimatorController.animationClips;
        for (int i = 0; i < clips.Length; i++)
        {
            if (clips[i] != null && clips[i].name == clipName)
                return clips[i].length;
        }

        return 0f;
    }

    Vector3 CurrentScale()
    {
        if (pet == null)
            return homeScale;
        Vector3 start = pet.transform.localScale;
        if (start.sqrMagnitude < 0.0001f)
            return homeScale;
        return start;
    }

    IEnumerator ScalePet(Vector3 from, Vector3 to)
    {
        float shrink = Mathf.Max(0.05f, shrinkSeconds);
        float elapsed = 0f;
        while (elapsed < shrink)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / shrink);
            if (pet != null)
                pet.transform.localScale = Vector3.Lerp(from, to, t);
            yield return null;
        }

        if (pet != null)
            pet.transform.localScale = to;
    }

    IEnumerator SendPostcards(float duration)
    {
        EnsureMemeSprites();
        if (memeSprites == null || memeSprites.Length == 0)
            yield break;

        float started = Time.unscaledTime;
        int count = memeSprites.Length;
        for (int i = 0; i < count; i++)
        {
            float sendAt = duration * (i + 1f) / (count + 1f);
            while (away && Time.unscaledTime - started < sendAt)
                yield return null;
            if (!away)
                yield break;

            RememberMeme(memeSprites[i]);
            if (PetPhone.Instance != null)
                PetPhone.Instance.AddPostcard(memeSprites[i], "Postcard " + (i + 1));
        }
    }

    void ShowMeme(int index)
    {
        memeIndex = index;
        if (memePopup == null)
            return;

        EnsureMemeSprites();
        if (galleryPanel != null)
            galleryPanel.SetActive(false);
        memePopup.SetActive(true);
        memePopup.transform.SetAsLastSibling();
        if (index >= 0 && index < memeSprites.Length)
        {
            RememberMeme(memeSprites[index]);
            if (memeImage != null)
                memeImage.sprite = memeSprites[index];
        }
        if (memeLabel != null)
            memeLabel.text = "Meme " + (index + 1);
    }

    void OnMemeClicked()
    {
        if (browsingGallery)
        {
            browsingGallery = false;
            if (memePopup != null)
                memePopup.SetActive(false);
            return;
        }

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

    void RememberMeme(Sprite sprite)
    {
        if (sprite != null && !collectedMemes.Contains(sprite))
            collectedMemes.Add(sprite);
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

    void ShakeMood()
    {
        if (moodWidget == null)
            moodWidget = FindMoodWidget();
        if (moodWidget == null)
            return;

        if (!moodRestCached)
        {
            moodRestPosition = moodWidget.anchoredPosition;
            moodRestCached = true;
        }

        if (moodShake != null)
            StopCoroutine(moodShake);
        moodWidget.anchoredPosition = moodRestPosition;
        moodShake = StartCoroutine(ShakeMoodRoutine());
    }

    RectTransform FindMoodWidget()
    {
        if (moodFill == null)
            return null;

        Transform node = moodFill.transform;
        while (node != null)
        {
            if (node.name == "Mood")
                return node as RectTransform;
            node = node.parent;
        }

        return moodFill.rectTransform;
    }

    IEnumerator ShakeMoodRoutine()
    {
        const float duration = 0.38f;
        const float amplitude = 10f;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            float damp = 1f - elapsed / duration;
            float offset = Mathf.Sin(elapsed * 46f) * amplitude * damp;
            moodWidget.anchoredPosition = moodRestPosition + new Vector2(offset, 0f);
            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        moodWidget.anchoredPosition = moodRestPosition;
        moodShake = null;
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
        return FindNamedButton("Journey");
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

    void BindGalleryButton()
    {
        if (galleryButton == null)
            galleryButton = FindNamedButton("Collections");
        if (galleryButton != null)
            galleryButton.onClick.AddListener(ToggleGallery);
    }

    static Button FindNamedButton(string objectName)
    {
        var buttons = FindObjectsOfType<Button>(true);
        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i].gameObject.name == objectName)
                return buttons[i];
        }

        return null;
    }

    void CreateGallery()
    {
        if (galleryPanel != null)
            return;

        Transform parent = galleryButton != null ? galleryButton.transform.parent : null;
        if (parent == null)
        {
            Canvas canvas = PetDialogue.OverlayCanvas();
            if (canvas == null)
                return;
            parent = canvas.transform;
        }

        galleryPanel = new GameObject("Gallery", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        galleryPanel.transform.SetParent(parent, false);
        var rect = galleryPanel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(248f, 108f);
        var background = galleryPanel.GetComponent<Image>();
        background.color = new Color(0.99f, 0.96f, 0.9f, 1f);
        background.raycastTarget = true;
        var buttonImage = galleryButton != null ? galleryButton.GetComponent<Image>() : null;
        if (buttonImage != null && buttonImage.sprite != null)
        {
            background.sprite = buttonImage.sprite;
            background.type = Image.Type.Sliced;
        }

        galleryPanel.SetActive(false);
    }

    public void OpenGallery()
    {
        if (galleryPanel == null)
            CreateGallery();
        if (galleryPanel == null)
            return;

        if (!galleryPanel.activeSelf)
        {
            var rect = galleryPanel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0.5f);
            rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(340f, 0f);
            galleryPanel.SetActive(true);
            galleryPanel.transform.SetAsLastSibling();
            RebuildGallery();
            return;
        }

        galleryPanel.SetActive(false);
        browsingGallery = false;
        if (memePopup != null)
            memePopup.SetActive(false);
    }

    void ToggleGallery()
    {
        if (galleryPanel == null || returning || waitingForDialogue)
            return;

        bool open = !galleryPanel.activeSelf;
        galleryPanel.SetActive(open);
        if (open)
        {
            galleryPanel.transform.SetAsLastSibling();
            RebuildGallery();
        }
        else if (browsingGallery)
        {
            browsingGallery = false;
            if (memePopup != null)
                memePopup.SetActive(false);
        }
    }

    void RebuildGallery()
    {
        for (int i = galleryPanel.transform.childCount - 1; i >= 0; i--)
            Destroy(galleryPanel.transform.GetChild(i).gameObject);

        int count = collectedMemes.Count;
        if (count == 0)
        {
            galleryPanel.GetComponent<RectTransform>().sizeDelta = new Vector2(220f, 72f);
            var emptyObject = new GameObject("Empty", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            emptyObject.transform.SetParent(galleryPanel.transform, false);
            var emptyRect = emptyObject.GetComponent<RectTransform>();
            emptyRect.anchorMin = Vector2.zero;
            emptyRect.anchorMax = Vector2.one;
            emptyRect.offsetMin = new Vector2(8f, 8f);
            emptyRect.offsetMax = new Vector2(-8f, -8f);
            var emptyText = emptyObject.GetComponent<Text>();
            emptyText.font = BuiltinFont();
            emptyText.fontSize = 14;
            emptyText.alignment = TextAnchor.MiddleCenter;
            emptyText.color = new Color(0.24f, 0.16f, 0.12f, 1f);
            emptyText.raycastTarget = false;
            emptyText.text = "No postcards yet.";
            return;
        }

        float slot = 68f;
        float width = Mathf.Max(108f, count * slot + 24f);
        galleryPanel.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 108f);
        for (int i = 0; i < count; i++)
        {
            int index = i;
            var thumbObject = new GameObject("Meme" + (i + 1), typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            thumbObject.transform.SetParent(galleryPanel.transform, false);
            var thumbRect = thumbObject.GetComponent<RectTransform>();
            thumbRect.anchorMin = new Vector2(0f, 0.5f);
            thumbRect.anchorMax = new Vector2(0f, 0.5f);
            thumbRect.pivot = new Vector2(0f, 0.5f);
            thumbRect.sizeDelta = new Vector2(60f, 60f);
            thumbRect.anchoredPosition = new Vector2(16f + i * slot, 0f);
            var image = thumbObject.GetComponent<Image>();
            image.sprite = collectedMemes[i];
            image.preserveAspect = true;
            image.raycastTarget = true;
            var thumbButton = thumbObject.GetComponent<Button>();
            thumbButton.targetGraphic = image;
            thumbButton.onClick.AddListener(() => ViewCollected(index));
        }
    }

    void ViewCollected(int index)
    {
        if (memePopup == null || index < 0 || index >= collectedMemes.Count)
            return;

        browsingGallery = true;
        memePopup.SetActive(true);
        memePopup.transform.SetAsLastSibling();
        if (memeImage != null)
            memeImage.sprite = collectedMemes[index];
        if (memeLabel != null)
            memeLabel.text = "Meme " + (index + 1);
    }

    void SetGalleryButtonVisible(bool visible)
    {
        if (galleryButton != null)
            galleryButton.gameObject.SetActive(visible);
    }

    public static void ShowJourneyButton()
    {
        if (Instance != null)
            Instance.SetJourneyButtonVisible(true);
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
