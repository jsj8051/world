using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using World.Application.Generation;
using World.Application.Loading;
using World.Application.Pages;
using World.Application.World;
using World.Data;
using World.Data.Generation;
using World.Data.Work;
using World.Logic;
using World.Utils;

namespace World.Tests;

static class SculptingGenerationTests
{
	internal static void ProductionEntryPublishesEveryCompleteRound()
		=> CheckProductionEntry(1, verifySnapshot: true);

	internal static void ProductionScaleSmoke()
		=> CheckProductionEntry(WorldGenerationPresets.Production.Resolution, verifySnapshot: false);

	static void CheckProductionEntry(int resolution, bool verifySnapshot)
	{
		var options = WorldGenerationPresets.Production;
		var stopwatch = Stopwatch.StartNew();
		var config = new GenerationConfig();
		using var flow = resolution == options.Resolution
			? WorldGenerationFlow.CreateProduction(config)
			: new WorldGenerationFlow(WorldGenerationSpecs.Create(config), options with { Resolution = resolution });
		flow.Start();
		var outcome = flow.Poll();
		while (outcome.State == WorldGenerationState.Running && stopwatch.Elapsed < TimeSpan.FromMinutes(3))
		{
			Thread.Sleep(1);
			outcome = flow.Poll();
		}
		Program.True(outcome.State == WorldGenerationState.Succeeded,
			$"配置页的生成流程必须交付完整雕刻世界。{outcome.Failure?.Message}");
		var work = new List<WorkSignal>();
		while (flow.TryReadProgress(out var signal)) work.Add(signal);
		var startedStages = work.Where(signal => signal.Kind == WorkSignalKind.StageStarted)
			.Select(signal => signal.StageId);
		Program.True(startedStages.SequenceEqual(new[]
		{
			"grid", "plate", "terrain", "sculpting", "sculpting.plate.movement", "sculpting.surface.transport",
			"sculpting.terrain", "sculpting.climate", "sculpting.hydrology",
			"sculpting.biome.classification", "sculpting.index",
		}) && work[^1].Kind == WorkSignalKind.JobCompleted, "加载页只能看到按实际工作顺序发布的阶段");
		var result = outcome.Result;
		var sim = result.Simulation;
		Program.True(sim.Facts == null, "后台生成的完整快照由会话发布");
		using var world = new WorldSession(reportEvolutionFailure: error => throw new InvalidOperationException("雕刻失败", error));
		IGeographyEvolutionControl control = world;
		var published = new List<long>();
		world.WorldPublished += facts => published.Add(facts.SculptingRoundIndex.Value);
		world.Adopt(result);
		var initial = sim.Facts;
		long start = initial.SculptingRoundIndex.Value;
		Console.WriteLine($"  res{resolution} 初始雕刻至第 {start} 轮，耗时 {stopwatch.Elapsed.TotalSeconds:F1}s");
		Program.True(start == options.InitialSculptingRounds && initial.SourceTimeMa == null
			&& control.Status == EvolutionControlStatus.Paused, "生成入口发布完整轮次并处于暂停状态");
		var drainedHeights = (float[])initial.HeightM.Clone();
		InitialInlandSeaOps.Fill(sim.Ball, drainedHeights, initial.Crust.Type, initial.SeaLevelM);
		Program.True(initial.HeightM.SequenceEqual(drainedHeights), "首张地图的地表在派生水系之前完成封闭浅海清理");
		Program.True(control.Seek(start + 2) == null, "应可跳转到未来轮号");
		var playback = Stopwatch.StartNew();
		while (world.EvolutionBusy && playback.Elapsed < TimeSpan.FromMinutes(5))
		{
			world.Tick(0);
			Thread.Sleep(1);
		}
		Program.True(control.Status == EvolutionControlStatus.Paused
			&& published.SequenceEqual(new[] { start, start + 1, start + 2 }),
			"跳转必须顺序发布每个完整中间轮次");
		Program.True(sim.Facts.BiomeByCell.Count == sim.Facts.HeightM.Length
			&& sim.Facts.BiomeByCell.Any(type => type.HasValue), "生产入口发布与地表对齐的群系");
		if (verifySnapshot)
		{
			var snapshot = GeographySnapshotCapture.Capture(sim.Facts,
				Guid.NewGuid(), "雕刻地图", "", DateTimeOffset.UnixEpoch, "tests");
			var imported = ImportedWorldFactsFactory.Build(snapshot).Facts;
			Program.True(imported.HeightM.SequenceEqual(sim.Facts.HeightM)
				&& imported.BiomeByCell.SequenceEqual(sim.Facts.BiomeByCell), "只靠地图事实就能恢复相同群系");

			WorldFacts beforeRegeneration = sim.Facts;
			world.Regenerate();
			Program.True(ReferenceEquals(world.Sim, sim) && !ReferenceEquals(sim.Facts, beforeRegeneration)
				&& sim.Facts.SculptingRoundIndex == start && sim.Facts.Evolution == null && published[^1] == start,
				"重新生成复用当前查看器的事实来源，并发布新世界的首轮雕刻");
		}
		Console.WriteLine($"  res{resolution} 发布至第 {world.SculptingRoundIndex} 轮，共 {sim.Ball.CellIds.Length} 格，累计 {stopwatch.Elapsed.TotalSeconds:F1}s");
	}

