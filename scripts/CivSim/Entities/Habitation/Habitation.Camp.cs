namespace World.CivSim.Entities;

/// <summary>
/// 营地形态分区（Habitation.Camp——camp 形态专属状态；2026-08-23 占位）。
/// camp = 旧石器 band 宿营地：随部落迁徙流动（Cell 跟随）、拆营走人（无废墟）、随身携带（无粮仓）、
/// 季节/迁徙节奏（无 Dwell×P 等级）。
/// ⚠️ 待 nomadic 概念（Herd + 季节转场 Migrate）落地时在此填状态：夏/冬营地格、季节相位等；
/// 实体骨架（核心分区）已就位——形态由占据者生产方式涌现（KindOf）。
/// </summary>
public partial class Habitation
{
    // （camp 形态专属字段——nomadic 概念落地时补充；现状无：camp 形态未激活）
}