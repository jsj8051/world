using System;
using Godot;
using World.Application.Components;
using World.Application.Generation;
using World.Application.Loading;
using World.Application.Pages;
using World.Application.Storage;
using World.Data.Generation;
using World.Logic;
using World.UI.Pages;

namespace World.Scene;

/// <summary>
/// 页面创建与依赖注入的唯一入口：场景路径、动作端口与入树前初始化都集中在这里。
/// 只负责"造出已装配好的页面"，不决定何时挂载；挂载归 <see cref="IPageHost"/>，时机归 <see cref="AppShell"/>。
/// </summary>
internal sealed class AppPageFactory
{
	const string MainMenuScene = "res://scenes/pages/MainMenu.tscn";
	const string GenerationConfigScene = "res://scenes/pages/MapGenerationPage.tscn";
	const string GeographyEvolutionScene = "res://scenes/pages/GeographyEvolutionPage.tscn";
	const string MapLibraryScene = "res://scenes/pages/MapLibrary.tscn";
	const string MapBrowserScene = "res://scenes/pages/MapBrowser.tscn";

	readonly INoticeSink _notice;
	readonly IGeographyMapRepository _mapRepository;
	readonly Action _quitApplication;

	internal AppPageFactory(INoticeSink notice, IGeographyMapRepository mapRepository, Action quitApplication)
	{
		_notice = notice ?? throw new ArgumentNullException(nameof(notice));
		_mapRepository = mapRepository ?? throw new ArgumentNullException(nameof(mapRepository));
		_quitApplication = quitApplication ?? throw new ArgumentNullException(nameof(quitApplication));
	}

	internal Node CreateMainMenu(IMainMenuActions actions)
	{
		MainMenuView page = Instantiate<MainMenuView>(MainMenuScene);
		if (page == null) return null;
		page.SetController(new MainMenuController(actions, _quitApplication));
		return page;
	}

	internal Node CreateGenerationConfig(IGenerationConfigPageActions actions, GenerationConfigSession session)
	{
		if (session == null) throw new ArgumentNullException(nameof(session));
		GenerationConfigView page = Instantiate<GenerationConfigView>(GenerationConfigScene);
		if (page == null) return null;
		page.SetController(new GenerationConfigController(session, actions));
		return page;
	}

	internal Node CreateMapLibrary(IMapCatalogActions actions)
	{
		MapLibraryView page = Instantiate<MapLibraryView>(MapLibraryScene);
		if (page == null) return null;
		page.SetRepository(_mapRepository);
		page.SetActions(actions);
		return page;
	}

	/// <summary>世界页接收已完成的生成产物与仍在进行的加载操作；装配完成由页面收尾。</summary>
	internal Node CreateGeneratedWorld(WorldGenerationResult result, LoadOperation operation,
		Action<Exception> onBuildFailure, IMainMenuReturn mainMenu)
	{
		if (result == null) throw new ArgumentNullException(nameof(result));
		if (operation == null) throw new ArgumentNullException(nameof(operation));
		GeographyEvolutionView page = Instantiate<GeographyEvolutionView>(GeographyEvolutionScene);
		if (page == null) return null;
		page.SetMapRepository(_mapRepository);
		page.SetMainMenuReturn(mainMenu);
		page.SetGeneratedWorld(result, operation, onBuildFailure);
		return page;
	}

	internal Node CreateMapBrowser(ImportedWorldFactsSource source, LoadOperation operation,
		IMapBrowserPageActions actions)
	{
		if (source == null) throw new ArgumentNullException(nameof(source));
		MapBrowserView page = Instantiate<MapBrowserView>(MapBrowserScene);
		if (page == null) return null;
		page.SetSource(source);
		page.SetBuildOperation(operation);
		page.SetActions(actions);
		return page;
	}

	T Instantiate<T>(string path) where T : Node
	{
		var packed = ResourceLoader.Load<PackedScene>(path);
		if (packed == null)
		{
			_notice.Notify($"页面场景缺失：{path}", NoticeKind.Error);
			return null;
		}
		Node page = packed.Instantiate();
		if (page is T typedPage) return typedPage;
		page?.Free();
		_notice.Notify($"页面场景类型不符：{path}", NoticeKind.Error);
		return null;
	}
}
