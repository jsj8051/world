using System;
using System.Threading;
using World.Data;
using World.Data.Work;
using World.Logic;

namespace World.Application.Loading;

/// <summary>生成结果：Sim 与候选 State 由工作线程产出，主线程 Adopt。</summary>
internal sealed record WorldGenerationResult(WorldGenSimulation Simulation, WorldGenSimulation.State State,
	WorldSculptingGeneration Sculpting, WorldSpec Spec);

/// <summary>
/// 世界生成的纯数据 Job：准备初始世界，再逐轮完成生产雕刻。
/// 监控器可订阅 <see cref="Signal"/>；没有订阅者时行为不变。
/// </summary>
internal sealed class WorldGenerationJob : IWorkSignalSource
{
	public event Action<WorkSignal> Signal;

	internal WorldGenerationResult Run(WorldSpec spec, WorldGenerationOptions options, CancellationToken token)
	{
		token.ThrowIfCancellationRequested();
		Signal?.Invoke(WorkSignal.Started("grid", "构建球面网格"));
		var simulation = new WorldGenSimulation(options.Resolution, PlanetGeometry.ProductionRadius);
		return Run(simulation, spec, options.InitialSculptingRounds, token);
	}

	internal WorldGenerationResult Run(WorldGenSimulation simulation, WorldSpec spec,
		int initialSculptingRounds, CancellationToken token)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialSculptingRounds);
		void OnSignal(WorkSignal signal) => Signal?.Invoke(signal);
		simulation.Signal += OnSignal;

		try
		{
			var (plate, terrain) = simulation.PrepareInitialSurface(spec, token);
			var sculpting = new WorldSculptingGeneration(simulation.Ball, plate, terrain, spec);
			WorldGenSimulation.State state = null;
			for (int round = 0; round < initialSculptingRounds; round++)
			{
				string label = $"雕刻第 {round + 1} 轮";
				Signal?.Invoke(WorkSignal.Started("sculpting", label));
				var candidate = sculpting.PrepareNext(token, stage => Signal?.Invoke(
					WorkSignal.Started("sculpting." + stage, $"{label} · {StageLabel(stage)}")));
				sculpting.Commit(candidate);
				state = candidate.Presentation;
				Signal?.Invoke(WorkSignal.Unit("sculpting", label, round + 1, initialSculptingRounds));
			}

			token.ThrowIfCancellationRequested();
			Signal?.Invoke(WorkSignal.Finished());
			return new WorldGenerationResult(simulation, state, sculpting, spec);
		}
		catch (Exception error)
		{
			Signal?.Invoke(WorkSignal.Failed(error.Message));
			throw;
		}
		finally { simulation.Signal -= OnSignal; }
	}

	static string StageLabel(string stage) => stage switch
	{
		PlateMovementModule.ModuleId => "移动板块",
		SculptingSurfaceTransportModule.ModuleId => "搬运地壳",
		SculptingTerrainModule.ModuleId => "修整地形",
		"climate" => "计算气候",
		"hydrology" => "生成水系",
		"biome.classification" => "划分生物群系",
		"index" => "建立空间索引",
		_ => stage,
	};
}
