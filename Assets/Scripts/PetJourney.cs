using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ScreamJam.Audio;
using ScreamJam.Debugging;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 远行：播放 JourneyStart 后离开，途中把明信片发到手机。回来后说一句，不再展示带回了什么。
/// 第一次离开 30 秒，第二次及以后每次 1 分钟。
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
    const string CollectedMemesKey = "PetJourney.CollectedMemes";
    const string PhaseKey = "PetStory.Phase";
    const string IntroducedKey = "PetCampaign.Introduced";
    const string OfferKey = "PetCampaign.OfferUnlocked";

    [SerializeField] SpriteRenderer pet;
    [SerializeField] Button journeyButton;
    [SerializeField] Image moodFill;
    [SerializeField] float shrinkSeconds = 1.2f;
    [SerializeField] float firstTripSeconds = 30f;
    [SerializeField] float laterTripSeconds = 60f;
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
    public bool PetBodyVisible => VisibleBody() != null && VisibleBody().enabled && VisibleBody().gameObject.activeInHierarchy;
    public bool UsingFinalBody => finalForm;
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
    bool bodyExpected;
    bool finalForm;
    bool finalBodyShown = true;
    SpriteRenderer finalBody;
    SpriteRenderer[] finalParts;
    Transform redEye;
    Vector3 redEyeRest;
    bool redEyeReady;
    const float RedEyeRadius = 0.3f;
    float departUnlockAt;
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
    GifClip morseGif;
    GifClip puzzleGif;
    Sprite[] weirdSprites;
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
    bool moodBarSuppressed;
    long returnAtTicks;
    long moodReadyAtTicks;
    float returnSecondsLeft = -1f;
    bool returnCountdownArmed;
    Coroutine postcardRoutine;
    Sprite[] memeSprites;
    Button galleryButton;
    GameObject galleryPanel;
    CanvasGroup galleryGroup;
    Coroutine galleryAnim;
    bool galleryClosing;
    const float GalleryPageScale = 1.5f;
    readonly List<Sprite> collectedMemes = new List<Sprite>();
    readonly List<string> collectedMemeKeys = new List<string>();
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
        DebugConsole.RegisterCommand("sendpics", "/sendpics", "Send 10 random postcards and memes to the phone for testing.", _ => SendTestPictures());

        tripCount = LoadInt(TripCountKey);
        returnAtTicks = LoadTicks(ReturnAtKey);
        moodReadyAtTicks = LoadTicks(MoodReadyKey);
        newsShown = LoadInt(NewsSeenKey) != 0;
        thirdRestSpokenFor = LoadTicks(ThirdRestSpokenKey);
        fourthRestSpokenFor = LoadTicks(FourthRestSpokenKey);
        BindJourneyButton();
        BindGalleryButton();
        LoadCollectedMemes();
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
                    SetShown(false);
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
        if (PetStory.Current == PetStory.Phase.Final || PetCampaign.Ensure().HasJoinedCult)
            UseFinalCharacter();
        else
            SetFinalChildActive(false);

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

        if (departing && departUnlockAt > 0f && Time.unscaledTime >= departUnlockAt)
            departing = false;

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
        string name = PetStory.SpokenName("there");
        PetDialogue.SayOnce("Hi, " + name + ". I met a lot of friends while I was traveling. I really want to understand them more deeply!");
    }

    string NewsPageUrl()
    {
        string path = System.IO.Path.Combine(Application.streamingAssetsPath, "News", "trump-greenville-rally.html");
        if (System.IO.File.Exists(path))
            return new System.Uri(path).AbsoluteUri;
        return newsUrl;
    }

    string XPostUrl()
    {
        string path = System.IO.Path.Combine(Application.streamingAssetsPath, "News", "X-Post-ARG-release", "index.html");
        if (!System.IO.File.Exists(path))
            path = System.IO.Path.Combine(Application.dataPath, "News", "X-Post-ARG-release", "index.html");
        if (System.IO.File.Exists(path))
            return new System.Uri(path).AbsoluteUri;
        return NewsPageUrl();
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

        if (PetCampaign.Ensure().HasJoinedCult)
        {
            PetDialogue.Say("We don't need to limit you this way anymore.");
            return;
        }

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
        bodyExpected = false;
        float duration = Mathf.Max(1f, tripCount <= 0 ? firstTripSeconds : laterTripSeconds);
        if (PetCampaign.Ensure().HoldsSecondJourney && tripCount >= 2)
            tripCount = 2;
        else if (PetCampaign.Ensure().HoldsFifthJourney && tripCount >= 5)
            tripCount = 5;
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
        departUnlockAt = Time.unscaledTime + 2.5f;
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
        PlayerPrefs.DeleteKey("PetDialogue.OpeningComplete");
        PlayerPrefs.DeleteKey(CollectedMemesKey);
        PlayerPrefs.DeleteKey("PetPhone.ChatHistory");
        PlayerPrefs.DeleteKey(PhaseKey);
        PlayerPrefs.DeleteKey(IntroducedKey);
        PlayerPrefs.DeleteKey(OfferKey);
        PlayerPrefs.DeleteKey("PetCampaign.FeaturesUnlocked");
        PlayerPrefs.DeleteKey("PetCampaign.AwaitingAgree");
        PlayerPrefs.DeleteKey("PetCampaign.SecondAsks");
        PlayerPrefs.DeleteKey("PetCampaign.CultJoined");
        PlayerPrefs.DeleteKey("PetCampaign.MemberId");
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
        SetShown(visible);
    }

    public SpriteRenderer HiddenClickRenderer
    {
        get
        {
            if (!finalForm || pet == null || pet.enabled || pet.sprite == null)
                return null;
            return pet;
        }
    }

    public void UseFinalCharacter()
    {
        if (!finalForm)
        {
            GameObject found = FindFinalChild();
            if (found == null)
                found = FindSceneObject("Final");
            if (found == null)
                return;

            found.SetActive(true);
            finalBody = found.GetComponent<SpriteRenderer>();
            finalParts = found.GetComponentsInChildren<SpriteRenderer>(true);
            if (finalBody == null && finalParts != null && finalParts.Length > 0)
                finalBody = finalParts[0];
            finalForm = true;
            finalBodyShown = !away && !departing;
            BindRedEye(found.transform);
            CapsuleMenu.RetainHiddenClick(pet, finalParts);
            if (PetDialogue.Instance != null)
                PetDialogue.Instance.FollowBody(found.transform);
        }

        if (pet != null)
            pet.enabled = false;
        SetFinalParts(finalBodyShown);
    }

    void SetFinalChildActive(bool active)
    {
        GameObject found = FindFinalChild();
        if (found != null)
            found.SetActive(active);
    }

    GameObject FindFinalChild()
    {
        Transform root = pet != null ? pet.transform : transform;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            if (child != null && string.Equals(child.name, "Final", StringComparison.OrdinalIgnoreCase))
                return child.gameObject;
        }

        return null;
    }

    void BindRedEye(Transform final)
    {
        redEye = FindChildNamed(final, "RedEye");
        redEyeReady = redEye != null;
        if (redEyeReady)
            redEyeRest = redEye.localPosition;
    }

    static Transform FindChildNamed(Transform root, string objectName)
    {
        if (root == null)
            return null;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform child = root.GetChild(i);
            if (child != null && string.Equals(child.name, objectName, StringComparison.OrdinalIgnoreCase))
                return child;

            Transform nested = FindChildNamed(child, objectName);
            if (nested != null)
                return nested;
        }

        return null;
    }

    void TrackRedEye()
    {
        if (!finalForm || !redEyeReady || redEye == null)
            return;

        if (!finalBodyShown || !redEye.gameObject.activeInHierarchy || redEye.parent == null)
        {
            redEye.localPosition = redEyeRest;
            return;
        }

        Camera cam = Camera.main;
        if (cam == null)
            return;

        Vector3 screen = Input.mousePosition;
        float depth = Mathf.Abs(cam.transform.position.z - redEye.position.z);
        Vector3 world = cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, depth));
        Vector3 mouseLocal = redEye.parent.InverseTransformPoint(world);
        var offset = new Vector2(mouseLocal.x - redEyeRest.x, mouseLocal.y - redEyeRest.y);
        if (offset.sqrMagnitude > RedEyeRadius * RedEyeRadius)
            offset = offset.normalized * RedEyeRadius;

        var target = new Vector3(redEyeRest.x + offset.x, redEyeRest.y + offset.y, redEyeRest.z);
        float follow = 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);
        redEye.localPosition = Vector3.Lerp(redEye.localPosition, target, follow);
    }

    static GameObject FindSceneObject(string objectName)
    {
        Animator[] animators = Resources.FindObjectsOfTypeAll<Animator>();
        for (int i = 0; i < animators.Length; i++)
        {
            Animator animator = animators[i];
            if (animator == null || animator.gameObject.name != objectName)
                continue;
            if (!animator.gameObject.scene.IsValid())
                continue;
            return animator.gameObject;
        }

        return null;
    }

    SpriteRenderer VisibleBody()
    {
        if (finalForm && finalBody != null)
            return finalBody;
        return pet;
    }

    void SetShown(bool visible)
    {
        finalBodyShown = visible;
        if (finalForm)
        {
            if (pet != null)
                pet.enabled = false;
            SetFinalParts(visible);
            return;
        }

        if (pet != null)
            pet.enabled = visible;
    }

    void SetFinalParts(bool visible)
    {
        if (finalParts == null)
            return;

        for (int i = 0; i < finalParts.Length; i++)
        {
            if (finalParts[i] != null)
                finalParts[i].enabled = visible;
        }
    }

    public void ShowCollections()
    {
    }

    public static void SetMoodBarVisible(bool visible)
    {
        if (Instance == null)
            return;

        Instance.moodBarSuppressed = !visible;
        if (!visible)
        {
            if (Instance.moodWidget != null)
                Instance.moodWidget.gameObject.SetActive(false);
        }
        else
        {
            Instance.PlaceMoodWidget();
        }
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
        SpriteRenderer body = VisibleBody();
        if (body == null || !body.enabled)
            return false;

        Camera cam = Camera.main;
        if (cam == null)
            return false;

        Bounds bounds = body.bounds;
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
        RevealBody();

        Animator animator = PetAnimator();
        if (animator != null)
        {
            animator.Play(PetDialogue.BodyPose("Idle"), 0, 0f);
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
            SetShown(false);
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
        string name = PetStory.SpokenName("there");
        PetPhone.Instance.AddMessage("Hi, " + name + ". If I run into anything interesting while I'm out, I'll send it to you through this. Remember to check.");
    }

    IEnumerator ReturnHome()
    {
        BeginRest();
        arrivalLineQueued = true;
        PetCampaign.Ensure().FlushTripLines();

        if (pet != null)
            SetShown(true);

        try
        {
            if (PetAnimator() != null)
                yield return PlayState("JourneyEnd");
            else
                yield return ScalePet(Vector3.zero, HomeScale());
        }
        finally
        {
            RevealBody();
            returning = false;
            arrivalLineQueued = false;
            if (PetPhone.Instance != null)
                PetPhone.Instance.SetAway(false);
            SetJourneyButtonVisible(true);
        }
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

        // 缩放为 0 时默认剔除会让 JourneyEnd 的缩放曲线根本不采样。
        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        animator.updateMode = AnimatorUpdateMode.UnscaledTime;

        float length = Mathf.Max(ClipLength(animator, stateName), 0.05f);
        float elapsed = 0f;
        animator.Play(stateName, 0, 0f);
        animator.Update(0f);

        while (elapsed < length)
        {
            float normalized = Mathf.Clamp01(elapsed / length);
            AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
            if (!info.IsName(stateName))
                animator.Play(stateName, 0, normalized);

            yield return null;
            elapsed += Time.unscaledDeltaTime;
        }

        animator.Play(stateName, 0, 1f);
        animator.Update(0f);

        // 回来要停在 JourneyEnd 的最后一帧。Idle 没有缩放曲线，切过去就看不到回来。
        if (stateName == "JourneyEnd")
            yield break;

        animator.Play(PetDialogue.BodyPose("Idle"), 0, 0f);
        animator.Update(0f);
    }

    Vector3 HomeScale()
    {
        if (homeScale.sqrMagnitude < 0.0001f)
            return new Vector3(0.5f, 0.5f, 0.5f);
        return homeScale;
    }

    void ApplyHomeScale()
    {
        if (pet == null)
            return;

        pet.transform.localScale = HomeScale();
        SetShown(true);
    }

    void RevealBody()
    {
        bodyExpected = true;
        if (pet == null)
            return;

        SetShown(true);
        if (!finalForm && pet.transform.localScale.sqrMagnitude < 0.0004f)
            ApplyHomeScale();
    }

    void LateUpdate()
    {
        if (finalForm && pet != null && pet.enabled)
            pet.enabled = false;

        TrackRedEye();

        if (!bodyExpected || away || departing || returning || pet == null)
            return;

        if (finalForm)
        {
            if (finalBodyShown && finalBody != null && !finalBody.enabled)
                SetFinalParts(true);
            return;
        }

        if (!pet.enabled || pet.transform.localScale.sqrMagnitude < 0.0004f)
            ApplyHomeScale();
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

        if (tripCount == 5 && !PetCampaign.Ensure().PlaysSecondScript(tripCount))
        {
            yield return SendFifthJourney(duration);
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
        string name = PetStory.SpokenName("there");
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
                        XPostUrl());
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

    void SendTestPictures()
    {
        EnsureMemeSprites();
        var pool = new List<Sprite>();
        AppendUniqueSprites(pool, LoadPostCards());
        AppendUniqueSprites(pool, memeSprites);
        if (pool.Count == 0)
        {
            DebugConsole.WriteLine("No postcards or memes available.");
            return;
        }

        PetPhone.Ensure();
        if (PetPhone.Instance == null)
        {
            DebugConsole.WriteLine("Phone is not available.");
            return;
        }

        Shuffle(pool);
        int index = 0;
        for (int i = 0; i < 10; i++)
        {
            if (index >= pool.Count)
            {
                Shuffle(pool);
                index = 0;
            }

            Sprite sprite = pool[index++];
            PetPhone.Instance.AddPostcard(sprite, "Test picture " + (i + 1));
        }

        PetPhone.Instance.SetOpen(true);
        DebugConsole.WriteLine("Sent 10 random test pictures to the phone.");
        DebugConsole.CloseConsole();
    }

    IEnumerator SendThirdJourney(float duration)
    {
        BeginThirdPlaceLookup();
        LoadMorseGif();
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
        const int count = 5;
        for (int i = 0; i < count; i++)
        {
            float sendAt = duration * (i + 1f) / (count + 1f);
            while (away && Time.unscaledTime - started < sendAt)
                yield return null;
            if (!away)
                yield break;

            if (i == 2)
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
                    SendMorseGif();
                    break;
                case 2:
                    PetPhone.Instance.AddMessage("Are there any interesting sights in " + thirdPlace + "? I'll take a photo for you.");
                    break;
                case 3:
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

    void SendMorseGif()
    {
        SendAnimatedGif(LoadMorseGif(), "edocesrom", "__gif_morse__");
    }

    void SendAnimatedGif(GifClip clip, string caption, string collectionKey = null)
    {
        if (PetPhone.Instance == null || string.IsNullOrEmpty(caption))
            return;

        if (clip == null)
        {
            PetPhone.Instance.AddMessage(caption);
            return;
        }

        RememberMeme(clip.First, collectionKey, false);
        SaveFoundBytes(clip.SourceName, clip.SourceBytes);
        PetPhone.Instance.AddPostcard(clip.First, caption, clip);
    }

    GifClip LoadMorseGif()
    {
        return LoadJourneyGif(ref morseGif, "yellatacat", "yellatacat.gif");
    }

    GifClip LoadPuzzleGif()
    {
        return LoadJourneyGif(ref puzzleGif, "brain", "brain.gif");
    }

    public GifClip PlaceholderGif()
    {
        return LoadPuzzleGif();
    }

    static GifClip LoadJourneyGif(ref GifClip cache, string resourceName, string fileName)
    {
        if (cache != null)
            return cache;

        byte[] bytes = null;
        TextAsset asset = Resources.Load<TextAsset>(resourceName);
        if (asset != null)
            bytes = asset.bytes;
        if (bytes == null || bytes.Length == 0)
        {
            string path = System.IO.Path.Combine(Application.dataPath, "ArtResources", "Gif", "gif", fileName);
            if (System.IO.File.Exists(path))
                bytes = System.IO.File.ReadAllBytes(path);
        }

        if (bytes == null || bytes.Length == 0)
            return null;

        cache = GifClip.Decode(bytes);
        if (cache != null)
        {
            cache.SourceName = fileName;
            cache.SourceBytes = bytes;
        }

        return cache;
    }

    IEnumerator SendFifthJourney(float duration)
    {
        bool mic = TryStartFifthRecording();
        float started = Time.unscaledTime;
        yield return null;
        while (away && departing)
            yield return null;
        if (!away)
            yield break;

        RevealPhoneIfClosed();
        string[] lines;
        int noteAt;
        int audioAt;
        if (mic)
        {
            lines = new[]
            {
                "Maybe you could try to flesh out what I can do a little more.",
                "Maybe there are some other friends in my forum?",
                "My phone's network is acting up. I left you a message on the desktop.",
                "Maybe you'd be interested in this conversation."
            };
            noteAt = 2;
            audioAt = 3;
        }
        else
        {
            lines = new[]
            {
                "Oh, you don't even have a microphone. I wanted to chat with you.",
                "Maybe you could try to flesh out what I can do a little more.",
                "Maybe there are some other friends in my forum?",
                "My phone's network is acting up. I left you a message on the desktop."
            };
            noteAt = 3;
            audioAt = -1;
        }

        int count = lines.Length;
        for (int i = 0; i < count; i++)
        {
            float sendAt = duration * (i + 1f) / (count + 1f);
            while (away && Time.unscaledTime - started < sendAt)
                yield return null;
            if (!away)
                yield break;

            RevealPhoneIfClosed();
            if (PetPhone.Instance == null)
                continue;

            if (i == noteAt)
                WriteFifthNote();

            if (i == audioAt)
                PetPhone.Instance.AddAudioMessage(lines[i], "Play", PlayFifthRecording);
            else
                PetPhone.Instance.AddMessage(lines[i]);
        }
    }

    static bool TryStartFifthRecording()
    {
        string[] devices;
        try
        {
            devices = Microphone.devices;
        }
        catch (Exception)
        {
            return false;
        }

        if (devices == null || devices.Length == 0)
            return false;

        MicrophoneLevelService service = MicrophoneLevelService.Instance;
        if (service != null && service.IsRunning && !service.IsRecording)
            service.StartRecording(10f);
        return true;
    }

    static void PlayFifthRecording()
    {
        MicrophoneLevelService service = MicrophoneLevelService.Instance;
        if (service == null)
            return;

        string error;
        service.TryPlayLastRecording(out error);
    }

    static void WriteFifthNote()
    {
        try
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(dir))
                return;

            System.IO.File.WriteAllText(
                System.IO.Path.Combine(dir, "blinky-note.txt"),
                "I want to see OMNIS place in the world. Maybe my developer has some thing good for me. https://zerzhou.itch.io/blinky\r\n");
            DebugConsole.WriteLine("Left blinky-note.txt on the desktop.");
        }
        catch (Exception ex)
        {
            DebugConsole.WriteLine("Could not write blinky-note.txt: " + ex.Message);
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

        LoadPuzzleGif();
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
                    SendAnimatedGif(LoadPuzzleGif(), "mljvdzsxccoh", "__gif_puzzle__");
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
                SavePlayerPhoto(fourthCameraFrame);
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
            RememberMeme(fourthCameraFrame, "__camera__");
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

    public static string PlayerPhotoFile =>
        System.IO.Path.Combine(Application.persistentDataPath, "BlinkyPlayerPhoto.png");

    public static void SavePlayerPhoto(Sprite sprite)
    {
        if (sprite == null || sprite.texture == null)
            return;

        try
        {
            byte[] png = sprite.texture.EncodeToPNG();
            if (png == null || png.Length == 0)
                return;
            System.IO.File.WriteAllBytes(PlayerPhotoFile, png);
        }
        catch (Exception)
        {
        }
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

    void RememberMeme(Sprite sprite, string forcedKey = null, bool saveFile = true)
    {
        if (sprite == null)
            return;

        bool added = !collectedMemes.Contains(sprite);
        if (added)
            collectedMemes.Add(sprite);

        string key = string.IsNullOrEmpty(forcedKey) ? sprite.name : forcedKey;
        if (!string.IsNullOrEmpty(key) && !collectedMemeKeys.Contains(key))
        {
            collectedMemeKeys.Add(key);
            SaveCollectedMemes();
            added = true;
        }

        if (saveFile && added)
            SaveFoundPicture(sprite);
    }

    void SaveCollectedMemes()
    {
        if (!keepProgressBetweenLaunches)
            return;

        PlayerPrefs.SetString(CollectedMemesKey, string.Join("\n", collectedMemeKeys));
        PlayerPrefs.Save();
    }

    void LoadCollectedMemes()
    {
        if (!keepProgressBetweenLaunches)
            return;

        collectedMemes.Clear();
        collectedMemeKeys.Clear();
        string saved = PlayerPrefs.GetString(CollectedMemesKey, string.Empty);
        if (string.IsNullOrEmpty(saved))
            return;

        Dictionary<string, Sprite> lookup = BuildSavedSpriteLookup();
        string[] keys = saved.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i].Trim();
            if (string.IsNullOrEmpty(key))
                continue;

            if (!collectedMemeKeys.Contains(key))
                collectedMemeKeys.Add(key);

            if (lookup.TryGetValue(key, out Sprite sprite)
                && sprite != null
                && !collectedMemes.Contains(sprite))
            {
                collectedMemes.Add(sprite);
            }
        }
    }

    Dictionary<string, Sprite> BuildSavedSpriteLookup()
    {
        var lookup = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        RegisterCollectionSprites(lookup, LoadPostCards());
        EnsureMemeSprites();
        RegisterCollectionSprites(lookup, memeSprites);
        RegisterCollectionSprites(lookup, LoadWeirdMemes());

        Sprite sleeping = LoadFriendSleeping();
        if (sleeping != null)
            RegisterCollectionSprite(lookup, sleeping.name, sleeping);
        Sprite bed = LoadFourthBed();
        if (bed != null)
            RegisterCollectionSprite(lookup, bed.name, bed);
        Sprite meeting = LoadFourthMeeting();
        if (meeting != null)
            RegisterCollectionSprite(lookup, meeting.name, meeting);

        GifClip morse = LoadMorseGif();
        if (morse != null && morse.First != null)
            RegisterCollectionSprite(lookup, "__gif_morse__", morse.First);
        GifClip puzzle = LoadPuzzleGif();
        if (puzzle != null && puzzle.First != null)
            RegisterCollectionSprite(lookup, "__gif_puzzle__", puzzle.First);

        Sprite cameraPhoto = LoadSavedPlayerPhotoSprite();
        if (cameraPhoto != null)
            RegisterCollectionSprite(lookup, "__camera__", cameraPhoto);

        return lookup;
    }

    static void RegisterCollectionSprites(Dictionary<string, Sprite> lookup, Sprite[] sprites)
    {
        if (sprites == null)
            return;
        for (int i = 0; i < sprites.Length; i++)
        {
            if (sprites[i] != null)
                RegisterCollectionSprite(lookup, sprites[i].name, sprites[i]);
        }
    }

    static void RegisterCollectionSprite(Dictionary<string, Sprite> lookup, string key, Sprite sprite)
    {
        if (!string.IsNullOrEmpty(key) && !lookup.ContainsKey(key))
            lookup[key] = sprite;
    }

    public static string SavedSpriteKey(Sprite sprite)
    {
        if (sprite == null)
            return string.Empty;

        if (Instance != null)
        {
            if (sprite == Instance.fourthCameraFrame)
                return "__camera__";

            GifClip morse = Instance.LoadMorseGif();
            if (morse != null && morse.First == sprite)
                return "__gif_morse__";

            GifClip puzzle = Instance.LoadPuzzleGif();
            if (puzzle != null && puzzle.First == sprite)
                return "__gif_puzzle__";
        }

        return string.IsNullOrEmpty(sprite.name) ? string.Empty : sprite.name;
    }

    public static Sprite ResolveSavedSprite(string key)
    {
        if (Instance == null || string.IsNullOrEmpty(key))
            return null;

        Dictionary<string, Sprite> lookup = Instance.BuildSavedSpriteLookup();
        return lookup.TryGetValue(key, out Sprite sprite) ? sprite : null;
    }

    static Sprite LoadSavedPlayerPhotoSprite()
    {
        if (!System.IO.File.Exists(PlayerPhotoFile))
            return null;

        try
        {
            byte[] bytes = System.IO.File.ReadAllBytes(PlayerPhotoFile);
            if (bytes == null || bytes.Length == 0)
                return null;

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.filterMode = FilterMode.Bilinear;
            var sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                100f);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static void SaveFoundPicture(Sprite sprite)
    {
        if (sprite == null || sprite.name == "PlaceholderMeme")
            return;

        try
        {
            string folder = EnsureCollectionsFolder();
            if (string.IsNullOrEmpty(folder))
                return;

            string source = SourceFile(sprite);
            if (!string.IsNullOrEmpty(source))
            {
                string dest = Path.Combine(folder, Path.GetFileName(source));
                if (!File.Exists(dest))
                    File.Copy(source, dest, false);
                return;
            }

            if (sprite.texture == null || !sprite.texture.isReadable)
                return;

            byte[] png = sprite.texture.EncodeToPNG();
            if (png == null || png.Length == 0)
                return;

            string name = string.IsNullOrEmpty(sprite.name) ? "picture.png" : SanitizeFileName(sprite.name) + ".png";
            File.WriteAllBytes(UniqueFile(folder, name), png);
        }
        catch (Exception ex)
        {
            DebugConsole.WriteLine("Could not save a collection picture: " + ex.Message);
        }
    }

    static void SaveFoundBytes(string fileName, byte[] bytes)
    {
        if (string.IsNullOrEmpty(fileName) || bytes == null || bytes.Length == 0)
            return;

        try
        {
            string folder = EnsureCollectionsFolder();
            if (string.IsNullOrEmpty(folder))
                return;

            string dest = Path.Combine(folder, SanitizeFileName(fileName));
            if (!File.Exists(dest))
                File.WriteAllBytes(dest, bytes);
        }
        catch (Exception ex)
        {
            DebugConsole.WriteLine("Could not save a collection picture: " + ex.Message);
        }
    }

    static string EnsureCollectionsFolder()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop))
            return null;

        string folder = Path.Combine(desktop, "Collections");
        Directory.CreateDirectory(folder);
        return folder;
    }

    static string SourceFile(Sprite sprite)
    {
#if UNITY_EDITOR
        string assetPath = UnityEditor.AssetDatabase.GetAssetPath(sprite);
        if (string.IsNullOrEmpty(assetPath) && sprite.texture != null)
            assetPath = UnityEditor.AssetDatabase.GetAssetPath(sprite.texture);
        if (!string.IsNullOrEmpty(assetPath) && assetPath.StartsWith("Assets/"))
        {
            string full = Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));
            if (File.Exists(full))
                return full;
        }
