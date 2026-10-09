namespace World.CivSim.Entities;

/// <summary>
/// 国家档案实体（EU4 式通用国家机制，2026-08-25 用户拍板"先做一个通用的国家机制"）。
/// **制度层持久状态**：国家不是成员标记（StateId 每 tick 派生的"涌现标签"），而是有自身
/// 历史惯性的实体——国库累积、稳定度随事件涨落、合法性随君主更替。全部字段入档（.cmp STAT 段）。
///
/// 设计要点：
/// ① **实体性嫁接聚落网络**（阶段6 ⓪ P6 拍板延续）：国家中心 = 都城聚落（CapitalHabId，
///    场所比人长寿），不建独立表项——本实体只是"制度档案"，都城/成员仍是既有派生。
/// ② 君主 = 虚拟头衔（P7 推荐）：国家内 Prestige 最高成员，可替换；君主死 → 继位 → 国家不灭
///    （P9 推荐：制度化推举——官僚制不随君主死亡中断）。
/// ③ 确定性：全部机制无 Rng（Rng 只存在于战争结算），T04 读档续跑无分叉。
/// </summary>
public sealed class StateEntity
{
    /// <summary>国家 Id = 酋长 Polity Id（与 Polity.StateId 同源——国家身份锚定创建者）。</summary>
    public int Id;

    /// <summary>都城聚落 Id（制度载体——官职绑定场所；场所比人长寿，国库/稳定度不随君主死丢失）。</summary>
    public int CapitalHabId;

    /// <summary>君主 Polity Id（当前执政者；= 建档/继位时成员中 Prestige 最高者——虚拟头衔）。</summary>
    public int MonarchId;

    /// <summary>国库（每 tick 税收 − 官僚/军队维持费累积；负值 = 赤字 → 压稳定度）。</summary>
    public float Treasury;

    /// <summary>稳定度 [-3, +3]（EU4 口径：战争/继承窗口/财政危机 ↓，和平盈余缓慢回归 0；
    /// ≤ −2 崩盘 → 都城陷落 → 三条件断 → 国家消亡）。</summary>
    public float Stability;

    /// <summary>合法性 [0, 100]（新君初立低；向 50 温和回归；低合法 → 税收折损）。</summary>
    public float Legitimacy;

    /// <summary>建国 tick（制度档案创建时刻；国家涌现本身另有 StateDwellTicks 存续门槛）。</summary>
    public int BornTick;

    public StateEntity()
    {
        Stability = 0f;
        Legitimacy = CivSimContext.StateLegitimacyBase;
    }
}