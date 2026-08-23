namespace World.CivSim;

/// <summary>宗教阶段（固定 5 段 key；升级链：泛灵→萨满→祖先→多神→一神）。
/// key 与科技/文化同风格（字符串可读）；固定表 → 存档只存份额，key 由常量表重建。</summary>
public static class ReligionStage
{
    public const string Animism = "animism";
    public const string Shaman = "shaman";
    public const string Ancestor = "ancestor";
    public const string Polytheism = "polytheism";
    public const string Monotheism = "monotheism";
    public static readonly string[] All = { Animism, Shaman, Ancestor, Polytheism, Monotheism };
    public const int Count = 5;
}
