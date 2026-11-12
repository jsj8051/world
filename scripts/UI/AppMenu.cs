using System;
using System.Collections.Generic;
using Godot;
using World.Utils;

namespace World.UI;

/// <summary>菜单项：稳定 id + 显示文字。页面只配置数据，不拼节点。</summary>
public readonly record struct AppMenuItem(string Id, string Text);

/// <summary>
/// 共享应用菜单：左上角「菜单」按钮 + 居中弹出面板。
/// 页面在初始化时调用 <see cref="Configure"/> 提供标题与菜单项，通过 <see cref="ItemPressed"/> 处理动作。
/// </summary>
/// <example><code>
/// menu.Configure("菜单", new AppMenuItem("back", "返回主界面"));
/// menu.ItemPressed += id => { if (id == "back") app.ReturnToMainMenu(); };
/// </code></example>
public partial class AppMenu : CanvasLayer
{
	[Export] public NodePath MenuButtonPath { get; set; }
	[Export] public NodePath DimPath { get; set; }
	[Export] public NodePath PanelPath { get; set; }
	[Export] public NodePath TitlePath { get; set; }
	[Export] public NodePath ItemsPath { get; set; }
	[Export] public NodePath CloseButtonPath { get; set; }

	Button _menuButton;
	Control _dim;
	Control _panel;
	Label _title;
	VBoxContainer _items;
	Button _close;

	/// <summary>菜单项被点击（菜单已先收起）。</summary>
	public event Action<string> ItemPressed;

	public override void _Ready()
	{
		_menuButton = this.Require<Button>(MenuButtonPath, nameof(MenuButtonPath));
		_dim = this.Require<Control>(DimPath, nameof(DimPath));
		_panel = this.Require<Control>(PanelPath, nameof(PanelPath));
		_title = this.Require<Label>(TitlePath, nameof(TitlePath));
		_items = this.Require<VBoxContainer>(ItemsPath, nameof(ItemsPath));
		_close = this.Require<Button>(CloseButtonPath, nameof(CloseButtonPath));

		_menuButton.Pressed += Toggle;
		_close.Pressed += HideMenu;
		ShowMenu(false);
	}

	/// <summary>设置标题并重建菜单项。重复调用会先清空旧项。</summary>
	public void Configure(string title, params AppMenuItem[] items)
	{
		_title.Text = title ?? string.Empty;
		foreach (Node child in _items.GetChildren()) child.QueueFree();
		foreach (AppMenuItem item in items ?? Array.Empty<AppMenuItem>())
		{
			var button = new Button { Text = item.Text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
			string id = item.Id;
			button.Pressed += () =>
			{
				ShowMenu(false);
				ItemPressed?.Invoke(id);
			};
			_items.AddChild(button);
		}
	}

	public bool IsOpen => _panel?.Visible == true;

	public void Toggle() => ShowMenu(!IsOpen);

	public void HideMenu() => ShowMenu(false);

	public void ShowMenu(bool visible)
	{
		if (_panel == null) return;
		_panel.Visible = visible;
		_dim.Visible = visible;
	}
}
