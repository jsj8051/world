using System;
using World.Application.Loading;
using World.Application.Storage;
using World.Data.Work;

namespace World.Application.Pages;

internal enum MapBrowserState { Idle, Loading, Building, Ready, Failed, Closed }

/// <summary>拥有一次地图浏览：读取、事实装配、画面构建、就绪及退出。</summary>
internal sealed class MapBrowserController : IDisposable
{
	// 各阶段的工作单位不同，按固定权重汇总为同一个进度估计。
	const int DecodeCompletePercent = 50;
	const int FactsCompletePercent = 60;

	readonly string _path;
	readonly IMapBrowseFlow _flow;
	readonly IMapBrowserPageActions _actions;
	IMapBrowserPresentation _presentation;

	internal MapBrowserController(string path, FileLoadComponent<global::World.Data.Maps.GeographySnapshot> loader,
		IMapBrowserPageActions actions = null)
		: this(path, new MapBrowseFlow(loader), actions) { }

	internal MapBrowserController(string path, IMapBrowseFlow flow, IMapBrowserPageActions actions = null)
	{
		if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("地图路径不能为空。", nameof(path));
		_path = path;
		_flow = flow ?? throw new ArgumentNullException(nameof(flow));
		_actions = actions;
	}

	internal event Action StateChanged;
	internal MapBrowserState State { get; private set; }
	internal string Title { get; private set; } = "浏览地图";
	internal int CellCount { get; private set; }
	internal string Stage { get; private set; } = "准备读取地图";
	internal long ProgressStep { get; private set; }
	internal long ProgressTotal { get; private set; }
	internal int ProgressPercent { get; private set; }
	internal LoadFailure Failure { get; private set; }
	internal bool IsBusy => State is MapBrowserState.Loading or MapBrowserState.Building;

	internal void Start(IMapBrowserPresentation presentation)
	{
		if (State != MapBrowserState.Idle) throw new InvalidOperationException("浏览只能启动一次。");
		_presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
		State = MapBrowserState.Loading;
		Stage = "读取地图存档";
		try { _flow.Start(_path); }
		catch (Exception error) { Fail(new LoadFailure("LoadError", Stage, error.Message)); return; }
		StateChanged?.Invoke();
	}

	internal void Tick(double delta)
	{
		if (State is MapBrowserState.Idle or MapBrowserState.Closed or MapBrowserState.Failed) return;
		try
		{
			if (State == MapBrowserState.Loading)
			{
				BrowseOutcome outcome = _flow.Poll();
				while (_flow.TryReadProgress(out WorkSignal progress))
				{
					if (progress.Kind == WorkSignalKind.JobCompleted) continue;
					Stage = progress.Stage;
					ProgressStep = progress.Done;
					ProgressTotal = progress.Total;
					if (progress.StageId == MapBrowseJob.DecodeStage)
						AdvanceProgress(0, DecodeCompletePercent);
					else if (progress.StageId == MapBrowseJob.FactsStage)
						AdvanceProgress(DecodeCompletePercent, FactsCompletePercent);
				}
				if (outcome.State == BrowseState.Failed) { Fail(outcome.Failure); return; }
				if (outcome.State == BrowseState.Abandoned) { Dispose(); return; }
				if (outcome.State == BrowseState.Succeeded)
				{
					Title = outcome.Source.Snapshot.Manifest.Title;
					CellCount = outcome.Source.Facts.Ball.CellIds.Length;
					_flow.Dispose();
					State = MapBrowserState.Building;
					Stage = "装配地图画面";
					_presentation.Begin(outcome.Source);
					ProgressStep = 0;
					ProgressTotal = _presentation.BuildTotal;
					AdvanceProgress(FactsCompletePercent, 100);
				}
			}
			else if (State == MapBrowserState.Building)
			{
				bool done = _presentation.StepBuild(8.0);
				ProgressStep = _presentation.BuildStep;
				ProgressTotal = _presentation.BuildTotal;
				Stage = _presentation.BuildStage;
				if (done)
				{
					State = MapBrowserState.Ready;
					Stage = "就绪";
					ProgressPercent = 100;
				}
				else AdvanceProgress(FactsCompletePercent, 100);
			}
			if (State == MapBrowserState.Ready) _presentation.Tick(delta);
		}
		catch (Exception error)
		{
			Fail(new LoadFailure("BrowseError", Stage, error.Message));
			return;
		}
		StateChanged?.Invoke();
	}

	void AdvanceProgress(int startPercent, int endPercent)
	{
		double completed = ProgressTotal > 0 ? Math.Clamp((double)ProgressStep / ProgressTotal, 0, 1) : 0;
		int percent = startPercent + (int)((endPercent - startPercent) * completed);
		ProgressPercent = Math.Max(ProgressPercent, Math.Min(percent, 99));
	}

	internal void BackToMapLibrary()
	{
		Dispose();
		_actions?.BackToMapLibrary();
	}

	void Fail(LoadFailure failure)
	{
		_flow.Dispose();
		_presentation?.Stop();
		Failure = failure;
		State = MapBrowserState.Failed;
		Stage = $"{failure.Code}：{failure.Message}";
		StateChanged?.Invoke();
	}

	public void Dispose()
	{
		if (State == MapBrowserState.Closed) return;
		_flow.Dispose();
		_presentation?.Stop();
		_presentation = null;
		State = MapBrowserState.Closed;
		StateChanged?.Invoke();
	}
}
