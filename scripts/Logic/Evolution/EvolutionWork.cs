using System;
using System.Threading.Tasks;

namespace World.Logic;

/// <summary>连续格区间并行。调用方负责让每个区间独占它的写入范围。</summary>
internal static class EvolutionWork
{
	internal const int CellsPerBatch = 4096;
	static readonly ParallelOptions Options = new()
	{
		MaxDegreeOfParallelism = Math.Min(8, Math.Max(1, Environment.ProcessorCount - 1)),
	};
	internal static int MaxWorkers => Options.MaxDegreeOfParallelism;

	public static void ForCells(int count, Action<int, int> run)
	{
		if (count <= CellsPerBatch || Options.MaxDegreeOfParallelism == 1)
		{
			run(0, count);
			return;
		}

		int batches = (count - 1) / CellsPerBatch + 1;
		Parallel.For(0, batches, Options, batch =>
		{
			int start = batch * CellsPerBatch;
			run(start, Math.Min(count - start, CellsPerBatch) + start);
		});
	}
}
