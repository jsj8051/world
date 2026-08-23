using System;
using System.Reflection;
using World.Services;
using World.Utils;

using World.CivSim.Entities;
namespace World.CivSim;

/// <summary>
/// 声明式存档字段清单（2026-08-18 阶段3 方案 D：消除"Write/Read 两处手写清单漏字段"缺陷）。
///
/// 设计要点：
/// 1. **单源真值**：Band 持久字段在此集中声明，Write/Read 由表驱动 → 不可能一处写一处漏。
/// 2. **布局不变**：字段顺序/大小与 .cmp 字节布局严格一致——清单是"中央可见的布局表"，
///    不是自动布局。每项含 SinceVer（版本引入号），Write 按当前版本过滤。
///    （2026-08-23 段表化：v15 起段表格式，SinceVer 语义保留作字段引入记录，但全字段恒写，
///    不再有版本兼容分支——旧档全删。）
/// 3. **反射校验**：Validate() 检查每项 Name 在 Band 上真实存在（字段改名后清单过期 → 测试红）。
/// 4. 派生字段（FLast/Territory/IsBigMan 等）**不入清单**——它们是 SettleDerived 重算的缓存，入档即冗余。
/// </summary>
public static class CivArchiveSchema
{
    /// <summary>字段定义：Name（反射校验）、SinceVer（该版本引入）、写入/读取委托。
    /// 顺序即 .cmp 字节顺序（勿重排——破坏兼容）。
    /// 2026-08-23 段表化：委托签名 FileAccess → ChunkWriter/ChunkReader（方法同名，lambda 体不变）。
    /// ⚠️ 2026-08-23 Phase 3（概念 = 机制组合）：v16 起按概念分组重排字节序（Core → Chiefdom → State，
    ///   与 Band partial 分区对齐）——旧 v15 档作废（用户拍板），读端拒绝 Older。</summary>
    public readonly record struct FieldDef(
        string Name,
        int SinceVer,
        Action<ChunkWriter, Band> Write,
        Func<ChunkReader, Band, bool> Read);

