using System;
using System.Collections.Generic;
using World.CivSim.Entities;
using World.CivSim.Events;
using World.CivSim.Policies;

namespace World.CivSim.Observation;

/// <summary>
/// 观测投影工厂（2026-08-24，docs/设计-观测面板与文明记录.md ①投影层）。
/// `Observe(ctx)` 纯函数：把 CivSimContext 组装为只读快照 CivSnapshot——**所有字段读取集中在此一个文件**。
/// 设计红线：
///   · 纯函数、无 Rng、不改 Context——可单测、可缓存、可离线（未来玩家介入层同接口）；
///   · 概念标签与策略族单一事实源同判据（state = WarPolicies.Of 同式：IsChief && StateId==Id && Size≥2）；
///   · 防御读取（TerritoryCells 未建/长度不足 → 0），永不抛异常——面板永不因数据缺失崩溃。
/// </summary>
public static class CivOverlay
{
    /// <summary>组装快照（人口/计数 → 政体列表(声望降序) → 国家卡片 → 科技卷轴）。</summary>
    public static CivSnapshot Observe(CivSimContext ctx)
    {
        var snap = new CivSnapshot();
        if (ctx == null) return snap;

        snap.Tick = ctx.Tick;
        snap.TotalPop = (long)Math.Round(ctx.TotalPopulation());
        snap.HabitationCount = ctx.Habitations?.Count ?? 0;
        snap.WarCount = ctx.Wars?.Count ?? 0;

        var polities = ctx.Polities;
        if (polities != null)
        {
            // ── 政体列表（仅存活；声望降序——大人物/酋长/国家靠前）──
            var rows = new List<PolityRow>(polities.Count);
            foreach (var e in polities)
            {
                if (e == null || e.Dead) continue;
                rows.Add(MakePolityRow(ctx, e));
            }
            rows.Sort((a, b) => b.Prestige.CompareTo(a.Prestige));
            snap.Polities = rows;
            snap.PolityCount = rows.Count;

            // ── 国家卡片（至尊酋长集合——WarPolicies.Of 同判据）──
            var states = new List<StateRow>();
            foreach (var e in polities)
            {
                if (e == null || e.Dead) continue;
                if (!WarPolicies.Of(e).CanDeclareWar(e)) continue;
                states.Add(MakeStateRow(ctx, e));
            }
            states.Sort((a, b) => b.MemberCount.CompareTo(a.MemberCount));
            snap.States = states;
            snap.StateCount = states.Count;

            // ── 酋邦计数（正式酋邦：内部落数 ≥ 2）──
            snap.ChiefdomCount = CountChiefdoms(ctx);
        }

        // ── 科技卷轴（techs.csv 全表 + 持有者计数）──
        snap.Techs = BuildTechRows(polities);

        // ── 文明事件流（EventTypes 展示文本派生集中在投影层——面板零格式化逻辑）──
        if (ctx.Events != null)
        {
            var evRows = new List<EventRow>(ctx.Events.Count);
            foreach (var e in ctx.Events)
                evRows.Add(new EventRow { Tick = e.Tick, TypeIndex = e.TypeIndex, Text = FormatEvent(ctx, e) });
            snap.Events = evRows;
        }
        return snap;
    }

    /// <summary>事件展示文本（派生——记录是数据，文案在此；改文案不动数据不 bump 版本）。</summary>
    private static string FormatEvent(CivSimContext ctx, CivEventRecord e)
    {
        return e.TypeIndex switch
        {
            var t when t == EventTypes.FarmStart => "首个政权转农——文明纪元开始",
            var t when t == EventTypes.StateEmerge => $"国家 #{e.SubjectId} 涌现（都城 + 贡赋池 + 存续）",
            var t when t == EventTypes.StateGone => $"国家 #{e.SubjectId} 崩溃",
            var t when t == EventTypes.WarDeclared => $"国家 #{e.SubjectId} 向国家 #{e.TargetId} 宣战",
            var t when t == EventTypes.WarAnnex => $"国家 #{e.SubjectId} 吞并国家 #{e.TargetId}",
            var t when t == EventTypes.WarTribute => $"国家 #{e.SubjectId} 令国家 #{e.TargetId} 朝贡",
            var t when t == EventTypes.WarPeace => $"战争终结：国家 #{e.SubjectId} × 国家 #{e.TargetId}",
            var t when t == EventTypes.Invention => $"政体 #{e.SubjectId} 发明了 {TechName((int)e.Value)}",
            var t when t == EventTypes.TechSpread => $"{TechName((int)e.Value)} 传入政体 #{e.TargetId}（自 #{e.SubjectId}）",
            var t when t == EventTypes.Split => $"政体 #{e.SubjectId} 分裂 → 新政权 #{e.TargetId}",
            var t when t == EventTypes.PolityDied =>
                e.TargetId >= 0 ? $"政体 #{e.SubjectId} 覆灭（并入 #{e.TargetId}）" : $"政体 #{e.SubjectId} 覆灭",
            var t when t == EventTypes.HabUpscale => $"政体 #{e.SubjectId} 建立聚落 #{e.TargetId}",
            _ => $"#{e.SubjectId} · 事件 {EventTypes.NameOf(e.TypeIndex)}",
        };
    }

    /// <summary>科技名（Value 编码 TechTable.All 索引；表未加载/越界 → key 回退）。</summary>
    private static string TechName(int index)
    {
        var all = TechTable.All;
        if (all != null && index >= 0 && index < all.Count && all[index] != null)
            return all[index].Name;
        return $"科技[{index}]";
    }

