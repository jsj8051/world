using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Godot;
using World.Data;
using World.Logic;
using World.Params;

namespace World.Tests;

/// <summary>演化冒烟：真实 H3 网格上跑零步 + 若干步，打印耗时与判读读数。</summary>
static class EvolutionSmoke
{
	public static void Audit(int res)
	{
		var spec = new WorldSpec();
		foreach (string path in new[] { "res/params/world_params.json", "res/params/world_params.user.json" })
		{
			if (!File.Exists(path)) continue;
			WorldParams.ReadJsonInto(File.ReadAllText(path), spec, out var problems);
			foreach (string problem in problems) Console.WriteLine(problem);
		}
		var world = new WorldGenSimulation(res, PlanetRadius);
		var solver = world.CreateEvolutionSolver(spec, out var frame);
		var grid = solver.Grid;
		double globe = grid.AreasKm2.Sum();
		foreach (int ma in new[] { 0, 50, 100 })
		{
			frame = solver.CalculateToStep(frame, frame.Clock.StepsFor(ma * 1_000_000L));
			var projection = frame.Projection;
			double material = 0, above = 0;
			for (int cell = 0; cell < grid.CellCount; cell++)
			{
				double area = grid.AreasKm2[cell];
				material += area * (1 - projection.OceanicCoverage[cell]);
				if (projection.HeightM[cell] > spec.Terrain.SeaLevelM) above += area;
			}
			float collisionMax = float.NegativeInfinity;
			int collisionEdges = 0;
			for (int edge = 0; edge < projection.PlateBoundaries.Edges.Length; edge++)
			{
				if (projection.Boundary.ConvergenceKinds[edge] != ConvergenceKind.Collision) continue;
				var boundary = projection.PlateBoundaries.Edges[edge];
				collisionMax = Math.Max(collisionMax, Math.Max(projection.HeightM[boundary.CellA], projection.HeightM[boundary.CellB]));
				collisionEdges++;
			}
			Console.WriteLine($"[EVOLVE-AUDIT] res={res} seed={spec.Seed} Ma={frame.Clock.TimeMa:F2} " +
				$"continental={material / globe:P2} exposed={projection.EmergentLandFraction:P2} land-mask={above / globe:P2} " +
				$"collision-edges={collisionEdges} collision-max={(collisionEdges == 0 ? "—" : $"{collisionMax:F0} m")} " +
				$"max={projection.HeightM.Max():F0} m volume-residual={frame.Budget.CumulativeResidual:G6} km³");
		}
	}

	public static void Run(int res, int steps)
	{
		var spec = new WorldSpec();
		spec.Seed = 42;
		spec.Evolution.StepYears = 100_000;
		var sim = new WorldGenSimulation(res, PlanetRadius);
		var build = Stopwatch.StartNew();
		EvolutionSimulation evolution = sim.CreateEvolutionSolver(spec, out var frame);
		sim.Publish(sim.PrepareEvolution(evolution, frame));
		build.Stop();
		EvolutionGrid grid = evolution.Grid;
		if (res <= 2) CheckInitialTerrain(spec, sim, frame);
		CheckRigidTransport(spec, sim.Facts, grid);
		Console.WriteLine($"[EVOLVE] res={res} cells={grid.CellCount} minSpacing={grid.MinNeighborDistanceKm:F1} km " +
			$"zero-step={build.ElapsedMilliseconds} ms slots={frame.Material.OccupiedSlotCount} " +
			$"crust={frame.Budget.InitialCrustVolume:G4} km³ mantle={frame.Mantle.StoredVolumeKm3:G4} km³");

		long elapsedTicks = 0;
		for (int step = 1; step <= steps; step++)
		{
			var watch = Stopwatch.StartNew();
			frame = evolution.CalculateToStep(frame, step);
			watch.Stop();
			elapsedTicks += watch.ElapsedTicks;
			double land = 0;
			double minAge = double.MaxValue, maxAge = 0;
			int n = frame.Projection.HeightM.Count;
			for (int i = 0; i < n; i++)
			{
				if (frame.Projection.HeightM[i] > 0) land++;
				if (frame.Projection.OceanAgeMa[i] is double age)
				{
					minAge = Math.Min(minAge, age);
					maxAge = Math.Max(maxAge, age);
				}
			}
			Console.WriteLine($"[EVOLVE] step={step} {watch.ElapsedMilliseconds} ms slots={frame.Material.OccupiedSlotCount} " +
				$"land={100 * land / n:F1}% age={minAge:F1}…{maxAge:F1} Ma " +
				$"sub={frame.Budget.TotalSubducted:G4} new={frame.Budget.TotalNewOceanic:G4} " +
				$"volc={frame.Budget.TotalVolcanic:G4} residual={frame.Budget.CumulativeResidual:G4} " +
				$"areaCorrection={frame.CoverageCorrectionKm2:G4}");
		}
		var materialHash = HashMaterial(frame.Material);
		float[] height = frame.Projection.HeightM.ToArray();
		var heightHash = SHA256.HashData(MemoryMarshal.AsBytes(height.AsSpan()));
		Console.WriteLine($"[EVOLVE] elapsed={elapsedTicks * 1000d / Stopwatch.Frequency:F0} ms " +
			$"material-sha256={Convert.ToHexString(materialHash)} height-sha256={Convert.ToHexString(heightHash)} " +
			$"crust={frame.Material.VolumeKm3:R} mantle={frame.Mantle.StoredVolumeKm3:R} " +
			$"slot-storage={frame.Material.Rows.AllocatedEntries * Marshal.SizeOf<CrustEntry>() / 1048576d:F1} MiB");
	}

