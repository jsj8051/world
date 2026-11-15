using System;
using System.IO;
using Godot;
using World.Application.Loading;
using World.Application.Pages;
using World.Application.Storage;
using World.Data;
using World.Data.Maps;
using World.Logic;

namespace World.Scene;

/// <summary>拥有应用级后台任务：同一时刻最多一次浏览或生成，负责起流程、轮询结果与失败提示。</summary>
internal partial class AppLoadCoordinator : Node, IMapOpener, IWorldGenerator
{
	Action<ImportedWorldFactsSource> _openBrowser;
	Action<WorldGenerationResult> _openWorld;
	INoticeSink _notice;
	IGeographyMapRepository _mapRepository;
	MapBrowseFlow _browse;
	WorldGenerationFlow _generation;

	internal bool HasActive => _browse != null || _generation != null;

	internal void Configure(Action<ImportedWorldFactsSource> openBrowser,
		Action<WorldGenerationResult> openWorld, INoticeSink notice,
		IGeographyMapRepository mapRepository)
	{
		ArgumentNullException.ThrowIfNull(openBrowser);
		ArgumentNullException.ThrowIfNull(openWorld);
		ArgumentNullException.ThrowIfNull(notice);
		ArgumentNullException.ThrowIfNull(mapRepository);
		if (_openBrowser != null) throw new InvalidOperationException("The load coordinator is already configured.");
		_openBrowser = openBrowser;
		_openWorld = openWorld;
		_notice = notice;
		_mapRepository = mapRepository;
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

		_browse = new MapBrowseFlow(_mapRepository);
		try
		{
			_browse.Start(path);
		}
		catch (Exception error)
		{
			_browse.Dispose();
			_browse = null;
			_notice.Notify($"无法开始加载：{error.Message}", NoticeKind.Error);
		}
	}

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

		_generation = new WorldGenerationFlow(spec, options);
		try
		{
			_generation.Start();
		}
		catch (Exception error)
		{
			_generation.Dispose();
			_generation = null;
			_notice.Notify($"无法开始生成：{error.Message}", NoticeKind.Error);
		}
	}

	public override void _Process(double delta)
	{
		PollBrowse();
		PollGeneration();
	}

	void PollBrowse()
	{
		if (_browse == null) return;
		BrowseOutcome outcome = _browse.Poll();
		if (outcome.State == BrowseState.Running) return;

		_browse.Dispose();
		_browse = null;
		if (outcome.State == BrowseState.Failed)
		{
			_notice.Notify($"{outcome.Failure.Code}：{outcome.Failure.Message}", NoticeKind.Error);
			return;
		}
		if (outcome.State != BrowseState.Succeeded) return;

		try
		{
			_openBrowser(outcome.Source);
		}
		catch (Exception error)
		{
			_notice.Notify($"画面装配失败：{error.Message}", NoticeKind.Error);
		}
	}

	void PollGeneration()
	{
		if (_generation == null) return;
		WorldGenerationOutcome outcome = _generation.Poll();
		if (outcome.State == WorldGenerationState.Running) return;

		_generation.Dispose();
		_generation = null;
		if (outcome.State == WorldGenerationState.Failed)
		{
			_notice.Notify($"{outcome.Failure.Code}：{outcome.Failure.Message}", NoticeKind.Error);
			return;
		}
		if (outcome.State != WorldGenerationState.Succeeded) return;

		try
		{
			_openWorld(outcome.Result);
		}
		catch (Exception error)
		{
			_notice.Notify($"画面装配失败：{error.Message}", NoticeKind.Error);
		}
	}

	public override void _ExitTree()
	{
		_browse?.Dispose();
		_browse = null;
		_generation?.Dispose();
		_generation = null;
	}
}
