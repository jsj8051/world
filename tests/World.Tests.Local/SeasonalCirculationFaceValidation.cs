using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Godot;
using World.Data;
using World.H3Grid;
using World.Logic;
using static World.Logic.CirculationInterpolation;
using H3 = World.Utils.H3.H3;

namespace World.Tests;

static partial class SeasonalCirculationExperiment
{
	public enum Audit { Downstream, LowWind, Thermal, Annual }

	public static int RunConservativeTransport(Audit audit = Audit.Downstream)
	{
		var coarseBall = new Ball(2, 1f);
		bool passed = audit == Audit.Downstream || CheckGeostrophicFaceWind(coarseBall);
		foreach (var sample in new[] { (ContinentLayout.Earthlike, 42), (ContinentLayout.Pangea, 7), (ContinentLayout.Archipelago, 123) })
		{
			var spec = new WorldSpec { Layout = sample.Item1, Seed = sample.Item2 };
			Console.WriteLine($"Generating {spec.Layout} seed {spec.Seed} at res4...");
			var simulation = new WorldGenSimulation(4, 2f);
			simulation.Run(spec);
			passed &= ValidateFaceTransport(coarseBall, simulation, spec, audit);
		}
		return passed ? 0 : 1;
	}

	static bool ValidateFaceTransport(Ball coarseBall, WorldGenSimulation simulation, WorldSpec worldSpec, Audit audit)
	{
		var facts = simulation.Facts;
		var spec = worldSpec.Climate;
		var fineBall = simulation.Ball;
		var fineGrid = ClimateGrid.From(fineBall, simulation.Terrain, facts.SeaLevelM);
		var lowClosure = new ClimateSpec();
		spec.CopyTo(lowClosure);
		lowClosure.FrictionSurface *= spec.FrictionLowFactor;
		double fluxScale = ValidationRelaxationDays * 86400 * WaveSpeedMs * WaveSpeedMs;
		long setup = Stopwatch.GetTimestamp();
		int[] parent = fineBall.CellIds.Select(id => coarseBall.CellIndexOf(H3.CellToParent(id, coarseBall.Res))).ToArray();
		var parentCells = new double[coarseBall.CellIds.Length];
		var parentLand = new double[parentCells.Length];
		var fine = new CirculationOperator(fineBall, fineGrid, lowClosure);
		for (int i = 0; i < fineGrid.Count; i++)
		{
			parentCells[parent[i]] += fine.CellAreas[i];
			if (fineGrid.Land[i]) parentLand[parent[i]] += fine.CellAreas[i];
		}
		var coarseGrid = BuildGrid(coarseBall, parentLand.Select((land, i) => land >= 0.5 * parentCells[i]).ToArray());
		var coarse = new CirculationOperator(coarseBall, coarseGrid, lowClosure);
		coarse.SetRelaxation(fluxScale);
		var interpolation = BuildInterpolation(fineGrid, coarseGrid, parent);
		var transport = new ConservativeFaceTransport(fineBall, fineGrid, fine, coarse, parent);
		if (audit is Audit.Thermal or Audit.Annual)
		{
			const double days = 0.5;
			double scale = days * 86400 * WaveSpeedMs * WaveSpeedMs;
			coarse.SetRelaxation(scale);
			return AuditThermalForcing(simulation, fineGrid, coarseGrid, worldSpec, fine, coarse, transport,
				interpolation, parent, parentCells, scale, fullYear: audit == Audit.Annual);
		}
		if (audit == Audit.LowWind)
		{
			bool windAuditPassed = true;
			foreach (double days in new[] { ValidationRelaxationDays, 0.5 })
			{
				double scale = fluxScale * days / ValidationRelaxationDays;
				coarse.SetRelaxation(scale);
				Console.WriteLine($"  Low-wind relaxation: {days:F1} days.");
				windAuditPassed &= AuditLowWind(fineGrid, spec, facts, fine, coarse, transport, interpolation, parent, parentCells, scale);
			}
			return windAuditPassed;
		}
		double setupMs = Stopwatch.GetElapsedTime(setup).TotalMilliseconds;

		int months = facts.PressureMonthlyHpa.Length;
		var wind = new Vector3[months][];
		var upstream = new int[months][];
		var speed = new float[months][];
		var annual = new Vector3[fineGrid.Count];
		var coarseFlux = new double[coarse.Edges.Length];
		var fineFlux = new double[fine.Edges.Length];
		var provisional = new double[fineGrid.Count];
		var desiredNet = new double[fineGrid.Count];
		var solution = new double[coarseGrid.Count];
		double maxCoarseResidual = 0, maxFineResidual = 0, maxParentResidual = 0, maxCrossingError = 0;
		double solveMs = 0, transferMs = 0, consumerInputMs = 0;
		int maxIterations = 0;
		bool passed = true;
		for (int month = 0; month < months; month++)
		{
			long start = Stopwatch.GetTimestamp();
			var fineTarget = ToZeroMeanPotential(fine, facts.PressureMonthlyHpa[month]);
			var coarseTarget = new double[parentCells.Length];
			for (int i = 0; i < fineTarget.Length; i++) coarseTarget[parent[i]] += fineTarget[i] * fine.CellAreas[i];
			for (int i = 0; i < coarseTarget.Length; i++) coarseTarget[i] /= parentCells[i];
			int iterations = coarse.Solve(coarseTarget, solution);
			double coarseResidual = coarse.RelativeResidual(solution, coarseTarget);
			solveMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
			maxIterations = Math.Max(maxIterations, iterations);
			maxCoarseResidual = Math.Max(maxCoarseResidual, coarseResidual);
			passed &= iterations >= 0 && coarseResidual <= ResidualTolerance;

			start = Stopwatch.GetTimestamp();
			coarse.BuildFaceFlux(solution, coarseFlux);
			for (int i = 0; i < fineTarget.Length; i++)
			{
				double adjustment = 0;
				foreach (var entry in interpolation[i])
					adjustment += entry.Weight * (solution[entry.Cell] - coarseTarget[entry.Cell]);
				provisional[i] = fineTarget[i] + adjustment;
				desiredNet[i] = -adjustment * fine.CellAreas[i] / fluxScale;
			}
			fine.BuildFaceFlux(provisional, fineFlux);
			var error = transport.Correct(coarseFlux, fineFlux, desiredNet);
			maxFineResidual = Math.Max(maxFineResidual, error.Fine);
			maxParentResidual = Math.Max(maxParentResidual, error.Parent);
			maxCrossingError = Math.Max(maxCrossingError, error.Crossing);
			transferMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;

			start = Stopwatch.GetTimestamp();
			upstream[month] = new int[fineGrid.Count];
			speed[month] = new float[fineGrid.Count];
			wind[month] = transport.BuildWind(fineFlux, spec.WindSpeedRefMs, upstream[month], speed[month]);
			for (int i = 0; i < annual.Length; i++) annual[i] += wind[month][i];
			passed &= wind[month].All(value => value.IsFinite()) && speed[month].All(float.IsFinite);
			consumerInputMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
		}
		for (int i = 0; i < annual.Length; i++) annual[i] /= months;
		var curl = transport.BuildWindCurl(annual);
		var curlControl = transport.BuildWindCurl(facts.Wind.Free.Annual);
		// A unit eastward solid-body rotation has vorticity 2*sin(latitude) on the unit sphere.
		var rotationCurl = transport.BuildWindCurl(fineGrid.Dirs.Select(dir => -Vector3.Up.Cross(dir)).ToArray());
		double radiusM = fineGrid.RadiusKm * 1000.0;
		double errorSquared = 0, referenceSquared = 0;
		for (int i = 0; i < rotationCurl.Length; i++)
		{
			double expected = fineGrid.Dirs[i].Y * fine.CellAreas[i] / (radiusM * radiusM);
			errorSquared += (rotationCurl[i] - expected) * (rotationCurl[i] - expected);
			referenceSquared += expected * expected;
		}
		double rotationError = Math.Sqrt(errorSquared / referenceSquared);
		passed &= maxFineResidual < 1e-9 && maxParentResidual < 1e-9 && maxCrossingError < 1e-9;
		passed &= rotationError < 0.15;
		Console.WriteLine($"  Setup {setupMs:F0} ms. {months} months: coarse solve {solveMs:F0} ms, " +
			$"flux transfer {transferMs:F0} ms, consumer inputs {consumerInputMs:F0} ms, max iterations {maxIterations}.");
		Console.WriteLine($"  Relative residual: coarse {maxCoarseResidual:E2}, fine flux budget {maxFineResidual:E2}, " +
			$"parent net {maxParentResidual:E2}, shared crossings {maxCrossingError:E2}. " +
			$"Fine crossings without a corresponding coarse edge: {transport.UnmatchedCrossingCount}.");
		Console.WriteLine($"  Boundary curl solid-body relative RMS error: {rotationError:P2}.");
		passed &= CompareFaceConsumers(fineGrid, worldSpec, facts, fine.CellAreas, wind, upstream, speed, annual, curl, curlControl);
		Console.WriteLine($"  {worldSpec.Layout} seed {worldSpec.Seed} {(passed ? "PASS" : "FAIL")}");
		return passed;
	}

