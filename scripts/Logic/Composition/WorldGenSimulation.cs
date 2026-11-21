using System;
using System.Threading;
using World.Data;
using World.Data.Work;
using World.H3Grid;

namespace World.Logic;

/// <summary>持有世界的球面网格与已发布事实，提供雕刻前的初始板块和地表。</summary>
public sealed class WorldGenSimulation : IWorldFactsSource
{
	/// <summary>球壳数据层（H3）。只读口，供表现层判读。</summary>
	public Ball Ball { get; }

	/// <summary>最近一次发布的完整世界事实。</summary>
	public WorldFacts Facts => _state?.Facts;
	State _state;

	/// <summary>领域工作信号：生成阶段完成时发出；不引用进度/UI。</summary>
	public event Action<WorkSignal> Signal;

	internal sealed record State(PlatePipeline Plate, TerrainPipeline Terrain, ClimatePipeline Climate,
		HydrologyPipeline Hydrology, IndexPipeline Index, WorldFacts Facts);

	/// <summary>⓪ 板块（划分 + 壳型）。</summary>
	public PlatePipeline Plate => _state?.Plate;
	/// <summary>① 地形（最终高度 + 世界事实）。</summary>
	public TerrainPipeline Terrain => _state?.Terrain;
	/// <summary>② 气候（降水 / 温度）。</summary>
	public ClimatePipeline Climate => _state?.Climate;
	/// <summary>③ 水文（河网 / 流域 / 湖泊 / 水系）。</summary>
	public HydrologyPipeline Hydrology => _state?.Hydrology;
	/// <summary>④ 空间索引。</summary>
	public IndexPipeline Index => _state?.Index;

	/// <param name="resLevel">世界空间离散档（生产 = res4 冻结）。</param>
	public WorldGenSimulation(int resLevel, float radius)
	{
		Ball = new Ball(resLevel, radius);
	}

	internal (PlatePipeline Plate, TerrainPipeline Terrain) PrepareInitialSurface(WorldSpec spec, CancellationToken cancellation)
	{
		cancellation.ThrowIfCancellationRequested();
		Signal?.Invoke(WorkSignal.Started("plate", "初始化板块"));
		var plate = new PlatePipeline();
		plate.Run(Ball, spec);
		Signal?.Invoke(WorkSignal.Unit("plate", "板块", 1, 1));

		cancellation.ThrowIfCancellationRequested();
		Signal?.Invoke(WorkSignal.Started("terrain", "塑造初始地表"));
		var terrain = new TerrainPipeline();
		terrain.Run(Ball, spec.Seed, spec.Terrain, plate.PlateOfCell, plate.Plates, plate.Crust,
			plate.Boundaries, plate.Boundary, plate.LandBaseDropM);
		Signal?.Invoke(WorkSignal.Unit("terrain", "地形", 1, 1));
		return (plate, terrain);
	}

	internal void Publish(State state) => _state = state;
}
