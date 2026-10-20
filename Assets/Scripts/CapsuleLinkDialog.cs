using System.Collections;
using UnityEngine;

/// <summary>
/// 点击胶囊体弹出对话框，再点击对话框或胶囊体时用系统浏览器打开网页。
/// </summary>
public class CapsuleLinkDialog : MonoBehaviour
{
    [SerializeField] SpriteRenderer capsule;
    [SerializeField] string url = "https://itch.io";
    [SerializeField] string message = "Click again to open the page.";
    [SerializeField] float clickSlopPixels = 12f;
    [SerializeField] Vector2 panelSize = new Vector2(4.4f, 1.35f);
    [SerializeField] float gap = 0.28f;

    static readonly string[] FontCandidates =
    {
        "Microsoft YaHei",
        "微软雅黑",
        "SimHei",
        "黑体",
        "SimSun",
        "宋体",
        "Arial"
    };

    Camera cam;
    SpriteRenderer dialogRenderer;
    TextMesh dialogText;
    MeshRenderer dialogTextRenderer;
    bool dialogVisible;
    bool pressedCapsule;
    bool pressedDialog;
    Vector2 pressScreen;

    void Awake()
    {
        if (capsule == null)
            capsule = GetComponent<SpriteRenderer>();
        cam = Camera.main;
        CreateDialog();
        SetDialogVisible(false);
    }

    void Update()
    {
        if (cam == null)
            cam = Camera.main;
        if (capsule == null || cam == null)
            return;

        if (Input.GetMouseButtonDown(0))
        {
            pressScreen = Input.mousePosition;
            pressedDialog = dialogVisible && Hits(dialogRenderer);
            pressedCapsule = !pressedDialog && Hits(capsule);
        }

        if (!Input.GetMouseButtonUp(0))
            return;

        bool wasCapsule = pressedCapsule;
        bool wasDialog = pressedDialog;
        pressedCapsule = false;
        pressedDialog = false;

        float slop = Mathf.Max(1f, clickSlopPixels);
        if ((Input.mousePosition - (Vector3)pressScreen).sqrMagnitude > slop * slop)
            return;

        if (dialogVisible && (wasDialog || wasCapsule))
        {
            Application.OpenURL(url);
            SetDialogVisible(false);
            return;
        }

        if (wasCapsule)
            SetDialogVisible(true);
    }

    void CreateDialog()
    {
        var panel = new GameObject("LinkDialog");
        panel.transform.SetParent(transform, false);
        dialogRenderer = panel.AddComponent<SpriteRenderer>();
        dialogRenderer.sprite = CreatePanelSprite();
        dialogRenderer.sortingOrder = 20;

        var textObject = new GameObject("LinkDialogText");
        textObject.transform.SetParent(panel.transform, false);
        textObject.transform.localPosition = new Vector3(0f, 0f, -0.01f);

        dialogText = textObject.AddComponent<TextMesh>();
        dialogText.anchor = TextAnchor.MiddleCenter;
        dialogText.alignment = TextAlignment.Center;
        dialogText.fontSize = 32;
        dialogText.characterSize = 0.1f;
        dialogText.color = new Color(0.22f, 0.14f, 0.1f, 1f);

        Font font = Font.CreateDynamicFontFromOSFont(FontCandidates, dialogText.fontSize);
        if (font != null)
        {
            dialogText.font = font;
            font.RequestCharactersInTexture(message, dialogText.fontSize, FontStyle.Normal);
        }

        dialogText.text = message;

        dialogTextRenderer = dialogText.GetComponent<MeshRenderer>();
        dialogTextRenderer.sortingOrder = 21;
        if (dialogText.font != null)
            dialogTextRenderer.material = dialogText.font.material;
    }

    IEnumerator FitTextWhenReady()
    {
        yield return null;
        FitText();
    }

    void FitText()
    {
        if (dialogText == null || dialogTextRenderer == null || dialogRenderer == null)
            return;

        dialogText.transform.localScale = Vector3.one;
        float textWidth = dialogTextRenderer.bounds.size.x;
        float textHeight = dialogTextRenderer.bounds.size.y;
        float panelWidth = dialogRenderer.bounds.size.x;
        float panelHeight = dialogRenderer.bounds.size.y;
        if (textWidth < 0.001f || textHeight < 0.001f || panelWidth < 0.001f || panelHeight < 0.001f)
            return;

        float scale = Mathf.Min(panelWidth * 0.72f / textWidth, panelHeight * 0.46f / textHeight);
        dialogText.transform.localScale = new Vector3(scale, scale, 1f);
    }

    Sprite CreatePanelSprite()
    {
        const int width = 440;
        const int height = 136;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;

        var fill = new Color(0.98f, 0.94f, 0.86f, 1f);
        var border = new Color(0.42f, 0.26f, 0.18f, 1f);
        float radius = 28f;
        var pixels = new Color[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float dx = CornerDistance(x, y, width, height, radius);
                if (dx > radius)
                    continue;

                bool edge = dx > radius - 6f || x < 4 || y < 4 || x >= width - 4 || y >= height - 4;
                Color color = edge ? border : fill;
                float alpha = Mathf.Clamp01(radius - dx);
                color.a = alpha;
                if (color.r < 0.04f)
                    color.r = 0.04f;
                pixels[y * width + x] = color;
            }
        }

        texture.SetPixels(pixels);
        texture.Apply();

        float pixelsPerUnit = height / Mathf.Max(0.1f, panelSize.y);
        return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), pixelsPerUnit);
    }

    static float CornerDistance(int x, int y, int width, int height, float radius)
    {
        float left = radius;
        float right = width - 1 - radius;
        float bottom = radius;
        float top = height - 1 - radius;
        float px = Mathf.Clamp(x, left, right);
        float py = Mathf.Clamp(y, bottom, top);
        float dx = x - px;
        float dy = y - py;
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    void SetDialogVisible(bool visible)
    {
        dialogVisible = visible;
        if (dialogRenderer == null)
            return;

        dialogRenderer.gameObject.SetActive(visible);
        if (!visible)
            return;

        PlaceAboveCapsule();
        StartCoroutine(FitTextWhenReady());
    }

    void PlaceAboveCapsule()
    {
        Bounds bounds = capsule.bounds;
        float height = dialogRenderer.sprite.bounds.size.y * dialogRenderer.transform.lossyScale.y;
        var world = new Vector3(bounds.center.x, bounds.max.y + gap + height * 0.5f, capsule.transform.position.z);
        dialogRenderer.transform.position = world;
    }

    bool Hits(SpriteRenderer renderer)
    {
        if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy || renderer.sprite == null)
            return false;

        float depth = Mathf.Abs(cam.transform.position.z - renderer.transform.position.z);
        Vector3 world = cam.ScreenToWorldPoint(new Vector3(Input.mousePosition.x, Input.mousePosition.y, depth));
        Vector3 local = renderer.transform.InverseTransformPoint(world);
        Bounds bounds = renderer.sprite.bounds;
        return local.x >= bounds.min.x && local.x <= bounds.max.x && local.y >= bounds.min.y && local.y <= bounds.max.y;
    }
}
