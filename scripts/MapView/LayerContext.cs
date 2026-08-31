using Godot;
using System.Collections.Generic;
using World.CivSim;
using World.HexPlanet;
using World.MapGen;

using World.CivSim.Entities;
namespace World.MapView;
/// <summary>图层策略上下文（2026-08-21 策略模式重构 M1）：策略类访问数据的唯一通道——
/// 不直接依赖 MapViewer 本体（颜色/图例/覆盖层方法经此取数）。M1 先收数据字段；回调 M3 接入。</summary>
public sealed class LayerContext
{
    public MapData Map;              // 存档数据（场数据源）
    public List<HexTile> Tiles;      // 显示格拓扑（Goldberg dual）
    public TileIndex TileIndex;      // 显示格↔逻辑格映射（2026-08-19 收敛）
    public CivSimContext CivCtx;     // 文明演化上下文（null=纯自然地图）
    public TileDataCache Cache;      // 每格图层值缓存
    public float RadiusKm;           // 星球半径（存档口径）
    public int Month;                // 当前月份 0-11

    /// <summary>选国形态选中政权 Id（-1=未选中；政体图层压暗/选国高亮用；NationSelectMenu 写、策略读）。</summary>
    public int SelectionId = -1;

    /// <summary>季风月风场 [12][n]（异步重算；就绪前 null——MapViewer.ApplyMonthWind 后同步本引用）。</summary>
    public Vector3[][] MonthWind;

    /// <summary>请求当前图层覆盖层重建（月份切换等；由 MapViewer 填充）。</summary>
    public System.Action RequestOverlayRebuild;

    /// <summary>请求重算颜色（月降水/月温度刷新缓存后；由 MapViewer 填充）。</summary>
    public System.Action RequestRecolor;

    /// <summary>显示海陆判定（2026-08-17）：视觉海（byte 量化 elev&lt;hSea）且逻辑非陆地
    /// （R≤0 或无 civ）才判海；近海格（elev&lt;hSea 但 R>0 逻辑可居）显示陆地/数据色——
    /// 人口点不落在"视觉海水"上（byte 量化误差——R>0 是模拟权威）。
    /// ⚠️ 2026-08-18：R 是逻辑格（顶点）数组——id 是显示格——按 FaceToVertex 查。</summary>
    public bool IsSea(int id)
        => Cache.TileElev[id] < Cache.HSea && (CivCtx?.R == null || CivCtx.R[TileIndex.FaceToVertex(id)] <= 0f);

    /// <summary>文化/宗教派别 → 语言群 映射（图例族系取色用；惰性建一次——实体表只读）。</summary>
    public void EnsureIdentityCaches()
    {
        if (Cache.CultGroup != null || CivCtx == null) return;
        Cache.CultGroup = new Dictionary<int, int>();
        Cache.SectGroup = new Dictionary<int, int>();
        foreach (var e in CivCtx.Polities)
        {
            if (e.Dead) continue;
            int c = ShareField.KeyHash(ShareField.DomKey(e.CultureShare));
            int r = ShareField.KeyHash(ShareField.DomKey(e.ReligionCultShare));
            int g = ShareField.KeyHash(ShareField.DomKey(e.CultureGroupShare));
            if (c != 0 && !Cache.CultGroup.ContainsKey(c)) Cache.CultGroup[c] = g;
            if (r != 0 && !Cache.SectGroup.ContainsKey(r)) Cache.SectGroup[r] = g;
        }
    }

    /// <summary>刷新当月温度缓存（月温度图层用；月份切换时调用——原 MapViewer.RefreshMonthTemp）。</summary>
    public void RefreshMonthTemp()
    {
        if (Cache.TileMonthTemp == null || Map == null || Map.MonthTemp == null) return;
        int n = Cache.TileMonthTemp.Length;
        var arr = Map.MonthTemp[Month];
        for (int i = 0; i < n; i++)
            Cache.TileMonthTemp[i] = arr != null ? arr[TileIndex.FaceToVertex(i)] : (byte)0;
    }

    /// <summary>刷新当月降水缓存（月降水图层用；月份切换时调用——原 MapViewer.RefreshMonthPrecip）。
    /// ⚠️ 2026-08-16：自适应色带——当月陆地月降水 min/max（用户拍板：最低到最高归一化）。</summary>
    public void RefreshMonthPrecip()
    {
        if (Cache.TileMonthPrecip == null || Map == null || Map.MonthPrecip == null) return;
        int n = Cache.TileMonthPrecip.Length;
        var arr = Map.MonthPrecip[Month];
        for (int i = 0; i < n; i++)
            Cache.TileMonthPrecip[i] = arr != null ? arr[TileIndex.FaceToVertex(i)] : (byte)0;
        Cache.MonthPrecipMin = float.MaxValue;
        Cache.MonthPrecipMax = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (IsSea(i)) continue;   // ⚠️ 2026-08-17：统一海陆判定（只统计陆地格）
            float mm = FieldCodec.ByteMonthPrecipToMm(Cache.TileMonthPrecip[i], Cache.TilePrecip[i]) * 12f;   // 等效年尺度（比例×年降水×12）
            Cache.MonthPrecipMin = Mathf.Min(Cache.MonthPrecipMin, mm);
            Cache.MonthPrecipMax = Mathf.Max(Cache.MonthPrecipMax, mm);
        }
        if (Cache.MonthPrecipMax <= Cache.MonthPrecipMin) Cache.MonthPrecipMax = Cache.MonthPrecipMin + 1f;
    }
}
