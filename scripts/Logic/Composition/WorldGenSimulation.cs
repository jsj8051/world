using System;
using System.Threading;
using World.H3Grid;
using World.Data;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 世界生成空间 · 纯逻辑入口（非 Godot 节点）：独占球壳数据层与五个阶段，按固定因果序驱动。
/// 引擎耦合必须为零——不得出现 <c>GD.*</c> / <c>Node*</c> / <c>Godot.Color</c> / <c>Mathf</c>。
/// 全量重算复用同一实例再 <c>Run()</c>，不要 new；参数一律由 <c>Run()</c> 接收。
/// </summary>
public sealed class WorldGenSimulation
{
	/// <summary>球壳数据层（H3）。只读口，供表现层判读。</summary>
	public Ball Ball { get; }

	/// <summary>世界事实快照（<see cref="Run"/> 末尾捕获，每次 Run 一份新的）。
	/// 下游读它，不要顺着下面五个阶段句柄去翻各自的产物。</summary>
	public WorldFacts Facts => _state?.Facts;
	State _state;

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

	/// <summary>全量重算。五阶段按固定因果序执行，顺序不可换（后段吃前段产物）：
	/// ⓪ 板块（含壳型）→ ① 地形（含世界事实）→ ② 气候 → ③ 水文 → ④ 空间索引。
	/// 本方法是唯一把全量 <see cref="WorldSpec"/> 交给 ⓪ 的地方（⓪ 要定布局档），其余阶段按段切片下传。
	/// 跑完捕获一份 <see cref="Facts"/>（同一快照里的场必来自同一次生成）。</summary>
	/// <param name="spec">世界定义（见 <see cref="WorldSpec"/>）。</param>
	public void Run(WorldSpec spec) => Publish(PrepareStatic(spec));

	internal State PrepareStatic(WorldSpec spec)
	{
		var plate = new PlatePipeline();
		plate.Run(Ball, spec);
		var terrain = new TerrainPipeline();
		terrain.Run(Ball, spec.Seed, spec.Terrain, plate.PlateOfCell, plate.Plates, plate.Crust,
			plate.Boundaries, plate.Boundary, plate.LandBaseDropM);
		return BuildState(spec, plate, terrain, null, CancellationToken.None);
	}

	internal EvolutionSimulation CreateEvolutionSolver(WorldSpec spec, out EvolutionFrame initialFrame)
	{
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (spec.Evolution == null) throw new ArgumentException("世界定义缺 Evolution 段", nameof(spec));
		var plate = new PlateManager();
		plate.Run(Ball, spec);
		float[] baseHeight = TerrainManager.BuildInitialHeight(Ball, spec, plate.Crust, plate.LandBaseDropM);
		var grid = EvolutionGrid.FromBall(Ball, SphericalFbmNoise.WorldRadiusKm, spec.Evolution.GeometryAngularToleranceRad);
		return EvolutionSimulation.Create(spec, grid, plate.PlateOfCell, plate.Crust, baseHeight, plate.Plates, out initialFrame);
	}

	internal State PrepareEvolution(EvolutionSimulation solver, EvolutionFrame frame, CancellationToken cancellation = default)
	{
		cancellation.ThrowIfCancellationRequested();
		if (!ReferenceEquals(frame.Definition, solver.Definition)
			|| frame.Definition.H3Resolution != Ball.Res || frame.Material.CellCount != Ball.CellIds.Length)
			throw new ArgumentException("演化帧与世界网格或求解器定义不一致", nameof(frame));
		long timing = WorldBuildProfile.Start();
		WorldSpec spec = frame.Definition.CreateSpecCopy();
		WorldSurfaceInput surface = WorldSurfaceInput.FromEvolution(solver, frame, spec.Evolution);
		WorldBuildProfile.Mark(ref timing, "surface-adapter");
		var plate = new PlatePipeline();
		plate.SetSurface(surface);
		var terrain = new TerrainPipeline();
		TectonicInfluenceSet tectonic = TectonicInfluenceOps.Build(Ball, surface.Boundaries, surface.Plates,
			surface.Crust, surface.PlateOfCell, surface.Boundary.EdgeMotion,
			TectonicInfluenceOps.WorldSearchRadiusKm, surface.Boundary, frame.Projection.SurfaceCrustType);
		terrain.SetSurface(Ball, surface.HeightM, spec.Terrain.SeaLevelM, tectonic);
		WorldBuildProfile.Mark(ref timing, "terrain");
		return BuildState(spec, plate, terrain, frame, cancellation);
	}

	internal void Publish(State state) => _state = state;

	State BuildState(WorldSpec spec, PlatePipeline plate, TerrainPipeline terrain,
		EvolutionFrame evolution, CancellationToken cancellation)
	{
		float seaM = spec.Terrain.SeaLevelM;
		var climate = new ClimatePipeline();
		var hydrology = new HydrologyPipeline();
		var index = new IndexPipeline();
		long timing = WorldBuildProfile.Start();
		cancellation.ThrowIfCancellationRequested();
		climate.Run(Ball, terrain, seaM, spec.Climate, spec.Seed, cancellation);
		WorldBuildProfile.Mark(ref timing, "climate-total");
		cancellation.ThrowIfCancellationRequested();
		hydrology.Run(Ball, terrain, seaM, climate.Precipitation);
		WorldBuildProfile.Mark(ref timing, "hydrology");
		index.Run(Ball, terrain, hydrology.Rivers);
		WorldBuildProfile.Mark(ref timing, "spatial-index");
		cancellation.ThrowIfCancellationRequested();
		return new State(plate, terrain, climate, hydrology, index,
			new WorldFacts(Ball, plate, terrain, climate, hydrology, index, evolution));
	}
}
