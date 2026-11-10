using System;
using System.Threading;
using World.Data;
using World.Data.Work;
using World.Logic;

namespace World.Scene;

/// <summary>生成结果：Sim 与候选 State 由工作线程产出，主线程 Adopt。</summary>
internal sealed record WorldGenerationResult(WorldGenSimulation Simulation, WorldGenSimulation.State State,
	EvolutionSimulation Solver, WorldSpec Spec);

/// <summary>
/// 世界生成的纯数据 Job：建 Sim、跑五阶段或演化路径，只发领域信号，不认识进度/UI。
/// 监控器可订阅 <see cref="Signal"/>；没有订阅者时行为不变。
/// </summary>
internal sealed class WorldGenerationJob : IWorkSignalSource
{
	public event Action<WorkSignal> Signal;

	internal WorldGenerationResult Run(WorldSpec spec, int resolution, float radius,
		bool evolution, int evolutionSteps, CancellationToken token)
	{
		var simulation = new WorldGenSimulation(resolution, radius);
		simulation.Signal += signal => Signal?.Invoke(signal);

		try
		{
			WorldGenSimulation.State state;
			EvolutionSimulation solver = null;

			if (evolution)
			{
				solver = simulation.CreateEvolutionSolver(spec, out EvolutionFrame frame);
				token.ThrowIfCancellationRequested();
				frame = solver.CalculateToStep(frame, evolutionSteps, token);
				token.ThrowIfCancellationRequested();
				state = simulation.PrepareEvolution(solver, frame, token);
			}
			else
			{
				state = simulation.PrepareStatic(spec);
			}

			token.ThrowIfCancellationRequested();
			Signal?.Invoke(WorkSignal.Finished());
			return new WorldGenerationResult(simulation, state, solver, spec);
		}
		catch (Exception error)
		{
			Signal?.Invoke(WorkSignal.Failed(error.Message));
			throw;
		}
	}
}
