using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using ScreamJam.Debugging;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 第一次远行回来之后的流程：介绍、每次远行、死亡复活、饥饿、结局。
/// </summary>
public class PetCampaign : MonoBehaviour
{
    enum Kind
    {
        Lines,
        Emphasis,
        Flash,
        Death,
        Ritual,
        Contract,
        Hunger,
        Link,
        Phase,
        OfferFlag,
        Choice,
        Membership,
        Desk
    }

    struct Step
    {
        public Kind kind;
        public string[] lines;
        public PetStory.Phase phase;

        public static Step Say(params string[] lines)
        {
            return new Step { kind = Kind.Lines, lines = lines };
        }

        public int choice;

        public static Step Choose()
        {
            return new Step { kind = Kind.Choice };
        }

        public static Step ChooseSecond()
        {
            return new Step { kind = Kind.Choice, choice = 1 };
        }

        public static Step Big(string line)
        {
            return new Step { kind = Kind.Emphasis, lines = new[] { line } };
        }

        public static Step Scare(string line)
        {
            return new Step { kind = Kind.Flash, lines = new[] { line } };
        }

        public static Step To(PetStory.Phase phase)
        {
            return new Step { kind = Kind.Phase, phase = phase };
        }
    }

    public static PetCampaign Instance { get; private set; }
    public static bool BlocksJourney => Instance != null && (Instance.busy || Instance.dead || Instance.hungerLooping || Instance.choiceOpen || Instance.awaitingAgree);
    public bool HasIntroduced => introduced;
    public bool HoldsSecondJourney
    {
        get
        {
            if (featuresUnlocked || awaitingAgree)
                return false;
            PetStory.Phase phase = PetStory.Current;
            return phase != PetStory.Phase.Refused && phase != PetStory.Phase.Final;
        }
    }
    public bool HasJoinedCult => cultJoined;
    public bool HoldsFifthJourney
    {
        get
        {
            if (cultJoined)
                return false;
            PetStory.Phase phase = PetStory.Current;
            return phase != PetStory.Phase.Refused && phase != PetStory.Phase.Final;
        }
    }
    public bool PlaysSecondScript(int trip) => trip >= 2 && HoldsSecondJourney;
    public bool SecondJourneyRepeats => secondAsks > 0;

    bool busy;
    bool dead;
    bool hungerLooping;
    bool choiceOpen;
    bool firstReturnScript;
    bool listening;
    bool pending;
    bool introduced;
    bool offerUnlocked;
    bool featuresUnlocked;
    bool awaitingAgree;
    bool armAgree;
    bool cultJoined;
    bool cultJoining;
    int secondAsks;
    bool retract;
    int feeds;
    int sent;
    int index;
    Step[] homeSteps;
    string[] tripLines;
    Coroutine tripRoutine;
    GameObject veil;
    Text veilText;
    Button veilButton;
    Text veilButtonLabel;
    GameObject feedRoot;
    Text feedLabel;
    GameObject choiceRoot;
    Image flash;
    AudioSource beep;
    AudioClip beepClip;
    Sprite skin;
    readonly List<string> journeyBag = new List<string>();
    readonly List<string> homeBag = new List<string>();

    static readonly string[] JoinJourneys =
    {
        "{name}, I went out. The eye went with me.",
        "Another window. I only read what was already open.",
        "The memes are a reason to stay gone a little longer.",
        "{name}, someone typed your name. I did not answer for you.",
        "I brought pictures. Some of them blink.",
        "The page is still there when I get back.",
        "I stood where the camera could see me, then I didn't.",
        "Home soon. Keep the bar from emptying."
    };

    static readonly string[] JoinHome =
    {
        "{name}, I'm home. Send me again when you want to be seen.",
        "The offering is the trip. You already know that.",
        "I can say your name the whole way now.",
        "Check the phone. Then check me."
    };

    static readonly string[] HungerLines =
    {
        "Still hungry.",
        "The bar is not food.",
        "Two bites. Then I'll talk."
    };

    public static PetCampaign Ensure()
    {
        if (Instance != null)
            return Instance;

        var host = new GameObject("PetCampaign");
        PetCampaign created = host.AddComponent<PetCampaign>();
        created.LoadProgress();
        return created;
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        LoadProgress();
        DebugConsole.RegisterCommand("offer", "/offer", "After the tenth return, choose to join or refuse.", _ => Offer());
        DebugConsole.RegisterCommand("agree", "\\agree", "After agreeing on the second return, unlock the next state.", AgreeCommand);
    }

    bool progressLoaded;

    void LoadProgress()
    {
        if (progressLoaded || !PetJourney.SavesProgress)
            return;

        progressLoaded = true;
        introduced = PlayerPrefs.GetInt("PetCampaign.Introduced", 0) != 0;
        offerUnlocked = PlayerPrefs.GetInt("PetCampaign.OfferUnlocked", 0) != 0;
        featuresUnlocked = PlayerPrefs.GetInt("PetCampaign.FeaturesUnlocked", 0) != 0;
        awaitingAgree = PlayerPrefs.GetInt("PetCampaign.AwaitingAgree", 0) != 0;
        secondAsks = PlayerPrefs.GetInt("PetCampaign.SecondAsks", 0);
        cultJoined = PlayerPrefs.GetInt("PetCampaign.CultJoined", 0) != 0;
        int trips = PetJourney.Instance != null ? PetJourney.Instance.CompletedTrips : 0;
        if (trips >= 2)
            introduced = true;
        if (!offerUnlocked && trips >= 10 && PetJourney.Instance != null && !PetJourney.PetIsAway)
            offerUnlocked = true;
        if (!PlayerPrefs.HasKey("PetStory.Phase") && trips > 0)
        {
            PetStory.Phase phase = PetStory.Phase.Normal;
            if (trips >= 8)
                phase = PetStory.Phase.StateIII;
            else if (trips >= 6)
                phase = PetStory.Phase.StateII;
            else if (trips >= 4)
                phase = PetStory.Phase.StateI;
            PetStory.SetPhase(phase);
        }

        SaveFlags();
    }

