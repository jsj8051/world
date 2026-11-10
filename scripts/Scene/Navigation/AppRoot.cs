using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using World.Data.Generation;
using World.Data.Maps;
using World.Params.Generation;
using Godot;
using World.Logic;
using World.Utils;

namespace World.Scene;

/// <summary>
/// 常驻应用根：只做页面切换与全局加载/错误提示，不持有世界数据或生成器。
/// 每个页面是独立场景，进入用新实例、离开即释放。
/// </summary>
/// <example><code>var app = (AppRoot)GetTree().CurrentScene; app.ShowMapLibrary();</code></example>
public partial class AppRoot : Node, IPageHost, ILoadProgressSink
{
	const string MainMenuScene = "res://scenes/app/MainMenu.tscn";
	const string GenerationConfigScene = "res://scenes/app/MapGenerationPage.tscn";
	const string GeographyScene = "res://scenes/core/WorldGenWorld.tscn";
	const string MapLibraryScene = "res://scenes/app/MapLibrary.tscn";
	const string MapBrowserScene = "res://scenes/app/MapBrowser.tscn";

	/// <summary>页面宿主（当前页面唯一子节点）。</summary>
	[Export] public NodePath ScreenHostPath { get; set; }

	/// <summary>常驻状态条组件（独立 CanvasLayer）。</summary>
	[Export] public NodePath StatusBarPath { get; set; }

	/// <summary>常驻加载进度层。</summary>
	[Export] public NodePath LoadingOverlayPath { get; set; }

	Node _screenHost;
	AppStatusBar _statusBar;
	LoadingOverlay _loadingOverlay;
	Node _current;
	CancellationTokenSource _pageLifetime;
	GenerationConfigSession _generationConfig;
	Task<ImportedWorldFactsSource> _browseTask;
	LoadOperation _browseOperation;
	LoadProgressMonitor _browseMonitor;
	LoadOperation _active;

	public override void _Ready()
	{
		_screenHost = this.Require<Node>(ScreenHostPath, nameof(ScreenHostPath));
		_statusBar = this.Require<AppStatusBar>(StatusBarPath, nameof(StatusBarPath));
		_loadingOverlay = this.Require<LoadingOverlay>(LoadingOverlayPath, nameof(LoadingOverlayPath));
		_statusBar.Clear();
		ShowMainMenu();
	}

	public override void _Process(double delta)
	{
		PollBrowse();
		if (_active != null && _active.IsTerminal)
		{
			_loadingOverlay?.Unbind();
			_active = null;
		}
	}

	/// <summary>当前是否有未结束的页面加载。</summary>
	public bool HasActive => _active != null && !_active.IsTerminal;

	LoadOperation ILoadProgressSink.Begin(LoadFlow flow, string title) => BeginLoad(flow, title);

	/// <summary>创建一次页面加载操作；同一时刻只允许一个。</summary>
	LoadOperation BeginLoad(LoadFlow flow, string title)
	{
		if (HasActive) throw new InvalidOperationException("已有加载在进行。");
		_active = new LoadOperation(Guid.NewGuid(), flow, title, new CancellationTokenSource());
		_loadingOverlay?.Bind(_active);
		return _active;
	}

	/// <summary>打开所选地图：后台读包/装配，完成后切换到浏览页。</summary>
	public void OpenMap(string path)
	{
		if (_browseTask != null) return;
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			ShowNotice("地图文件不存在。", NoticeKind.Error);
			return;
		}
		if (HasActive)
		{
			ShowNotice("已有加载在进行，请等它结束。", NoticeKind.Error);
			return;
		}

