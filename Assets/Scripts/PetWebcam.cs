using System.Collections;
using UnityEngine;

/// <summary>
/// 打开本机摄像头，并把画面显示在宠物窗口里。
/// 编辑器和 Windows 打包都能用。系统需允许桌面应用访问相机。
/// </summary>
[DefaultExecutionOrder(-600)]
public class PetWebcam : MonoBehaviour
{
    [SerializeField] bool playOnStart = true;
    [SerializeField] bool showPreview = true;
    [SerializeField] bool mirror = true;
    [SerializeField] int requestedWidth = 320;
    [SerializeField] int requestedHeight = 240;
    [SerializeField] int requestedFps = 30;
    [SerializeField] float previewHeight = 3.2f;
    [SerializeField] Vector2 previewPosition = new Vector2(0f, -2.4f);

    WebCamTexture feed;
    Coroutine readyRoutine;
    Texture2D previewTexture;
    Sprite previewSprite;
    SpriteRenderer previewRenderer;
    string error;

    public WebCamTexture Feed => feed;
    public bool IsPlaying => feed != null && feed.isPlaying;
    public string DeviceName => feed != null ? feed.deviceName : null;
    public string Error => error;

    void Start()
    {
        if (playOnStart)
            StartCamera();
    }

    public void StartCamera(string preferredDevice = null)
    {
        if (readyRoutine != null)
        {
            StopCoroutine(readyRoutine);
            readyRoutine = null;
        }

        StopCamera();
        error = null;

        WebCamDevice[] devices = WebCamTexture.devices;
        if (devices == null || devices.Length == 0)
        {
            error = "No camera found.";
            Debug.LogWarning(error);
            return;
        }

        string deviceName = preferredDevice;
        if (string.IsNullOrEmpty(deviceName))
            deviceName = devices[0].name;

        feed = new WebCamTexture(deviceName, requestedWidth, requestedHeight, requestedFps);
        feed.Play();
        readyRoutine = StartCoroutine(WaitUntilReady());
    }

    public void StopCamera()
    {
        if (feed == null)
            return;

        if (feed.isPlaying)
            feed.Stop();
        Destroy(feed);
        feed = null;
        HidePreview();
    }

    void Update()
    {
        if (feed == null || !feed.isPlaying || !feed.didUpdateThisFrame || previewTexture == null)
            return;

        Color32[] pixels = feed.GetPixels32();
        for (int i = 0; i < pixels.Length; i++)
        {
            Color32 pixel = pixels[i];
            if (pixel.r < 8)
                pixel.r = 8;
            if (pixel.g < 8)
                pixel.g = 8;
            if (pixel.b < 8)
                pixel.b = 8;
            pixel.a = 255;
            pixels[i] = pixel;
        }

        previewTexture.SetPixels32(pixels);
        previewTexture.Apply(false);
    }

    void OnDestroy()
    {
        StopCamera();
        if (previewSprite != null)
            Destroy(previewSprite);
        if (previewTexture != null)
            Destroy(previewTexture);
    }

    IEnumerator WaitUntilReady()
    {
        float timeout = Time.realtimeSinceStartup + 8f;
        while (feed != null && feed.width <= 16 && Time.realtimeSinceStartup < timeout)
            yield return null;

        if (feed == null)
            yield break;

        if (!feed.isPlaying || feed.width <= 16)
        {
            error = "The camera has no picture. Allow desktop apps to use the camera in Windows privacy settings.";
            Debug.LogWarning(error);
            yield break;
        }

        Debug.Log("摄像头已打开：" + feed.deviceName + " " + feed.width + "x" + feed.height);
        if (showPreview)
            ShowPreview();
    }

    void ShowPreview()
    {
        if (feed == null)
            return;

        if (previewTexture == null || previewTexture.width != feed.width || previewTexture.height != feed.height)
        {
            if (previewTexture != null)
                Destroy(previewTexture);
            if (previewSprite != null)
                Destroy(previewSprite);

            previewTexture = new Texture2D(feed.width, feed.height, TextureFormat.RGBA32, false);
            previewTexture.filterMode = FilterMode.Bilinear;
            previewTexture.wrapMode = TextureWrapMode.Clamp;

            float pixelsPerUnit = feed.height / Mathf.Max(0.1f, previewHeight);
            previewSprite = Sprite.Create(
                previewTexture,
                new Rect(0f, 0f, feed.width, feed.height),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit);
        }

        if (previewRenderer == null)
        {
            var go = new GameObject("WebcamPreview");
            go.transform.SetParent(transform, false);
            previewRenderer = go.AddComponent<SpriteRenderer>();
            previewRenderer.sortingOrder = 10;
        }

        previewRenderer.sprite = previewSprite;
        previewRenderer.flipX = mirror;
        previewRenderer.transform.localPosition = new Vector3(previewPosition.x, previewPosition.y, 0f);
        previewRenderer.enabled = true;
    }

    void HidePreview()
    {
        if (previewRenderer != null)
            previewRenderer.enabled = false;
    }
}
