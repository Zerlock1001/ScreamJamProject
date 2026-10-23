using System;
using System.Collections;
using System.Collections.Generic;
using ScreamJam.Debugging;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
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
    const string ThirdRestSpokenKey = "PetJourney.ThirdRestSpokenFor";
    const string FourthRestSpokenKey = "PetJourney.FourthRestSpokenFor";
    const string NameKey = "PetDialogue.PlayerName";
    const string BeatKey = "PetDialogue.OpeningBeat";
    const string PhaseKey = "PetStory.Phase";
    const string IntroducedKey = "PetCampaign.Introduced";
    const string OfferKey = "PetCampaign.OfferUnlocked";

    [SerializeField] SpriteRenderer pet;
    [SerializeField] Button journeyButton;
    [SerializeField] Image moodFill;
    [SerializeField] float shrinkSeconds = 1.2f;
    [SerializeField] float firstTripSeconds = 30f;
    [SerializeField] float extraSecondsPerTrip = 60f;
    [SerializeField] float moodRecoverSeconds = 60f;
    [SerializeField] float newsDelaySeconds = 15f;
    [SerializeField] string returnLine = "I'm back! I brought you quite a few photos. Remember to check your phone.";
    [SerializeField] string restLine = "I need to rest for a bit now.";
    [SerializeField] string tiredLine = "I'm a little tired. I need to rest.";
    [SerializeField] string newsUrl = "https://itch.io";
    [SerializeField] int memeCount = 3;
    [SerializeField] bool keepProgressBetweenLaunches = true;

    public static bool SavesProgress => Instance != null && Instance.keepProgressBetweenLaunches;
    public int CompletedTrips => tripCount;
    public bool JourneyAvailable { get; private set; }
    public float MoodNormalized => CurrentMood();

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
    Coroutine restNag;
    const string RestNagLine = "I'm rested. When will you let me go find more memes for you?";
    bool newsShown;
    long thirdRestSpokenFor;
    long thirdRestPendingFor;
    string thirdRestPending;
    Sprite friendSleeping;
    Sprite[] weirdSprites;
    Sprite[] clueSprites;
    Sprite fourthBed;
    Sprite fourthMeeting;
    WebCamTexture fourthCamera;
    Sprite fourthCameraFrame;
    long fourthRestSpokenFor;
    long fourthRestPendingFor;
    string fourthRestPending;
    string thirdPlace = "your area";
    bool thirdPlaceReady;
    int thirdPlaceToken;
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
        DebugConsole.RegisterCommand("remake", "/remake", "Erase the save and start the game over.", _ => Remake());

        tripCount = LoadInt(TripCountKey);
        returnAtTicks = LoadTicks(ReturnAtKey);
        moodReadyAtTicks = LoadTicks(MoodReadyKey);
        newsShown = LoadInt(NewsSeenKey) != 0;
        thirdRestSpokenFor = LoadTicks(ThirdRestSpokenKey);
        fourthRestSpokenFor = LoadTicks(FourthRestSpokenKey);
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

    void Start()
    {
        if (tripCount >= 1)
        {
            PetPhone.ShowButton();
            if (PetPhone.Instance != null)
                PetPhone.Instance.SetAway(away);
        }

        if (!away && tripCount == 1 && PetCampaign.Ensure().HasIntroduced)
            BeginRestNag();

        if (!away)
        {
            DebugConsole.CloseImmediate();
            return;
        }

        PetPhone.Ensure();
        if (PetPhone.Instance != null)
            PetPhone.Instance.SetAway(true);

        if (postcardRoutine != null)
            StopCoroutine(postcardRoutine);
        postcardRoutine = StartCoroutine(SendPostcards(Mathf.Max(1f, returnSecondsLeft)));
        PetCampaign.Ensure().OnTripStarted(tripCount, Mathf.Max(1f, returnSecondsLeft));
        DebugConsole.CloseImmediate();
    }

    void OnDestroy()
    {
        ReleaseFourthCamera();
        if (Instance == this)
            Instance = null;
    }

    void Update()
    {
        RefreshMoodBar();
        PlaceMoodWidget();
        if (waitingForDialogue && PetDialogue.Instance != null && !PetDialogue.Instance.IsOpen && !PetCampaign.BlocksJourney)
        {
            waitingForDialogue = false;
            if (homeScriptPending)
            {
                homeScriptPending = false;
                PetCampaign.Ensure().BeginAfterFirstReturn();
            }
        }
        TryShowNews();
        TryShowThirdRestLine();
        TryShowFourthRestLine();

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
                ReleaseFourthCamera();
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

        // newsDelaySeconds is elapsed rest time. The mood bar fills at moodRecoverSeconds.
        long newsAt = moodReadyAtTicks - SecondsToTicks(Mathf.Max(0f, moodRecoverSeconds - newsDelaySeconds));
        if (DateTime.UtcNow.Ticks < newsAt)
            return;

        newsShown = true;
        SaveInt(NewsSeenKey, 1);
        string name = PetDialogue.HasPlayerName ? PetDialogue.PlayerName : "there";
        PetDialogue.SayOnce("Hi, " + name + ". I met a lot of friends while I was traveling. I really want to understand them more deeply!");
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

    public void StartJourney()
    {
        if (!JourneyAvailable)
            return;
        BeginJourney();
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

        StopRestNag();
        float duration = Mathf.Max(1f, firstTripSeconds + tripCount * extraSecondsPerTrip);
        if (PetCampaign.Ensure().HoldsSecondJourney && tripCount >= 2)
            tripCount = 2;
        else
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

    static void Remake()
    {
        EraseSave();
        PetDialogue.ClearSession();
        PetStory.SetPhase(PetStory.Phase.Normal);
        EraseSave();
        DebugConsole.WriteLine("Save erased. Starting over.");
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    public static void EraseSave()
    {
        PlayerPrefs.DeleteKey(TripCountKey);
        PlayerPrefs.DeleteKey(ReturnAtKey);
        PlayerPrefs.DeleteKey(MoodReadyKey);
        PlayerPrefs.DeleteKey(NewsSeenKey);
        PlayerPrefs.DeleteKey(ThirdRestSpokenKey);
        PlayerPrefs.DeleteKey(FourthRestSpokenKey);
        PlayerPrefs.DeleteKey(NameKey);
        PlayerPrefs.DeleteKey(BeatKey);
        PlayerPrefs.DeleteKey(PhaseKey);
        PlayerPrefs.DeleteKey(IntroducedKey);
        PlayerPrefs.DeleteKey(OfferKey);
        PlayerPrefs.DeleteKey("PetCampaign.FeaturesUnlocked");
        PlayerPrefs.DeleteKey("PetCampaign.AwaitingAgree");
        PlayerPrefs.DeleteKey("PetCampaign.SecondAsks");
        PlayerPrefs.DeleteKey("PetStory.ComputerName");
        PlayerPrefs.DeleteKey("PetDialogue.IntroSeen");
        PlayerPrefs.Save();
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
    }

    public void SetJourneyAvailable(bool visible)
    {
        SetJourneyButtonVisible(visible);
    }

    public string PageUrl()
    {
        return NewsPageUrl();
    }

    public string PaperPageUrl()
    {
        string path = System.IO.Path.Combine(Application.streamingAssetsPath, "News", "LucioSomnus-Knowing-Your-Friends-and-Quality-of-Life.pdf");
        if (System.IO.File.Exists(path))
            return new System.Uri(path).AbsoluteUri;
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
        ReleaseFourthCamera();
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
        Instance.SaveString(ReturnAtKey, Instance.returnAtTicks.ToString());
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
        else if (PetCampaign.Ensure().PlaysSecondScript(tripCount))
            RevealSecondJourneyPhone();
        else
            PetPhone.ShowButton();
    }

    void RevealSecondJourneyPhone()
    {
        PetPhone.ShowButton();
        if (PetPhone.Instance != null)
            PetPhone.Instance.Reveal();
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
            waitingForDialogue = true;
            PetCampaign.Ensure().BeginAfterFirstReturn();
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
        if (tripCount == 3 && !PetCampaign.Ensure().PlaysSecondScript(tripCount))
        {
            yield return SendThirdJourney(duration);
            yield break;
        }

        if (tripCount == 4 && !PetCampaign.Ensure().PlaysSecondScript(tripCount))
        {
            yield return SendFourthJourney(duration);
            yield break;
        }

        bool firstTrip = tripCount <= 1;
        bool secondTrip = PetCampaign.Ensure().PlaysSecondScript(tripCount);
        Sprite[] cards = null;
        if (firstTrip || secondTrip)
        {
            EnsureMemeSprites();
            cards = CombineSpritePools(LoadPostCards(), memeSprites);
        }

        if (cards == null || cards.Length == 0)
        {
            EnsureMemeSprites();
            cards = memeSprites;
        }

        if (cards == null || cards.Length == 0)
            yield break;

        int count = firstTrip || secondTrip ? 4 : Mathf.Max(1, Mathf.Min(memeCount, cards.Length));
        string name = PetDialogue.HasPlayerName ? PetDialogue.PlayerName : "there";
        int linkAt = -1;
        bool paperLink = false;
        if (firstTrip)
            linkAt = 1;
        else if (secondTrip)
        {
            linkAt = UnityEngine.Random.Range(0, count);
            paperLink = PetCampaign.Ensure().SecondJourneyRepeats;
        }

        var bag = new List<Sprite>(cards);
        Shuffle(bag);
        int bagIndex = 0;
        int memeCaption = 0;
        float started = Time.unscaledTime;
        for (int i = 0; i < count; i++)
        {
            float sendAt = duration * (i + 1f) / (count + 1f);
            while (away && Time.unscaledTime - started < sendAt)
                yield return null;
            if (!away)
                yield break;

            if (secondTrip)
                RevealSecondJourneyPhone();
            if (PetPhone.Instance == null)
                continue;

            if (i == linkAt)
            {
                if (firstTrip)
                {
                    PetPhone.Instance.AddLinkMessage(
                        "I bet you'll like this.",
                        "https://blinkyfindsnews.com",
                        NewsPageUrl());
                }
                else if (paperLink)
                {
                    PetPhone.Instance.AddLinkMessage(
                        "I saw a really interesting post.",
                        "Lucio Somnus: Knowing One's Friends and the Improvement of Quality of Life",
                        PaperPageUrl());
                }
                else
                {
                    PetPhone.Instance.AddLinkMessage(
                        "I saw a really interesting post.",
                        "Cosimo Lunus",
                        NewsPageUrl());
                }

                continue;
            }

            if (bagIndex >= bag.Count)
            {
                Shuffle(bag);
                bagIndex = 0;
            }

            Sprite sprite = bag[bagIndex++];
            RememberMeme(sprite);
            string caption = firstTrip
                ? FirstMemeCaption(i, name)
                : secondTrip
                    ? SecondMemeCaption(memeCaption, name)
                    : "Postcard " + (i + 1);
            if (secondTrip)
                memeCaption++;
            PetPhone.Instance.AddPostcard(sprite, caption);
        }
    }

    IEnumerator SendThirdJourney(float duration)
    {
        BeginThirdPlaceLookup();
        Sprite[] weird = LoadWeirdMemes();
        Sprite firstWeird;
        Sprite laterWeird;
        PickTwoWeird(weird, out firstWeird, out laterWeird);
        Sprite sleeping = LoadFriendSleeping();
        float started = Time.unscaledTime;
        yield return null;
        while (away && departing)
            yield return null;
        if (!away)
            yield break;

        RevealPhoneIfClosed();
        const int count = 4;
        for (int i = 0; i < count; i++)
        {
            float sendAt = duration * (i + 1f) / (count + 1f);
            while (away && Time.unscaledTime - started < sendAt)
                yield return null;
            if (!away)
                yield break;

            if (i == 1)
            {
                while (away && !thirdPlaceReady)
                    yield return null;
                if (!away)
                    yield break;
            }

            if (PetPhone.Instance == null)
                continue;

            switch (i)
            {
                case 0:
                    RememberMeme(firstWeird);
                    PetPhone.Instance.AddPostcard(firstWeird, PetStory.ApplyName("{name}, I think you'd be interested in this."));
                    break;
                case 1:
                    PetPhone.Instance.AddMessage("Are there any interesting sights in " + thirdPlace + "? I'll take a photo for you.");
                    break;
                case 2:
                    RememberMeme(sleeping);
                    PetPhone.Instance.AddPostcard(sleeping, "Haha, this friend of yours is sleeping so peacefully.");
                    break;
                default:
                    RememberMeme(laterWeird);
                    PetPhone.Instance.AddPostcard(laterWeird, PetStory.ApplyName("{name}, this looks like something your friend has been using lately."));
                    break;
            }
        }
    }

    IEnumerator SendFourthJourney(float duration)
    {
        float started = Time.unscaledTime;
        yield return null;
        while (away && departing)
            yield return null;
        if (!away)
            yield break;

        RevealPhoneIfClosed();
        const int count = 4;
        for (int i = 0; i < count; i++)
        {
            float sendAt = duration * (i + 1f) / (count + 1f);
            while (away && Time.unscaledTime - started < sendAt)
                yield return null;
            if (!away)
            {
                ReleaseFourthCamera();
                yield break;
            }

            RevealPhoneIfClosed();
            if (PetPhone.Instance == null)
                continue;

            switch (i)
            {
                case 0:
                    SendFourthPicture(PickClueMeme(), "mljvdzsxccoh");
                    break;
                case 1:
                    SendFourthPicture(LoadFourthBed(), "Looks like one of your friends had a fling.");
                    break;
                case 2:
                    SendFourthPicture(LoadFourthMeeting(), "Looks like your friends are having a little meeting.");
                    break;
                default:
                    yield return SendFourthCameraBeat();
                    break;
            }
        }
    }

    void SendFourthPicture(Sprite picture, string caption)
    {
        if (PetPhone.Instance == null || string.IsNullOrEmpty(caption))
            return;

        if (picture == null)
        {
            PetPhone.Instance.AddMessage(caption);
            return;
        }

        RememberMeme(picture);
        PetPhone.Instance.AddPostcard(picture, caption);
    }

    IEnumerator SendFourthCameraBeat()
    {
        if (!TryOpenFourthCamera())
        {
            if (away && PetPhone.Instance != null)
                PetPhone.Instance.AddMessage("What a shame. You don't have a camera, and I wanted to take a picture of you.");
            yield break;
        }

        float until = Time.unscaledTime + 3f;
        while (away && fourthCamera != null && Time.unscaledTime < until)
        {
            WebCamTexture camera = fourthCamera;
            if (camera != null && camera.width > 16 && camera.height > 16 && camera.didUpdateThisFrame)
            {
                fourthCameraFrame = CopyCameraFrame(camera);
                break;
            }

            yield return null;
        }

        ReleaseFourthCamera();
        if (!away || PetPhone.Instance == null)
            yield break;

        RevealPhoneIfClosed();
        if (fourthCameraFrame != null)
        {
            RememberMeme(fourthCameraFrame);
            PetPhone.Instance.AddPostcard(fourthCameraFrame, "Do you like the photo I took for you?");
        }
        else
            PetPhone.Instance.AddMessage("What a shame. You don't have a camera, and I wanted to take a picture of you.");
    }

    bool TryOpenFourthCamera()
    {
        ReleaseFourthCamera();
        fourthCameraFrame = null;

        WebCamDevice[] devices;
        try
        {
            devices = WebCamTexture.devices;
        }
        catch (Exception)
        {
            return false;
        }

        if (devices == null || devices.Length == 0)
            return false;

        string deviceName = null;
        for (int i = 0; i < devices.Length; i++)
        {
            if (!string.IsNullOrEmpty(devices[i].name))
            {
                deviceName = devices[i].name;
                break;
            }
        }

        if (string.IsNullOrEmpty(deviceName))
            return false;

        WebCamTexture camera = null;
        try
        {
            camera = new WebCamTexture(deviceName, 640, 480, 30);
            camera.Play();
        }
        catch (Exception)
        {
            if (camera != null)
                Destroy(camera);
            return false;
        }

        fourthCamera = camera;
        return camera != null;
    }

    void ReleaseFourthCamera()
    {
        WebCamTexture camera = fourthCamera;
        fourthCamera = null;
        if (camera == null)
            return;

        if (camera.isPlaying)
            camera.Stop();
        Destroy(camera);
    }

    static Sprite CopyCameraFrame(WebCamTexture camera)
    {
        if (camera == null)
            return null;

        int width = camera.width;
        int height = camera.height;
        if (width <= 16 || height <= 16)
            return null;

        Color32[] pixels;
        try
        {
            pixels = camera.GetPixels32();
        }
        catch (Exception)
        {
            return null;
        }

        if (pixels == null || pixels.Length < width * height)
            return null;

        if (camera.videoVerticallyMirrored)
            FlipCameraRows(pixels, width, height);

        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.SetPixels32(pixels);
        texture.Apply(false, false);
        return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), 100f);
    }

    static void FlipCameraRows(Color32[] pixels, int width, int height)
    {
        for (int y = 0; y < height / 2; y++)
        {
            int top = y * width;
            int bottom = (height - 1 - y) * width;
            for (int x = 0; x < width; x++)
            {
                Color32 held = pixels[top + x];
                pixels[top + x] = pixels[bottom + x];
                pixels[bottom + x] = held;
            }
        }
    }

    Sprite PickClueMeme()
    {
        Sprite[] pool = LoadClueMemes();
        if (pool == null || pool.Length == 0)
            return null;
        return pool[UnityEngine.Random.Range(0, pool.Length)];
    }

    Sprite[] LoadClueMemes()
    {
        if (clueSprites != null)
            return clueSprites;

        var loaded = new List<Sprite>();
        AppendResourceSprites(loaded, "Clue");
        AppendResourceSprites(loaded, "clue");
        AppendResourceSprites(loaded, "Memes/Clue");
        AppendResourceSprites(loaded, "memes/clue");
        AppendResourceSprites(loaded, "memes/\u7ebf\u7d22");
#if UNITY_EDITOR
        AppendClueNamedSprites(loaded);
#endif
        if (loaded.Count == 0)
            AppendOrdinaryMemes(loaded);

        clueSprites = loaded.Count > 0 ? loaded.ToArray() : new Sprite[0];
        return clueSprites;
    }

    static void AppendResourceSprites(List<Sprite> loaded, string resourcePath)
    {
        Sprite[] found = Resources.LoadAll<Sprite>(resourcePath);
        if (found == null)
            return;

        for (int i = 0; i < found.Length; i++)
        {
            if (found[i] != null && !loaded.Contains(found[i]))
                loaded.Add(found[i]);
        }
    }

#if UNITY_EDITOR
    static void AppendClueNamedSprites(List<Sprite> loaded)
    {
        string[] guids = UnityEditor.AssetDatabase.FindAssets("t:Sprite", new[] { "Assets" });
        for (int i = 0; i < guids.Length; i++)
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
            if (!PathIsClue(path))
                continue;

            Sprite sprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite != null && !loaded.Contains(sprite))
                loaded.Add(sprite);
        }
    }

    static bool PathIsClue(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
            return false;

        string[] parts = assetPath.Split('/', '\\');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = System.IO.Path.GetFileNameWithoutExtension(parts[i]);
            if (NameIsClue(part))
                return true;
        }

        return false;
    }
