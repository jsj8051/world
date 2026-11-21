using System;
using System.Collections.Generic;
using World.Application.Loading;
using World.Application.Pages;
using World.Data;
using World.Data.Maps;
using World.Data.Work;
using World.Logic;

namespace World.Tests;

internal static class MapBrowserControllerTests
{
	static readonly Lazy<ImportedWorldFactsSource> Source = new(CreateSource);

	public static void SuccessBuildsOnceBeforeTicking()
	{
		var flow = new FakeFlow();
		var presentation = new FakePresentation();
		using var controller = new MapBrowserController("sample.wmap", flow);
		Program.True(controller.State == MapBrowserState.Idle && !controller.IsBusy, "Controller must start idle.");
		controller.Start(presentation);
		Program.True(controller.State == MapBrowserState.Loading && controller.IsBusy
			&& flow.StartCalls == 1 && flow.Path == "sample.wmap", "Start must forward the path once and enter Loading.");
		Program.Throws(() => controller.Start(presentation), "A browse must only start once.");
		controller.Tick(0.1);
		Program.True(presentation.BeginCalls == 0 && presentation.TickCalls == 0, "Loading must not touch the presentation.");

		flow.Outcome = BrowseOutcome.Succeeded(Source.Value);
		controller.Tick(0.2);
		Program.True(controller.State == MapBrowserState.Building && controller.IsBusy && flow.DisposeCalls == 1,
			"Successful loading must release the flow and enter Building.");
		Program.True(controller.Title == Source.Value.Snapshot.Manifest.Title
			&& controller.CellCount == Source.Value.Facts.Ball.CellIds.Length, "Source metadata must reach the page.");
		Program.True(ReferenceEquals(presentation.Source, Source.Value) && presentation.BeginCalls == 1
			&& presentation.StepCalls == 0 && presentation.TickCalls == 0, "Success must only Begin, without building or ticking that frame.");
		int polls = flow.PollCalls;
		controller.Tick(0.3);
		controller.Tick(0.4);
		Program.True(controller.State == MapBrowserState.Building && presentation.StepCalls == 2
			&& presentation.TickCalls == 0 && presentation.LastBudget == 8.0, "Incomplete builds must step within budget without ticking.");
		presentation.BuildDone = true;
		controller.Tick(0.5);
		Program.True(controller.State == MapBrowserState.Ready && !controller.IsBusy && controller.Stage == "就绪"
			&& presentation.TickCalls == 1 && presentation.LastDelta == 0.5, "Completed build must enter Ready and tick the current frame.");
		controller.Tick(0.6);
		Program.True(presentation.BeginCalls == 1 && presentation.StepCalls == 3 && presentation.TickCalls == 2
			&& flow.PollCalls == polls && presentation.LastDelta == 0.6, "Ready must only tick, never restart or poll the disposed flow.");
	}

	public static void ReadFailureStopsPolling()
	{
		var failure = new LoadFailure("ReadError", "decode", "broken package");
		var flow = new FakeFlow { Outcome = BrowseOutcome.Failed(failure) };
		var presentation = new FakePresentation();
		using var controller = new MapBrowserController("broken.wmap", flow);
		controller.Start(presentation);
		controller.Tick(0.1);
		Program.True(controller.State == MapBrowserState.Failed && !controller.IsBusy
			&& ReferenceEquals(controller.Failure, failure) && controller.Stage.Contains(failure.Code)
			&& controller.Stage.Contains(failure.Message) && flow.DisposeCalls == 1 && presentation.StopCalls == 1, "Read failure details must survive and release the flow.");
		flow.Outcome = BrowseOutcome.Succeeded(Source.Value);
		controller.Tick(0.1);
		Program.True(flow.PollCalls == 1 && presentation.BeginCalls == 0 && presentation.TickCalls == 0,
			"Failed controller must ignore a late successful result.");
	}

	public static void FlowExceptionsBecomeFailures()
	{
		foreach (bool duringStart in new[] { true, false })
		{
			var flow = new FakeFlow { ThrowOnStart = duringStart, ThrowOnPoll = !duringStart };
			var presentation = new FakePresentation();
			using var controller = new MapBrowserController("sample.wmap", flow);
			controller.Start(presentation);
			controller.Tick(0.1);
			Program.True(controller.State == MapBrowserState.Failed && flow.DisposeCalls == 1
				&& controller.Failure.Code == (duringStart ? "LoadError" : "BrowseError")
				&& controller.Failure.Message == "flow failed" && presentation.BeginCalls == 0 && presentation.StopCalls == 1,
				"Start/Poll exception must become a terminal failure without presentation work.");
			int polls = flow.PollCalls;
			controller.Tick(0.1);
			Program.True(flow.PollCalls == polls, "Exception failure must stop polling.");
		}
	}

