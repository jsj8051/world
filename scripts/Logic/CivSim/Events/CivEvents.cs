using System.Collections.Generic;

namespace World.CivSim.Events;

/// <summary>
/// 文明事件记录（旁路只写——2026-08-24 文明记录⑪，docs/设计-观测面板与文明记录.md）。
/// 设计红线：
///   · **旁路**：事件只 append，不参与任何模拟分支、不产生 Rng、不改遍历序——开/关事件系统，
///     同 seed 演化结果逐比特一致（T90 回归守护）；
///   · **紧凑入档**：TypeIndex = EventTypes 注册表顺序号（短整型），存档不存字符串；
///   · **文本展示端派生**：记录是数据，中文文案由 EventTypes 展示端还原——改文案不碰数据不 bump 版本。
/// </summary>
public readonly struct CivEventRecord
{
    /// <summary>演化 tick。</summary>
    public readonly int Tick;

    /// <summary>事件类型索引（EventTypes 注册表序——入档语义）。</summary>
    public readonly short TypeIndex;

    /// <summary>主体政体 Id（-1=无）。</summary>
    public readonly int SubjectId;

    /// <summary>客体政体/聚落/科技 Id（-1=无）。</summary>
    public readonly int TargetId;

    /// <summary>数值（贡赋/胜场/人口等）。</summary>
    public readonly float Value;

    public CivEventRecord(int tick, int typeIndex, int subjectId = -1, int targetId = -1, float value = 0f)
    {
        Tick = tick;
        TypeIndex = (short)typeIndex;
        SubjectId = subjectId;
        TargetId = targetId;
        Value = value;
    }
}

/// <summary>
/// 事件类型注册表（追加式——加新事件 = 注册一行 + 领口一行，零 switch 地狱）。
/// 注册表序 = 入档索引：**只能在尾部追加，永不删除/重排**（旧档索引语义稳定）。
/// </summary>
public sealed class EventTypeDef
{
    /// <summary>事件 key（代码/诊断可读，存档不用）。</summary>
    public readonly string Key;

    /// <summary>展示名（中文文案——展示端重建事件文本用）。</summary>
    public readonly string Name;

    public EventTypeDef(string key, string name)
    {
        Key = key;
        Name = name;
    }
}

/// <summary>事件类型注册表（静态初始化按声明序注册——索引确定性）。</summary>
public static class EventTypes
{
    private static readonly List<EventTypeDef> Defs = new();

    /// <summary>全部定义（诊断/展示端遍历）。</summary>
    public static IReadOnlyList<EventTypeDef> All => Defs;

    private static int Register(string key, string name)
    {
        Defs.Add(new EventTypeDef(key, name));
        return Defs.Count - 1;
    }

    // ── 事件类型（追加式；新类型在此追加，勿插入中间）──
    public static readonly int FarmStart = Register("farm_start", "文明纪元");          // 首转农
    public static readonly int StateEmerge = Register("state_emerge", "国家涌现");      // 三条件达成
    public static readonly int StateGone = Register("state_gone", "国家崩溃");          // 国家消失/被吞并
    public static readonly int WarDeclared = Register("war_declared", "宣战");          // A 对 B 宣战
    public static readonly int WarAnnex = Register("war_annex", "吞并");                // 战败国并入
    public static readonly int WarTribute = Register("war_tribute", "朝贡");            // 朝贡开始
    public static readonly int WarPeace = Register("war_peace", "停战");                // 停战/朝贡终止
    public static readonly int Invention = Register("invention", "发明");               // 政体首次发明科技
    public static readonly int TechSpread = Register("tech_spread", "科技传入");        // 科技传入政体（首见）
    public static readonly int Split = Register("split", "分裂");                       // 政体分裂出新实体
    public static readonly int PolityDied = Register("polity_died", "政权覆灭");        // 政体灭亡
    public static readonly int HabUpscale = Register("hab_upscale", "聚落升级");        // camp→settlement/职能晋级

    /// <summary>类型名（展示端用；越界 → "未知"。</summary>
    public static string NameOf(int typeIndex) =>
        typeIndex >= 0 && typeIndex < Defs.Count ? Defs[typeIndex].Name : "未知";

    /// <summary>类型 key（诊断/测试用；越界 → ""。)</summary>
    public static string KeyOf(int typeIndex) =>
        typeIndex >= 0 && typeIndex < Defs.Count ? Defs[typeIndex].Key : "";
}