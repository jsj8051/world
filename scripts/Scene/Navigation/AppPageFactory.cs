using System;
using Godot;
using World.Data.Generation;
using World.Logic;
using World.Params.Generation;

namespace World.Scene;

/// <summary>显式登记页面创建方法，集中管理场景路径与入树前初始化。</summary>
internal sealed class AppPageFactory
{
	const string MainMenuScene = "res://scenes/app/MainMenu.tscn";
	const string GenerationConfigScene = "res://scenes/app/MapGenerationPage.tscn";
	const string GeographyScene = "res://scenes/core/WorldGenWorld.tscn";
	const string MapLibraryScene = "res://scenes/app/MapLibrary.tscn";
	const string MapBrowserScene = "res://scenes/app/MapBrowser.tscn";

	readonly INoticeSink _notice;

	internal AppPageFactory(INoticeSink notice)
		=> _notice = notice ?? throw new ArgumentNullException(nameof(notice));

	public void RegisterPages(PageRegistry registry, Func<GenerationConfigSession> getSession)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(getSession);
		registry.Register<MainMenuRequest>(_ => Instantiate(MainMenuScene));
		registry.Register<MapLibraryRequest>(_ => Instantiate(MapLibraryScene));
		registry.Register<GenerationConfigRequest>(_ => CreateGenerationConfig(getSession()));
		registry.Register<WorldGenerationRequest>(request => CreateWorld(request.Config, request.OnFailure));
		registry.Register<MapBrowserRequest>(request => CreateMapBrowser(request.Source, request.Operation));
	}

	Node CreateGenerationConfig(GenerationConfigSession session)
	{
		if (session == null) throw new ArgumentNullException(nameof(session));
		MapGenerationPage page = Instantiate<MapGenerationPage>(GenerationConfigScene);
		page?.SetSession(session);
		return page;
	}

	Node CreateWorld(GenerationConfig config, Action<Exception> onFailure)
	{
		if (config == null) throw new ArgumentNullException(nameof(config));
		if (onFailure == null) throw new ArgumentNullException(nameof(onFailure));
		config.Validate();

		Node page = Instantiate(GeographyScene);
		if (page == null) return null;
		if (page is IGenerationPage generation)
		{
			generation.ConfigureGeneration(config, onFailure);
			return page;
		}

		page.Free();
		_notice.Notify("地图生成场景没有实现配置初始化契约。", NoticeKind.Error);
		return null;
	}

	Node CreateMapBrowser(ImportedWorldFactsSource source, LoadOperation operation)
	{
		if (source == null) throw new ArgumentNullException(nameof(source));
		MapBrowser page = Instantiate<MapBrowser>(MapBrowserScene);
		if (page == null) return null;
		page.SetSource(source);
		page.SetBuildOperation(operation);
		return page;
	}

	Node Instantiate(string path) => Instantiate<Node>(path);

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