	public static void BuildFailuresStopPresentation()
	{
		foreach (bool duringBegin in new[] { true, false })
		{
			var flow = new FakeFlow { Outcome = BrowseOutcome.Succeeded(Source.Value) };
			var presentation = new FakePresentation { ThrowOnBegin = duringBegin, ThrowOnStep = !duringBegin };
			using var controller = new MapBrowserController("sample.wmap", flow);
			controller.Start(presentation);
			controller.Tick(0.1);
			if (!duringBegin) controller.Tick(0.1);
			Program.True(controller.State == MapBrowserState.Failed && !controller.IsBusy
				&& controller.Failure.Code == "BrowseError" && controller.Failure.Message == "build failed",
				"Begin/StepBuild exception must become a browse failure.");
			int steps = presentation.StepCalls;
			controller.Tick(0.1);
			Program.True(presentation.BeginCalls == 1 && presentation.StepCalls == steps && presentation.TickCalls == 0
				&& flow.PollCalls == 1 && flow.DisposeCalls >= 1 && presentation.StopCalls == 1, "Build failure must stop all subsequent work.");
		}
	}

	public static void DisposeAndBackIgnoreLateResults()
	{
		foreach (bool back in new[] { false, true })
		{
			var flow = new FakeFlow();
			var presentation = new FakePresentation();
			var actions = new FakeActions();
			var controller = new MapBrowserController("sample.wmap", flow, actions);
			controller.Start(presentation);
			controller.Tick(0.1);
			if (back) controller.BackToMapLibrary();
			else controller.Dispose();
			int polls = flow.PollCalls;
			int progressReads = flow.ProgressReads;
			flow.Outcome = BrowseOutcome.Succeeded(Source.Value);
			flow.Progress.Enqueue(WorkSignal.Unit("late", "late progress", 1, 1));
			controller.Tick(0.1);
			controller.Dispose();
			Program.True(controller.State == MapBrowserState.Closed && !controller.IsBusy && flow.DisposeCalls == 1 && presentation.StopCalls == 1,
				"Closing must release the flow exactly once and be idempotent.");
			Program.True(flow.PollCalls == polls && flow.ProgressReads == progressReads && presentation.BeginCalls == 0
				&& presentation.TickCalls == 0 && actions.Backs == (back ? 1 : 0), "Closed controller must ignore late results/progress and route Back only on request.");
		}
	}

	public static void ClosingStopsBuildAndReadyTicks()
	{
		foreach (bool ready in new[] { false, true })
		{
			var flow = new FakeFlow { Outcome = BrowseOutcome.Succeeded(Source.Value) };
			var presentation = new FakePresentation { BuildDone = ready };
			using var controller = new MapBrowserController("sample.wmap", flow);
			controller.Start(presentation);
			controller.Tick(0.1);
			controller.Tick(0.1);
			Program.True(controller.State == (ready ? MapBrowserState.Ready : MapBrowserState.Building), "Setup must reach the expected state.");
			controller.BackToMapLibrary();
			int steps = presentation.StepCalls, ticks = presentation.TickCalls;
			controller.Tick(0.1);
			Program.True(controller.State == MapBrowserState.Closed && presentation.StepCalls == steps
				&& presentation.TickCalls == ticks && flow.PollCalls == 1 && presentation.StopCalls == 1, "Back without a host must stop build/ready work.");
		}
	}

