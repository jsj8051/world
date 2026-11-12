using System;
using System.IO;
using Godot;
using World.Data.Maps;
using World.Logic;

namespace World.Scene;

/// <summary>拥有应用级加载操作、地图浏览流程与加载视图生命周期。</summary>
internal partial class AppLoadCoordinator : Node, IMapOpener, ILoadProgressSink
{
	const string LoadingOverlayScene = "res://scenes/app/LoadingOverlay.tscn";

	Func<PageContext> _getPageContext;
	Action<ImportedWorldFactsSource, LoadOperation> _openBrowser;
	INoticeSink _notice;
	LoadingOverlay _loadingOverlay;
	MapBrowseFlow _browse;
	LoadOperation _active;
	LoadOperationBinding _activeBinding;

	internal void Configure(Func<PageContext> getPageContext,
		Action<ImportedWorldFactsSource, LoadOperation> openBrowser, INoticeSink notice)
	{
		_getPageContext = getPageContext ?? throw new ArgumentNullException(nameof(getPageContext));
		_openBrowser = openBrowser ?? throw new ArgumentNullException(nameof(openBrowser));
		_notice = notice ?? throw new ArgumentNullException(nameof(notice));
	}

	public bool HasActive => _active != null && !_active.IsTerminal;

	internal void OnPageMounted(PageContext context, LoadOperation transferredOperation)
	{
		if (transferredOperation == null) return;
		if (!ReferenceEquals(_active, transferredOperation))
			throw new InvalidOperationException("转交的加载操作不属于协调器。");
		_activeBinding = new LoadOperationBinding(transferredOperation, context.PageId, context.Lifetime);
	}

	public override void _Process(double delta)
	{
		PollBrowse();
		if (_active != null && _active.IsTerminal && _browse == null)
			ReleaseActive();
	}

	LoadOperation ILoadProgressSink.Begin(LoadFlow flow, string title) => Begin(flow, title);

	LoadOperation Begin(LoadFlow flow, string title)
	{
		if (HasActive) throw new InvalidOperationException("已有加载在进行。");
		PageContext context = _getPageContext();
		if (context == null) throw new InvalidOperationException("没有当前页面。");
		_active?.Dispose();
		_active = new LoadOperation(Guid.NewGuid(), flow, title);
		_activeBinding = new LoadOperationBinding(_active, context.PageId, context.Lifetime);
		BindLoadingOverlay(_active);
		return _active;
	}

	public void OpenMap(string path)
	{
		if (_browse != null) return;
		if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
		{
			_notice.Notify("地图文件不存在。", NoticeKind.Error);
			return;
		}
		if (HasActive)
		{
			_notice.Notify("已有加载在进行，请等它结束。", NoticeKind.Error);
			return;
		}

		LoadOperation operation = Begin(LoadFlow.MapBrowse, "加载地图");
		_browse = new MapBrowseFlow(operation, _activeBinding,
			() => _getPageContext()?.PageId ?? Guid.Empty);
		_browse.Start(path);
	}

	internal void OnPageLeaving(LoadOperation transferredOperation)
	{
		_browse?.Dispose();
		_browse = null;
		if (_active == transferredOperation)
		{
			_activeBinding = null;
			return;
		}
		ReleaseActive();
	}

	void PollBrowse()
	{
		if (_browse == null) return;
		BrowseOutcome outcome = _browse.Poll();
		if (outcome.State == BrowseState.Running) return;

		_browse.Dispose();
		_browse = null;
		if (outcome.State == BrowseState.Succeeded)
		{
			try
			{
				_openBrowser(outcome.Source, _active);
			}
			catch (Exception error)
			{
				var failure = new LoadFailure("LoadError", "画面装配", error.Message);
				_active?.Fail(failure);
				_notice.Notify($"{failure.Code}：{failure.Message}", NoticeKind.Error);
			}
			return;
		}
		if (outcome.State == BrowseState.Failed)
			_notice.Notify($"{outcome.Failure.Code}：{outcome.Failure.Message}", NoticeKind.Error);
		else if (outcome.State == BrowseState.Abandoned)
			_active?.Cancel();
	}

	void BindLoadingOverlay(LoadOperation operation)
	{
		if (_loadingOverlay == null)
		{
			var packed = ResourceLoader.Load<PackedScene>(LoadingOverlayScene);
			_loadingOverlay = packed?.Instantiate<LoadingOverlay>();
			if (_loadingOverlay == null)
			{
				GD.PushError($"[LOAD] 加载进度层场景缺失：{LoadingOverlayScene}");
				return;
			}
			AddChild(_loadingOverlay);
			_loadingOverlay.Finished += ReleaseOverlay;
		}
		_loadingOverlay.Bind(operation);
	}

	void ReleaseOverlay()
	{
		if (_loadingOverlay == null) return;
		_loadingOverlay.Finished -= ReleaseOverlay;
		if (IsInsideTree()) _loadingOverlay.QueueFree();
		_loadingOverlay = null;
	}

	void ReleaseActive()
	{
		ReleaseOverlay();
		_active?.Dispose();
		_active = null;
		_activeBinding = null;
	}

	public override void _ExitTree()
	{
		_browse?.Dispose();
		_browse = null;
		_active?.Cancel();
		_active?.Dispose();
		_active = null;
		_activeBinding = null;
	}
}