	static bool CompareFaceConsumers(ClimateGrid grid, WorldSpec worldSpec, WorldFacts facts, double[] area,
		Vector3[][] wind, int[][] upstream, float[][] speed, Vector3[] annual, float[] curl, float[] curlControl)
	{
		var spec = worldSpec.Climate;
		var relaxation = OceanCurrentModel.BuildRelaxationCells(grid);
		var initial = new OceanCurrentModel();
		initial.Compute(grid, spec, PrevailingWind.BuildAll(grid.Dirs, spec.RotationSpeed, spec.Prograde),
			null, null, 120, relaxation, CancellationToken.None);
		var warmth = initial.BuildCoastalWarmth(grid);
		var rainVector = MonthlyClimate.ComputePrecipitationFromWind(grid, spec, warmth, worldSpec.Seed, wind);
		var rainFace = MonthlyClimate.ComputePrecipitationFromWind(grid, spec, warmth, worldSpec.Seed, wind, upstream, speed);
		var oceanControl = Ocean(facts.Wind.Free.Annual, null);
		var oceanVector = Ocean(annual, null);
		var oceanFace = Ocean(null, curl);
		var oceanBoundaryControl = Ocean(null, curlControl);
		bool control = oceanControl.Direction.SequenceEqual(facts.OceanCurrentDirection)
			&& oceanControl.Strength.SequenceEqual(facts.OceanCurrentStrength)
			&& oceanControl.Warmth.SequenceEqual(facts.OceanCurrentWarmth);
		Console.WriteLine($"  Published ocean replay: {(control ? "exact" : "mismatch")}. Rain comparisons use published face-transport rain.");
		PrintRain("all land", i => grid.Land[i]);
		PrintRain("tropical land", i => grid.Land[i] && Math.Abs(grid.LatDeg(i)) < 25);
		PrintRain("inland land", i => grid.Land[i] && grid.DistToCoast[i] >= 10);
		PrintOcean("vector route", oceanVector);
		PrintOcean("original free wind / face curl", oceanBoundaryControl);
		PrintOcean("face curl", oceanFace);
		double curlIntegral = curl.Sum(value => (double)value);
		double curlMagnitude = curl.Sum(value => Math.Abs((double)value));
		Console.WriteLine($"  Signed global boundary circulation / sum of magnitudes: {Math.Abs(curlIntegral) / curlMagnitude:E2}.");
		return control && Math.Abs(curlIntegral) <= 1e-6 * curlMagnitude
			&& rainFace.All(row => row.All(value => float.IsFinite(value) && value >= 0))
			&& oceanFace.Direction.All(value => value.IsFinite()) && oceanFace.Strength.All(float.IsFinite)
			&& oceanFace.Warmth.All(float.IsFinite);

		OceanCurrentModel Ocean(Vector3[] driver, float[] boundaryCurl)
		{
			var ocean = new OceanCurrentModel();
			ocean.Compute(grid, spec, driver, facts.TemperatureC, initial.Psi, 300, relaxation, CancellationToken.None, boundaryCurl);
			return ocean;
		}

		void PrintRain(string label, Predicate<int> includes)
		{
			double old = 0, vector = 0, face = 0, total = 0;
			for (int i = 0; i < grid.Count; i++)
			{
				if (!includes(i)) continue;
				total += area[i];
				old += area[i] * facts.PrecipitationMm[i];
				for (int m = 0; m < rainFace.Length; m++)
				{
					vector += area[i] * rainVector[m][i];
					face += area[i] * rainFace[m][i];
				}
			}
			if (total > 0) Console.WriteLine($"  Rain {label}, original/vector/face: {old / total:F1}/{vector / total:F1}/{face / total:F1} mm/yr.");
		}

		void PrintOcean(string label, OceanCurrentModel ocean)
		{
			double seaArea = 0, turned = 0, oldStrength = 0, newStrength = 0;
			for (int i = 0; i < grid.Count; i++)
			{
				if (grid.Land[i]) continue;
				seaArea += area[i];
				oldStrength += area[i] * oceanControl.Strength[i];
				newStrength += area[i] * ocean.Strength[i];
				if (oceanControl.Direction[i].Dot(ocean.Direction[i]) < 0) turned += area[i];
			}
			Console.WriteLine($"  Ocean {label}: strength {oldStrength / seaArea:F3} -> {newStrength / seaArea:F3}, " +
				$">90 deg ocean area {turned / seaArea:P1}, SOR residual {ocean.Residual:E2}.");
		}
	}
}