    /// <summary>政体行组装（概念标签 + 领地格防御读取）。</summary>
    private static PolityRow MakePolityRow(CivSimContext ctx, Polity e)
    {
        return new PolityRow
        {
            Id = e.Id,
            Concept = ConceptOf(ctx, e),
            Pop = e.P,
            IsFarming = e.IsFarming,
            TerritoryCells = TerritoryCellsOf(ctx, e.Id),
            TechCount = e.TechKeys?.Count ?? 0,
            Prestige = e.Prestige,
            ChiefdomId = e.ChiefdomId,
            StateId = e.StateId,
            PlaceId = e.PlaceId,
            CultureGroup = DominantKey(e.CultureGroupShare),
            IsChief = e.IsChief,
        };
    }

    /// <summary>国家卡片组装：都城 = 至尊酋长占据聚落；君主 = 成员中声望最高者（虚拟头衔）。</summary>
    private static StateRow MakeStateRow(CivSimContext ctx, Polity chief)
    {
        int monarchId = chief.Id;
        var members = MembersOf(ctx, chief.Id);
        if (members != null)
        {
            float best = -1f;
            foreach (int mid in members)
            {
                var m = FindById(ctx, mid);
                if (m == null || m.Dead) continue;
                if (m.Prestige > best) { best = m.Prestige; monarchId = m.Id; }
            }
        }
        var capital = ctx.HabitationOf(chief);   // 至尊酋长占据聚落 = 都城（制度载体）
        return new StateRow
        {
            Id = chief.Id,
            CapitalPlaceId = capital?.Id ?? -1,
            MonarchId = monarchId,
            Pool = chief.Contributed,
            MemberCount = members?.Count ?? 0,
            TechCount = chief.TechKeys?.Count ?? 0,
            Prestige = chief.Prestige,
            CultureGroup = DominantKey(chief.CultureGroupShare),
            IsAtWar = IsAtWar(ctx, chief.Id),
        };
    }

    /// <summary>概念阶段标签（派生——与机制层同判据，唯一事实源）：
    /// state = 至尊酋长且正式国家（WarPolicies 同式）；chiefdom = 正式酋邦成员/酋长；tribe = 务农；其余 band。</summary>
    private static string ConceptOf(CivSimContext ctx, Polity e)
    {
        if (e.IsChief && e.StateId == e.Id && e.StateSize >= 2) return "state";
        if (e.ChiefdomId >= 0 && e.ChiefdomSize >= 2) return "chiefdom";
        return e.IsFarming ? "tribe" : "band";
    }

    /// <summary>正式酋邦计数：至尊酋长（或分量首领）且内部落数 ≥ 2——遍历去重（ChiefdomId 可能重复）。</summary>
    private static int CountChiefdoms(CivSimContext ctx)
    {
        var seen = new HashSet<int>();
        foreach (var e in ctx.Polities)
        {
            if (e == null || e.Dead || e.ChiefdomId < 0 || e.ChiefdomSize < 2) continue;
            seen.Add(e.ChiefdomId);
        }
        return seen.Count;
    }

    /// <summary>领地格数（TerritoryCells 未建/越界 → 0）。</summary>
    private static int TerritoryCellsOf(CivSimContext ctx, int polityId)
    {
        var tc = ctx.TerritoryCells;
        if (tc == null || polityId < 0 || polityId >= tc.Length || tc[polityId] == null) return 0;
        return tc[polityId].Count;
    }

    /// <summary>进行中的战争是否涉及该国家。</summary>
    private static bool IsAtWar(CivSimContext ctx, int stateId)
    {
        var wars = ctx.Wars;
        if (wars == null || stateId < 0) return false;
        for (int i = 0; i < wars.Count; i++)
            if (wars[i].Involves(stateId)) return true;
        return false;
    }

    /// <summary>科技卷轴：techs.csv 全表 + 存活政体持有计数。</summary>
    private static List<TechRow> BuildTechRows(List<Polity> polities)
    {
        var rows = new List<TechRow>();
        var techs = TechTable.All;
        if (techs == null) return rows;
        foreach (var t in techs)
        {
            int holders = 0;
            if (polities != null)
            {
                foreach (var e in polities)
                {
                    if (e == null || e.Dead || e.TechKeys == null) continue;
                    if (e.TechKeys.Contains(t.Key)) holders++;
                }
            }
            rows.Add(new TechRow { Key = t.Key, Name = t.Name, Holders = holders });
        }
        return rows;
    }

    /// <summary>主导 key = 份额最大条目（WarAims.DominantKey 同式——跨文件复用避免重复实现；无 → 空串）。</summary>
    private static string DominantKey(ShareEntry[] share)
    {
        if (share == null || share.Length == 0) return "";
        ShareEntry best = share[0];
        for (int i = 1; i < share.Length; i++)
            if (share[i].Frac > best.Frac) best = share[i];
        return best.Frac > 0f ? best.Key : "";
    }

    /// <summary>成员表（ChiefdomCells——国家=酋邦，同 Id 语义；未建/越界 → null）。</summary>
    private static List<int> MembersOf(CivSimContext ctx, int id)
    {
        var cells = ctx.ChiefdomCells;
        if (cells == null || id < 0 || id >= cells.Length) return null;
        return cells[id];
    }

    /// <summary>按 Id 查存活实体（线性——政府体规模小；与 WarAims.FindById 同式）。</summary>
    private static Polity FindById(CivSimContext ctx, int id)
    {
        var polities = ctx.Polities;
        for (int i = 0; i < polities.Count; i++)
            if (polities[i].Id == id && !polities[i].Dead) return polities[i];
        return null;
    }
}