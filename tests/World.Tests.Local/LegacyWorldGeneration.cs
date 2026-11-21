using System;
using System.Linq;
using System.Threading;
using World.Data;
using World.H3Grid;
using World.Logic;
using World.Utils;

namespace World.Tests;

/// <summary>旧静态世界和物理演化的对照入口，供逻辑测试和诊断使用。</summary>
internal static class LegacyWorldGeneration
{
	internal static void RunStatic(WorldGenSimulation world, WorldSpec spec)
	{
		var (plate, terrain) = world.PrepareInitialSurface(spec, CancellationToken.None);
		world.Publish(BuildState(world.Ball, spec, plate, terrain, null, CancellationToken.None));
	}

	internal static EvolutionSimulation CreateEvolutionSolver(WorldGenSimulation world, WorldSpec spec,
		out EvolutionFrame initialFrame)
	{
		ArgumentNullException.ThrowIfNull(spec);
		if (spec.Evolution == null) throw new ArgumentException("世界定义缺 Evolution 段", nameof(spec));
		var plate = new PlateManager();
		plate.Run(world.Ball, spec);
		float[] baseHeight = TerrainManager.BuildInitialHeight(world.Ball, spec, plate.Crust, plate.LandBaseDropM);
		var grid = EvolutionGrid.FromBall(world.Ball, SphericalFbmNoise.WorldRadiusKm,
			spec.Evolution.GeometryAngularToleranceRad);
		return EvolutionSimulation.Create(spec, grid, plate.PlateOfCell, plate.Crust, baseHeight, plate.Plates, out initialFrame);
	}

	internal static WorldGenSimulation.State PrepareEvolution(WorldGenSimulation world, EvolutionSimulation solver,
		EvolutionFrame frame, CancellationToken cancellation = default)
	{
		cancellation.ThrowIfCancellationRequested();
		Ball ball = world.Ball;
		if (!ReferenceEquals(frame.Definition, solver.Definition)
			|| frame.Definition.H3Resolution != ball.Res || frame.Material.CellCount != ball.CellIds.Length)
			throw new ArgumentException("演化帧与世界网格或求解器定义不一致", nameof(frame));
		long timing = WorldBuildProfile.Start();
		WorldSpec spec = frame.Definition.CreateSpecCopy();
		CrustProjection projection = frame.Projection;
		var plates = solver.Plates.ToArray();
		var crust = new CrustField((CrustType[])projection.Crust.Type.Clone(),
			(int[])projection.Crust.ProvinceId.Clone(), (CrustProvince[])projection.Crust.Provinces.Clone());
		var boundary = PlateBoundaryFields.FromEvolution(projection.PlateBoundaries, projection.Boundary,
			plates, spec.Evolution.ActiveSpeedKmPerMa);
		var surface = new WorldSurfaceInput(projection.PlateOfCell.ToArray(), plates, crust,
			projection.HeightM.ToArray(), projection.PlateBoundaries, boundary);
		WorldBuildProfile.Mark(ref timing, "surface-adapter");
		var plate = new PlatePipeline();
		plate.SetSurface(surface);
		var terrain = new TerrainPipeline();
		var tectonic = TectonicInfluenceOps.Build(ball, surface.Boundaries, surface.Plates, surface.Crust,
			surface.PlateOfCell, surface.Boundary.EdgeMotion, TectonicInfluenceOps.WorldSearchRadiusKm,
			surface.Boundary, projection.SurfaceCrustType);
		terrain.SetSurface(ball, surface.HeightM, spec.Terrain.SeaLevelM, tectonic);
		WorldBuildProfile.Mark(ref timing, "terrain");
		return BuildState(ball, spec, plate, terrain, frame, cancellation);
	}

	static WorldGenSimulation.State BuildState(Ball ball, WorldSpec spec, PlatePipeline plate,
		TerrainPipeline terrain, EvolutionFrame evolution, CancellationToken cancellation)
	{
		float seaM = spec.Terrain.SeaLevelM;
		var climate = new ClimatePipeline();
		var hydrology = new HydrologyPipeline();
		var index = new IndexPipeline();
		long timing = WorldBuildProfile.Start();
		cancellation.ThrowIfCancellationRequested();
		climate.Run(ball, terrain, seaM, spec.Climate, spec.Seed, cancellation);
		WorldBuildProfile.Mark(ref timing, "climate-total");
		cancellation.ThrowIfCancellationRequested();
		hydrology.Run(ball, terrain, seaM, climate.Precipitation);
		WorldBuildProfile.Mark(ref timing, "hydrology");
		index.Run(ball, terrain, hydrology.Rivers);
		WorldBuildProfile.Mark(ref timing, "spatial-index");
		cancellation.ThrowIfCancellationRequested();
		return new WorldGenSimulation.State(plate, terrain, climate, hydrology, index,
			new WorldFacts(ball, plate, terrain, climate, hydrology, index, evolution, seaM));
	}
}
