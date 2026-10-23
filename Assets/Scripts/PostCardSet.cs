using UnityEngine;

/// <summary>
/// Assets/PostCards 里的明信片。放在 Resources 下，打包后第一次远行也能读到。
/// </summary>
public class PostCardSet : ScriptableObject
{
    public Sprite[] cards;
}
