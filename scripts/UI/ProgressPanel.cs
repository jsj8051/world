using Godot;

namespace World.UI;

/// <summary>
/// 生成进度条组件（2026-08-31 拆分：原 MapViewer.Ui.cs 进度条块独立成组件）。
/// 四层模型：表现+容器在 scenes/ui/ProgressPanel.tscn（ProgressPanel/PBox + sb_hud_panel），
/// 本组件只做逻辑层——显示/隐藏/数值同步。数据由 MapViewer 下行注入（_progress/_phase 后台线程写）。
/// </summary>
public partial class ProgressPanel : PanelContainer
{
    private Label _label;        // 阶段文字（如 "预计算图层值  45%"）
    private ProgressBar _bar;    // 进度条（0-100）

    public override void _Ready()
    {
        _bar = GetNode<ProgressBar>("%ProgressBar");
        _label = GetNode<Label>("%ProgressLabel");
    }

    /// <summary>显示进度面板（生成开始；条归零）。⚠️ 隐藏继承的 CanvasItem.Show()——语义一致（显面板）。</summary>
    public new void Show()
    {
        Visible = true;
        _bar.Value = 0;
    }

    /// <summary>隐藏进度面板（生成完成/取消）。⚠️ 隐藏继承的 CanvasItem.Hide()——语义一致（隐面板）。</summary>
    public new void Hide() => Visible = false;

    /// <summary>每帧同步后台进度（只读派生；面板不可见时跳过——同旧 _Process 语义）。</summary>
    public void SetProgress(float progress, string phase)
    {
        if (!Visible) return;
        _bar.Value = progress * 100f;
        _label.Text = $"{phase}  {progress * 100f:F0}%";
    }
}