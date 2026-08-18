using Godot;
using World.HexPlanet;

namespace World.MapView;

/// <summary>图层分类（用户拍板 2026-08：图层分 地理/气候/人文 三类；切换分类不改当前图层）。</summary>
public enum LayerCategory { Geo, Climate, Human }

/// <summary>地图图层策略（2026-08-21 策略模式重构 M2）：每图层一个策略类，
/// 颜色/图例/覆盖层/月份回调全部内聚在本类；MapViewer 只做编排（上下文/导演）。
/// 新增图层 = 新建策略类 + LayerRegistry 注册一行，不碰 MapViewer。
/// 生命周期：Precompute（可选，构建期）→ ColorOf（每格取色）/ BuildOverlay（3D 覆盖层）
/// → BuildLegend（图例条目）/ OnMonthChanged（月份滑块）。</summary>
public abstract class MapLayer
{
    public abstract int Id { get; }
    public abstract string Name { get; }
    public abstract LayerCategory Category { get; }

    /// <summary>按钮 SVG 图标（纯直线 M/L/H/V/Z——thorvg 不支持 Q/T/A 曲线；M4 启用）。</summary>
    public virtual string IconSvg => null;

    /// <summary>有 3D 覆盖层（风场/洋流/河流——颜色浅色底 + 独立网格）。</summary>
    public virtual bool HasOverlay => false;

    /// <summary>覆盖层节点（MapViewer 挂载/切换可见性；策略自持——构建一次常驻，切图层只切 Visible）。
    /// M3 接入：EnsureOverlayFor 懒建 + 切图层 Visible 跟随当前层。</summary>
    public Node3D OverlayNode { get; set; }

    /// <summary>吃月份滑块（风场/月降水/月温度——滑块可见性 + 月份切换回调）。</summary>
    public virtual bool UsesMonth => false;

    /// <summary>独立势力层用带边界 A 通道构建颜色（势力色块描边）。</summary>
    public virtual bool NeedsPowerBorders => false;

    /// <summary>可选的逐层预计算（构建期调用；默认无）。</summary>
    public virtual void Precompute(LayerContext ctx) { }

    /// <summary>每格取色（原 MakeColorFn switch 的分支体；查预计算缓存，零采样）。</summary>
    public abstract Color ColorOf(LayerContext ctx, HexTile tile);

    /// <summary>浅色底（风场/洋流/河流共用，原 MakeColorFn case 4/5/6 同块）：
    /// 湖格湖蓝（单色），海=SeaColor，其他淡沙色突出覆盖层。</summary>
    protected static Color PaleBase(LayerContext ctx, int id)
    {
        // ⚠️ 2026-08-02：湖泊 = 陆地盆地 + 水量≥阈值（RiverSystem 已过滤干湖）。
        //   湖格单色湖蓝（用户确认：单色、放河流图层）；其他格淡色底突出河道。
        if (ctx.Cache.TileLake[id] > 0)
            return new Color(0.25f, 0.45f, 0.75f);   // 湖蓝（单色）
        return ctx.IsSea(id) ? MapLayerColors.SeaColor : new Color(0.72f, 0.68f, 0.55f);
    }

    /// <summary>3D 覆盖层网格（原 BuildMonsoonArrows/BuildCurrentFlow/BuildRivers；null=无覆盖层）。
    /// 画法约定（2026-08-21 M4）：简单几何直接写本策略 BuildOverlay；
    /// 复杂算法抽成 Layers/ 下组件（如 CurrentFlow.cs）由策略引用——图层实现全部内聚在 Layers/。</summary>
    public virtual Node3D BuildOverlay(LayerContext ctx, MapViewer host) => null;

    /// <summary>月份滑块回调（原 Layer setter / 滑块 ValueChanged 的分支逻辑；M3 接入）。</summary>
    public virtual void OnMonthChanged(LayerContext ctx, int month) { }

    /// <summary>图例条目（原 RebuildLegend switch 的分支体；经 LegendBuilder 添加）。</summary>
    public virtual void BuildLegend(LegendBuilder b, LayerContext ctx) { }
}
