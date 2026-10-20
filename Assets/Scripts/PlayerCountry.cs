using System;
using System.Collections;
using System.Globalization;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 启动时用公网 IP 查询所在国家。结果只到国家，不到城市。
/// </summary>
public class PlayerCountry : MonoBehaviour
{
    [SerializeField] bool showLabel = true;

    public static PlayerCountry Instance { get; private set; }

    public string CountryCode { get; private set; }
    public string CountryName { get; private set; }
    public bool IsReady { get; private set; }

    TextMesh label;
    MeshRenderer labelRenderer;

    void Awake()
    {
        Instance = this;
        if (showLabel)
            CreateLabel();
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    void Start()
    {
        StartCoroutine(Lookup());
    }

    IEnumerator Lookup()
    {
        if (showLabel)
            SetLabel("Looking up country");

        string code = null;
        yield return RequestCountryCode("https://api.country.is/", true, value => code = value);
        if (string.IsNullOrEmpty(code))
            yield return RequestCountryCode("https://ipinfo.io/country", false, value => code = value);

        if (string.IsNullOrEmpty(code))
        {
            SetLabel("Country lookup failed");
            Debug.LogWarning("没有查询到国家。请确认游戏可以访问网络。");
            yield break;
        }

        CountryCode = code;
        CountryName = ToCountryName(code);
        IsReady = true;
        SetLabel(CountryName);
        Debug.Log("所在国家：" + CountryName + " (" + CountryCode + ")");
    }

    static IEnumerator RequestCountryCode(string url, bool json, Action<string> onCode)
    {
        using (var request = UnityWebRequest.Get(url))
        {
            request.timeout = 8;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                onCode(null);
                yield break;
            }

            string body = request.downloadHandler.text.Trim();
            if (!json)
            {
                onCode(NormalizeCode(body));
                yield break;
            }

            var payload = JsonUtility.FromJson<CountryPayload>(body);
            onCode(payload == null ? null : NormalizeCode(payload.country));
        }
    }

    static string NormalizeCode(string value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        value = value.Trim().ToUpperInvariant();
        if (value.Length != 2)
            return null;
        return value;
    }

    static string ToCountryName(string code)
    {
        try
        {
            return new RegionInfo(code).DisplayName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    void CreateLabel()
    {
        var textObject = new GameObject("CountryLabel");
        textObject.transform.SetParent(transform, false);
        textObject.transform.localPosition = new Vector3(0f, 4.15f, 10f);

        label = textObject.AddComponent<TextMesh>();
        label.anchor = TextAnchor.MiddleCenter;
        label.alignment = TextAlignment.Center;
        label.fontSize = 32;
        label.characterSize = 0.08f;
        label.color = new Color(0.98f, 0.94f, 0.86f, 1f);
        label.text = string.Empty;

        Font font = Font.CreateDynamicFontFromOSFont(
            new[] { "Microsoft YaHei", "微软雅黑", "SimHei", "黑体", "SimSun", "Arial" },
            label.fontSize);
        if (font != null)
            label.font = font;

        labelRenderer = label.GetComponent<MeshRenderer>();
        labelRenderer.sortingOrder = 40;
        if (label.font != null)
            labelRenderer.material = label.font.material;
        labelRenderer.enabled = false;
    }

    void SetLabel(string message)
    {
        if (!showLabel || label == null)
            return;

        if (label.font != null)
            label.font.RequestCharactersInTexture(message, label.fontSize, FontStyle.Normal);
        label.text = message;
        labelRenderer.enabled = true;
        StartCoroutine(FitLabel());
    }

    IEnumerator FitLabel()
    {
        yield return null;
        if (label == null || labelRenderer == null || string.IsNullOrEmpty(label.text))
            yield break;

        label.transform.localScale = Vector3.one;
        float width = labelRenderer.bounds.size.x;
        float height = labelRenderer.bounds.size.y;
        if (width < 0.001f || height < 0.001f)
            yield break;

        float scale = Mathf.Min(3.1f / width, 0.5f / height);
        label.transform.localScale = new Vector3(scale, scale, 1f);
    }

    [Serializable]
    class CountryPayload
    {
        public string country;
    }
}