#endif

    static bool NameIsClue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        return value.IndexOf("clue", StringComparison.OrdinalIgnoreCase) >= 0
            || value.IndexOf("\u7ebf\u7d22") >= 0;
    }

    static void AppendOrdinaryMemes(List<Sprite> loaded)
    {
        OrdinaryMemeSet set = Resources.Load<OrdinaryMemeSet>("OrdinaryMemeSet");
        if (set != null && set.memes != null)
        {
            for (int i = 0; i < set.memes.Length; i++)
            {
                if (set.memes[i] != null && !loaded.Contains(set.memes[i]))
                    loaded.Add(set.memes[i]);
            }
        }

#if UNITY_EDITOR
        if (loaded.Count > 0)
            return;

        if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/memes/Nomral-1"))
            return;

        string[] guids = UnityEditor.AssetDatabase.FindAssets("t:Sprite", new[] { "Assets/memes/Nomral-1" });
        for (int i = 0; i < guids.Length; i++)
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
            Sprite sprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite != null && !loaded.Contains(sprite))
                loaded.Add(sprite);
        }
#endif
    }

    Sprite LoadFourthBed()
    {
        if (fourthBed != null)
            return fourthBed;

        fourthBed = Resources.Load<Sprite>("FourthJourneyBed");
#if UNITY_EDITOR
        if (fourthBed == null)
            fourthBed = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Resources/FourthJourneyBed.png");
#endif
        return fourthBed;
    }

    Sprite LoadFourthMeeting()
    {
        if (fourthMeeting != null)
            return fourthMeeting;

        fourthMeeting = Resources.Load<Sprite>("FourthJourneyMeeting");
#if UNITY_EDITOR
        if (fourthMeeting == null)
            fourthMeeting = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Resources/FourthJourneyMeeting.png");
#endif
        return fourthMeeting;
    }

    void RevealPhoneIfClosed()
    {
        PetPhone.ShowButton();
        if (PetPhone.Instance != null && !PetPhone.Instance.IsOpen)
            PetPhone.Instance.SetOpen(true);
    }

    static void PickTwoWeird(Sprite[] pool, out Sprite first, out Sprite second)
    {
        first = null;
        second = null;
        if (pool == null || pool.Length == 0)
            return;

        int a = UnityEngine.Random.Range(0, pool.Length);
        first = pool[a];
        if (pool.Length == 1)
        {
            second = first;
            return;
        }

        int b = UnityEngine.Random.Range(0, pool.Length - 1);
        if (b >= a)
            b++;
        second = pool[b];
    }

    Sprite[] LoadWeirdMemes()
    {
        if (weirdSprites != null)
            return weirdSprites;

        var loaded = new List<Sprite>();
        WeirdMemeSet set = Resources.Load<WeirdMemeSet>("WeirdMemeSet");
        if (set != null && set.memes != null)
        {
            for (int i = 0; i < set.memes.Length; i++)
            {
                if (set.memes[i] != null)
                    loaded.Add(set.memes[i]);
            }
        }

#if UNITY_EDITOR
        if (loaded.Count == 0)
            AppendWeirdFolder(loaded);
#endif
        weirdSprites = loaded.ToArray();
        return weirdSprites;
    }

