using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using Godot;
using World.Data;
using World.H3Grid;
using World.Logic;
using H3 = World.Utils.H3.H3;

namespace World.Tests;

/// <summary>Winter/summer comparison on a flat idealized continent. Timings exclude world generation.</summary>
static partial class SeasonalCirculationExperiment
{
	const double AirDensity = 1.2;
	const double WaveSpeedMs = 40;
	const double ResidualTolerance = 1e-7;
	const int BenchmarkRuns = 11;
	const int January = 0;
	const int July = 6;
	const double DegreesToRadians = Math.PI / 180;

	public static int Run()
	{
		CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
		var ball = new Ball(res: 2, radius: 1f);
		var oceanGrid = BuildGrid(ball, new bool[ball.CellIds.Length]);
		var spec = new ClimateSpec { ContinentalityNoiseC = 0f };
		var insolation = MonthlyInsolation.BuildYear(spec.Months, spec.ObliquityDeg);
		var oceanTemperatures = BuildTemperatures(oceanGrid, spec, insolation);
		bool midlatitudePassed = RunContinent(ball, spec, insolation, oceanTemperatures, 10, 55);
		bool equatorwardPassed = RunContinent(ball, spec, insolation, oceanTemperatures, 0, 45);
		var belts = new BeltProfiles(ball, spec, insolation);
		bool sweepPassed = RunBeltSweep(ball, spec, insolation, belts);
		bool generatedWorldsPassed = RunGeneratedWorlds(ball, belts);
		return midlatitudePassed && equatorwardPassed && sweepPassed && generatedWorldsPassed ? 0 : 1;
	}

	static ClimateGrid BuildGrid(Ball ball, bool[] land) =>
		ClimateGrid.FromArrays(ball.CellDirs, ball.CellNeighbors, land, new float[land.Length],
			neighborKm: SpatialScale.Of(ball).NeighborCenterKm);

