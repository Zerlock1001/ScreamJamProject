using UnityEngine;

/// <summary>
/// Assets/memes 里名字带 Weird 的暗 meme。放在 Resources 下，打包后第三次远行也能读到。
/// </summary>
public class WeirdMemeSet : ScriptableObject
{
    public Sprite[] memes;
}