#endif
        return null;
    }

    static string UniqueFile(string folder, string fileName)
    {
        string dest = Path.Combine(folder, fileName);
        if (!File.Exists(dest))
            return dest;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            dest = Path.Combine(folder, stem + " (" + i + ")" + ext);
            if (!File.Exists(dest))
                return dest;
        }

        return Path.Combine(folder, stem + "-" + DateTime.UtcNow.Ticks + ext);
    }

    static string SanitizeFileName(string name)
    {
        char[] bad = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(bad, chars[i]) >= 0)
                chars[i] = '_';
        }

        string clean = new string(chars).Trim();
        return string.IsNullOrEmpty(clean) ? "picture" : clean;
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
        AddMoodBarBorder();
        moodWidget.gameObject.SetActive(false);
        RefreshMoodBar();
        PlaceMoodWidget();
    }

    void AddMoodBarBorder()
    {
        if (moodWidget == null)
            return;

        Transform track = moodWidget.Find("MoodTrack");
        if (track == null || track.GetComponent<Image>() == null)
            return;

        var outline = track.GetComponent<Outline>();
        if (outline == null)
            outline = track.gameObject.AddComponent<Outline>();
        outline.effectColor = new Color(0.07f, 0.07f, 0.08f, 1f);
        outline.effectDistance = new Vector2(1.5f, -1.5f);
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

        bool openingReady = PetDialogue.Instance != null && PetDialogue.OpeningComplete;
        SpriteRenderer body = VisibleBody();
        bool visible = openingReady
            && !moodBarSuppressed
            && body != null
            && body.enabled
            && body.gameObject.activeInHierarchy;
        if (!visible)
        {
            if (moodWidget.gameObject.activeSelf)
                moodWidget.gameObject.SetActive(false);
            return;
        }

        if (!moodWidget.gameObject.activeSelf)
            moodWidget.gameObject.SetActive(true);

        float homeScaleX = homeScale.x > 0.0001f ? homeScale.x : 0.5f;
        float scale = finalForm ? 1f : Mathf.Clamp01(pet.transform.localScale.x / homeScaleX);
        moodWidget.localScale = Vector3.one * scale;

        Camera cam = Camera.main;
        Canvas canvas = moodWidget.GetComponentInParent<Canvas>();
        RectTransform canvasRect = canvas != null ? canvas.transform as RectTransform : null;
        if (cam == null || canvasRect == null)
            return;

        Bounds bounds = body.bounds;
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

        var loaded = new List<Sprite>();
        OrdinaryMemeSet set = Resources.Load<OrdinaryMemeSet>("OrdinaryMemeSet");
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
            AppendOrdinaryMemeFolder(loaded);
#endif

        if (loaded.Count > 0)
        {
            memeSprites = loaded.ToArray();
            return;
        }

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

#if UNITY_EDITOR
    static void AppendOrdinaryMemeFolder(List<Sprite> loaded)
    {
        const string folder = "Assets/memes/Nomral-1";
        if (!UnityEditor.AssetDatabase.IsValidFolder(folder))
            return;

        string[] guids = UnityEditor.AssetDatabase.FindAssets("t:Sprite", new[] { folder });
        for (int i = 0; i < guids.Length; i++)
        {
            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[i]);
            Sprite sprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite != null)
                loaded.Add(sprite);
        }
    }
