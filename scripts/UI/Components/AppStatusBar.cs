using Godot;
using World.Utils;

namespace World.UI.Components;

/// <summary>
/// 页面状态条：显示提示文本，文本为空时隐藏面板。
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

	public void Notify(string message)
	{
		_label.Text = message ?? string.Empty;
		_panel.Visible = !string.IsNullOrEmpty(message);
	}

	public void Clear()
	{
		_label.Text = string.Empty;
		_panel.Visible = false;
	}
}