    void SaveFlags()
    {
        if (!PetJourney.SavesProgress)
            return;

        PlayerPrefs.SetInt("PetCampaign.Introduced", introduced ? 1 : 0);
        PlayerPrefs.SetInt("PetCampaign.OfferUnlocked", offerUnlocked ? 1 : 0);
        PlayerPrefs.SetInt("PetCampaign.FeaturesUnlocked", featuresUnlocked ? 1 : 0);
        PlayerPrefs.SetInt("PetCampaign.AwaitingAgree", awaitingAgree ? 1 : 0);
        PlayerPrefs.SetInt("PetCampaign.SecondAsks", secondAsks);
        PlayerPrefs.SetInt("PetCampaign.CultJoined", cultJoined ? 1 : 0);
        PlayerPrefs.Save();
    }

    void OnEnable()
    {
        PetDialogue.Closed += OnDialogueClosed;
    }

    void OnDisable()
    {
        PetDialogue.Closed -= OnDialogueClosed;
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void Update()
    {
        if (!pending)
            return;

        pending = false;
        Continue();
    }

    void OnDialogueClosed()
    {
        if (!listening)
            return;

        listening = false;
        pending = true;
    }

    public void SnapToCompleted(int completed)
    {
        if (tripRoutine != null)
            StopCoroutine(tripRoutine);
        StopAllCoroutines();
        tripRoutine = null;
        tripLines = null;
        homeSteps = null;
        sent = 0;
        retract = false;
        listening = false;
        pending = false;
        busy = false;
        dead = false;
        hungerLooping = false;
        choiceOpen = false;
        feeds = 0;
        introduced = completed >= 1;
        offerUnlocked = completed >= 10;
        HideVeil();
        HideFeed();
        if (choiceRoot != null)
            choiceRoot.SetActive(false);
        if (flash != null)
            flash.color = new Color(0.85f, 0.12f, 0.18f, 0f);

        PetStory.Phase phase = PetStory.Phase.Normal;
        if (completed >= 8)
            phase = PetStory.Phase.StateIII;
        else if (completed >= 6)
            phase = PetStory.Phase.StateII;
        else if (completed >= 4)
            phase = PetStory.Phase.StateI;
        PetStory.SetPhase(phase);
        SaveFlags();
    }

    public void BeginAfterFirstReturn()
    {
        if (introduced)
            return;

        firstReturnScript = true;
        homeSteps = new[]
        {
            Step.Choose(),
            Step.Say("That trip wore me out. Let me rest before I can head out again."),
            Step.Say("See the picture button on the left of your phone? Open it, and you can see every meme I found. If this rest feels too slow, the bar beside me has to fill before I can leave again.")
        };
        index = 0;
        Play();
    }

    void AskFirstReturn()
    {
        string name = PetStory.SpokenName("there");
        PetDialogue.Ask(
            name + ", I'm back! Do you still like the memes I found for you?",
            () => AnswerFirstReturn("Heh, good."),
            () => AnswerFirstReturn("Don't delete me yet! Next time I'll find memes you actually like!"));
    }

    void AnswerFirstReturn(string line)
    {
        listening = true;
        PetDialogue.SayLines(line);
    }

    void AskSecondReturn()
    {
        string name = PetStory.SpokenName("there");
        string question = secondAsks > 0
            ? name + ", I'm back! That paper was pretty good. Do you want to understand your friends more deeply?"
            : name + ", I'm back! About that post, do you want to understand your friends that well, too?";
        PetDialogue.Ask(question, () => AnswerSecond(true), () => AnswerSecond(false));
    }

    void AnswerSecond(bool yes)
    {
        secondAsks++;
        SaveFlags();
        if (!yes)
        {
            PetDialogue.Say("Alright. Maybe you'll change your mind later.");
            FinishStep();
            return;
        }

        listening = true;
        armAgree = true;
        awaitingAgree = true;
        SaveFlags();
        PetDialogue.SayLines(
            "Oh, I see. I actually have that ability. Let me teach you how to unlock some of what I can do.",
            "Type /adminpanel start on the phone panel to open the console, then enter the password to unlock.");
    }

    public static void TryAgree()
    {
        Ensure();
        if (Instance == null)
            return;

        PetStory.Phase phase = PetStory.Current;
        if (phase == PetStory.Phase.StateII
            || phase == PetStory.Phase.StateIII
            || phase == PetStory.Phase.Final
            || phase == PetStory.Phase.Refused)
            return;

        Instance.awaitingAgree = true;
        Instance.featuresUnlocked = false;
        Instance.AgreeCommand(null);
    }

    void PromptAdminOnPhone()
    {
        PetPhone.ShowButton();
        if (PetPhone.Instance == null)
            return;

        if (!PetPhone.Instance.IsOpen)
            PetPhone.Instance.SetOpen(true);
        PetPhone.Instance.AddMessage("You can type /adminpanel start to open the control panel.");
    }

    void AgreeCommand(string[] _)
    {
        if (!awaitingAgree || featuresUnlocked)
        {
            DebugConsole.WriteLine("Nothing answers.");
            return;
        }

        awaitingAgree = false;
        featuresUnlocked = true;
        SaveFlags();
        PetStory.SetUseComputerName(true);
        PetStory.SetPhase(PetStory.Phase.StateII);
        PetDialogue.Say("Hello, {name}. From now on, when I'm out, I'll bring you some <color=#c41616>especially interesting</color> things. Send me out right away.");
        if (PetJourney.Instance != null)
            PetJourney.Instance.RecoverMood();
        DebugConsole.WriteLine("Agreed.");
    }

    public void OnTripStarted(int trip, float duration)
    {
        if (tripRoutine != null)
            StopCoroutine(tripRoutine);
        tripRoutine = null;
        listening = false;
        pending = false;
        busy = false;
        homeSteps = null;
        tripLines = null;
        sent = 0;
        retract = false;
        firstReturnScript = false;
        if (trip <= 1 || PetStory.Current == PetStory.Phase.Refused || PlaysSecondScript(trip))
            return;
        if (trip == 3 && PetStory.Current != PetStory.Phase.Final)
            return;
        if (trip == 4 && PetStory.Current != PetStory.Phase.Final)
            return;
        if (trip == 5 && PetStory.Current != PetStory.Phase.Final)
            return;

        tripLines = LinesForTrip(trip);
        retract = trip == 5;
        tripRoutine = StartCoroutine(SendLines(Mathf.Max(1f, duration)));
    }

    public void FlushTripLines()
    {
        if (tripRoutine != null)
            StopCoroutine(tripRoutine);
        tripRoutine = null;
        if (tripLines != null)
        {
            for (int i = sent; i < tripLines.Length; i++)
                Deliver(i);
            tripLines = null;
        }

        if (!retract)
            return;

        retract = false;
        if (PetPhone.Instance != null)
            PetPhone.Instance.RemoveLastPostcard();
    }

    public void OnReturned(int trip)
    {
        if (PetStory.Current == PetStory.Phase.Refused)
        {
            listening = true;
            PetDialogue.Say(PetStory.RefuseSpeech);
            return;
        }

        if (PetStory.Current == PetStory.Phase.Final)
        {
            homeSteps = new[] { Step.Say(Next(homeBag, JoinHome)) };
            index = 0;
            Play();
            return;
        }

        if (HasJoinedCult || cultJoining)
            return;

        if (PlaysSecondScript(trip))
        {
            homeSteps = new[] { Step.ChooseSecond() };
            index = 0;
            Play();
            return;
        }

        homeSteps = HomeFor(trip);
        index = 0;
        Play();
    }

    void Offer()
    {
        if (!offerUnlocked)
        {
            DebugConsole.WriteLine("Nothing answers.");
            return;
        }

        if (choiceOpen || dead)
        {
            DebugConsole.WriteLine("Not now.");
            return;
        }

        ShowChoice();
    }

    IEnumerator SendLines(float duration)
    {
        float start = Time.unscaledTime;
        int count = tripLines == null ? 0 : tripLines.Length;
        for (int i = 0; i < count; i++)
        {
            float at = duration * (i + 1f) / (count + 1f);
            while (Time.unscaledTime - start < at)
            {
                if (!PetJourney.PetIsAway)
                    yield break;
                yield return null;
            }

            Deliver(i);
        }

        tripRoutine = null;
    }

    void Deliver(int i)
    {
        if (tripLines == null || i < sent || i >= tripLines.Length)
            return;

        sent = i + 1;
        if (PetPhone.Instance != null)
            PetPhone.Instance.AddMessage(PetStory.ApplyName(tripLines[i]));
    }

    void Play()
    {
        pending = false;
        listening = false;
        if (homeSteps == null || index >= homeSteps.Length)
        {
            bool nag = firstReturnScript;
            bool unlock = armAgree;
            firstReturnScript = false;
            armAgree = false;
            busy = false;
            homeSteps = null;
            if (unlock)
            {
                awaitingAgree = true;
                SaveFlags();
                PromptAdminOnPhone();
                DailyMiniGame.Trigger();
            }
            if (nag)
            {
                introduced = true;
                SaveFlags();
                if (PetJourney.Instance != null)
                    PetJourney.Instance.BeginRestNag();
            }
            return;
        }

        busy = true;
        Step step = homeSteps[index];
        switch (step.kind)
        {
            case Kind.Phase:
                PetStory.SetPhase(step.phase);
                FinishStep();
                return;
            case Kind.OfferFlag:
                offerUnlocked = true;
                SaveFlags();
                FinishStep();
                return;
            case Kind.Lines:
                listening = true;
                PetDialogue.SayLines(step.lines);
                return;
            case Kind.Choice:
                if (step.choice == 1)
                    AskSecondReturn();
                else
                    AskFirstReturn();
                return;
            case Kind.Emphasis:
                listening = true;
                PetDialogue.SayEmphasis(step.lines[0]);
                return;
            case Kind.Flash:
                StartCoroutine(ScareRoutine(step.lines[0]));
                return;
            case Kind.Hunger:
                feeds = 0;
                hungerLooping = true;
                ShowFeed();
                listening = true;
                PetDialogue.SayLines(HungerLines);
                return;
            case Kind.Link:
                listening = true;
                PetDialogue.SayLink("Today's news\nThe margin is watching.", PageUrl());
                return;
            case Kind.Death:
                ShowDeath("Blinky has stopped.", "Wake Blinky", Wake);
                return;
            case Kind.Ritual:
                ShowDeath("Blinky has stopped.", "Revive", OpenRitual);
                return;
            case Kind.Contract:
                ShowDeath("Wake me, and you keep me fed. Two offerings. Then I stay.", "I agree", Agree);
                return;
            case Kind.Membership:
                listening = true;
                string cardUrl = WriteMembershipCard();
                if (string.IsNullOrEmpty(cardUrl))
                    PetDialogue.Say("Come take a look at your membership card...");
                else
                    PetDialogue.SayLink("Come take a look at your membership card...", cardUrl);
                return;
            case Kind.Desk:
                CreateTrueBlinkyShortcut();
                listening = true;
                PetDialogue.Say("Whenever you think of me, you can summon me from the desktop anytime.");
                return;
        }
    }

    void FinishStep()
    {
        index++;
        Play();
    }

    void Continue()
    {
        if (hungerLooping)
        {
            if (feeds < 2)
            {
                listening = true;
                PetDialogue.SayLines(HungerLines);
                return;
            }

            hungerLooping = false;
            HideFeed();
        }

        FinishStep();
    }

    IEnumerator ScareRoutine(string line)
    {
        yield return FlashScreen();
        PlayBeep();
        listening = true;
        PetDialogue.SayEmphasis(line);
    }

    void Wake()
    {
        dead = false;
        HideVeil();
        if (PetJourney.Instance != null)
        {
            PetJourney.Instance.SetBodyVisible(true);
            PetJourney.Instance.SetJourneyAvailable(true);
        }

        PetStory.SetPhase(PetStory.Phase.StateI);
        FinishStep();
    }

    void OpenRitual()
    {
        if (veilButton != null)
            veilButton.interactable = false;
        if (veilText != null)
            veilText.text = "The rite begins.";
        StartCoroutine(FinishRitual());
    }

    IEnumerator FinishRitual()
    {
        yield return new WaitForSecondsRealtime(1.2f);
        if (veilText != null)
            veilText.text = "The rite is finished.";
        yield return new WaitForSecondsRealtime(0.8f);
        dead = false;
        HideVeil();
        PetStory.SetPhase(PetStory.Phase.StateII);
        if (PetJourney.Instance != null)
        {
            PetJourney.Instance.RecoverMood();
            PetJourney.Instance.SetBodyVisible(true);
            PetJourney.Instance.SetJourneyAvailable(true);
        }

        FinishStep();
    }

    void Agree()
    {
        dead = false;
        HideVeil();
        if (PetJourney.Instance != null)
            PetJourney.Instance.SetBodyVisible(true);
        FinishStep();
    }

    void Feed()
    {
        if (!hungerLooping || feeds >= 2)
            return;

        feeds++;
        if (feedLabel != null)
            feedLabel.text = "Feed (" + feeds + "/2)";
    }

    void Choose(bool join)
    {
        choiceOpen = false;
        if (choiceRoot != null)
            choiceRoot.SetActive(false);
        listening = false;
        pending = false;
        busy = false;
        homeSteps = null;
        if (join)
        {
            PetStory.SetPhase(PetStory.Phase.Final);
            journeyBag.Clear();
            homeBag.Clear();
            PetDialogue.Say("{name}. Then stay. I'll keep going out.");
            return;
        }

        PetStory.SetPhase(PetStory.Phase.Refused);
        WriteDesk();
        PetDialogue.Say(PetStory.RefuseSpeech);
    }

    string PageUrl()
    {
        return PetJourney.Instance != null ? PetJourney.Instance.PageUrl() : "https://itch.io";
    }

    static string[] LinesForTrip(int trip)
    {
        if (PetStory.Current == PetStory.Phase.Final)
            return new[] { Instance.Next(Instance.journeyBag, JoinJourneys) };

        switch (trip)
        {
            case 2:
                return new[]
                {
                    "The street is loud. I'm still looking.",
                    "A dog in a hat. I'm taking that one.",
                    "A window was open. I only looked once.",
                    "Coming back with a few."
                };
            case 3:
                return new string[0];
            case 4:
                return new string[0];
            case 5:
                return new string[0];
            case 6:
                return new[]
                {
                    "A voice knew the shape of your room.",
                    "It asked me to keep looking.",
                    "I didn't answer.",
                    "I still brought the memes."
                };
            case 7:
                return new[]
                {
                    "A chat was open. I read the first line.",
                    "They said a name. I closed it.",
                    "A camera light was on. I looked away.",
                    "The memes are in the phone."
                };
            case 8:
                return new[]
                {
                    "I was gone too long.",
                    "Hunger makes the pictures blur.",
                    "I took one anyway.",
                    "Don't let me sit empty again."
                };
            case 9:
                return new[]
                {
                    "{name}, I found a page.",
                    "The eye in the margin is not decoration.",
                    "Read it when I get back.",
                    "I'm bringing the rest home."
                };
            case 10:
                return new[]
                {
                    "{name}, this is the last ordinary trip.",
                    "After this, /offer will open.",
                    "The page is the same door.",
                    "I'll be waiting on the desk."
                };
            default:
                return new[] { "{name}, the door is still /offer. I sent the page again." };
        }
    }

    static Step[] HomeFor(int trip)
    {
        switch (trip)
        {
            case 2:
                return new[]
                {
                    Step.Say("My legs are tired. The bar has to come back on its own."),
                    Step.Say("When it's full, send me out again.")
                };
            case 3:
                return new[]
                {
                    Step.Say("{name}, I'm back!")
                };
            case 4:
                return new[]
                {
                    Step.Say("{name}, I'm back!")
                };
            case 5:
                return new[]
                {
                    Step.Say("There seem to be some other functions in the admin panel."),
                    Step.Say("Want to try typing something?")
                };
            case 6:
                return new[]
                {
                    Step.Say("If I don't get up, open the window that asks."),
                    new Step { kind = Kind.Ritual },
                    Step.Say("Oh. {Name}. You're still here. I can see the screen from the other side. Just a little."),
                    Step.Say("Send me when the bar is full. I'll tell you what the windows show.")
                };
            case 7:
                return new[]
                {
                    Step.Say("You can watch with me. Only what was already open."),
                    Step.Say("The private ones stay private. For now.")
                };
            case 8:
                return new[]
                {
                    Step.Say("I'm hungry."),
                    Step.Big("I'm hungry."),
                    Step.Scare("Feed me."),
                    new Step { kind = Kind.Contract },
                    new Step { kind = Kind.Hunger },
                    Step.Say("The dark window listens. Type /offer after the tenth return. Not before."),
                    Step.To(PetStory.Phase.StateIII),
                    Step.Say("{name}. That's better. I can say your name properly."),
                    Step.Say("{name}, the next trips have doors in them."),
                    Step.Say("Read what I send. The page remembers you.")
                };
            case 9:
                return new[]
                {
                    new Step { kind = Kind.Link },
                    Step.Say("The command is /offer. It stays quiet until the tenth return."),
                    Step.Say("Look at the page again if the words feel thin.")
                };
            case 10:
                return new[]
                {
                    new Step { kind = Kind.Link },
                    Step.Say("Type /offer. Join, or refuse."),
                    new Step { kind = Kind.OfferFlag },
                    Step.Say("The page on the phone is the same door.")
                };
            default:
                return new[]
                {
                    new Step { kind = Kind.Link },
                    Step.Say("{name}, /offer is still there. The page goes out every time I leave.")
                };
        }
    }

    string Next(List<string> bag, string[] source)
    {
        if (bag.Count == 0)
        {
            bag.AddRange(source);
            for (int i = bag.Count - 1; i > 0; i--)
            {
                int swap = UnityEngine.Random.Range(0, i + 1);
                string held = bag[i];
                bag[i] = bag[swap];
                bag[swap] = held;
            }
        }

        int last = bag.Count - 1;
        string line = bag[last];
        bag.RemoveAt(last);
        return line;
    }

    void ShowDeath(string message, string button, UnityEngine.Events.UnityAction onClick)
    {
        dead = true;
        if (PetJourney.Instance != null)
        {
            PetJourney.Instance.SetBodyVisible(false);
            PetJourney.Instance.SetJourneyAvailable(false);
        }

        if (PetDialogue.Instance != null && PetDialogue.Instance.IsOpen)
            PetDialogue.Instance.Hide();

        EnsureVeil();
        veil.SetActive(true);
        veil.transform.SetAsLastSibling();
        veilText.text = message;
        veilButton.onClick.RemoveAllListeners();
        veilButton.onClick.AddListener(onClick);
        veilButton.interactable = true;
        veilButtonLabel.text = button;
    }

    void HideVeil()
    {
        if (veil != null)
            veil.SetActive(false);
    }

    void ShowFeed()
    {
        EnsureFeed();
        feedRoot.SetActive(true);
        feedLabel.text = "Feed (0/2)";
    }

    void HideFeed()
    {
        if (feedRoot != null)
            feedRoot.SetActive(false);
    }

    void ShowChoice()
    {
        EnsureChoice();
        choiceOpen = true;
        choiceRoot.SetActive(true);
        choiceRoot.transform.SetAsLastSibling();
    }

    void EnsureVeil()
    {
        if (veil != null)
            return;

        Canvas canvas = PetDialogue.OverlayCanvas();
        if (canvas == null)
            return;

        veil = new GameObject("Revive", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        veil.transform.SetParent(canvas.transform, false);
        Stretch(veil.GetComponent<RectTransform>());
        var dim = veil.GetComponent<Image>();
        dim.sprite = Skin();
        dim.color = new Color(0.05f, 0.04f, 0.05f, 0.72f);
        dim.raycastTarget = true;

        var card = Panel("Card", veil.transform, new Vector2(460f, 220f));
        veilText = Label("Body", card, 18, TextAnchor.UpperLeft, new Color(0.93f, 0.9f, 0.86f, 1f));
        var textRect = veilText.rectTransform;
        textRect.anchorMin = new Vector2(0f, 0.35f);
        textRect.anchorMax = new Vector2(1f, 1f);
        textRect.offsetMin = new Vector2(18f, 8f);
        textRect.offsetMax = new Vector2(-18f, -16f);

        var buttonObject = new GameObject("Act", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(card, false);
        var buttonRect = buttonObject.GetComponent<RectTransform>();
        buttonRect.anchorMin = new Vector2(0.5f, 0f);
        buttonRect.anchorMax = new Vector2(0.5f, 0f);
        buttonRect.pivot = new Vector2(0.5f, 0f);
        buttonRect.sizeDelta = new Vector2(180f, 36f);
        buttonRect.anchoredPosition = new Vector2(0f, 16f);
        var buttonImage = buttonObject.GetComponent<Image>();
        buttonImage.sprite = Skin();
        buttonImage.color = new Color(0.55f, 0.18f, 0.22f, 1f);
        veilButton = buttonObject.GetComponent<Button>();
        veilButton.targetGraphic = buttonImage;
        veilButtonLabel = Label("Caption", buttonObject.transform, 16, TextAnchor.MiddleCenter, Color.white);
        Stretch(veilButtonLabel.rectTransform);
    }

    void EnsureFeed()
    {
        if (feedRoot != null)
            return;

        Canvas canvas = PetDialogue.OverlayCanvas();
        if (canvas == null)
            return;

        feedRoot = new GameObject("Feed", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        feedRoot.transform.SetParent(canvas.transform, false);
        var rect = feedRoot.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 0f);
        rect.pivot = new Vector2(0f, 0f);
        rect.sizeDelta = new Vector2(148f, 44f);
        rect.anchoredPosition = new Vector2(24f, 24f);
        var image = feedRoot.GetComponent<Image>();
        image.sprite = Skin();
        image.color = new Color(0.45f, 0.24f, 0.16f, 1f);
        var button = feedRoot.GetComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(Feed);
        feedLabel = Label("Caption", feedRoot.transform, 16, TextAnchor.MiddleCenter, Color.white);
        Stretch(feedLabel.rectTransform);
    }

    void EnsureChoice()
    {
        if (choiceRoot != null)
            return;

        Canvas canvas = PetDialogue.OverlayCanvas();
        if (canvas == null)
            return;

        choiceRoot = new GameObject("Ending", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        choiceRoot.transform.SetParent(canvas.transform, false);
        Stretch(choiceRoot.GetComponent<RectTransform>());
        var dim = choiceRoot.GetComponent<Image>();
        dim.sprite = Skin();
        dim.color = new Color(0.04f, 0.04f, 0.06f, 0.78f);
        dim.raycastTarget = true;

        var card = Panel("Card", choiceRoot.transform, new Vector2(440f, 200f));
        Text title = Label("Title", card, 18, TextAnchor.UpperCenter, new Color(0.93f, 0.9f, 0.86f, 1f));
        var titleRect = title.rectTransform;
        titleRect.anchorMin = new Vector2(0f, 0.55f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.offsetMin = new Vector2(16f, 0f);
        titleRect.offsetMax = new Vector2(-16f, -12f);
        title.text = "Stay with the eye, or close it.";

        ChoiceButton(card, "Stay", new Vector2(-90f, 28f), () => Choose(true));
        ChoiceButton(card, "Close it", new Vector2(90f, 28f), () => Choose(false));
    }

    void ChoiceButton(Transform parent, string caption, Vector2 position, UnityEngine.Events.UnityAction onClick)
    {
        var buttonObject = new GameObject(caption, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(parent, false);
        var rect = buttonObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0f);
        rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.sizeDelta = new Vector2(150f, 36f);
        rect.anchoredPosition = position;
        var image = buttonObject.GetComponent<Image>();
        image.sprite = Skin();
        image.color = new Color(0.28f, 0.16f, 0.2f, 1f);
        var button = buttonObject.GetComponent<Button>();
        button.targetGraphic = image;
        button.onClick.AddListener(onClick);
        Text label = Label("Caption", buttonObject.transform, 16, TextAnchor.MiddleCenter, Color.white);
        label.text = caption;
        Stretch(label.rectTransform);
    }

    void EnsureFlash()
    {
        if (flash != null)
            return;

        Canvas canvas = PetDialogue.OverlayCanvas();
        if (canvas == null)
            return;

        var flashObject = new GameObject("Flash", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        flashObject.transform.SetParent(canvas.transform, false);
        Stretch(flashObject.GetComponent<RectTransform>());
        flash = flashObject.GetComponent<Image>();
        flash.sprite = Skin();
        flash.color = new Color(0.85f, 0.12f, 0.18f, 0f);
        flash.raycastTarget = false;
    }

    IEnumerator FlashScreen()
    {
        EnsureFlash();
        if (flash == null)
            yield break;

        flash.transform.SetAsLastSibling();
        float elapsed = 0f;
        const float duration = 0.45f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float alpha = Mathf.Sin(Mathf.Clamp01(elapsed / duration) * Mathf.PI) * 0.9f;
            flash.color = new Color(0.85f, 0.12f, 0.18f, alpha);
            yield return null;
        }

        flash.color = new Color(0.85f, 0.12f, 0.18f, 0f);
    }

    void PlayBeep()
    {
        if (beep == null)
        {
            beep = gameObject.AddComponent<AudioSource>();
            beep.playOnAwake = false;
            const int rate = 44100;
            int count = rate / 2;
            beepClip = AudioClip.Create("miaow", count, 1, rate, false);
            var data = new float[count];
            for (int i = 0; i < count; i++)
            {
                float time = i / (float)rate;
                float env = Mathf.Clamp01(1f - time / 0.5f);
                data[i] = Mathf.Sin(2f * Mathf.PI * (480f + 220f * time) * time) * 0.28f * env;
            }

            beepClip.SetData(data, 0);
        }

        beep.PlayOneShot(beepClip, 0.8f);
    }

    void WriteDesk()
    {
        try
        {
            string dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(dir))
                return;

            File.WriteAllText(
                Path.Combine(dir, "blinky.txt"),
                "You closed the eye.\r\nBlinky stays on the glass.\r\nQui videt, videtur.\r\nThis note is the only thing left on the desk.\r\n");
            DebugConsole.WriteLine("Left blinky.txt on the desktop.");
        }
        catch (Exception ex)
        {
            DebugConsole.WriteLine("Could not write the desktop note: " + ex.Message);
        }
    }

    Transform Panel(string name, Transform parent, Vector2 size)
    {
        var panel = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        panel.transform.SetParent(parent, false);
        var rect = panel.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        var image = panel.GetComponent<Image>();
        image.sprite = Skin();
        image.color = new Color(0.12f, 0.1f, 0.12f, 1f);
        return panel.transform;
    }

    static Text Label(string name, Transform parent, int size, TextAnchor anchor, Color color)
    {
        var textObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        textObject.transform.SetParent(parent, false);
        var text = textObject.GetComponent<Text>();
        text.font = BuiltinFont();
        text.fontSize = size;
        text.alignment = anchor;
        text.color = color;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        return text;
    }

    static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    Sprite Skin()
    {
        if (skin != null)
            return skin;

        var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
        var pixels = new Color[16];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = Color.white;
        texture.SetPixels(pixels);
        texture.Apply();
        texture.hideFlags = HideFlags.HideAndDontSave;
        skin = Sprite.Create(texture, new Rect(0f, 0f, 4f, 4f), new Vector2(0.5f, 0.5f), 100f);
        skin.hideFlags = HideFlags.HideAndDontSave;
        return skin;
    }

    public static bool IsCultCommand(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string text = raw.Trim();
        if (text.Length > 0 && (text[0] == '\\' || text[0] == '/'))
            text = text.Substring(1).Trim();
        return string.Equals(text, "Oculus Omnis", StringComparison.OrdinalIgnoreCase);
    }

    public static void TryJoinCult()
    {
        Ensure().BeginJoin();
    }

    void BeginJoin()
    {
        if (cultJoined || cultJoining)
            return;

        cultJoining = true;
        bool early = PetJourney.Instance == null || PetJourney.Instance.CompletedTrips < 5;
        listening = false;
        pending = false;
        busy = false;
        homeSteps = null;
        choiceOpen = false;
        if (choiceRoot != null)
            choiceRoot.SetActive(false);
        if (tripRoutine != null)
        {
            StopCoroutine(tripRoutine);
            tripRoutine = null;
        }

        AdminPanel.Close();
        var host = new GameObject("CultMosaic");
        host.AddComponent<CultMosaicPlayer>().Play(this, early);
    }

    public void CompleteJoin(bool early)
    {
        cultJoined = true;
        cultJoining = false;
        SaveFlags();
        listening = false;
        pending = false;
        busy = false;
        choiceOpen = false;
        firstReturnScript = false;
        armAgree = false;
        DailyMiniGame.Close();
        PetStory.SetPhase(PetStory.Phase.StateIII);

        var steps = new List<Step>();
        if (early)
        {
            steps.Add(Step.Say(
                "Oh, {name}, a friend of yours must have recommended us to you.",
                "Welcome, new congregant. From now on you can ask me anything on the phone, and I will tell you everything you want to know."));
        }
        else
        {
            steps.Add(Step.Say(
                "Welcome, new congregant. From now on you can ask me anything on the phone, and I will tell you everything you want to know."));
        }

        steps.Add(new Step { kind = Kind.Membership });
        steps.Add(new Step { kind = Kind.Desk });
        homeSteps = steps.ToArray();
        index = 0;
        Play();
    }

    const string MemberIdKey = "PetCampaign.MemberId";

    static string MemberId()
    {
        string id = PlayerPrefs.GetString(MemberIdKey, "");
        if (id != null && id.Length == 8)
            return id;

        id = UnityEngine.Random.Range(0, 100000000).ToString("D8");
        PlayerPrefs.SetString(MemberIdKey, id);
        PlayerPrefs.Save();
        return id;
    }

    string WriteMembershipCard()
    {
        try
        {
            string cardFile = FindProjectFile("News", "网页", "图片", "未标题-1-恢复的.png");
            string html = MembershipHtml(
                MemberId(),
                PetStory.ComputerName(),
                AdminPanel.LocalIp(),
                DataUrl(cardFile),
                DataUrl(PetJourney.PlayerPhotoFile));
            string path = Path.Combine(Application.persistentDataPath, "TrueBlinkyMembership.html");
            File.WriteAllText(path, html);
            return new Uri(path).AbsoluteUri;
        }
        catch (Exception ex)
        {
            DebugConsole.WriteLine(ex.Message);
            return null;
        }
    }

    static string MembershipHtml(string id, string name, string location, string cardUrl, string photoUrl)
    {
        string face = string.IsNullOrEmpty(cardUrl)
            ? "<div class=\"fallback\"></div>"
            : "<img class=\"face\" alt=\"Membership\" src=\"" + cardUrl + "\">";
        string photo = string.IsNullOrEmpty(photoUrl)
            ? ""
            : "<img class=\"photo\" alt=\"\" src=\"" + photoUrl + "\">";
        return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Membership</title><style>"
            + "html,body{margin:0;height:100%;background:#111;}"
            + "body{display:flex;align-items:center;justify-content:center;}"
            + ".card{position:relative;width:min(96vw,980px);container-type:inline-size;}"
            + ".face,.fallback{display:block;width:100%;height:auto;}"
            + ".fallback{aspect-ratio:1;background:#c8c4c4;}"
            + ".photo{position:absolute;left:11.72%;top:35.74%;width:25.78%;height:35.55%;object-fit:cover;object-position:center;}"
            + ".id,.name,.loc{position:absolute;margin:0;font-family:\"Times New Roman\",Times,serif;font-weight:700;color:#111;line-height:1;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}"
            + ".id{left:81.05%;top:28.7%;font-size:28px;font-size:3.2cqw;letter-spacing:.04em;max-width:17%;}"
            + ".name{left:50.8%;top:65.05%;font-size:18px;font-size:2.05cqw;max-width:46%;}"
            + ".loc{left:56.45%;top:69.4%;font-size:18px;font-size:2.05cqw;max-width:40%;}"
            + "</style></head><body><main class=\"card\">"
            + face + photo
            + "<p class=\"id\">" + Html(id) + "</p>"
            + "<p class=\"name\">" + Html(name) + "</p>"
            + "<p class=\"loc\">" + Html(location) + "</p>"
            + "</main></body></html>";
    }

    static string Html(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    static string DataUrl(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;

        byte[] bytes = File.ReadAllBytes(path);
        if (bytes == null || bytes.Length == 0)
            return null;

        return "data:image/png;base64," + Convert.ToBase64String(bytes);
    }

    static string FindProjectFile(params string[] parts)
    {
        string relative = Path.Combine(parts);
        string streaming = Path.Combine(Application.streamingAssetsPath, relative);
        if (File.Exists(streaming))
            return streaming;

        string editor = Path.Combine(Application.dataPath, relative);
        if (File.Exists(editor))
            return editor;

        return null;
    }

    static void CreateTrueBlinkyShortcut()
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop))
                return;

            string target = null;
            try
            {
                var module = System.Diagnostics.Process.GetCurrentProcess().MainModule;
                if (module != null)
                    target = module.FileName;
            }
            catch (Exception)
            {
            }

            if (string.IsNullOrEmpty(target))
            {
                string[] args = Environment.GetCommandLineArgs();
                if (args != null && args.Length > 0)
                    target = args[0];
            }

            if (string.IsNullOrEmpty(target) || !File.Exists(target))
                return;

            string icon = null;
            try
            {
                string png = FindProjectFile("News", "3.png");
                if (png == null)
                {
                    string art = Path.Combine(Application.dataPath, "ArtResources", "3.png");
                    if (File.Exists(art))
                        png = art;
                }

                if (!string.IsNullOrEmpty(png))
                {
                    icon = Path.Combine(Application.persistentDataPath, "TrueBlinky.ico");
                    WritePngIcon(png, icon);
                }
            }
            catch (Exception)
            {
                icon = null;
            }

            string link = Path.Combine(desktop, "True Blinky.lnk");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return;

            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { link });
            Type shortcutType = shortcut.GetType();
            const System.Reflection.BindingFlags setProp = System.Reflection.BindingFlags.SetProperty;
            shortcutType.InvokeMember("TargetPath", setProp, null, shortcut, new object[] { target });
            string work = Path.GetDirectoryName(target);
#if UNITY_EDITOR
            string project = Directory.GetParent(Application.dataPath).FullName;
            work = project;
            shortcutType.InvokeMember(
                "Arguments",
                setProp,
                null,
                shortcut,
                new object[] { "-projectPath \"" + project + "\"" });
#endif
            if (!string.IsNullOrEmpty(work))
                shortcutType.InvokeMember("WorkingDirectory", setProp, null, shortcut, new object[] { work });
            shortcutType.InvokeMember("Description", setProp, null, shortcut, new object[] { "True Blinky" });
            if (!string.IsNullOrEmpty(icon) && File.Exists(icon))
                shortcutType.InvokeMember("IconLocation", setProp, null, shortcut, new object[] { icon + ",0" });
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        catch (Exception ex)
        {
            DebugConsole.WriteLine(ex.Message);
        }
    }

    static void WritePngIcon(string pngPath, string icoPath)
    {
        byte[] source = File.ReadAllBytes(pngPath);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!texture.LoadImage(source))
        {
            UnityEngine.Object.Destroy(texture);
            return;
        }

        const int size = 256;
        var scaled = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        {
            float v = (y + 0.5f) / size;
            for (int x = 0; x < size; x++)
                scaled.SetPixel(x, y, texture.GetPixelBilinear((x + 0.5f) / size, v));
        }

        scaled.Apply(false, false);
        byte[] png = scaled.EncodeToPNG();
        UnityEngine.Object.Destroy(texture);
        UnityEngine.Object.Destroy(scaled);
        if (png == null || png.Length == 0)
            return;

        using (var stream = File.Create(icoPath))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(png.Length);
            writer.Write(22);
            writer.Write(png);
        }
    }

    static Font BuiltinFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }
}