#if UNITY_EDITOR
    static void AppendWeirdFolder(List<Sprite> loaded)
    {
        if (!UnityEditor.AssetDatabase.IsValidFolder("Assets/memes"))
            return;

        string[] folders = UnityEditor.AssetDatabase.GetSubFolders("Assets/memes");
        var roots = new List<string>();
        for (int i = 0; i < folders.Length; i++)
        {
            string name = System.IO.Path.GetFileName(folders[i]);
            if (name.IndexOf("Weird", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            roots.Add(folders[i]);
        }

        if (roots.Count == 0)
            return;

        string[] guids = UnityEditor.AssetDatabase.FindAssets("t:Sprite", roots.ToArray());
        for (int i = 0; i < guids.Length; i++)
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
            Sprite sprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite != null)
                loaded.Add(sprite);
        }
    }
#endif

    Sprite LoadFriendSleeping()
    {
        if (friendSleeping != null)
            return friendSleeping;

        friendSleeping = Resources.Load<Sprite>("FriendSleeping");
#if UNITY_EDITOR
        if (friendSleeping == null)
            friendSleeping = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Resources/FriendSleeping.png");
#endif
        return friendSleeping;
    }

    void BeginThirdPlaceLookup()
    {
        int token = ++thirdPlaceToken;
        thirdPlace = "your area";
        thirdPlaceReady = false;
        StartCoroutine(LookupRoughPlace(value =>
        {
            if (token != thirdPlaceToken)
                return;
            if (!string.IsNullOrEmpty(value))
                thirdPlace = value;
            thirdPlaceReady = true;
        }));
    }

    // 只填第三次远行那一句里的地名。失败、超时、保留地址都回 your area。不把 IP 写入存档，也不打日志。
    IEnumerator LookupRoughPlace(Action<string> done)
    {
        string found = null;
        yield return RequestRoughPlace("https://ipwho.is/", ParseWhoPlace, value => found = value);
        if (string.IsNullOrEmpty(found))
            yield return RequestRoughPlace("https://ipinfo.io/json", ParseInfoPlace, value => found = value);
        if (done != null)
            done(found);
    }

    IEnumerator RequestRoughPlace(string url, Func<string, string> parse, Action<string> onPlace)
    {
        using (var request = UnityWebRequest.Get(url))
        {
            request.timeout = 4;
            request.SetRequestHeader("Accept", "application/json");
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success || request.downloadHandler == null)
                yield break;

            string place = null;
            try
            {
                place = parse(request.downloadHandler.text);
            }
            catch (Exception)
            {
                place = null;
            }

            if (!string.IsNullOrEmpty(place) && onPlace != null)
                onPlace(place);
        }
    }

    static string ParseWhoPlace(string body)
    {
        WhoPlace payload = JsonUtility.FromJson<WhoPlace>(body);
        if (payload == null || !payload.success)
            return null;
        return CleanPlaceName(payload.city, payload.region);
    }

    static string ParseInfoPlace(string body)
    {
        InfoPlace payload = JsonUtility.FromJson<InfoPlace>(body);
        if (payload == null || payload.bogon)
            return null;
        return CleanPlaceName(payload.city, payload.region);
    }

    static string CleanPlaceName(string city, string region)
    {
        string place = SpokenPlace(city);
        if (string.IsNullOrEmpty(place))
            place = SpokenPlace(region);
        return place;
    }

    static string SpokenPlace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim();
        if (value.Length == 0 || value.Length > 48 || IsReservedLabel(value) || LooksLikeAddress(value))
            return null;

        bool letter = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                if (c < '0' || c > '9')
                    letter = true;
                continue;
            }

            if (c == ' ' || c == '-' || c == '\'' || c == '.' || c == ',')
                continue;
            if (char.IsLetter(c) && c <= '\u024F')
            {
                letter = true;
                continue;
            }

            return null;
        }

        return letter ? value : null;
    }

    static bool IsReservedLabel(string value)
    {
        return string.Equals(value, "reserved", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "bogon", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "private", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase);
    }

    static bool LooksLikeAddress(string value)
    {
        int dots = 0;
        int digits = 0;
        int letters = 0;
        int colons = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c >= '0' && c <= '9')
                digits++;
            else if (c == '.')
                dots++;
            else if (c == ':')
                colons++;
            else if (char.IsLetter(c))
                letters++;
        }

        if (colons >= 2)
            return true;
        return dots == 3 && letters == 0 && digits >= 4;
    }

    void TryShowThirdRestLine()
    {
        if (tripCount != 3 || moodReadyAtTicks <= 0 || away || arrivalLineQueued)
            return;
        if (thirdRestSpokenFor == moodReadyAtTicks)
            return;

        long speakAt = moodReadyAtTicks - SecondsToTicks(Mathf.Max(0f, moodRecoverSeconds - newsDelaySeconds));
        if (DateTime.UtcNow.Ticks < speakAt)
            return;

        if (thirdRestPendingFor != moodReadyAtTicks)
        {
            thirdRestPendingFor = moodReadyAtTicks;
            thirdRestPending = UnityEngine.Random.Range(0, 2) == 0
                ? "As expected, you only get along with people better when you know everything about them."
                : "You must really enjoy this feeling.";
        }

        if (waitingForDialogue || (memePopup != null && memePopup.activeSelf))
            return;
        if (PetDialogue.Instance == null || PetDialogue.Instance.IsOpen || PetCampaign.BlocksJourney)
            return;

        thirdRestSpokenFor = moodReadyAtTicks;
        SaveString(ThirdRestSpokenKey, thirdRestSpokenFor.ToString());
        PetDialogue.SayOnce(thirdRestPending);
    }

    void TryShowFourthRestLine()
    {
        if (tripCount < 4 || moodReadyAtTicks <= 0 || away || arrivalLineQueued)
            return;
        if (fourthRestSpokenFor == moodReadyAtTicks)
            return;

        long speakAt = moodReadyAtTicks - SecondsToTicks(Mathf.Max(0f, moodRecoverSeconds - newsDelaySeconds));
        if (DateTime.UtcNow.Ticks < speakAt)
            return;

        if (fourthRestPendingFor != moodReadyAtTicks)
        {
            fourthRestPendingFor = moodReadyAtTicks;
            fourthRestPending = UnityEngine.Random.Range(0, 2) == 0
                ? "Nothing is hidden from you anymore. You must be under a lot less pressure."
                : "It's still more comfortable to know everything, isn't it?";
        }

        if (waitingForDialogue || (memePopup != null && memePopup.activeSelf))
            return;
        if (PetDialogue.Instance == null || PetDialogue.Instance.IsOpen || PetCampaign.BlocksJourney)
            return;

        fourthRestSpokenFor = moodReadyAtTicks;
        SaveString(FourthRestSpokenKey, fourthRestSpokenFor.ToString());
        PetDialogue.SayOnce(fourthRestPending);
    }

