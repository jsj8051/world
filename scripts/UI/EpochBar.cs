using Godot;

using World.CivSim;
using World.Services;

namespace World.UI;

/// <summary>
/// 右上时间行组件（2026-08-31 拆分：原 MapViewer.Ui.cs 的 RefreshEpochBar + 月份滑块逻辑独立成组件）。
/// 四层模型：表现+容器在 MapViewer.tscn（EpochPanel/EpochRow/EpochLabel/YearLabel/MonthRow + sb_epoch_cap），
/// 本组件只做逻辑层——纪元/演化年文本刷新 + 月份滑块交互。
/// 数据：纪元 = _civCtx 只读派生（**永不写 CivSim**）；月份变更经 MonthChanged 信号上行，
/// MapViewer 订阅后更新 _ctx.Month 并调策略 OnMonthChanged（数据层归属 MapViewer，本组件不持有 _ctx）。
/// </summary>
public partial class EpochBar : PanelContainer
{
    /// <summary>月份变更信号（上行：滑块 1-12 → month 0-11；MapViewer 订阅处理策略/上下文）。</summary>
    [Signal] public delegate void MonthChangedEventHandler(int month);

    // ── 场景骨架（EpochPanel/EpochRow 下；EpochLabel/YearLabel 无唯一名——用相对路径）──
    private Label _epochLabel;        // 纪元徽记（◆ 旧石器/新石器/自然世界）
    private Label _yearLabel;         // 演化年
    private HSlider _monthSlider;     // 月份滑块（1-12；可见性 = 策略 UsesMonth 下行控制）
    private Label _monthLabel;        // 当前月份文本（"1 月"）

    // ── 内部状态 ──
    private int _month = 6;           // 当前月份 0-11（默认 7 月；与 MapViewer._month 初始一致）

    public override void _Ready()
    {
        _epochLabel = GetNodeOrNull<Label>("EpochRow/EpochLabel");
        _yearLabel = GetNodeOrNull<Label>("EpochRow/YearLabel");
        _monthSlider = GetNodeOrNull<HSlider>("%MonthSlider");
        _monthLabel = GetNodeOrNull<Label>("%MonthLabel");
        if (_epochLabel != null) _epochLabel.AddThemeColorOverride("font_color", SaveRowStyle.Fg);
        if (_yearLabel != null) _yearLabel.AddThemeColorOverride("font_color", SaveRowStyle.Fg);

        if (_monthSlider != null)
        {
            _monthSlider.Value = _month + 1;
            _monthSlider.ValueChanged += v =>
            {
                int m = (int)v - 1;
                if (m == _month) return;
                _month = m;
                if (_monthLabel != null) _monthLabel.Text = $"{m + 1} 月";
                EmitSignal(SignalName.MonthChanged, m);   // 上行：MapViewer 处理 _ctx.Month + 策略回调
            };
            _monthSlider.Visible = false;   // 默认隐藏，进季风/月降水图层才显示（SetMonthVisible 下行打开）
        }
    }

    /// <summary>下行：刷新右上时间（纪元 + 演化年——只读派生 _civCtx，不碰 CivSim）。
    /// null = 纯自然地图 → "◆ 自然世界"。</summary>
    public void Refresh(CivSimContext civCtx)
    {
        if (_epochLabel == null || _yearLabel == null) return;
        if (civCtx == null)
        {
            _epochLabel.Text = "◆ 自然世界";
            _yearLabel.Text = "";
            return;
        }
        bool farm = false;
        foreach (var e in civCtx.Polities)
            if (!e.Dead && e.IsFarming) { farm = true; break; }
        _epochLabel.Text = farm ? "◆ 新石器" : "◆ 旧石器";
        _yearLabel.Text = $"演化 {(civCtx.Tick * 100L):N0} 年";
    }

    /// <summary>下行：月份滑块可见性（位置 = 策略 UsesMonth；MapViewer.Layer setter 调用）。</summary>
    public void SetMonthVisible(bool visible)
    {
        if (_monthSlider != null) _monthSlider.Visible = visible;
    }
}