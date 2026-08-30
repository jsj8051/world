using Godot;

using World.MapView;

namespace World.UI;

/// <summary>
/// 图例面板组件（2026-08-31 拆分：原 MapViewer.Ui.cs RebuildLegend 独立成组件）。
/// 四层模型：表现+容器在 MapViewer.tscn（UiLayer/LegendPanel 外壳 + sb_hud_panel），
/// 本组件只做逻辑层——条目重建（数据驱动，策略 BuildLegend）+ 高度自适应 + 滚轮独占。
/// 数据：图例内容由当前图层策略 BuildLegend 提供（LandRegistry.Of(_layer) 驱动），
/// 经 SetContext/Rebuild 下行注入，本组件不碰游戏状态。
/// </summary>
public partial class LegendPanel : PanelContainer
{
    private Label _title;          // 图例标题（图层名）
    private VBoxContainer _box;    // 图例条目容器（ScrollContainer 内）
    private VBoxContainer _footer; // 图例说明文字区（滚动区外，常驻面板底部——2026-08-17 用户拍板）

    /// <summary>当前图层上下文（null=构建前/未就绪；只读派生，不写）。</summary>
    private LayerContext _ctx;

    public override void _Ready()
    {
        _title = GetNode<Label>("%LegendTitle");
        _box = GetNode<VBoxContainer>("%LegendBox");
        _footer = GetNode<VBoxContainer>("%LegendFooter");
        // ⚠️ 2026-08-17：图例区滚轮只滚图例——ScrollContainer 滚到底不消费事件 → 穿透到
        //   3D 相机 _UnhandledInput → 地图缩放（用户报"滚动到底后再滚会导致地图缩放"）。
        //   在内容区（scroll+footer+标题）统一消费滚轮：滚动正常，滚到底/在说明文字上都不穿透。
        var legendVBox = GetChild<VBoxContainer>(0);
        legendVBox.GuiInput += (e) =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed
                && (mb.ButtonIndex == MouseButton.WheelUp || mb.ButtonIndex == MouseButton.WheelDown))
                legendVBox.AcceptEvent();   // Control.AcceptEvent（C# 里 InputEvent 无此方法）
        };
    }

    /// <summary>设置图层上下文（MapViewer 构建完成后注入；null=未就绪）。</summary>
    public void SetContext(LayerContext ctx) => _ctx = ctx;

    /// <summary>重建图例（当前图层颜色说明；内容超出固定面板 → ScrollContainer 滚动）。</summary>
    public void Rebuild(MapLayer strat)
    {
        if (_box == null) return;   // UI 未建（_Ready 前）或已释放
        ClearEntries(_box);
        ClearEntries(_footer);

        // 2026-08-21 M3 策略化：图例条目由当前层策略 BuildLegend 提供（原 20 分支 switch 删除）
        _title.Text = strat.Name;
        var builder = new LegendBuilder(_box, _footer);
        if (_ctx != null)
            strat.BuildLegend(builder, _ctx);
        else
            builder.Text("（生成中…）");   // ⚠️ M1 回归防护：构建前 _ctx/_cache 未就绪（原 case 13/16 的 NRE 隐患统一在此挡）

        // ⚠️ 2026-08-17 用户拍板：图例数量不足时面板高度自适应缩短（上限 250，贴底锚定）。
        //   内容高 = 色块行 min 高 + 行间隙；footer 常驻文字也计入；clamp [120, 250]。
        float contentH = 0f;
        for (int i = 0; i < _box.GetChildCount(); i++)
            if (_box.GetChild(i) is Control cc) contentH += cc.GetCombinedMinimumSize().Y;
        contentH += Mathf.Max(0, _box.GetChildCount() - 1) * 3;
        float footH = 0f;
        for (int i = 0; i < _footer.GetChildCount(); i++)
            if (_footer.GetChild(i) is Control cc) footH += cc.GetCombinedMinimumSize().Y;
        footH += Mathf.Max(0, _footer.GetChildCount() - 1) * 2;
        float panelH = Mathf.Clamp(26 + 4 + contentH + 4 + footH + 12, 120f, 250f);
        CustomMinimumSize = new Vector2(236, panelH);
        // 贴底：BottomRight 锚点下 OffsetTop = -高（已入树必须用 Offset，Position setter 会飞屏）
        OffsetTop = -panelH;
    }

    /// <summary>清空容器条目（RemoveChild 立即脱离树 + QueueFree 帧末释放——纯 QueueFree 会残留到帧末）。</summary>
    private static void ClearEntries(VBoxContainer box)
    {
        foreach (Node c in box.GetChildren())
        {
            box.RemoveChild(c);
            c.QueueFree();
        }
    }
}