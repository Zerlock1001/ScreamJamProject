using ScreamJam.Debugging;
using UnityEngine;

/// <summary>
/// 剧情状态。切换条件先记在这里，死亡、喂食和结局还没有接到这些条件上。
/// </summary>
public class PetStory : MonoBehaviour
{
    public enum Phase
    {
        Normal,
        StateI,
        StateII,
        StateIII,
        Final,
        Refused
    }

    public static PetStory Instance { get; private set; }
    public static Phase Current { get; private set; } = Phase.Normal;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Ensure()
    {
        if (Instance != null)
            return;

        var host = new GameObject("PetStory");
        host.AddComponent<PetStory>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DebugConsole.RegisterCommand("status", "/status", "Print the current story state and how it changes.", _ => PrintStatus());
    }

    void OnDestroy()
    {
        if (Instance == this)
            Instance = null;
    }

    public static string PhaseLabel(Phase phase)
    {
        switch (phase)
        {
            case Phase.StateI: return "状态 I";
            case Phase.StateII: return "状态 II";
            case Phase.StateIII: return "状态 III";
            case Phase.Final: return "最终态";
            case Phase.Refused: return "拒绝结局";
            default: return "普通态";
        }
    }

    static void PrintStatus()
    {
        DebugConsole.WriteLine("当前状态: " + PhaseLabel(Current));
        DebugConsole.WriteLine("称呼: " + NameRule(Current));
        DebugConsole.WriteLine("状态逻辑:");
        DebugConsole.WriteLine("普通态 — 开场。第四次远行死亡并复活后进入状态 I。");
        DebugConsole.WriteLine("状态 I — 不使用玩家名。第六次远行死亡，打开复活界面后自动完成妙妙仪式，进入状态 II，并刷新状态与远行冷却。");
        DebugConsole.WriteLine("状态 II — 偶尔使用玩家名，开始明示偷窥。饥饿死亡并复活后循环催喂，喂下两个食物，再经过控制台解锁和满足对话，进入状态 III。");
        DebugConsole.WriteLine("状态 III — 全部使用玩家名，频繁涉及隐私与邪教。第九、十次远行给出控制台指令，进入结局选择。");
        DebugConsole.WriteLine("最终态 — 选择加入后，从远行库和常规库里不重复抽取。");
        DebugConsole.WriteLine("拒绝结局 — 全部文本被替换，并在桌面留下 txt。");
        DebugConsole.WriteLine("死亡、喂食、控制台解锁和结局都还没有接入，所以现在不会离开普通态。");
    }

    static string NameRule(Phase phase)
    {
        switch (phase)
        {
            case Phase.StateII:
                return "偶尔使用玩家名";
            case Phase.StateIII:
            case Phase.Final:
                return "全部使用玩家名";
            case Phase.Refused:
                return "使用替换后的文本";
            default:
                return "不使用玩家名";
        }
    }
}
