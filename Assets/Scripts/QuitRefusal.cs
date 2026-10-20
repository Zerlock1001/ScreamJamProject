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

    Text awayNote;
    Outline awayOutline;
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
        if (awayNote == null || noteTime <= 0f)
            return;

        noteTime -= Time.deltaTime;
        float alpha = noteTime <= NoteFadeSeconds ? Mathf.Clamp01(noteTime / NoteFadeSeconds) : 1f;
        ApplyNoteAlpha(alpha);
        if (noteTime <= 0f)
            awayNote.gameObject.SetActive(false);
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
        if (awayNote == null)
            return;

        awayNote.gameObject.SetActive(true);
        awayNote.text = AwayLine;
        noteTime = NoteHoldSeconds + NoteFadeSeconds;
        ApplyNoteAlpha(1f);
    }

    void ApplyNoteAlpha(float alpha)
    {
        Color color = awayNote.color;
        color.a = alpha;
        awayNote.color = color;
        if (awayOutline != null)
        {
            Color outline = awayOutline.effectColor;
            outline.a = alpha;
            awayOutline.effectColor = outline;
        }
    }

    void CreateAwayNote()
    {
        var textObject = new GameObject("AwayNote", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(Outline));
        textObject.transform.SetParent(transform, false);
        var rect = textObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 0f);
        rect.anchoredPosition = new Vector2(0f, 8f);
        rect.sizeDelta = new Vector2(240f, 48f);

        awayNote = textObject.GetComponent<Text>();
        awayNote.font = BuiltinFont();
        awayNote.fontSize = 14;
        awayNote.alignment = TextAnchor.LowerCenter;
        awayNote.horizontalOverflow = HorizontalWrapMode.Wrap;
        awayNote.verticalOverflow = VerticalWrapMode.Overflow;
        awayNote.color = new Color(0.98f, 0.96f, 0.92f, 0f);
        awayNote.raycastTarget = false;

        awayOutline = textObject.GetComponent<Outline>();
        awayOutline.effectColor = new Color(0.28f, 0.16f, 0.12f, 0f);
        awayOutline.effectDistance = new Vector2(1f, -1f);
        textObject.SetActive(false);
    }

    static Font BuiltinFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }
}