	static byte[] HashMaterial(EvolutionCrust material)
	{
		// 沿用旧版逐格、板块、壳型顺序的字节摘要，每次只展开一个格的空槽。
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		var denseRow = new CrustSlot[material.PlateCount * 2];
		for (int cell = 0; cell < material.CellCount; cell++)
		{
			Array.Clear(denseRow);
			foreach (ref readonly CrustEntry entry in material.Rows.Row(cell)) denseRow[entry.Key] = entry.Slot;
			hash.AppendData(MemoryMarshal.AsBytes(denseRow.AsSpan()));
		}
		return hash.GetHashAndReset();
	}

	static void CheckInitialTerrain(WorldSpec spec, WorldGenSimulation sim, EvolutionFrame frame)
	{
		var varied = new WorldSpec();
		spec.CopyTo(varied);
		varied.Terrain.MountainPeakM *= 4;
		varied.Terrain.PlateauShieldHeightM *= 4;
		varied.Terrain.RiftDepthM *= 4;
		varied.Terrain.RidgeCrestDepthM = -2;
		varied.Terrain.TrenchDepthM *= 4;
		varied.Terrain.ArcIslandPeakM *= 4;
		var comparison = new WorldGenSimulation(sim.Ball.Res, PlanetRadius);
		var solver = comparison.CreateEvolutionSolver(varied, out var initial);
		comparison.Publish(comparison.PrepareEvolution(solver, initial));

		if (!comparison.Facts.HeightM.SequenceEqual(sim.Facts.HeightM)
			|| !frame.Projection.HeightM.SequenceEqual(sim.Facts.HeightM)
			|| !ReferenceEquals(frame.Projection.PlateBoundaries, sim.Facts.PlateBoundaries)
			|| frame.Clock.StepIndex != 0 || frame.Budget.TotalSubducted != 0
			|| frame.Budget.TotalNewOceanic != 0 || frame.Budget.TotalVolcanic != 0)
			throw new Exception("零步地形受静态构造参数影响，或与初始演化帧不一致");
		Console.WriteLine("[EVOLVE] 零步地形独立于静态构造参数");
	}