	static bool RunContinent(Ball ball, ClimateSpec spec, MonthlyInsolation[] insolation,
		float[][] oceanTemperatures, double south, double north)
	{
		var land = ball.Geo.LatRad.Select((lat, i) => lat > south * DegreesToRadians && lat < north * DegreesToRadians
			&& Math.Abs(Math.Atan2(ball.CellDirs[i].Z, ball.CellDirs[i].X)) < 55 * DegreesToRadians).ToArray();
		var grid = BuildGrid(ball, land);
		var temperatures = BuildTemperatures(grid, spec, insolation);
		var annualTemperature = AnnualMean(temperatures);
		var annualOceanTemperature = AnnualMean(oceanTemperatures);
		var coarse = CoarseLayer.Identity(grid);
		var pressureModel = PressureFieldModel.Create(grid, spec, coarse, annualTemperature);
		var backgroundModel = PressureFieldModel.Create(grid, new ClimateSpec { PressureThermalGain = 0f }, coarse, annualTemperature);
		int radiusHops = Math.Max(1, (int)Math.Ceiling(2 * spec.PressureThermalSmoothKm / coarse.NeighborKm));
		var thermalKernel = CoarseKernel.Build(coarse, spec.PressureThermalSmoothKm, radiusHops);
		var windSolver = LegacyWindSolver.Create(grid, spec);
		var pressure = new float[grid.Count];
		var gradient = new Vector3[grid.Count];
		var surface = new Vector3[grid.Count];
		var low = new Vector3[grid.Count];
		var free = new Vector3[grid.Count];
		var monthIndices = new[] { January, July };
		var setup = Stopwatch.StartNew();
		var model = new CirculationOperator(ball, grid, spec);
		setup.Stop();

		Console.WriteLine($"Continent {south:F0}-{north:F0} N. H3 res2: {grid.Count:N0} cells, {land.Count(value => value):N0} land, " +
			$"{model.CoastCount():N0} coastal edges. Operator setup: {setup.Elapsed.TotalMilliseconds:F1} ms.");
		Console.WriteLine($"Wave speed: {WaveSpeedMs} m/s. Positive coastal velocity means onshore.");
		var legacyTargets = new double[monthIndices.Length][];
		var backgroundTargets = new double[monthIndices.Length][];
		var localTargets = new double[monthIndices.Length][];
		var matchedTargets = new double[monthIndices.Length][];
		var fixedBackground = new float[grid.Count];
		backgroundModel.BuildMonth(annualTemperature, 0f, fixedBackground);
		var fixedBackgroundTarget = ToZeroMeanPotential(model, fixedBackground);
		var current = new double[monthIndices.Length][];
		for (int m = 0; m < monthIndices.Length; m++)
		{
			int month = monthIndices[m];
			void BuildCurrentWind()
			{
				pressureModel.BuildMonth(temperatures[month], insolation[month].DeclinationRad, pressure);
				TangentField.GradientField(grid, pressure, gradient);
				windSolver.SolveAll(gradient, surface, low, free);
			}
			double baselineMs = MedianMilliseconds(BuildCurrentWind);
			legacyTargets[m] = ToZeroMeanPotential(model, pressure);
			var background = new float[grid.Count];
			backgroundModel.BuildMonth(temperatures[month], insolation[month].DeclinationRad, background);
			backgroundTargets[m] = ToZeroMeanPotential(model, background);
			var oldThermal = legacyTargets[m].Zip(backgroundTargets[m], (legacy, background) => legacy - background).ToArray();
			var localThermal = ToZeroMeanPotential(model, BuildOceanReferenceThermal(grid, thermalKernel, spec, temperatures[month],
				annualTemperature, oceanTemperatures[month], annualOceanTemperature));
			double oldRms = model.AreaRms(oldThermal);
			double localRms = model.AreaRms(localThermal);
			// Match area-weighted RMS after centering so the comparison changes the spatial pattern alone.
			double scale = oldRms / localRms;
			localTargets[m] = backgroundTargets[m].Zip(localThermal, (value, thermal) => value + thermal).ToArray();
			matchedTargets[m] = backgroundTargets[m].Zip(localThermal, (value, thermal) => value + scale * thermal).ToArray();
			current[m] = model.CurrentCoastalVelocity(surface, spec.WindSpeedRefMs);
			Console.WriteLine($"Current {MonthName(month)}: coast {model.CoastalMean(current[m]):F3} m/s, " +
				$"median pressure + winds {baselineMs:F2} ms.");
			Console.WriteLine($"  Thermal target RMS (hPa equivalent), legacy {oldRms * AirDensity / 100:F2}, " +
				$"local ocean reference {localRms * AirDensity / 100:F2} before matching (scale {scale:F2}).");
		}
		Console.WriteLine($"Current winter offshore / summer onshore: {model.SeasonalReversalShare(current):P1} of coast by length.");
		Console.WriteLine("days target            Jan m/s    Jul m/s    reversed      residual*    drift hPa*    iterations  median ms");
		bool massBalancePassed = true;
		foreach (double days in new[] { 1.0, 2.0, 4.0 })
		{
			model.SetRelaxation(days * 86400 * WaveSpeedMs * WaveSpeedMs);
			var fixedBelt = CompareTargets(model, days, "fixed belt only", new[] { fixedBackgroundTarget, fixedBackgroundTarget }, benchmark: false);
			var background = CompareTargets(model, days, "background", backgroundTargets, benchmark: false);
			var legacy = CompareTargets(model, days, "legacy", legacyTargets, benchmark: true);
			var matched = CompareTargets(model, days, "ocean ref matched", matchedTargets, benchmark: true);
			var local = CompareTargets(model, days, "ocean ref raw", localTargets, benchmark: false);
			massBalancePassed &= fixedBelt.Balanced && background.Balanced && legacy.Balanced && matched.Balanced && local.Balanced;
			Console.WriteLine($"     Jul contribution, m/s: belt shift {background.July - fixedBelt.July:+0.000;-0.000;0.000}, " +
				$"legacy thermal {legacy.July - background.July:+0.000;-0.000;0.000}, " +
				$"matched thermal {matched.July - background.July:+0.000;-0.000;0.000}.");
		}

		Console.WriteLine("* Worst of January and July. Reversal requires winter offshore and summer onshore.");
		Console.WriteLine($"Discrete mass balance: {(massBalancePassed ? "PASS" : "FAIL")}.");
		return massBalancePassed;
	}

