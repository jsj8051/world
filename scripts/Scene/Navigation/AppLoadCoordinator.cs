using System;
using System.IO;
using Godot;
using World.Application.Loading;
using World.Application.Pages;
using World.Application.Storage;
using World.Data;
using World.Data.Maps;
using World.Logic;
using World.UI.Components;

namespace World.Scene;

/// <summary>拥有应用级加载操作、地图浏览/世界生成流程与加载视图生命周期。</summary>
internal partial class AppLoadCoordinator : Node, IMapOpener, IWorldGenerator, ILoadProgressSink
{
	const string LoadingOverlayScene = "res://scenes/components/LoadingOverlay.tscn";

	Func<PageContext> _getPageContext;
	Action<ImportedWorldFactsSource, LoadOperation> _openBrowser;
	Action<WorldGenerationResult, LoadOperation> _openWorld;
	INoticeSink _notice;
	IGeographyMapRepository _mapRepository;
	LoadingOverlay _loadingOverlay;
	MapBrowseFlow _browse;
	WorldGenerationFlow _generation;
	LoadOperation _active;
	LoadOperationBinding _activeBinding;

	internal void Configure(Func<PageContext> getPageContext,
		Action<ImportedWorldFactsSource, LoadOperation> openBrowser,
		Action<WorldGenerationResult, LoadOperation> openWorld, INoticeSink notice,
		IGeographyMapRepository mapRepository)
	{
		ArgumentNullException.ThrowIfNull(getPageContext);
		ArgumentNullException.ThrowIfNull(openBrowser);
		ArgumentNullException.ThrowIfNull(openWorld);
		ArgumentNullException.ThrowIfNull(notice);
		ArgumentNullException.ThrowIfNull(mapRepository);
		if (_getPageContext != null) throw new InvalidOperationException("The load coordinator is already configured.");
		_getPageContext = getPageContext;
		_openBrowser = openBrowser;
		_openWorld = openWorld;
		_notice = notice;
		_mapRepository = mapRepository;
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
		PollGeneration();
		if (_active != null && _active.IsTerminal && _browse == null && _generation == null)
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
		if (_browse != null)
		{
			_notice.Notify("浏览仍在进行，请等它结束。", NoticeKind.Error);
			return;
		}
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
			() => _getPageContext()?.PageId ?? Guid.Empty, _mapRepository);
		_browse.Start(path);
	}

	internal void OnPageLeaving(LoadOperation transferredOperation)
	{
		_browse?.Dispose();
		_browse = null;
		_generation?.Dispose();
		_generation = null;
		if (_active == transferredOperation)
		{
			_activeBinding = null;
			return;
		}
		ReleaseActive();
	}

	/// <summary>起一次世界生成：操作与覆盖层由本协调器持有，完成后由外壳挂载世界页。</summary>
	public void StartGeneration(WorldSpec spec, WorldGenerationOptions options)
	{
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (_generation != null)
		{
			_notice.Notify("生成仍在进行，请等它结束。", NoticeKind.Error);
			return;
		}
		if (HasActive)
		{
			_notice.Notify("已有加载在进行，请等它结束。", NoticeKind.Error);
			return;
		}

		LoadOperation operation = Begin(LoadFlow.WorldGeneration, "生成世界");
		_generation = new WorldGenerationFlow(operation, _activeBinding,
			() => _getPageContext()?.PageId ?? Guid.Empty, spec, options);
		_generation.Start();
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
				if (_openBrowser == null)
					throw new InvalidOperationException("加载协调器没有装配浏览入口。");
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

	void PollGeneration()
	{
		if (_generation == null) return;
		WorldGenerationOutcome outcome = _generation.Poll();
		if (outcome.State == WorldGenerationState.Running) return;

		_generation.Dispose();
		_generation = null;
		if (outcome.State == WorldGenerationState.Succeeded)
		{
			try
			{
				if (_openWorld == null)
					throw new InvalidOperationException("加载协调器没有装配世界页入口。");
				_openWorld(outcome.Result, _active);
			}
			catch (Exception error)
			{
				var failure = new LoadFailure("LoadError", "画面装配", error.Message);
				_active?.Fail(failure);
				_notice.Notify($"{failure.Code}：{failure.Message}", NoticeKind.Error);
			}
			return;
		}
		if (outcome.State == WorldGenerationState.Failed)
			_notice.Notify($"{outcome.Failure.Code}：{outcome.Failure.Message}", NoticeKind.Error);
		else if (outcome.State == WorldGenerationState.Abandoned)
			_active?.Cancel();
	}

	void BindLoadingOverlay(LoadOperation operation)
	{		if (_loadingOverlay == null)
		{
			var packed = ResourceLoader.Load<PackedScene>(LoadingOverlayScene);
			_loadingOverlay = packed?.Instantiate<LoadingOverlay>();
			if (_loadingOverlay == null)
			{
				GD.PushError($"[LOAD] 加载进度层场景缺失：{LoadingOverlayScene}");
				return;
			}
			AddChild(_loadingOverlay);
		}
		_loadingOverlay.Bind(operation);
	}

	/// <summary>覆盖层生命周期归本协调器：终态当帧由 <see cref="_Process"/> 统一释放。</summary>
	void ReleaseOverlay()
	{
		if (_loadingOverlay == null) return;
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
		_generation?.Dispose();
		_generation = null;
		_active?.Cancel();
		_active?.Dispose();
		_active = null;
		_activeBinding = null;
	}
}
