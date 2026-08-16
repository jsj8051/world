namespace World.CivSim;

/// <summary>
/// 战争状态（外交关系，2026-08-19 阶段5 军事征服，docs/阶段5设计-军事征服.md；用户拍板 P3）。
/// 战争不是瞬时事件，而是两个国家之间的持续交战状态——
/// **存档 v14 新段**（过程状态不可从持久字段派生重建，读档必须恢复原样）。
/// 生命周期：宣战（交战）→ 会战累计胜场（WarBattleInterval 节奏）→
///   吞并（移除）/ 朝贡（TributeTo 模式，每 tick 转移贡赋）/ 停战（WarMaxTicks 超时移除）。
/// 确定性：全部字段入档；会战胜负走 DeterministicRandom——读档续跑无分叉（T04 覆盖）。
/// </summary>
public sealed class War
{
    public int StateIdA;          // 交战方 A（国家至尊酋长 Id）
    public int StateIdB;          // 交战方 B
    public int Defender = -1;     // 被宣战方（城墙防御加成归属——城市=要塞，P6；-1=未定）
    public int StartTick;         // 宣战 tick（WarMaxTicks 超时停战）
    public int WinsA;             // A 累计会战胜场（战果分档：吞并/朝贡）
    public int WinsB;
    public int LastBattleTick = -1;  // 最近会战 tick（WarBattleInterval 会战节奏）
    public int TributeTo = -1;    // 朝贡模式：战胜方 StateId（-1=交战中）；朝贡 = 战争已决出但关系延续
    public int TributeFrom = -1;  // 朝贡模式：战败方 StateId
    public int TributesLeft = 0;  // 剩余朝贡 tick 数（WarTributeTicks 递减，归零移除）

    /// <summary>是否朝贡期（战争已决出，仅剩贡赋转移）。</summary>
    public bool IsTribute => TributeTo >= 0;

    /// <summary>该国家是否交战方（交战或朝贡期都算敌对——贸易中断/冲突×2 依据）。</summary>
    public bool Involves(int stateId) => stateId == StateIdA || stateId == StateIdB;
}