	static void CheckRigidTransport(WorldSpec spec, WorldFacts initial, EvolutionGrid grid)
	{
		foreach (Vector3 omega in new[] { Vector3.Zero, new Vector3(0.2f, 0.1f, -0.1f) })
		{
			Plate[] plates = (Plate[])initial.Plates.Clone();
			for (int i = 0; i < plates.Length; i++) plates[i].Omega = omega;
			var evolution = EvolutionSimulation.Create(spec, grid, initial.PlateOfCell,
				initial.Crust, initial.HeightM, plates, out var zero);
			var frame = evolution.CalculateToStep(zero, 1);
			if (frame.Budget.StepSubducted != 0 || frame.Budget.StepNewOceanic != 0
				|| frame.Budget.StepVolcanic != 0 || frame.CoverageCorrectionKm2 != 0
				|| frame.Projection.Boundary.MotionKinds.Any(kind => kind != EvolutionMotionKind.Inactive)
				|| (omega == Vector3.Zero && !frame.Projection.Crust.Type.SequenceEqual(initial.Crust.Type)))
				throw new Exception("真实 H3 的静止或刚体共转对照产生了伪构造");
		}
		Console.WriteLine("[EVOLVE] H3 静止与共转对照通过");
	}

	public static void CheckWorld(int res, int steps)
	{
		var spec = new WorldSpec { Seed = 42 };
		var sim = new WorldGenSimulation(res, PlanetRadius);
		var watch = Stopwatch.StartNew();
		sim.Run(spec);
		WorldFacts initial = sim.Facts;
		var solver = sim.CreateEvolutionSolver(spec, out var zero);
		var frame = solver.CalculateToStep(zero, steps);
		long evolutionMs = watch.ElapsedMilliseconds;
		var state = sim.PrepareEvolution(solver, frame);
		long downstreamMs = watch.ElapsedMilliseconds - evolutionMs;
		if (!ReferenceEquals(sim.Facts, initial)) throw new Exception("候选世界不应提前发布");
		sim.Publish(state);
		WorldFacts evolved = sim.Facts;
		if (!ReferenceEquals(evolved.Evolution, frame)
			|| evolved.HeightM.SequenceEqual(initial.HeightM) || ReferenceEquals(evolved.Rivers, initial.Rivers)
			|| ReferenceEquals(evolved.TemperatureC, initial.TemperatureC)
			|| evolved.HeightM.Length != initial.HeightM.Length
			|| evolved.PlateBoundaries.Edges.Length != evolved.BoundaryFields.EdgeMotion.Length)
			throw new Exception("演化世界没有生成同网格上的完整新快照");
		for (int i = 0; i < evolved.HeightM.Length; i++)
			if (!float.IsFinite(evolved.HeightM[i]) || evolved.FinalLand[i] != (evolved.HeightM[i] > spec.Terrain.SeaLevelM))
				throw new Exception($"格 {i} 的海拔与海陆事实不一致");
		Console.WriteLine($"[EVOLVE-WORLD] res={res} cells={evolved.HeightM.Length} step={steps} " +
			$"initial-and-evolution={evolutionMs} ms world-build={downstreamMs} ms");

		if (System.Environment.GetEnvironmentVariable("WORLD_BUILD_PROFILE") == "1")
		{
			using var process = Process.GetCurrentProcess();
			Console.WriteLine($"[WORLD-PROFILE] peak-working-set={process.PeakWorkingSet64 / 1048576d:F1} MiB " +
				$"height={HashField(evolved.HeightM)} temperature={HashField(evolved.TemperatureC)} " +
				$"precipitation={HashField(evolved.PrecipitationMm)} pressure={HashField(evolved.PressureAnnualHpa)} " +
				$"current={HashField(evolved.OceanCurrentDirection)}");
		}

		if (res > 2)
		{
			var remapper = new CrossResolutionSurfaceRemapper(spec, initial, 2, PlanetRadius);
			var coarse = remapper.Simulation.CalculateToStep(remapper.InitialFrame, steps);
			WorldSurfaceInput fine = remapper.Project(coarse);
			if (fine.HeightM.Length != initial.HeightM.Length || fine.HeightM.Any(h => !float.IsFinite(h))
				|| fine.Boundaries.Edges.Length != fine.Boundary.EdgeMotion.Length)
				throw new Exception("跨分辨率诊断生成了无效的地表");
			Console.WriteLine($"[EVOLVE-REMAP] res2 → res{res} cells={fine.HeightM.Length} step={steps}");
		}
	}

	static string HashField<T>(T[] values) where T : unmanaged =>
		Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));

	const float PlanetRadius = 2f;
}
