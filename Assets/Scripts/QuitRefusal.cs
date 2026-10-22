using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Quit 不会关闭游戏，只会让角色说完一段不肯放人走的话。
/// 远行途中在画面中下显示一行逐渐淡出的字。
/// </summary>
public class QuitRefusal : MonoBehaviour
{
    const string AwayLine = "Wait until Blinky comes back before saying goodbye.";
    const float NoteFadeSeconds = 3f;

    static readonly string[] Lines =
    {
        "Are you really leaving?",
        "Don't abandon me.",
        "I'll be watching you from afar."
    };

    static QuitRefusal instance;

    GameObject noteRoot;
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

        noteTime -= Time.unscaledDeltaTime;
        float alpha = Mathf.Clamp01(noteTime / NoteFadeSeconds);
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
        noteTime = NoteFadeSeconds;
        ApplyNoteAlpha(1f);
    }

    void ApplyNoteAlpha(float alpha)
    {
        Color color = awayNote.color;
        color.a = alpha;
        awayNote.color = color;
    }

    void CreateAwayNote()
    {
        Transform parent = transform.parent != null ? transform.parent : transform;

        noteRoot = new GameObject("AwayNote", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        noteRoot.transform.SetParent(parent, false);
        var rootRect = noteRoot.GetComponent<RectTransform>();
        rootRect.anchorMin = new Vector2(0.5f, 0.5f);
        rootRect.anchorMax = new Vector2(0.5f, 0.5f);
        rootRect.pivot = new Vector2(0.5f, 0.5f);
        rootRect.sizeDelta = new Vector2(640f, 36f);
        rootRect.anchoredPosition = new Vector2(0f, -48f);

        awayNote = noteRoot.GetComponent<Text>();
        awayNote.font = BuiltinFont();
        awayNote.fontSize = 16;
        awayNote.alignment = TextAnchor.MiddleCenter;
        awayNote.horizontalOverflow = HorizontalWrapMode.Overflow;
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
