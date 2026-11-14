using Godot;
using World.Application.Generation;
using World.Application.Storage;
using World.Infrastructure;
using World.Infrastructure.Generation;
using World.Infrastructure.Maps;
using World.Logic;
using World.Utils;

namespace World.Scene;

/// <summary>Connects the app scene's page host, load coordinator, and notice nodes, then opens the main menu.</summary>
public partial class AppRoot : Node
{
	[Export] public NodePath PageHostPath { get; set; }
	[Export] public NodePath LoadCoordinatorPath { get; set; }

	AppPageHost _pageHost;
	AppLoadCoordinator _loadCoordinator;
	AppNoticeBoard _notice;
	AppShell _shell;

	public override void _Ready()
	{
		_pageHost = this.Require<AppPageHost>(PageHostPath, nameof(PageHostPath));
		_loadCoordinator = this.Require<AppLoadCoordinator>(LoadCoordinatorPath, nameof(LoadCoordinatorPath));
		_notice = new AppNoticeBoard(this);

		IGeographyMapRepository mapRepository = GameMapRepository.CreateDefault();
		var factory = new AppPageFactory(_notice, mapRepository, () => GetTree().Quit());
		_shell = new AppShell(_pageHost, factory, _loadCoordinator, _loadCoordinator, _notice,
			GenerationSessions.CreateDefault);
		_pageHost.Configure(_loadCoordinator, _notice);
		_loadCoordinator.Configure(() => _pageHost.CurrentContext, _shell.OpenBrowser, _shell.OpenWorld,
			_notice, mapRepository);
		_shell.Start();
	}
}

/// <summary>
/// 生成配置会话的出厂入口：档案文件位置留在游戏目录，
/// 让组合根不认识 <c>user://</c> 约定与具体存储。
/// </summary>
internal static class GenerationSessions
{
	internal static GenerationConfigSession CreateDefault()
	{
		var store = new FileGenerationProfileStore();
		return new GenerationConfigSession(store, GamePaths.GenerationProfilesPath);
	}
}