	public static void ProgressPropagatesAcrossLoadingAndBuilding()
	{
		var flow = new FakeFlow();
		var presentation = new FakePresentation { BuildStep = 2, BuildTotal = 7, BuildStage = "mesh" };
		using var controller = new MapBrowserController("sample.wmap", flow);
		int notifications = 0;
		controller.StateChanged += () => notifications++;
		controller.Start(presentation);
		flow.Progress.Enqueue(WorkSignal.Started("decode", "decode"));
		flow.Progress.Enqueue(WorkSignal.Unit("decode", "decoded blocks", 3, 10));
		flow.Progress.Enqueue(WorkSignal.Finished());
		controller.Tick(0.1);
		Program.True(controller.State == MapBrowserState.Loading && controller.Stage == "decoded blocks"
			&& controller.ProgressStep == 3 && controller.ProgressTotal == 10 && notifications == 2,
			"Loading must drain progress, keep the latest unit, ignore JobCompleted, and notify observers.");
		flow.Outcome = BrowseOutcome.Succeeded(Source.Value);
		controller.Tick(0.1);
		Program.True(controller.Stage == "装配地图画面" && controller.ProgressStep == 0 && controller.ProgressTotal == 7,
			"Success must announce the building phase and reset loading progress to the build total.");
		controller.Tick(0.1);
		Program.True(controller.Stage == "mesh" && controller.ProgressStep == 2 && controller.ProgressTotal == 7,
			"Building must expose presentation stage and counters.");
		presentation.BuildStep = 7;
		presentation.BuildDone = true;
		controller.Tick(0.1);
		Program.True(controller.Stage == "就绪" && controller.ProgressStep == 7 && controller.ProgressTotal == 7
			&& notifications == 5, "Ready must preserve completed build progress and notify observers.");
	}

	public static void AbandonedFlowClosesWithoutBuilding()
	{
		var flow = new FakeFlow { Outcome = BrowseOutcome.Abandoned };
		var presentation = new FakePresentation();
		using var controller = new MapBrowserController("sample.wmap", flow);
		controller.Start(presentation);
		controller.Tick(0.1);
		controller.Tick(0.1);
		Program.True(controller.State == MapBrowserState.Closed && controller.Failure == null
			&& flow.PollCalls == 1 && flow.DisposeCalls == 1 && presentation.BeginCalls == 0,
			"Abandoned flow must close without reporting a failure or beginning presentation.");
	}

	static ImportedWorldFactsSource CreateSource()
	{
		var simulation = new WorldGenSimulation(1, PlanetGeometry.ProductionRadius);
		LegacyWorldGeneration.RunStatic(simulation, new WorldSpec());
		GeographySnapshot snapshot = GeographySnapshotCapture.Capture(simulation.Facts, Guid.NewGuid(),
			"browser fixture", string.Empty, DateTimeOffset.UtcNow, "tests");
		return ImportedWorldFactsFactory.Build(snapshot);
	}

	sealed class FakeFlow : IMapBrowseFlow
	{
		public BrowseOutcome Outcome = BrowseOutcome.Running;
		public readonly Queue<WorkSignal> Progress = new();
		public string Path;
		public int StartCalls, PollCalls, DisposeCalls, ProgressReads;
		public bool ThrowOnStart, ThrowOnPoll;
		public void Start(string path)
		{
			StartCalls++;
			Path = path;
			if (ThrowOnStart) throw new InvalidOperationException("flow failed");
		}
		public BrowseOutcome Poll()
		{
			PollCalls++;
			if (ThrowOnPoll) throw new InvalidOperationException("flow failed");
			return Outcome;
		}
		public bool TryReadProgress(out WorkSignal signal)
		{
			ProgressReads++;
			return Progress.TryDequeue(out signal);
		}
		public void Dispose() => DisposeCalls++;
	}

	sealed class FakePresentation : IMapBrowserPresentation
	{
		public ImportedWorldFactsSource Source;
		public int BeginCalls, StepCalls, TickCalls, StopCalls;
		public bool BuildDone, ThrowOnBegin, ThrowOnStep;
		public double LastBudget, LastDelta;
		public int BuildTotal { get; set; } = 7;
		public int BuildStep { get; set; }
		public string BuildStage { get; set; } = "building";
		public void Begin(ImportedWorldFactsSource source)
		{
			BeginCalls++;
			Source = source;
			if (ThrowOnBegin) throw new InvalidOperationException("build failed");
		}
		public bool StepBuild(double budgetMs)
		{
			StepCalls++;
			LastBudget = budgetMs;
			if (ThrowOnStep) throw new InvalidOperationException("build failed");
			return BuildDone;
		}
		public void Tick(double delta) { TickCalls++; LastDelta = delta; }
		public void Stop() => StopCalls++;
	}

	sealed class FakeActions : IMapBrowserPageActions
	{
		public int Backs;
		public void BackToMapLibrary() => Backs++;
	}
}
