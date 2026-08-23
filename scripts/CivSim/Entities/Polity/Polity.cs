using System;
using System.Collections.Generic;

using World.CivSim;

namespace World.CivSim.Entities;

/// <summary>
/// 时代 = 反应性统计标签（只显示/统计，不驱动机制）。
/// 判定：生产方式=农（IsFarming）→ 新石器；否则旧石器。无硬切换。
/// </summary>
public enum EpochKind
{
    StoneAge = 0,   // 旧石器：狩猎采集
    Neolithic = 1,  // 新石器：农业（实体异步进入，金字塔分布）
}

/// <summary>
/// 社会单元实体（Polity，v4 纯实体模型；唯一社会实体——部落/酋邦/国家均为派生概念）。
/// 身份 = 人口份额，不是实体标签：文化/文化群/宗教都是人口上的分布场（top-2 存储，Σ=1）。
/// 文化/文化群用字符串 key 标识（与科技一致：存档/诊断可读，如 "cult_3"）；宗教为固定 5 段份额。
/// 分裂时份额等比例继承（人口分走，身份随人口走）；合并按人口加权融合。
///
/// ⚠️ 概念 = 机制组合（2026-08-23 拍板）：本类按概念层拆 partial 分区文件——
///    Polity.Core.cs（本文件：人口/迁徙/分裂/份额场）
///    Polity.Chiefdom.cs（酋邦层：声望/贡赋/继承窗口/政治归属）
///    Polity.State.cs（国家层 + 军事征服痕迹）
///    实体纯数据（贫血模型——P6 拍板）：行为全在 Mechanics/ 原子机制积木。
/// </summary>
public partial class Polity
{
    public int Id;
    public int Cell;                  // 所在格（band 领地 = 1 格）
    public float P;                   // 人口
    public HashSet<string> TechKeys = new();   // 已获科技 key 集合（字符串可读，非位掩码）
    public int OriginCell;
    public int BornTick;
    public int LastMigrateTick = -1;   // 最近迁移 tick（迁移冷却；入档——读档续跑无分叉）
    public int LastSplitTick = -1;     // 最近分裂 tick（分裂冷却；入档）
    public int LastConflictTick = -1;  // 最近冲突 tick（冲突冷却；入档——2026-08-10 冲突机制）
    public bool Dead;
    public bool IsFarming;            // 生产方式（入档——读档续跑滞回无分叉）

    // ── 领地派生状态（TerritoryModel 凝聚重算填充；不存档——从实体表确定性重算，读档后重建）──
    public int TerritoryId = -1;     // 领地 id = 分量内最小实体 Id（连通分量标号，确定性）
    public int TerritorySize = 1;    // 领地内 band 数（≥2 = 正式领地，触发加成）

    // ── 能力位图缓存（CapabilityTable.MaskOf；RefreshCellState 每 tick；不存档——从科技/状态确定性重算）──
    public uint CapMask;

    // ── 商品随身池（2026-08-18 阶段3：动态商品目录 CommodityTable；2026-08-19 聚落设计改语义）──
    //   ⚠️ v12 存档字段 Stocks 保留同名但语义改为**随身携带**（容量 CarryFoodCap/CarryMatCap×P——
    //   游群即随身；定居部落也随身基础量）。**正式存储（粮仓）迁到聚落实体**（Settlement.Stocks，
    //   用户拍板"存粮迁移到聚落"）——人走粮留。旧 v12 档 Stocks 读入 = 随身池。
    public float[] Stocks = CommodityTable.NewStocks();   // 随身池（索引 = CommodityTable.Index(id)；Food 类被人口消耗）

    // ── 聚落关联（2026-08-19 阶段3 聚落设计；v13 入档）──
    public int SettledSince = -1;   // 当前农业定居起点 tick（-1=游动中/未定居；迁徙重置）；v12 旧档默认 -1
    public int PlaceId = -1;        // 占据聚落 Id（-1=无）；SettlementModel 形成/接管时赋值

    // ── 生产方式 F 分量（派生缓存：RefreshCellState 每 tick；不存档——货物分解用）──
    public float FHuntLast, FHerdLast, FFarmLast;   // 各方式当 tick 产出
    public float FBerryLast;                        // 当 tick 浆果采集（采集拆分 2026-08-17；猎物 = FHuntLast−FBerryLast）

    // ── 身份份额场（Σ=1，255 归一）──
    public ShareEntry[] CultureShare = NewEmpty();        // top-2：{key,份额}×2（具体文化，快）
    public ShareEntry[] CultureGroupShare = NewEmpty();   // top-2：{key,份额}×2（文化群，慢）
    public ShareEntry[] ReligionShare = ShareField.NewReligion(ReligionStage.Animism);   // 宗教类型：固定 5 段 key（机制层）
    public ShareEntry[] ReligionCultShare = NewEmpty();   // 具体宗教派别：top-2 动态 key "relig_N"（身份层，同文化群规则）

    // ── 运行时缓存（不存档，RefreshCellState 每 tick 重算）──
    public float EPerCap;    // 人均能量 e = Y/P
    public float Surplus;    // 盈余 s = e − 1
    public float CarryMult = 0f;   // 工具乘数链缓存（0=未算，FHunt fallback 实时算；两层模型 2026-08-17）
    public float FLast;      // 当 tick 实际产出 F_i 缓存（增长/核算直读，避免重复算）

    public EpochKind Epoch => IsFarming ? EpochKind.Neolithic : EpochKind.StoneAge;

    internal static ShareEntry[] NewEmpty() => new[] { new ShareEntry(), new ShareEntry() };
}
