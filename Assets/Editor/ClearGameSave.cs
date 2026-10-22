using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 编辑器菜单：清掉玩家存档，让下一局从开场开始。
/// </summary>
static class ClearGameSave
{
    [MenuItem("Scream Jam/移除游戏存档")]
    static void RemoveSave()
    {
        if (!EditorUtility.DisplayDialog(
            "移除游戏存档",
            "会清掉名字、远行进度和心情。正在运行的话，这一局会立刻从头开始。",
            "移除",
            "取消"))
            return;

        PetJourney.EraseSave();
        if (EditorApplication.isPlaying)
        {
            PetDialogue.ClearSession();
            PetStory.SetPhase(PetStory.Phase.Normal);
            PetJourney.EraseSave();
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }

        Debug.Log("游戏存档已移除。下次进入 Play 会从开场开始。");
    }
}