#pragma warning disable 0649
    [Serializable]
    sealed class WhoPlace
    {
        public bool success;
        public string city;
        public string region;
    }

    [Serializable]
    sealed class InfoPlace
    {
        public string city;
        public string region;
        public bool bogon;
    }
#pragma warning restore 0649

    Sprite[] LoadPostCards()
    {
        PostCardSet set = Resources.Load<PostCardSet>("PostCardSet");
        if (set != null && set.cards != null)
        {
            var loaded = new List<Sprite>();
            for (int i = 0; i < set.cards.Length; i++)
            {
                if (set.cards[i] != null)
                    loaded.Add(set.cards[i]);
            }

            if (loaded.Count > 0)
                return loaded.ToArray();
        }

#if UNITY_EDITOR
        string[] guids = UnityEditor.AssetDatabase.FindAssets("t:Sprite", new[] { "Assets/PostCards" });
        var editorCards = new List<Sprite>();
        for (int i = 0; i < guids.Length; i++)
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
            Sprite sprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite != null)
                editorCards.Add(sprite);
        }

        if (editorCards.Count > 0)
            return editorCards.ToArray();
#endif
        return null;
    }

    static Sprite[] CombineSpritePools(Sprite[] postcards, Sprite[] memes)
    {
        var pool = new List<Sprite>();
        AppendUniqueSprites(pool, postcards);
        AppendUniqueSprites(pool, memes);
        return pool.Count > 0 ? pool.ToArray() : null;
    }

    static void AppendUniqueSprites(List<Sprite> pool, Sprite[] source)
    {
        if (source == null)
            return;

        for (int i = 0; i < source.Length; i++)
        {
            Sprite sprite = source[i];
            if (sprite == null)
                continue;

            bool seen = false;
            for (int j = 0; j < pool.Count; j++)
            {
                if (pool[j] == sprite)
                {
                    seen = true;
                    break;
                }
            }

            if (!seen)
                pool.Add(sprite);
        }
    }

    static void Shuffle(List<Sprite> bag)
    {
        for (int i = bag.Count - 1; i > 0; i--)
        {
            int swap = UnityEngine.Random.Range(0, i + 1);
            Sprite held = bag[i];
            bag[i] = bag[swap];
            bag[swap] = held;
        }
    }

    static string FirstMemeCaption(int index, string name)
    {
        switch (index)
        {
            case 0:
                return "Hey, " + name + ", look what I found!";
            case 1:
                return "I bet you'll like this.";
            case 2:
                return name + ", this is so interesting!";
            default:
                return "I have to show you this!";
        }
    }

    static string SecondMemeCaption(int index, string name)
    {
        switch (index)
        {
            case 0:
                return name + ", look! Look! This is so funny!";
            case 1:
                return "The moment I saw this, I knew I had to send it to you!";
            default:
                return name + ", this is so interesting. I'll send it to you!";
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

    public void BeginRestNag()
    {
        StopRestNag();
        restNag = StartCoroutine(RestNagRoutine());
    }

    void StopRestNag()
    {
        if (restNag == null)
            return;

        StopCoroutine(restNag);
        restNag = null;
    }

    IEnumerator RestNagRoutine()
    {
        float waitUntil = Time.unscaledTime + 10f;
        while (!away && tripCount == 1)
        {
            if (Time.unscaledTime < waitUntil || CurrentMood() < 0.999f || departing || returning)
            {
                yield return null;
                continue;
            }

            if (PetCampaign.BlocksJourney || !PetDialogue.HasPlayerName || (PetDialogue.Instance != null && PetDialogue.Instance.IsOpen))
            {
                yield return null;
                continue;
            }

            PetDialogue.SayOnce(RestNagLine);
            float shownAt = Time.unscaledTime;
            while (!away && tripCount == 1 && Time.unscaledTime < shownAt + 5f)
                yield return null;

            PetDialogue.HideIf(RestNagLine);
            waitUntil = shownAt + 15f;
        }
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

        if (moodFill != null)
        {
            moodFill.type = Image.Type.Filled;
            moodFill.fillMethod = Image.FillMethod.Horizontal;
            moodFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            moodFill.raycastTarget = false;
        }

        if (moodWidget == null)
            moodWidget = FindMoodWidget();
        if (moodWidget == null)
        {
            var moodObject = GameObject.Find("Mood");
            if (moodObject != null)
                moodWidget = moodObject.transform as RectTransform;
        }

        if (moodWidget == null)
            return;

        moodWidget.anchorMin = new Vector2(0.5f, 0.5f);
        moodWidget.anchorMax = new Vector2(0.5f, 0.5f);
        moodWidget.pivot = new Vector2(0.5f, 1f);
        moodWidget.gameObject.SetActive(true);
        RefreshMoodBar();
        PlaceMoodWidget();
    }

    void PlaceMoodWidget()
    {
        if (moodWidget == null)
            moodWidget = FindMoodWidget();
        if (moodWidget == null)
        {
            var moodObject = GameObject.Find("Mood");
            if (moodObject != null)
                moodWidget = moodObject.transform as RectTransform;
        }
        if (moodWidget == null)
            return;

        bool visible = pet != null && pet.enabled && pet.gameObject.activeInHierarchy;
        if (!visible)
        {
            if (moodWidget.gameObject.activeSelf)
                moodWidget.gameObject.SetActive(false);
            return;
        }

        if (!moodWidget.gameObject.activeSelf)
            moodWidget.gameObject.SetActive(true);

        Camera cam = Camera.main;
        Canvas canvas = moodWidget.GetComponentInParent<Canvas>();
        RectTransform canvasRect = canvas != null ? canvas.transform as RectTransform : null;
        if (cam == null || canvasRect == null)
            return;

        Bounds bounds = pet.bounds;
        Vector3 world = new Vector3(bounds.center.x, bounds.min.y - 0.15f, bounds.center.z);
        Vector3 screen = cam.WorldToScreenPoint(world);
        if (screen.z < 0f)
            return;

        Camera uiCamera = canvas.renderMode == RenderMode.ScreenSpaceOverlay
            ? null
            : (canvas.worldCamera != null ? canvas.worldCamera : cam);
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                canvasRect, screen, uiCamera, out Vector2 local))
            return;

        Rect rect = canvasRect.rect;
        float halfWidth = moodWidget.rect.width * 0.5f;
        float height = moodWidget.rect.height;
        local.x = Mathf.Clamp(local.x, rect.xMin + halfWidth, rect.xMax - halfWidth);
        local.y = Mathf.Clamp(local.y, rect.yMin + height + 6f, rect.yMax - 6f);
        moodWidget.anchoredPosition = local - rect.center;
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

        memeSprites = new Sprite[Mathf.Max(4, memeCount)];
        var colors = new[]
        {
            new Color(0.95f, 0.55f, 0.28f, 1f),
            new Color(0.35f, 0.62f, 0.9f, 1f),
            new Color(0.45f, 0.75f, 0.42f, 1f),
            new Color(0.72f, 0.38f, 0.78f, 1f)
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
                int kind = pattern % 4;
                if (kind == 0)
                    mark = dx * dx + dy * dy <= 28f * 28f;
                else if (kind == 1)
                    mark = Mathf.Abs(dx) <= 26f && Mathf.Abs(dy) <= 26f;
                else if (kind == 2)
                    mark = dy > -30f && dy < 34f && Mathf.Abs(dx) < (34f - dy) * 0.7f;
                else
                    mark = Mathf.Abs(dx) + Mathf.Abs(dy) <= 36f;

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
        else
        {
            background.sprite = GallerySkin();
            background.type = Image.Type.Simple;
        }

        var drag = galleryPanel.AddComponent<WindowDrag>();
        drag.Target = rect;

        var handleObject = new GameObject("Handle", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        handleObject.transform.SetParent(galleryPanel.transform, false);
        var handleRect = handleObject.GetComponent<RectTransform>();
        handleRect.anchorMin = new Vector2(0f, 1f);
        handleRect.anchorMax = new Vector2(1f, 1f);
        handleRect.pivot = new Vector2(0.5f, 1f);
        handleRect.sizeDelta = new Vector2(0f, 28f);
        handleRect.anchoredPosition = Vector2.zero;
        var handleImage = handleObject.GetComponent<Image>();
        handleImage.sprite = background.sprite;
        handleImage.type = background.type;
        handleImage.color = new Color(0.9f, 0.84f, 0.74f, 1f);
        handleImage.raycastTarget = true;
        var handleDrag = handleObject.AddComponent<WindowDrag>();
        handleDrag.Target = rect;
        var title = new GameObject("Title", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        title.transform.SetParent(handleObject.transform, false);
        var titleRect = title.GetComponent<RectTransform>();
        titleRect.anchorMin = Vector2.zero;
        titleRect.anchorMax = Vector2.one;
        titleRect.offsetMin = new Vector2(10f, 0f);
        titleRect.offsetMax = new Vector2(-34f, 0f);
        var titleText = title.GetComponent<Text>();
        titleText.font = BuiltinFont();
        titleText.fontSize = 14;
        titleText.fontStyle = FontStyle.Bold;
        titleText.alignment = TextAnchor.MiddleLeft;
        titleText.color = new Color(0.24f, 0.16f, 0.12f, 1f);
        titleText.raycastTarget = false;
        titleText.text = "Collections";
        AddGalleryClose(handleObject.transform);

        galleryPanel.SetActive(false);
    }

    void CloseGallery()
    {
        if (galleryPanel != null)
            galleryPanel.SetActive(false);
        browsingGallery = false;
        if (memePopup != null)
            memePopup.SetActive(false);
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
            rect.anchoredPosition = new Vector2(424f, 0f);
            galleryPanel.SetActive(true);
            galleryPanel.transform.SetAsLastSibling();
            RebuildGallery();
            return;
        }

        CloseGallery();
    }

    void AddGalleryClose(Transform parent)
    {
        var buttonObject = new GameObject("Close", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        var rect = buttonObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = new Vector2(-4f, -3f);
        rect.sizeDelta = new Vector2(22f, 22f);
        var image = buttonObject.GetComponent<Image>();
        image.sprite = GallerySkin();
        image.type = Image.Type.Simple;
        image.color = new Color(0.86f, 0.22f, 0.18f, 1f);
        image.raycastTarget = true;
        var button = buttonObject.GetComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(CloseGallery);
        var labelObject = new GameObject("X", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        labelObject.transform.SetParent(buttonObject.transform, false);
        var labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;
        var label = labelObject.GetComponent<Text>();
        label.font = BuiltinFont();
        label.fontSize = 14;
        label.fontStyle = FontStyle.Bold;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = Color.white;
        label.raycastTarget = false;
        label.text = "X";
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
        else
            CloseGallery();
    }

    void RebuildGallery()
    {
        for (int i = galleryPanel.transform.childCount - 1; i >= 0; i--)
        {
            Transform child = galleryPanel.transform.GetChild(i);
            if (child.name == "Handle")
                continue;
            Destroy(child.gameObject);
        }

        int count = collectedMemes.Count;
        if (count == 0)
        {
            galleryPanel.GetComponent<RectTransform>().sizeDelta = new Vector2(220f, 100f);
            var emptyObject = new GameObject("Empty", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            emptyObject.transform.SetParent(galleryPanel.transform, false);
            var emptyRect = emptyObject.GetComponent<RectTransform>();
            emptyRect.anchorMin = Vector2.zero;
            emptyRect.anchorMax = Vector2.one;
            emptyRect.offsetMin = new Vector2(8f, 8f);
            emptyRect.offsetMax = new Vector2(-8f, -32f);
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
        galleryPanel.GetComponent<RectTransform>().sizeDelta = new Vector2(width, 136f);
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
            thumbRect.anchoredPosition = new Vector2(16f + i * slot, -12f);
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
        if (index < 0 || index >= collectedMemes.Count)
            return;

        PetPhone.ShowPicture(collectedMemes[index]);
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
        JourneyAvailable = visible;
        if (journeyButton != null)
            journeyButton.gameObject.SetActive(false);
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

    static Sprite gallerySkin;

    static Sprite GallerySkin()
    {
        if (gallerySkin != null)
            return gallerySkin;

        var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        var pixels = new Color[16];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = Color.white;
        texture.SetPixels(pixels);
        texture.Apply();
        texture.hideFlags = HideFlags.HideAndDontSave;
        gallerySkin = Sprite.Create(texture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 100f);
        gallerySkin.hideFlags = HideFlags.HideAndDontSave;
        return gallerySkin;
    }

    static Font BuiltinFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }
}
