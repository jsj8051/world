using Godot;

namespace World.UI;

/// <summary>
/// 带自定义文本的进度条（2026-08-23）：引擎自带百分比文本格式写死（"42%"），无 API 可改内容。
/// 本控件继承 ProgressBar 重写 <see cref="CanvasItem._Draw"/>，在进度条内自绘
/// 「阶段文字 + 百分比」合成文本（如「（生成中…板块模拟阶段）42%」），居中显示。
/// 用法：场景里挂本脚本到 ProgressBar 节点；代码设置 <see cref="Prefix"/>（如"（生成中…）"），
/// 百分比每帧由 ProgressBar.Value 驱动（值变化自动重绘）；ShowPercentage 需为 false（防止引擎再画一份）。
/// </summary>
public partial class ProgressTextBar : ProgressBar
{
    /// <summary>百分比前的阶段文字（如"（生成中…板块模拟阶段）"）。</summary>
    public string Prefix { get; set; } = "";

    public ProgressTextBar()
    {
        // 关引擎自带百分比：场景若未显式设置 show_percentage，默认 true 会再画一份 "53%"
        // 与自绘文本（已含百分比）在条内重叠（2026-08-23 用户反馈"数字和文字叠在一起"）。
        ShowPercentage = false;
    }

    public override void _Draw()
    {
        // 引擎已画背景和填充色（ProgressBar 内置绘制）；这里只补文本层
        Font font = GetThemeFont("font");
        int size = GetThemeFontSize("font_size");
        Color color = GetThemeColor("font_color");
        // 百分比在前、阶段文字在后（如「42%（生成中…板块模拟阶段）」）
        string text = $"{GetValue():0}%{Prefix}";
        Vector2 textSize = font.GetStringSize(text, HorizontalAlignment.Left, -1, size);
        // 水平居中（不贴最右）；垂直基线对齐（基线 ≈ 行中下 1/3 处）
        Vector2 pos = new((Size.X - textSize.X) / 2f, Size.Y / 2f + size * 0.38f);
        DrawString(font, pos, text, HorizontalAlignment.Left, -1, size, color);
    }
}