#endif

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
        Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
        sprite.name = "PlaceholderMeme";
        return sprite;
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

        galleryPanel = new GameObject("Gallery", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
        galleryPanel.transform.SetParent(parent, false);
        var rect = galleryPanel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(248f, 108f);
        rect.localScale = Vector3.one * GalleryPageScale;
        galleryGroup = galleryPanel.GetComponent<CanvasGroup>();
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
        if (galleryPanel == null || !galleryPanel.activeSelf || galleryClosing)
            return;

        browsingGallery = false;
        if (memePopup != null)
            memePopup.SetActive(false);

        if (galleryAnim != null)
        {
            StopCoroutine(galleryAnim);
            galleryAnim = null;
        }

        galleryClosing = true;
        if (galleryGroup != null)
        {
            galleryGroup.interactable = false;
            galleryGroup.blocksRaycasts = false;
        }
        galleryAnim = StartCoroutine(AnimateGalleryClose());
    }

    public void OpenGallery()
    {
        if (galleryPanel == null)
            CreateGallery();
        if (galleryPanel == null)
            return;

        bool wasActive = galleryPanel.activeSelf;
        if (!wasActive)
        {
            var rect = galleryPanel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0.5f);
            rect.anchorMax = new Vector2(0f, 0.5f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.anchoredPosition = new Vector2(424f, 0f);
            rect.localScale = Vector3.one * (GalleryPageScale * 0.86f);
            galleryPanel.SetActive(true);
            galleryPanel.transform.SetAsLastSibling();
            RebuildGallery();
            if (galleryGroup != null)
                galleryGroup.alpha = 0f;
        }
        else if (!galleryClosing)
        {
            CloseGallery();
            return;
        }

        if (galleryAnim != null)
        {
            StopCoroutine(galleryAnim);
            galleryAnim = null;
        }

        galleryClosing = false;
        galleryPanel.transform.SetAsLastSibling();
        if (galleryGroup != null)
        {
            galleryGroup.interactable = false;
            galleryGroup.blocksRaycasts = false;
        }
        galleryAnim = StartCoroutine(AnimateGalleryOpen());
    }

    IEnumerator AnimateGalleryOpen()
    {
        const float duration = 0.24f;
        float elapsed = 0f;
        var rect = galleryPanel.GetComponent<RectTransform>();
        Vector3 fromScale = rect.localScale;
        float fromAlpha = galleryGroup != null ? galleryGroup.alpha : 1f;
        Vector3 targetScale = Vector3.one * GalleryPageScale;

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float eased = 1f - Mathf.Pow(1f - t, 3f);
            rect.localScale = Vector3.LerpUnclamped(fromScale, targetScale, eased);
            if (galleryGroup != null)
                galleryGroup.alpha = Mathf.Lerp(fromAlpha, 1f, eased);
            yield return null;
        }

        rect.localScale = targetScale;
        if (galleryGroup != null)
        {
            galleryGroup.alpha = 1f;
            galleryGroup.interactable = true;
            galleryGroup.blocksRaycasts = true;
        }
        galleryAnim = null;
    }

    IEnumerator AnimateGalleryClose()
    {
        const float duration = 0.18f;
        float elapsed = 0f;
        var rect = galleryPanel.GetComponent<RectTransform>();
        Vector3 fromScale = rect.localScale;
        float fromAlpha = galleryGroup != null ? galleryGroup.alpha : 1f;
        Vector3 targetScale = Vector3.one * (GalleryPageScale * 0.9f);

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float eased = Mathf.SmoothStep(0f, 1f, t);
            rect.localScale = Vector3.LerpUnclamped(fromScale, targetScale, eased);
            if (galleryGroup != null)
                galleryGroup.alpha = Mathf.LerpUnclamped(fromAlpha, 0f, eased);
            yield return null;
        }

        galleryPanel.SetActive(false);
        rect.localScale = Vector3.one * GalleryPageScale;
        if (galleryGroup != null)
        {
            galleryGroup.alpha = 1f;
            galleryGroup.interactable = true;
            galleryGroup.blocksRaycasts = true;
        }
        galleryClosing = false;
        galleryAnim = null;
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

        if (galleryPanel.activeSelf && !galleryClosing)
            CloseGallery();
        else
            OpenGallery();
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