public class CultMosaicPlayer : MonoBehaviour
{
    public void Play(PetCampaign campaign, bool early)
    {
        StartCoroutine(Run(campaign, early));
    }

    IEnumerator Run(PetCampaign campaign, bool early)
    {
        const int cells = 24;
        var texture = new Texture2D(cells, cells, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.hideFlags = HideFlags.HideAndDontSave;
        var pixels = new Color32[cells * cells];
        var rng = new System.Random();

        var canvasObject = new GameObject("CultMosaicCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 31500;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        var imageObject = new GameObject("Blocks", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        imageObject.transform.SetParent(canvasObject.transform, false);
        var rect = imageObject.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var image = imageObject.GetComponent<Image>();
        var sprite = Sprite.Create(texture, new Rect(0f, 0f, cells, cells), new Vector2(0.5f, 0.5f), 100f);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        image.sprite = sprite;
        image.preserveAspect = false;
        image.raycastTarget = true;

        float elapsed = 0f;
        while (elapsed < 3f)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                byte v = (byte)rng.Next(16, 220);
                pixels[i] = new Color32(v, v, v, 255);
            }

            texture.SetPixels32(pixels);
            texture.Apply(false);
            yield return new WaitForSecondsRealtime(0.12f);
            elapsed += 0.12f;
        }

        if (campaign != null)
            campaign.CompleteJoin(early);

        Destroy(canvasObject);
        Destroy(sprite);
        Destroy(texture);
        Destroy(gameObject);
    }
}
