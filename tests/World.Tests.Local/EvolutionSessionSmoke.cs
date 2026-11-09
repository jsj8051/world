using System;
using System.Diagnostics;
using System.Threading;
using World.Data;
using World.Logic;
using World.Scene;

namespace World.Tests;

static class EvolutionSessionSmoke
{
	public static void Run()
	{
		var world = new WorldGenSimulation(1, 2f);
		var spec = new WorldSpec { Seed = 42 };
		spec.Evolution.NewOceanicThicknessKm = 10_000;
		var probe = world.CreateEvolutionSolver(spec, out var zero);
		var one = probe.CalculateToStep(zero, 1);
		var two = probe.CalculateToStep(one, 2);
		double firstCost = zero.Mantle.StoredVolumeKm3 - one.Mantle.StoredVolumeKm3;
		double secondCost = zero.Mantle.StoredVolumeKm3 - two.Mantle.StoredVolumeKm3;
		Program.True(firstCost > 0 && secondCost > firstCost, "故障夹具需在第二步继续消耗地幔");
		spec.Evolution.InitialMantleVolumeKm3 = (firstCost + secondCost) / 2;
		var failing = Initialize(world, spec);
		WorldFacts initial = world.Facts;
		string rejection = failing.Seek(initial.Evolution.Clock.StepYears * 3 / 1_000_000d);
		Program.True(rejection == null && failing.Status == EvolutionControlStatus.Seeking && failing.Busy,
			"有效跳转应进入后台计算");
		Exception fault = null;
		Drain(failing, error => fault = error);
		Program.True(fault is EvolutionStepException { Code: EvolutionProblemCode.InsufficientMantleMaterial }
			&& failing.Status == EvolutionControlStatus.Faulted && ReferenceEquals(world.Facts, initial),
			"失败的跳转不能发布部分结果");
		failing.ConfirmFault();
		Program.True(failing.Status == EvolutionControlStatus.Paused && ReferenceEquals(world.Facts, initial),
			"确认故障应回到上一个已发布的年代");
		failing.Step();
		WorldFacts recovered = Drain(failing, error => throw error);
		Program.True(recovered?.Evolution.Clock.StepIndex == 1, "确认故障后可从原快照推进到合法年代");
		failing.Stop();

		spec = new WorldSpec { Seed = 42 };
		var cancelled = Initialize(world, spec);
		cancelled.Step();
		Program.True(cancelled.Busy, "单步应启动后台任务");
		cancelled.Stop();
		var replacement = Initialize(world, spec);
		WorldFacts newInitial = world.Facts;
		Drain(cancelled, error => throw error);
		Program.True(ReferenceEquals(world.Facts, newInitial) && newInitial.Evolution.Clock.StepIndex == 0,
			"取消的旧任务不能覆盖重生成的世界");

		replacement.Play();
		replacement.Step();
		Program.True(replacement.Status == EvolutionControlStatus.Playing && !replacement.Busy,
			"播放时不能叠加手动单步");
		replacement.Tick(10, error => throw error);
		Program.True(replacement.Busy && ReferenceEquals(world.Facts, newInitial),
			"播放应异步推进并在准备完成前保留当前快照");
		replacement.Pause();
		WorldFacts published = Drain(replacement, error => throw error);
		Program.True(ReferenceEquals(world.Facts, published) && published.Evolution.Clock.StepIndex == 1
			&& published.Evolution.Projection.HeightM[0] == published.HeightM[0]
			&& replacement.Status == EvolutionControlStatus.Paused,
			"完成的播放步应一起发布演化帧和世界事实");
		replacement.Stop();
		Console.WriteLine("[EVOLVE-SESSION] 失败回退、确认恢复、取消、重生成及播放提交通过");
	}

	static WorldEvolutionSession Initialize(WorldGenSimulation world, WorldSpec spec)
	{
		var solver = world.CreateEvolutionSolver(spec, out var frame);
		world.Publish(world.PrepareEvolution(solver, frame));
		return new WorldEvolutionSession(world, solver);
	}

	static WorldFacts Drain(WorldEvolutionSession session, Action<Exception> report)
	{
		var timer = Stopwatch.StartNew();
		WorldFacts published = null;
		while (session.Busy && timer.Elapsed < TimeSpan.FromSeconds(20))
		{
			published = session.Tick(0, report);
			if (session.Busy) Thread.Sleep(1);
		}
		Program.True(!session.Busy, "后台演化未在测试时限内结束");
		return published;
	}
}