	static (double July, bool Balanced) CompareTargets(CirculationOperator model, double days, string label,
		double[][] targets, bool benchmark)
	{
		var velocities = new double[targets.Length][];
		var iterations = new int[targets.Length];
		var timings = new double[targets.Length];
		double worstResidual = 0, worstDrift = 0;
		for (int month = 0; month < targets.Length; month++)
		{
			var target = targets[month];
			var result = new double[target.Length];
			if (benchmark) timings[month] = MedianMilliseconds(() => iterations[month] = model.Solve(target, result));
			else iterations[month] = model.Solve(target, result);
			velocities[month] = model.CoastalVelocity(result);
			worstResidual = Math.Max(worstResidual, model.RelativeResidual(result, target));
			worstDrift = Math.Max(worstDrift, Math.Abs(model.AreaMean(result) - model.AreaMean(target)) * AirDensity / 100);
		}

		double july = model.CoastalMean(velocities[1]);
		string timing = benchmark ? $"{timings[0]:F1}/{timings[1]:F1}" : "  -";
		Console.WriteLine($"{days,4:F0} {label,-17} {model.CoastalMean(velocities[0]),8:F3} {july,10:F3} " +
			$"{model.SeasonalReversalShare(velocities),11:P1} {worstResidual,14:E3} {worstDrift,13:E2} " +
			$"{iterations[0],7}/{iterations[1],-3}   {timing}");
		return (july, double.IsFinite(worstResidual) && worstResidual <= ResidualTolerance && worstDrift < 1e-5);
	}

	static float[][] BuildTemperatures(ClimateGrid grid, ClimateSpec spec, MonthlyInsolation[] insolation)
	{
		var temperatures = insolation.Select(_ => new float[grid.Count]).ToArray();
		var baseTemperature = new TemperatureModel();
		baseTemperature.Compute(grid, spec, coastalWarmth: null, seed: 7);
		MonthlyTemperatureModel.Compute(grid, spec, baseTemperature.AnnualC, insolation, temperatures, CancellationToken.None);
		return temperatures;
	}

	static float[] AnnualMean(float[][] temperatures) =>
		Enumerable.Range(0, temperatures[0].Length).Select(i => temperatures.Average(month => month[i])).ToArray();

	static double[] ToZeroMeanPotential(CirculationOperator model, float[] pressureHpa)
		=> ConservativeWindSolver.ToPotential(model, pressureHpa);

	// Compare the cell's seasonal temperature anomaly with its all-ocean counterpart at the same location.
	static float[] BuildOceanReferenceThermal(ClimateGrid grid, CoarseKernel kernel, ClimateSpec spec,
		float[] month, float[] annual, float[] oceanMonth, float[] oceanAnnual)
	{
		var thermalHpa = new float[grid.Count];
		var smoothed = new float[grid.Count];
		for (int i = 0; i < grid.Count; i++)
		{
			float x = (MathF.Abs(grid.LatDeg(i)) - 45f) / 30f;
			float latitudeWeight = 0.5f + 0.5f * MathF.Exp(-x * x);
			float anomaly = (month[i] - annual[i]) - (oceanMonth[i] - oceanAnnual[i]);
			thermalHpa[i] = -spec.PressureThermalGain * latitudeWeight * anomaly;
		}
		kernel.Convolve(thermalHpa, smoothed);
		for (int pass = 0; pass < 2; pass++)
		{
			for (int i = 0; i < grid.Count; i++)
			{
				float sum = smoothed[i];
				foreach (int neighbor in grid.Neighbors[i]) sum += smoothed[neighbor];
				thermalHpa[i] = sum / (1 + grid.Neighbors[i].Length);
			}
			(smoothed, thermalHpa) = (thermalHpa, smoothed);
		}
		return smoothed;
	}

	static string MonthName(int month) => month == January ? "Jan" : "Jul";

	static double MedianMilliseconds(Action action)
	{
		for (int warmup = 0; warmup < 3; warmup++) action();
		var timings = new double[BenchmarkRuns];
		for (int run = 0; run < timings.Length; run++)
		{
			long start = Stopwatch.GetTimestamp();
			action();
			timings[run] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
		}
		Array.Sort(timings);
		return timings[timings.Length / 2];
	}
}
