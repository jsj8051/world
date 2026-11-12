using System;
using System.Threading;
using World.Data.Generation;
using World.Logic;
using Godot;
using World.Utils;

namespace World.Scene;

/// <summary>
/// 应用组合根与页面宿主：创建应用级协作者，负责页面生命周期，不持有玩法数据。
/// 每个页面是独立场景，进入用新实例、离开即释放。
/// </summary>
/// <example><code>var app = (AppRoot)GetTree().CurrentScene; var page = app.CurrentScreen;</code></example>
public partial class AppRoot : Node, IPageHost, IGenerationStarter
{

	/// <summary>页面宿主（当前页面唯一子节点）。</summary>
	[Export] public NodePath ScreenHostPath { get; set; }

	Node _screenHost;
	AppNoticeBoard _notice;
	AppNavigator _navigation;
	internal IPageNavigator Navigation => _navigation;
	Node _current;
	CancellationTokenSource _pageLifetime;
	PageContext _pageContext;
	AppLoadCoordinator _loadCoordinator;

	public override void _Ready()
	{
		_screenHost = this.Require<Node>(ScreenHostPath, nameof(ScreenHostPath));
		_notice = new AppNoticeBoard(this);
		_navigation = new AppNavigator(this, new AppPageFactory(_notice).RegisterPages,
			GenerationSessions.CreateDefault);
		_loadCoordinator = new AppLoadCoordinator();
		_loadCoordinator.Configure(() => _pageContext, OpenBrowser, _notice);
		AddChild(_loadCoordinator);
		_navigation.Navigate(new MainMenuRequest());
	}

	internal bool HasActiveLoad => _loadCoordinator?.HasActive == true;
	internal IMapOpener MapOpener => _loadCoordinator;

	void OpenBrowser(ImportedWorldFactsSource source, LoadOperation operation)
		=> _navigation.Navigate(new MapBrowserRequest(source, operation));

	/// <summary>当前页面节点（无页面时为 null）。</summary>
	public Node CurrentScreen => _current;

	public void StartGeography(GenerationConfig config)
	{
		Node page = null;
		_navigation.Navigate(new WorldGenerationRequest(config, error => Callable.From(() =>
		{
			if (_current != page || !IsInsideTree()) return;
			_navigation.Navigate(new GenerationConfigRequest());
			_notice.Notify($"地图生成失败：{error.Message}", NoticeKind.Error);
		}).CallDeferred()));
		page = _current;
	}

	void IPageHost.MountPage(Node page, LoadOperation transferredOperation) => MountPage(page, transferredOperation);

	void MountPage(Node page, LoadOperation transferredOperation = null)
	{
		// 工厂已提示场景缺失；保留当前页面比切到空白更好。
		if (page == null) return;
		UnmountCurrent(transferredOperation);
		_pageLifetime = new CancellationTokenSource();
		_pageContext = new PageContext(_pageLifetime.Token,
			navigation: _navigation, mapOpener: _loadCoordinator, generation: this,
			notice: _notice, progress: _loadCoordinator);
		_loadCoordinator.OnPageMounted(_pageContext, transferredOperation);
		if (page is IAppPage appPage) appPage.Configure(_pageContext);
		// AddChild runs _Ready synchronously; ownership must already be established.
		_current = page;
		_notice.Clear();
		_screenHost.AddChild(page);
	}

	void UnmountCurrent(LoadOperation transferredOperation = null)
	{
		_pageLifetime?.Cancel();
		if (_current is IAppPage appPage) appPage.OnHidden();
		_loadCoordinator.OnPageLeaving(transferredOperation);
		_current?.QueueFree();
		_current = null;
		_pageContext = null;
		_pageLifetime?.Dispose();
		_pageLifetime = null;
	}

	public override void _ExitTree() => UnmountCurrent();
}
