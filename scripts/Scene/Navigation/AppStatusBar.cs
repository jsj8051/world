using Godot;
using World.Utils;

namespace World.Scene;

/// <summary>
/// 常驻应用状态条：独立 CanvasLayer 组件，显示跨页面提示并按等级着色。
/// 无消息时只隐藏自身，不影响其它覆盖层。
/// </summary>
public partial class AppStatusBar : CanvasLayer
{
	[Export] public NodePath PanelPath { get; set; }
	[Export] public NodePath LabelPath { get; set; }

	Control _panel;
	Label _label;

	public override void _Ready()
	{
		_panel = this.Require<Control>(PanelPath, nameof(PanelPath));
		_label = this.Require<Label>(LabelPath, nameof(LabelPath));
		Clear();
	}

	public void ShowNotice(string message, NoticeKind kind)
	{
		_label.Text = message ?? string.Empty;
		_label.AddThemeColorOverride("font_color", kind switch
		{
			NoticeKind.Success => new Color(0.76f, 0.93f, 0.70f),
			NoticeKind.Error => new Color(1f, 0.66f, 0.58f),
			_ => new Color(0.984f, 0.961f, 0.878f),
		});
		_panel.Visible = !string.IsNullOrEmpty(message);
	}

	public void Clear()
	{
		_label.Text = string.Empty;
		_panel.Visible = false;
	}
}