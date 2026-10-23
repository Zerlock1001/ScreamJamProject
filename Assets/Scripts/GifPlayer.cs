using UnityEngine;
using UnityEngine.UI;

public class GifPlayer : MonoBehaviour
{
    Image image;
    Sprite[] frames;
    float[] delays;
    int index;
    float remaining;

    public void Play(GifClip clip)
    {
        if (clip == null || clip.Frames == null || clip.Frames.Length == 0)
            return;

        image = GetComponent<Image>();
        frames = clip.Frames;
        delays = clip.Delays;
        index = 0;
        remaining = delays != null && delays.Length > 0 ? delays[0] : 0.1f;
        if (image != null)
            image.sprite = frames[0];
    }

    void Update()
    {
        if (image == null || frames == null || frames.Length < 2)
            return;

        remaining -= Time.unscaledDeltaTime;
        if (remaining > 0f)
            return;

        index = (index + 1) % frames.Length;
        image.sprite = frames[index];
        float delay = delays != null && index < delays.Length ? delays[index] : 0.1f;
        remaining += delay;
        if (remaining < 0f)
            remaining = delay;
    }
}
