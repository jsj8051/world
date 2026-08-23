namespace World.CivSim.Entities;

// ══════════════════════════════════════════════════════════════════
// Band 国家层分区（概念 = 机制组合 2026-08-23 拍板 P3：partial 分文件）。
// 本文件：国家涌现 + 军事征服冷却痕迹——国家配方选用的机制状态字段。
// 概念定义见 桌面 概念机制组合设计.html ③ 配方单：国家 = 酋邦常用 + Tax/Capital/
// InheritRule/Admin/Army/War/Order。字段均为纯派生（不存档，重建）或冷却痕迹（入档）。
// ══════════════════════════════════════════════════════════════════
public partial class Band
{
    // ── 国家层（2026-08-16 阶段4 国家涌现，docs/阶段4设计-国家涌现.md）──
    // 纯派生（不存档，同 ChiefdomId 模式）：从酋邦+聚落+贡赋等已入档持久字段确定性重建。
    // 国家 = 酋邦的制度化（官僚/税制/继承规则/强制力垄断），无规模阈值——性质跃迁非体积达标。
    public int StateId = -1;          // 所属国家 id = 至尊酋长 Id（-1=非国家；StateModel 重建）
    public int StateSize = 1;         // 国家内部落数（≥2 = 正式国家；StateModel 重建）

    // ── 阶段5 军事征服的参战冷却（2026-08-19，docs/阶段5设计-军事征服.md）──
    // 战争是外交状态（War 段入档）；LastWarTick 是战争的**冷却痕迹**（v14 入档）：
    public int LastWarTick = -1;      // 最近参战 tick（宣战/被宣战冷却——WarCooldownTicks 内不参战）
}