	internal static void CompleteRoundsUseOneSurfaceAndCommitInOrder()
	{
		var spec = new WorldSpec { Seed = 42 };
		var simulation = new WorldGenSimulation(1, 2f);
		LegacyWorldGeneration.RunStatic(simulation, spec);
		var initial = simulation.Facts;
		var north = new SpherePointD(0, 1, 0);
		var rotations = initial.Plates.Select(plate => new PlateRotation(plate.PlateId, north,
			plate.PlateId % 2 == 0 ? .25 : -.25)).ToArray();
		var sculpting = new WorldSculptingGeneration(initial, spec, rotations);
		var modules = new List<string>();
		var first = sculpting.PrepareNext(CancellationToken.None, modules.Add);
		Program.True(modules.SequenceEqual(new[]
		{
			"plate.movement", "surface.transport", "terrain", "climate", "hydrology", "biome.classification", "index",
		}), "本轮的全部模块按事实依赖执行");
		Program.True(sculpting.CompletedRounds == 0 && ReferenceEquals(simulation.Facts, initial),
			"完整候选产生前后都不会提前提交");
		CheckFacts(first, initial, 1);
		var firstTransport = first.Round.Data.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
		float ridgeElevation = spec.Terrain.RidgeCrestDepthM * spec.Terrain.VerticalScale;
		var gaps = Enumerable.Range(0, firstTransport.Surface.CellCount)
			.Where(cell => firstTransport.Contacts[cell] == SculptingCrustContact.OceanSpreading).ToArray();
		float sea = firstTransport.Surface.SeaLevelM;
		float waterline = sea - Math.Min(20, .1f * (sea - ridgeElevation));
		Program.True(gaps.Length > 0 && gaps.All(cell =>
		{
			var opening = firstTransport.Coverage.Openings[cell].Value;
			float firstShore = Math.Min(waterline, first.Round.Source.Surface.HeightM[opening.First]);
			float secondShore = Math.Min(waterline, first.Round.Source.Surface.HeightM[opening.Second]);
			float height = firstTransport.Surface.HeightM[cell];
			return height >= Math.Min(ridgeElevation, Math.Min(firstShore, secondShore)) - .01f
				&& height <= Math.Max(ridgeElevation, Math.Max(firstShore, secondShore)) + .01f;
		}), "新洋壳过渡至采用同一垂直倍率的洋脊基底");
		sculpting.Commit(first);
		var second = sculpting.PrepareNext(CancellationToken.None);
		Program.True(ReferenceEquals(second.Round.Source, first.Round.Candidate), "第二轮消费第一轮已雕刻地表");
		CheckFacts(second, first.Presentation.Facts, 2);
		sculpting.Commit(second);
		Program.True(sculpting.CompletedRounds == 2, "完成轮数随成功提交递增");

		using var cancellation = new CancellationTokenSource();
		bool canceled = false;
		try
		{
			sculpting.PrepareNext(cancellation.Token, stage =>
			{
				if (stage == "climate") cancellation.Cancel();
			});
		}
		catch (OperationCanceledException) { canceled = true; }
		Program.True(canceled && sculpting.CompletedRounds == 2, "同轮中途取消保留最近完整状态");
	}

	static void CheckFacts(WorldSculptingGeneration.Candidate candidate, WorldFacts previous, long round)
	{
		var facts = candidate.Presentation.Facts;
		var data = candidate.Round.Data;
		var transported = data.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId).Surface;
		Program.True(facts.SculptingRoundIndex == round && facts.SourceTimeMa == null
			&& data.ReadableModuleIds.All(id => data.Read<WorldSculptingModuleOutput>(id).Round.Index == round),
			"所有领域输出属于同一个无地质时间的轮次");
		Program.True(!transported.HeightM.SequenceEqual(facts.HeightM)
			&& candidate.Round.Candidate.Surface.HeightM.SequenceEqual(facts.HeightM)
			&& !previous.HeightM.SequenceEqual(facts.HeightM), "局部地形改变量进入可提交的持久地表");
		Program.True(!previous.TemperatureC.SequenceEqual(facts.TemperatureC)
			&& facts.BiomeByCell.Any(type => type.HasValue), "气候随本轮地形更新并产出陆地生物群系");
		Program.True(facts.BiomeByCell.SequenceEqual(BiomeClassifier.Classify(facts.Ball,
			candidate.Presentation.Terrain, candidate.Presentation.Climate, facts.Lakes)),
			"群系与同轮地形、气候及湖泊一致");
	}
}
