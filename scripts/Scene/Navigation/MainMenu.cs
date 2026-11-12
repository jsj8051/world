using Godot;
using World.Utils;

namespace World.Scene;

/// <summary>
/// 主界面：地理演化、地图库可进入；文明演化与游玩本轮明确不可用，按钮禁用并显示原因。
/// </summary>
public partial class MainMenu : Control, IAppPage
{
	[Export] public NodePath GeographyButtonPath { get; set; }
	[Export] public NodePath LibraryButtonPath { get; set; }
	[Export] public NodePath QuitButtonPath { get; set; }
	[Export] public NodePath DisabledReasonPath { get; set; }

	IPageNavigator _nav;
	Button _geography;
	Button _library;
	Label _reason;

	void IAppPage.Configure(PageContext context) => _nav = context?.Navigation;

	void IAppPage.OnHidden() { }

	public override void _Ready()
	{
		_geography = this.Require<Button>(GeographyButtonPath, nameof(GeographyButtonPath));
		_library = this.Require<Button>(LibraryButtonPath, nameof(LibraryButtonPath));
		var quit = this.Require<Button>(QuitButtonPath, nameof(QuitButtonPath));
		_reason = this.Require<Label>(DisabledReasonPath, nameof(DisabledReasonPath));

		_geography.Pressed += () => _nav?.Navigate(new GenerationConfigRequest());
		_library.Pressed += () => _nav?.Navigate(new MapLibraryRequest());
		quit.Pressed += () => GetTree().Quit();
		_reason.Text = "文明演化与游玩尚未实现：本版本只提供地理演化与地图库。";

		if (_nav == null)
		{
			_geography.Disabled = true;
			_library.Disabled = true;
			_reason.Text = "主界面需要从 AppRoot 启动；当前为独立场景运行。";
		}
	}
}