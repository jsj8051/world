using System.Collections.Generic;

namespace World.Gameplay;

/// <summary>
/// 玩家会话（第二阶段——"实际游玩"：玩家绑定一个国家并操纵它，EU4 式）。
/// 纯逻辑状态（不依赖 Godot 节点）；UI 后补（后续 PlayerPanel/StateSelectOverlay 读写本会话）。
/// 挂载点：CivSimContext.Player（null = 纯自动演化模式——既有确定性行为不变）。
/// 接线：
/// ① 玩家命令队列 → PlayerCommands.ApplyPending 每 tick 开头注入（CivEngine.Continue 循环内）
/// ② 税率覆盖 → StateMechanism 国库税收用 Player.TaxRateOverride（若 ≥0）
/// ③ 入档：.cmp PLAY 段（StateId/税率覆盖/待处理队列）——读档续玩不丢玩家状态
/// </summary>
public sealed class PlayerSession
{
    /// <summary>玩家绑定的国家 Id（-1 = 未绑定；绑定 = 玩家"扮演"该国的制度主管——君主是虚拟头衔，玩家可视为凌驾制度之上）。</summary>
    public int StateId = -1;

    /// <summary>玩家税率覆盖（-1 = 未设置，用国家默认 StateTaxPerCap；≥0 = 玩家税率，持续生效）。</summary>
    public float TaxRateOverride = -1f;

    /// <summary>待处理命令队列（入档；按队列序应用，读档续玩不丢）。</summary>
    public List<PlayerCommand> Queue = new();

    public bool IsBound => StateId >= 0;
}