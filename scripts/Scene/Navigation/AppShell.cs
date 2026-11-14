using System;
using Godot;
using World.Application.Components;
using World.Application.Generation;
using World.Application.Loading;
using World.Application.Pages;
using World.Data;
using World.Data.Generation;
using World.Logic;
using World.Simulation;
using World.UI.Pages;

namespace World.Scene;

/// <summary>
/// 组合根的运行时入口：把页面动作端口翻成"创建 + 挂载"，把跨页长任务交给协调器。
/// 具体页面类型只出现在本类与 <see cref="AppPageFactory"/>；宿主只保留槽位与生命周期。
/// </summary>
internal sealed class AppShell : IMainMenuActions, IGenerationConfigPageActions, IMapCatalogActions,
	IMapBrowserPageActions, IMainMenuReturn
{
	readonly IPageHost _host;
	readonly AppPageFactory _factory;
	readonly IMapOpener _mapOpener;
	readonly IWorldGenerator _worldGenerator;
	readonly INoticeSink _notice;
	readonly Func<GenerationConfigSession> _createSession;

	GenerationConfigSession _session;

	internal AppShell(IPageHost host, AppPageFactory factory, IMapOpener mapOpener,
		IWorldGenerator worldGenerator, INoticeSink notice, Func<GenerationConfigSession> createSession)
	{
		_host = host ?? throw new ArgumentNullException(nameof(host));
		_factory = factory ?? throw new ArgumentNullException(nameof(factory));
		_mapOpener = mapOpener ?? throw new ArgumentNullException(nameof(mapOpener));
		_worldGenerator = worldGenerator ?? throw new ArgumentNullException(nameof(worldGenerator));
		_notice = notice ?? throw new ArgumentNullException(nameof(notice));
		_createSession = createSession ?? throw new ArgumentNullException(nameof(createSession));
	}

	/// <summary>应用启动：打开主菜单。</summary>
	internal void Start() => ShowMainMenu();

	/// <summary>地图包读取完成：把仍在进行的加载操作转交给浏览页；场景缺失时直接判这次加载失败。</summary>
	internal void OpenBrowser(ImportedWorldFactsSource source, LoadOperation operation)
	{
		Node page = _factory.CreateMapBrowser(source, operation, this);
		if (page == null)
		{
			operation?.Fail(new LoadFailure("LoadError", "画面装配", "地图浏览场景缺失。"));
			return;
		}
		_host.MountPage(page, operation);
	}

	/// <summary>世界生成完成：把生成产物与仍在进行的加载操作一起转交给世界页。</summary>
	internal void OpenWorld(WorldGenerationResult result, LoadOperation operation)
	{
		Node page = _factory.CreateGeneratedWorld(result, operation, HandleGenerationFailure, this);
		if (page == null)
		{
			operation?.Fail(new LoadFailure("LoadError", "画面装配", "地理演化页面场景缺失。"));
			return;
		}
		_host.MountPage(page, operation);
	}

	void IMainMenuActions.OpenGenerationConfig() => ShowGenerationConfig();
	void IMainMenuActions.OpenMapLibrary() => ShowMapLibrary();
	void IGenerationConfigPageActions.BackToMainMenu() => ShowMainMenu();
	void IGenerationConfigPageActions.StartGeneration(GenerationConfig config) => StartGeneration(config);
	void IMapCatalogActions.BackToMainMenu() => ShowMainMenu();
	void IMapCatalogActions.LoadMap(string path) => _mapOpener.OpenMap(path);
	void IMapBrowserPageActions.BackToMapLibrary() => ShowMapLibrary();
	void IMainMenuReturn.BackToMainMenu() => ShowMainMenu();

	/// <summary>
	/// 生成或世界页装配失败：退回配置页。延迟到帧末再改挂载，避免在被处理页面的 <c>_Process</c> 里替换节点；
	/// 用户已切走则丢弃这次失败，不打断当前页面。
	/// </summary>
	internal void HandleGenerationFailure(Exception error)
	{
		_host.Defer(() =>
		{
			bool onWorld = _host.CurrentScreen is GeographyEvolutionView;
			bool onConfig = _host.CurrentScreen is GenerationConfigView;
			if (!onWorld && !onConfig) return;
			if (onWorld) ShowGenerationConfig();
			_notice.Notify($"地图生成失败：{error?.Message}", NoticeKind.Error);
		});
	}

	void StartGeneration(GenerationConfig config)
	{
		if (config == null) throw new ArgumentNullException(nameof(config));
		try
		{
			_worldGenerator.StartGeneration(WorldGenerationSpecs.Create(config), WorldGenerationPresets.Production);
		}
		catch (Exception error)
		{
			// 入树前失败（配置非法、默认参数缺失）时留在配置页并说明原因，避免"点了没反应"。
			_notice.Notify($"无法开始生成：{error.Message}", NoticeKind.Error);
		}
	}

	void ShowMainMenu() => Show(_factory.CreateMainMenu(this));

	void ShowGenerationConfig() => Show(_factory.CreateGenerationConfig(this, GetSession()));

	void ShowMapLibrary() => Show(_factory.CreateMapLibrary(this));

	GenerationConfigSession GetSession()
		=> _session ??= _createSession()
			?? throw new InvalidOperationException("The generation session factory returned null.");

	void Show(Node page)
	{
		if (page != null) _host.MountPage(page);
	}
}