		LoadOperation operation = BeginLoad(LoadFlow.MapBrowse, "加载地图");
		_browseOperation = operation;
		CancellationToken token = operation.Token;
		var job = new MapBrowseJob();
		_browseMonitor = new LoadProgressMonitor(operation, LoadStagePlans.Browse, 0f, 0.75f);
		_browseMonitor.Attach(job);
		_browseTask = Task.Run(() => job.Run(path, token));
	}

	void PollBrowse()
	{
		if (_browseTask == null || !_browseTask.IsCompleted) return;

		Task<ImportedWorldFactsSource> task = _browseTask;
		LoadOperation operation = _browseOperation;
		_browseTask = null;
		_browseOperation = null;
		_browseMonitor?.Dispose();
		_browseMonitor = null;

		if (task.IsCanceled)
		{
			operation?.Cancel();
			return;
		}
		if (task.IsFaulted)
		{
			HandleFailure(operation, task.Exception);
			return;
		}

		try
		{
			operation?.Report(0.75f, "画面装配");
			if (!ShowMapBrowser(task.Result, operation))
				HandleFailure(operation, new InvalidOperationException("地图浏览场景缺失。"));
		}
		catch (Exception error)
		{
			HandleFailure(operation, error);
		}
	}

	void HandleFailure(LoadOperation operation, Exception error)
	{
		Exception root = error is AggregateException aggregate ? aggregate.GetBaseException() : error;
		LoadFailure failure = ToFailure(root);
		operation?.Fail(failure);
		ShowNotice($"{failure.Code}：{failure.Message}", NoticeKind.Error);
	}

	static LoadFailure ToFailure(Exception error)
	{
		if (error is MapStructureException map)
			return new LoadFailure(map.Issue.Code.ToString(), map.Issue.Location ?? "加载地图", map.Issue.Message);
		return new LoadFailure("LoadError", "加载地图", error.Message);
	}

	/// <summary>当前页面节点（无页面时为 null）。</summary>
	public Node CurrentScreen => _current;

	public void ShowMainMenu() => Replace(MainMenuScene);

	public void ShowGeography()
	{
		_generationConfig ??= new GenerationConfigSession(new FileGenerationProfileStore(
			ProjectSettings.GlobalizePath("user://worldgen/profiles.json")));
		var packed = ResourceLoader.Load<PackedScene>(GenerationConfigScene);
		if (packed == null) { ShowNotice("地图配置场景缺失。", NoticeKind.Error); return; }
		var page = packed.Instantiate<MapGenerationPage>();
		page.SetSession(_generationConfig);
		MountPage(page);
	}

	void StartGeography(GenerationConfig config)
	{
		config.Validate();
		var packed = ResourceLoader.Load<PackedScene>(GeographyScene);
		if (packed == null) throw new InvalidOperationException("地图生成场景缺失。");
		var page = packed.Instantiate();
		if (page is not IGenerationPage generation)
		{
			page.Free();
			throw new InvalidOperationException("地图场景没有实现配置初始化契约。");
		}
		generation.ConfigureGeneration(config, error => Callable.From(() =>
		{
			if (_current != page || !IsInsideTree()) return;
			ShowGeography();
			ShowNotice($"地图生成失败：{error.Message}", NoticeKind.Error);
		}).CallDeferred());
		MountPage(page);
	}

	public void ShowMapLibrary() => Replace(MapLibraryScene);

	internal bool ShowMapBrowser(ImportedWorldFactsSource source, LoadOperation operation = null)
	{
		if (source == null) { ShowNotice("没有可浏览的地图数据。", NoticeKind.Error); return false; }
		var packed = ResourceLoader.Load<PackedScene>(MapBrowserScene);
		if (packed == null) { ShowNotice("地图浏览场景缺失。", NoticeKind.Error); return false; }
		var browser = packed.Instantiate<MapBrowser>();
		browser.SetSource(source);
		browser.SetBuildOperation(operation);
		MountPage(browser);
		return true;
	}

	public void ReturnToMainMenu() => ShowMainMenu();

	void IPageHost.ShowMainMenu() => ShowMainMenu();

	void IPageHost.ShowGeography() => ShowGeography();

	void IPageHost.StartGeography(GenerationConfig config) => StartGeography(config);

	void IPageHost.ShowMapLibrary() => ShowMapLibrary();

	void IPageHost.OpenMap(string path) => OpenMap(path);

	void IPageHost.ShowMapBrowser(ImportedWorldFactsSource source) => ShowMapBrowser(source);

	void IPageHost.Notify(string message, NoticeKind kind) => ShowNotice(message, kind);

	void ShowNotice(string message, NoticeKind kind) => _statusBar?.ShowNotice(message, kind);

	public void Quit() => GetTree().Quit();


	void Replace(string path)
	{
		var packed = ResourceLoader.Load<PackedScene>(path);
		if (packed == null)
		{
			ShowNotice($"页面场景缺失：{path}", NoticeKind.Error);
			return;
		}
		MountPage(packed.Instantiate());
	}

	void MountPage(Node page)
	{
		UnmountCurrent();
		_pageLifetime = new CancellationTokenSource();
		if (page is IAppPage appPage)
			appPage.Configure(new PageContext(this, _pageLifetime.Token, this));
		_screenHost.AddChild(page);
		_current = page;
		_statusBar?.Clear();
	}

	void UnmountCurrent()
	{
		if (_current is IAppPage appPage) appPage.OnHidden();
		_current?.QueueFree();
		_current = null;
		_pageLifetime?.Cancel();
		_pageLifetime?.Dispose();
		_pageLifetime = null;
	}

	public override void _ExitTree()
	{
		_active?.Cancel();
		_browseMonitor?.Dispose();
		_browseMonitor = null;
		if (_browseTask != null)
			_ = _browseTask.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
		UnmountCurrent();
	}
}