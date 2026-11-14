using System;
using Godot;
using World.Application.Pages;
using World.Utils;

namespace World.UI.Pages;

public partial class MainMenuView : Control
{
	[Export] public NodePath GeographyButtonPath { get; set; }
	[Export] public NodePath LibraryButtonPath { get; set; }
	[Export] public NodePath QuitButtonPath { get; set; }
	[Export] public NodePath DisabledReasonPath { get; set; }

	MainMenuController _controller;

	internal void SetController(MainMenuController controller)
		=> _controller = controller ?? throw new ArgumentNullException(nameof(controller));

	public override void _Ready()
	{
		var geography = this.Require<Button>(GeographyButtonPath, nameof(GeographyButtonPath));
		var library = this.Require<Button>(LibraryButtonPath, nameof(LibraryButtonPath));
		var quit = this.Require<Button>(QuitButtonPath, nameof(QuitButtonPath));
		var reason = this.Require<Label>(DisabledReasonPath, nameof(DisabledReasonPath));

		geography.Pressed += () => _controller?.OpenGenerationConfig();
		library.Pressed += () => _controller?.OpenMapLibrary();
		quit.Pressed += () =>
		{
			if (_controller == null) GetTree().Quit();
			else _controller.QuitApplication();
		};
		reason.Text = "文明演化与游玩尚未实现：本版本只提供地理演化与地图库。";

		if (_controller == null)
		{
			geography.Disabled = true;
			library.Disabled = true;
			reason.Text = "主界面需要从 AppRoot 启动；当前为独立场景运行。";
		}
	}
}