    // ══════════════════════════════════════════════════════════════════
    // Band 字段清单（v16 概念分组，2026-08-23 拍板重排）：
    //   Core 组（Band.cs：身份/人口/科技/冷却/商品/聚落关联/份额场）
    //   Chiefdom 组（Band.Chiefdom.cs：声望/贡赋/继承窗口/政治归属）
    //   State 组（Band.State.cs：国家/军事冷却）
    // 字节序 = 组序 + 组内顺序（与 .cmp/.mpa CIVI 段字节布局一致；读端仅接受 v16+）
    // ══════════════════════════════════════════════════════════════════
    public static readonly FieldDef[] BandFields =
    {
        // ── Core 组：身份与人口 ──
        new("Id", 4, (f, e) => f.Store32((uint)e.Id), (f, e) => { e.Id = (int)f.Get32(); return true; }),
        new("P", 4, (f, e) => f.StoreFloat(e.P), (f, e) => { e.P = f.GetFloat(); return true; }),
        new("IsFarming", 4, (f, e) => f.Store8((byte)(e.IsFarming ? 1 : 0)), (f, e) => { e.IsFarming = f.Get8() != 0; return true; }),
        // TechKeys：变长（keyCount + 定长 key×n）——特例，不走委托表（见 CivMapArchive 内联读写）
        new("TechKeys", 4, null, null),

        // ── Core 组：身份份额场（Σ=1，255 归一）──
        new("CultureShare", 4, (f, e) => CivMapArchive.StoreShare(f, e.CultureShare), (f, e) => { e.CultureShare = CivMapArchive.ReadShare(f); return true; }),
        new("CultureGroupShare", 4, (f, e) => CivMapArchive.StoreShare(f, e.CultureGroupShare), (f, e) => { e.CultureGroupShare = CivMapArchive.ReadShare(f); return true; }),
        new("ReligionShare", 4, CivMapArchive.StoreReligionShare, CivMapArchive.ReadReligionShare),
        new("ReligionCultShare", 4, (f, e) => CivMapArchive.StoreShare(f, e.ReligionCultShare), (f, e) => { e.ReligionCultShare = CivMapArchive.ReadShare(f); return true; }),

        // ── Core 组：空间/时间锚点 + 冷却痕迹 ──
        new("Cell", 4, (f, e) => f.Store32((uint)e.Cell), (f, e) => { e.Cell = (int)f.Get32(); return true; }),
        new("OriginCell", 4, (f, e) => f.Store32((uint)e.OriginCell), (f, e) => { e.OriginCell = (int)f.Get32(); return true; }),
        new("BornTick", 4, (f, e) => f.Store32((uint)e.BornTick), (f, e) => { e.BornTick = (int)f.Get32(); return true; }),
        new("LastMigrateTick", 8, (f, e) => f.Store32((uint)e.LastMigrateTick), (f, e) => { e.LastMigrateTick = (int)f.Get32(); return true; }),
        new("LastSplitTick", 8, (f, e) => f.Store32((uint)e.LastSplitTick), (f, e) => { e.LastSplitTick = (int)f.Get32(); return true; }),
        new("LastConflictTick", 8, (f, e) => f.Store32((uint)e.LastConflictTick), (f, e) => { e.LastConflictTick = (int)f.Get32(); return true; }),

        // ── Core 组：商品随身池（2026-08-18 阶段3：Goods[3] → Stocks[N] 动态商品目录，含食物）──
        //   v12 起每实体写 CommodityTable.Count 个 float（顺序 = CommodityTable.All）；
        //   旧档读分支 2026-08-23 已删（用户拍板旧档全删）——ReadStocks 按当前目录全量直读。
        new("Stocks", 12, CivMapArchive.StoreStocks, CivMapArchive.ReadStocks),

        // ── Core 组：聚落关联（v13 阶段3 聚落设计：定居起点 + 占据聚落 Id）──
        //   -1 按 uint 全 1 存储（同 LastMigrateTick 惯例——(int) 读回还原 -1）
        new("SettledSince", 13, (f, e) => f.Store32((uint)e.SettledSince), (f, e) => { e.SettledSince = (int)f.Get32(); return true; }),
        new("PlaceId", 13, (f, e) => f.Store32((uint)e.PlaceId), (f, e) => { e.PlaceId = (int)f.Get32(); return true; }),

        // ── Chiefdom 组：声望/贡赋/继承窗口/政治归属（v10 酋邦累积；IsBigMan/IsChief/ChiefdomId 为派生不入档）──
        new("Prestige", 10, (f, e) => f.StoreFloat(e.Prestige), (f, e) => { e.Prestige = f.GetFloat(); return true; }),
        new("Contributed", 10, (f, e) => f.StoreFloat(e.Contributed), (f, e) => { e.Contributed = f.GetFloat(); return true; }),
        new("SuccessionUntil", 10, (f, e) => f.Store32((uint)e.SuccessionUntil), (f, e) => { e.SuccessionUntil = (int)f.Get32(); return true; }),
        // 被征服效忠（v14 阶段5 军事征服；v16 起归入 Chiefdom 组——政治归属，与代码分区对齐）
        new("ConqueredBy", 14, (f, e) => f.Store32((uint)e.ConqueredBy), (f, e) => { e.ConqueredBy = (int)f.Get32(); return true; }),

        // ── State 组：国家/军事冷却（v14 阶段5；v16 起独立组）──
        new("LastWarTick", 14, (f, e) => f.Store32((uint)e.LastWarTick), (f, e) => { e.LastWarTick = (int)f.Get32(); return true; }),
    };

    /// <summary>反射校验：清单字段名必须在 Band 上真实存在（防字段改名/删除后清单过期静默失效）。
    /// 返回 false = 清单过期（写档调用方拒绝写入防静默漏字段）；LogErr 报具体字段名（定位清单过期点）。</summary>
    public static bool Validate()
    {
        foreach (var def in BandFields)
        {
            if (def.Write == null || def.Read == null) continue;   // 特例字段（TechKeys 内联读写）
            if (typeof(Band).GetField(def.Name, BindingFlags.Public | BindingFlags.Instance) == null)
            {
                LogService.LogErr("CivArchiveSchema", $"字段 '{def.Name}' 在 Band 上不存在——清单过期！");
                return false;
            }
        }
        return true;
    }
}
