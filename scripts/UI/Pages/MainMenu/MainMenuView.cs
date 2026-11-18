using System;
using Godot;
using World.Application.Pages;
using World.Utils;

namespace World.UI.Pages;

public partial class MainMenuView : Control, IMainMenuActions
{
	[Export] public NodePath GeographyButtonPath { get; set; }
	[Export] public NodePath CivilizationButtonPath { get; set; }
	[Export] public NodePath LibraryButtonPath { get; set; }
	[Export] public NodePath QuitButtonPath { get; set; }
	[Export] public NodePath DisabledReasonPath { get; set; }

	MainMenuController _controller;

	internal static MainMenuView Create()
		=> GD.Load<PackedScene>("res://scenes/pages/MainMenu/MainMenuPage.tscn").Instantiate<MainMenuView>();

	internal void SetController(MainMenuController controller)
		=> _controller = controller ?? throw new ArgumentNullException(nameof(controller));

	public override void _Ready()
	{
		_controller ??= new MainMenuController(this, () => GetTree().Quit());
		var geography = this.Require<Button>(GeographyButtonPath, nameof(GeographyButtonPath));
		var library = this.Require<Button>(LibraryButtonPath, nameof(LibraryButtonPath));
		var quit = this.Require<Button>(QuitButtonPath, nameof(QuitButtonPath));
		var reason = this.Require<Label>(DisabledReasonPath, nameof(DisabledReasonPath));

		geography.Pressed += () => _controller?.OpenGenerationConfig();
		this.Require<Button>(CivilizationButtonPath, nameof(CivilizationButtonPath)).Pressed +=
			() => _controller.OpenCivilizationConfig();
		library.Pressed += () => _controller?.OpenMapLibrary();
		quit.Pressed += () => _controller.QuitApplication();
		reason.Text = "文明模拟与游玩尚未实现。";
	}

	void IMainMenuActions.OpenGenerationConfig()
		=> SceneReplacement.Replace(this, GenerationConfigView.Create());

	void IMainMenuActions.OpenCivilizationConfig()
		=> SceneReplacement.Replace(this, CivilizationConfigView.Create());

	void IMainMenuActions.OpenMapLibrary()
		=> SceneReplacement.Replace(this, MapBrowsingHost.Create());
}
