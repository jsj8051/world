using System;
using System.Threading;
using Godot;
using World.Application.Loading;
using World.Application.Pages;

namespace World.Scene;

internal partial class AppPageHost : Node, IPageHost
{
	AppLoadCoordinator _loadCoordinator;
	AppNoticeBoard _notice;
	CancellationTokenSource _pageLifetime;

	internal Node CurrentScreen { get; private set; }
	internal PageContext CurrentContext { get; private set; }
	Node IPageHost.CurrentScreen => CurrentScreen;

	internal void Configure(AppLoadCoordinator loadCoordinator, AppNoticeBoard notice)
	{
		ArgumentNullException.ThrowIfNull(loadCoordinator);
		ArgumentNullException.ThrowIfNull(notice);
		if (_loadCoordinator != null) throw new InvalidOperationException("The page host is already configured.");
		_loadCoordinator = loadCoordinator;
		_notice = notice;
		_loadCoordinator.TreeExiting += OnLoadCoordinatorTreeExiting;
	}

	void IPageHost.MountPage(Node page, LoadOperation transferredOperation) => MountPage(page, transferredOperation);

	internal void MountPage(Node page, LoadOperation transferredOperation = null)
	{
		if (page == null) return;
		if (_loadCoordinator == null) throw new InvalidOperationException("The page host is not configured.");

		// 先装配后卸载：Configure 抛错时旧页仍在，新页直接释放，不留半挂载状态。
		var lifetime = new CancellationTokenSource();
		var context = new PageContext(lifetime.Token, notice: _notice, progress: _loadCoordinator);
		try
		{
			if (page is IAppPage appPage) appPage.Configure(context);
		}
		catch (Exception error)
		{
			lifetime.Dispose();
			page.Free();
			GD.PushError($"[PAGE] 页面装配失败：{error}");
			_notice.Notify($"页面装配失败：{error.Message}", NoticeKind.Error);
			return;
		}

		UnmountCurrent(transferredOperation);
		_pageLifetime = lifetime;
		CurrentContext = context;
		_loadCoordinator.OnPageMounted(context, transferredOperation);
		// AddChild runs _Ready synchronously; ownership must already be established.
		CurrentScreen = page;
		_notice.Clear();
		AddChild(page);
	}

	void OnLoadCoordinatorTreeExiting() => UnmountCurrent();

	void IPageHost.Defer(Action action)
	{
		ArgumentNullException.ThrowIfNull(action);
		Callable.From(() =>
		{
			if (IsInsideTree()) action();
		}).CallDeferred();
	}

	void UnmountCurrent(LoadOperation transferredOperation = null)
	{
		if (CurrentScreen == null && _pageLifetime == null) return;
		_pageLifetime?.Cancel();
		if (CurrentScreen is IAppPage appPage) appPage.OnHidden();
		_loadCoordinator?.OnPageLeaving(transferredOperation);
		Node previous = CurrentScreen;
		CurrentScreen = null;
		CurrentContext = null;
		_pageLifetime?.Dispose();
		_pageLifetime = null;
		if (previous == null) return;
		// QueueFree is deferred; detach now so the old page's CanvasLayers cannot render over the new page.
		RemoveChild(previous);
		previous.QueueFree();
	}

	public override void _ExitTree()
	{
		_loadCoordinator.TreeExiting -= OnLoadCoordinatorTreeExiting;
		UnmountCurrent();
	}
}
