namespace World.Gameplay;

/// <summary>
/// 玩家命令类型（第二阶段"实际游玩"——EU4 式，2026-08-25 用户拍板：游玩 ≠ 演化，是活世界操作）。
/// 命令 = 玩家的意志表达，经命令队列注入演化 tick（PlayerCommands.ApplyPending——每 tick 开头 drain）。
/// 确定性：命令按队列序应用（固定序），命令序列入档（.cmp PLAY 段）→ 读档续玩不丢命令。
/// </summary>
public enum PlayerCommandKind : byte
{
    /// <summary>调整税率（Value = 新税率；持续生效直到再次调整——覆盖国家默认 StateTaxPerCap）。</summary>
    SetTaxRate = 1,

    /// <summary>国库出资提升稳定度（花 BoostStabilityCost 国库 → +BoostStabilityGain 稳定；国库不足则作废）。</summary>
    BoostStability = 2,

    /// <summary>宣战（TargetA = 玩家国/宣战方，TargetB = 目标国；受 CanDeclare 门槛约束：冷却/未交战/领地接触/贡赋池足——玩家意志但仍需条件，EU4 无理由开战外交惩罚）。</summary>
    DeclareWar = 3,
}

/// <summary>一条玩家命令（入档字段；IssuedTick 供诊断/事件追溯）。</summary>
public sealed class PlayerCommand
{
    public PlayerCommandKind Kind;
    public int TargetA;    // SetTax/Boost：玩家国 Id；DeclareWar：宣战方 Id
    public int TargetB;    // DeclareWar：目标国 Id（其余 = 0）
    public float Value;    // SetTaxRate：税率；其余 = 0
    public int IssuedTick; // 下达 tick
}