using System;
using World.Application.Loading;
using World.Application.Pages;
using World.Data.Work;
using World.Logic;

namespace World.Tests;

/// <summary>地图浏览控制器的纯逻辑检查：启动、读取失败与返回意图。</summary>
internal static class MapBrowserControllerCheck
{
	sealed class Actions : IMapBrowserPageActions
	{
		public int Backs;
		public void BackToMapLibrary() => Backs++;
	}

	sealed class Flow : IMapBrowseFlow
	{
		public string Path;
		public int Polls, Disposals;
		public BrowseOutcome Outcome = BrowseOutcome.Running;
		public void Start(string path) => Path = path;
		public BrowseOutcome Poll() { Polls++; return Outcome; }
		public bool TryReadProgress(out WorkSignal signal) { signal = default; return false; }
		public void Dispose() => Disposals++;
	}

	sealed class Presentation : IMapBrowserPresentation
	{
		public int Begins, Steps, Ticks, Stops;
		public int BuildTotal => 1;
		public int BuildStep => 0;
		public string BuildStage => "building";
		public void Begin(ImportedWorldFactsSource source) => Begins++;
		public bool StepBuild(double budgetMs) { Steps++; return false; }
		public void Tick(double delta) => Ticks++;
		public void Stop() => Stops++;
	}

	internal static int Run()
	{
		int assertions = 0;
		void Require(bool condition, string message)
		{
			if (!condition) throw new InvalidOperationException("[MAP-BROWSER-CONTROLLER] " + message);
			assertions++;
		}

		var actions = new Actions();
		var flow = new Flow();
		var presentation = new Presentation();
		using var controller = new MapBrowserController("sample.wmap", flow, actions);
		Require(controller.State == MapBrowserState.Idle && !controller.IsBusy, "Initial state must be idle.");
		controller.Start(presentation);
		Require(controller.State == MapBrowserState.Loading && controller.IsBusy && flow.Path == "sample.wmap",
			"Start must enter Loading and forward the path.");
		controller.Tick(0.1);
		Require(flow.Polls == 1 && presentation.Begins == 0 && presentation.Ticks == 0,
			"Pending load must not begin or tick the presentation.");
		controller.BackToMapLibrary();
		controller.Tick(0.1);
		controller.Dispose();
		Require(actions.Backs == 1 && controller.State == MapBrowserState.Closed && flow.Disposals == 1
			&& flow.Polls == 1 && presentation.Stops == 1, "Back must close once, stop polling and notify the host.");

		var failure = new LoadFailure("ReadError", "decode", "broken package");
		var failedFlow = new Flow { Outcome = BrowseOutcome.Failed(failure) };
		var failedPresentation = new Presentation();
		using var failed = new MapBrowserController("broken.wmap", failedFlow);
		failed.Start(failedPresentation);
		failed.Tick(0.1);
		failed.Tick(0.1);
		Require(failed.State == MapBrowserState.Failed && ReferenceEquals(failed.Failure, failure)
			&& failedFlow.Polls == 1 && failedPresentation.Stops == 1, "Read failure must retain details and stop work.");
		failed.BackToMapLibrary();
		Require(failed.State == MapBrowserState.Closed, "Back without a host must still close the controller.");
		return assertions;
	}
}
