using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Quit 不会关闭游戏，只会让角色说完一段不肯放人走的话。
/// 远行途中改为在按钮上方显示一段逐渐消失的文字。
/// </summary>
public class QuitRefusal : MonoBehaviour
{
    const string AwayLine = "Wait until Blinky comes back before saying goodbye.";
    const float NoteHoldSeconds = 1.1f;
    const float NoteFadeSeconds = 1.6f;

    static readonly string[] Lines =
    {
        "Are you really leaving?",
        "Don't abandon me.",
        "I'll be watching you from afar."
    };

    static QuitRefusal instance;

    GameObject noteRoot;
    Image noteBackground;
    Text awayNote;
    float noteTime;

    void Awake()
    {
        instance = this;
        var button = GetComponent<Button>();
        if (button != null)
            button.onClick.AddListener(Play);
        CreateAwayNote();
    }

    void OnDestroy()
    {
        if (instance == this)
            instance = null;
    }

    void Update()
    {
        if (noteRoot == null || noteTime <= 0f)
            return;

        noteTime -= Time.deltaTime;
        float alpha = noteTime <= NoteFadeSeconds ? Mathf.Clamp01(noteTime / NoteFadeSeconds) : 1f;
        ApplyNoteAlpha(alpha);
        if (noteTime <= 0f)
            noteRoot.SetActive(false);
    }

    public static void Play()
    {
        if (PetJourney.PetIsAway)
        {
            if (instance != null)
                instance.ShowAwayNote();
            return;
        }

        PetDialogue.PlaySequence(Lines);
    }

    void ShowAwayNote()
    {
        if (noteRoot == null || awayNote == null)
            return;

        noteRoot.SetActive(true);
        noteRoot.transform.SetAsLastSibling();
        awayNote.text = AwayLine;
        noteTime = NoteHoldSeconds + NoteFadeSeconds;
        ApplyNoteAlpha(1f);
    }

    void ApplyNoteAlpha(float alpha)
    {
        if (noteBackground != null)
        {
            Color background = noteBackground.color;
            background.a = alpha;
            noteBackground.color = background;
        }

        Color color = awayNote.color;
        color.a = alpha;
        awayNote.color = color;
    }

    void CreateAwayNote()
    {
        var quitRect = (RectTransform)transform;
        Transform parent = transform.parent != null ? transform.parent : transform;

        noteRoot = new GameObject("AwayNote", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        noteRoot.transform.SetParent(parent, false);
        var rootRect = noteRoot.GetComponent<RectTransform>();
        rootRect.anchorMin = quitRect.anchorMin;
        rootRect.anchorMax = quitRect.anchorMax;
        rootRect.pivot = new Vector2(0.5f, 0f);
        rootRect.sizeDelta = new Vector2(328f, 96f);
        rootRect.anchoredPosition = new Vector2(0f, quitRect.anchoredPosition.y + quitRect.sizeDelta.y * 0.5f + 8f);

        noteBackground = noteRoot.GetComponent<Image>();
        noteBackground.color = new Color(0.99f, 0.96f, 0.9f, 0f);
        noteBackground.raycastTarget = false;

        var textObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        textObject.transform.SetParent(noteRoot.transform, false);
        var textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(16f, 12f);
        textRect.offsetMax = new Vector2(-16f, -12f);

        awayNote = textObject.GetComponent<Text>();
        awayNote.font = BuiltinFont();
        awayNote.fontSize = 16;
        awayNote.alignment = TextAnchor.MiddleCenter;
        awayNote.horizontalOverflow = HorizontalWrapMode.Wrap;
        awayNote.verticalOverflow = VerticalWrapMode.Overflow;
        awayNote.color = new Color(0.24f, 0.16f, 0.12f, 0f);
        awayNote.raycastTarget = false;
        noteRoot.SetActive(false);
    }

    static Font BuiltinFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }
}
