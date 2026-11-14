using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using World.Data;
using World.H3Grid;
using World.Logic;
using static World.Logic.CirculationInterpolation;
using H3 = World.Utils.H3.H3;

namespace World.Tests;

static partial class SeasonalCirculationExperiment
{
	const float ReferencePressureHpa = 1013.25f;
	const double ValidationRelaxationDays = 1;

	sealed class BeltProfiles
	{
		readonly ClimateGrid _grid;
		readonly PressureFieldModel _model;
		readonly float[] _zeroTemperature;
		readonly MonthlyInsolation[] _insolation;

		public BeltProfiles(Ball ball, ClimateSpec spec, MonthlyInsolation[] insolation)
		{
			_grid = BuildGrid(ball, new bool[ball.CellIds.Length]);
			_insolation = insolation;
			_zeroTemperature = new float[_grid.Count];
			var beltSpec = new ClimateSpec();
			spec.CopyTo(beltSpec);
			beltSpec.PressureThermalGain = 0;
			beltSpec.PressureAsymGain = 0;
			beltSpec.PressureBeltShiftFactor = 1;
			_model = PressureFieldModel.Create(_grid, beltSpec, CoarseLayer.Identity(_grid), _zeroTemperature);
		}

		public float[] Build(int month, float shift)
		{
			var pressure = new float[_grid.Count];
			_model.BuildMonth(_zeroTemperature, _insolation[month].DeclinationRad * shift, pressure);
			return pressure;
		}
	}

	static bool RunBeltSweep(Ball ball, ClimateSpec spec, MonthlyInsolation[] insolation, BeltProfiles belts)
	{
		Console.WriteLine("Idealized belt sweep: keep thermal and coastal terms fixed. Signed coastal m/s.");
		Console.WriteLine("layout       shift gain   Jan global  Jul global   reversal    Jan S-facing  Jul S-facing");
		bool passed = true;
		foreach (var layout in new[] { (Name: "10-55 N", South: 10, North: 55), (Name: "0-45 N", South: 0, North: 45) })
		{
			var land = ball.Geo.LatRad.Select((lat, i) => lat > layout.South * DegreesToRadians
				&& lat < layout.North * DegreesToRadians && Math.Abs(Math.Atan2(ball.CellDirs[i].Z, ball.CellDirs[i].X)) < 55 * DegreesToRadians).ToArray();
			var grid = BuildGrid(ball, land);
			var temperatures = BuildTemperatures(grid, spec, insolation);
			var pressure = PressureFieldModel.Create(grid, spec, CoarseLayer.Identity(grid), AnnualMean(temperatures));
			var model = new CirculationOperator(ball, grid, spec);
			model.SetRelaxation(ValidationRelaxationDays * 86400 * WaveSpeedMs * WaveSpeedMs);
			var months = new[] { January, July };
			var nonBelt = months.Select(month =>
			{
				var combined = new float[grid.Count];
				pressure.BuildMonth(temperatures[month], insolation[month].DeclinationRad, combined);
				var baseline = belts.Build(month, spec.PressureBeltShiftFactor);
				return combined.Zip(baseline, (total, belt) => total - belt).ToArray();
			}).ToArray();
			var coasts = new CoastalEdges(ball, grid.Land);
			foreach (float shift in new[] { 0f, 0.3f, 0.6f })
			foreach (float gain in new[] { 0.5f, 1f, 1.5f })
			{
				var velocities = new double[2][];
				for (int m = 0; m < months.Length; m++)
				{
					var belt = belts.Build(months[m], shift);
					var target = ToZeroMeanPotential(model, belt.Zip(nonBelt[m],
						(value, other) => ReferencePressureHpa + gain * (value - ReferencePressureHpa) + other).ToArray());
					var result = new double[grid.Count];
					model.Solve(target, result);
					passed &= CheckBalance(model, result, target);
					velocities[m] = model.CoastalVelocity(result);
				}
				var south = coasts.Measure(velocities[0], velocities[1], edge => edge.NorthInward > 0.5);
				Console.WriteLine($"{layout.Name,-12} {shift,4:F1} {gain,4:F1} {model.CoastalMean(velocities[0]),11:F3} " +
					$"{model.CoastalMean(velocities[1]),11:F3} {model.SeasonalReversalShare(velocities),10:P1} {south.Winter,14:F3} {south.Summer,13:F3}");
			}
		}
		Console.WriteLine($"Belt sweep mass balance: {(passed ? "PASS" : "FAIL")}.");
		return passed;
	}

	static bool RunGeneratedWorlds(Ball coarseBall, BeltProfiles belts)
	{
		var samples = new[] { (Layout: ContinentLayout.Earthlike, Seed: 42), (Layout: ContinentLayout.Pangea, Seed: 7),
			(Layout: ContinentLayout.Archipelago, Seed: 123) };
		bool passed = true;
		foreach (var sample in samples)
		{
			var spec = new WorldSpec { Layout = sample.Layout, Seed = sample.Seed };
			Console.WriteLine($"Generating H3 res4 {sample.Layout} seed {sample.Seed}...");
			var simulation = new WorldGenSimulation(4, 2f);
			simulation.Run(spec);
			passed &= ValidateGeneratedWorld(coarseBall, simulation.Facts, spec, belts);
		}
		return passed;
	}

	static bool ValidateGeneratedWorld(Ball coarseBall, WorldFacts facts, WorldSpec worldSpec, BeltProfiles belts)
	{
		var spec = worldSpec.Climate;
		long setupStart = Stopwatch.GetTimestamp();
		var fineBall = facts.Ball;
		var heightAboveSea = facts.HeightM.Select(height => height - facts.SeaLevelM).ToArray();
		var fineGrid = ClimateGrid.FromArrays(fineBall.CellDirs, fineBall.CellNeighbors, facts.FinalLand, heightAboveSea,
			neighborKm: SpatialScale.Of(fineBall).NeighborCenterKm);
		int[] parent = fineBall.CellIds.Select(id => coarseBall.CellIndexOf(H3.CellToParent(id, coarseBall.Res))).ToArray();
		var parentCount = new int[coarseBall.CellIds.Length];
		var parentLand = new int[parentCount.Length];
		for (int i = 0; i < parent.Length; i++)
		{
			parentCount[parent[i]]++;
			if (fineGrid.Land[i]) parentLand[parent[i]]++;
		}
		var coarseGrid = BuildGrid(coarseBall, parentLand.Select((count, i) => 2 * count >= parentCount[i]).ToArray());
		var model = new CirculationOperator(coarseBall, coarseGrid, spec);
		model.SetRelaxation(ValidationRelaxationDays * 86400 * WaveSpeedMs * WaveSpeedMs);
		var interpolation = BuildInterpolation(fineGrid, coarseGrid, parent);
		var windSolver = LegacyWindSolver.Create(fineGrid, spec);
		var targets = facts.PressureMonthlyHpa.Select(month => ToZeroMeanPotential(model, Aggregate(month))).ToArray();
		var solutions = targets.Select(target => new double[target.Length]).ToArray();
		double setupMs = Stopwatch.GetElapsedTime(setupStart).TotalMilliseconds;
		var coast = new CoastalEdges(fineBall, fineGrid.Land);
		float[] Aggregate(float[] field)
		{
			var sum = new double[parentCount.Length];
			for (int i = 0; i < field.Length; i++) sum[parent[i]] += field[i];
			return sum.Select((value, i) => (float)(value / parentCount[i])).ToArray();
		}
		var iterations = new int[targets.Length];
		double solveMs = MedianGeneratedMilliseconds(() =>
		{
			for (int month = 0; month < targets.Length; month++)
				iterations[month] = model.Solve(targets[month], solutions[month]);
		});
		double residual = targets.Select((target, month) => model.RelativeResidual(solutions[month], target)).Max();
		bool passed = targets.Select((target, month) => CheckBalance(model, solutions[month], target)).All(ok => ok);

		var finePressure = new float[fineGrid.Count];
		var gradient = new Vector3[fineGrid.Count];
		var wind = new Vector3[fineGrid.Count];
		var low = new Vector3[fineGrid.Count];
		var free = new Vector3[fineGrid.Count];
		var balanced = new double[targets.Length][];
		var monthlyLow = targets.Select(_ => new Vector3[fineGrid.Count]).ToArray();
		var annualFree = new Vector3[fineGrid.Count];
		void RebuildFineWind(int month, double[] solution)
		{
			var baselineTarget = targets[month];
			var baselinePressure = facts.PressureMonthlyHpa[month];
			for (int i = 0; i < finePressure.Length; i++)
			{
				double adjustment = 0;
				foreach (var entry in interpolation[i])
					adjustment += entry.Weight * (solution[entry.Cell] - baselineTarget[entry.Cell]);
				finePressure[i] = baselinePressure[i] + (float)(AirDensity / 100 * adjustment);
			}
			TangentField.GradientField(fineGrid, finePressure, gradient);
			windSolver.SolveAll(gradient, wind, low, free);
			passed &= finePressure.All(float.IsFinite) && wind.All(vector => vector.IsFinite());
		}
		double rebuildMs = MedianGeneratedMilliseconds(() =>
		{
			for (int month = 0; month < targets.Length; month++) RebuildFineWind(month, solutions[month]);
		});
		for (int month = 0; month < targets.Length; month++)
		{
			RebuildFineWind(month, solutions[month]);
			Array.Copy(low, monthlyLow[month], low.Length);
			for (int i = 0; i < free.Length; i++) annualFree[i] += free[i];
		}
		for (int i = 0; i < annualFree.Length; i++) annualFree[i] /= targets.Length;
		foreach (int month in new[] { January, July })
		{
			RebuildFineWind(month, solutions[month]);
			balanced[month] = coast.Project(wind);
		}
		var baselineJan = coast.Project(facts.Wind.Surface.Monthly[January]);
		var baselineJul = coast.Project(facts.Wind.Surface.Monthly[July]);
		Console.WriteLine($"  12 months: setup {setupMs:F0} ms, solve {solveMs:F0} ms, fine wind rebuild {rebuildMs:F0} ms, " +
			$"iterations {iterations.Min()}-{iterations.Max()}, worst residual {residual:E3}.");
		Console.WriteLine("  Res4 coast, signed normalized surface speed and winter-offshore/summer-onshore share by length.");
		Console.WriteLine("  band             coast km     current winter/summer/reversal        balanced winter/summer/reversal");
		PrintRegional("N 0-25", edge => edge.Latitude >= 0 && edge.Latitude < 25);
		PrintRegional("N 25-50", edge => edge.Latitude >= 25 && edge.Latitude < 50);
		PrintRegional("S 0-25", edge => edge.Latitude < 0 && edge.Latitude >= -25);
		PrintRegional("S 25-50", edge => edge.Latitude < -25 && edge.Latitude >= -50);
		PrintRegional("N 0-50 south", edge => edge.Latitude >= 0 && edge.Latitude < 50 && edge.NorthInward > 0.5);
		PrintRegional("S 0-50 north", edge => edge.Latitude < 0 && edge.Latitude >= -50 && edge.NorthInward < -0.5);

		Console.WriteLine("  Belt shift sensitivity, summer coast speed / reversal share by length.");
		Console.WriteLine("  shift    N 0-25               N 25-50              S 0-25               S 25-50");
		PrintShift(spec.PressureBeltShiftFactor, balanced);
		foreach (float shift in new[] { 0f, 2 * spec.PressureBeltShiftFactor })
		{
			var velocities = new double[targets.Length][];
			foreach (int month in new[] { January, July })
			{
				// Change the coarse forcing while retaining the same fine pressure detail.
				var originalBelt = belts.Build(month, spec.PressureBeltShiftFactor);
				var shiftedBelt = belts.Build(month, shift);
				var delta = ToZeroMeanPotential(model, shiftedBelt.Zip(originalBelt, (shifted, original) => shifted - original).ToArray());
				var shiftedTarget = targets[month].Zip(delta, (value, change) => value + change).ToArray();
				var result = new double[shiftedTarget.Length];
				model.Solve(shiftedTarget, result);
				passed &= CheckBalance(model, result, shiftedTarget);
				RebuildFineWind(month, result);
				velocities[month] = coast.Project(wind);
			}
			PrintShift(shift, velocities);
		}
		passed &= AuditFineBalance(fineBall, fineGrid, spec, windSolver, parent, parentCount.Length,
			facts.PressureMonthlyHpa, month => { RebuildFineWind(month, solutions[month]); return finePressure; },
			out var correctedLow, out var correctedFree, out var cellAreas);
		passed &= CompareDownstream(fineGrid, worldSpec, facts, monthlyLow, annualFree, correctedLow, correctedFree, cellAreas);
		Console.WriteLine($"  Coarse mass balance, fine diagnostics and downstream controls: {(passed ? "PASS" : "FAIL")}.");
		return passed;

		void PrintShift(float shift, double[][] velocities)
		{
			var bands = new[] { (South: 0, North: 25), (South: 25, North: 50), (South: -25, North: 0), (South: -50, North: -25) };
			var values = bands.Select(band => coast.Measure(velocities[January], velocities[July],
				edge => edge.Latitude >= band.South && edge.Latitude < band.North));
			Console.WriteLine($"  {shift,5:F2}    " + string.Join("           ", values.Select(value => $"{value.Summer,7:F4}/{value.Reversal,6:P1}")));
		}

		void PrintRegional(string label, Predicate<CoastalEdges.Edge> region)
		{
			var current = coast.Measure(baselineJan, baselineJul, region);
			var updated = coast.Measure(balanced[January], balanced[July], region);
			Console.WriteLine($"  {label,-16} {current.LengthKm,8:F0}     {current.Winter,8:F4}/{current.Summer,8:F4}/{current.Reversal,7:P1} " +
				$"         {updated.Winter,8:F4}/{updated.Summer,8:F4}/{updated.Reversal,7:P1}");
		}
	}

	static bool CheckBalance(CirculationOperator model, double[] result, double[] target) =>
		model.RelativeResidual(result, target) <= ResidualTolerance
		&& Math.Abs(model.AreaMean(result) - model.AreaMean(target)) * AirDensity / 100 < 1e-5;

	static double MedianGeneratedMilliseconds(Action action)
	{
		action();
		var times = new double[3];
		for (int i = 0; i < times.Length; i++)
		{
			long start = Stopwatch.GetTimestamp();
			action();
			times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
		}
		Array.Sort(times);
		return times[1];
	}

	sealed class CoastalEdges
	{
		public readonly record struct Edge(int Sea, int Land, double LengthKm, float Latitude, float NorthInward, Vector3 Inward);
		readonly Edge[] _edges;

		public CoastalEdges(Ball ball, bool[] land)
		{
			var edges = new List<Edge>();
			for (int i = 0; i < land.Length; i++)
			foreach (int j in ball.CellNeighbors[i])
			{
				// Match CirculationOperator edge order for direct comparisons on the same grid.
				if (j <= i || land[i] == land[j]) continue;
				int sea = land[i] ? j : i;
				int coast = land[i] ? i : j;
				var midpoint = (ball.CellDirs[i] + ball.CellDirs[j]).Normalized();
				var inward = (ball.CellDirs[coast] - ball.CellDirs[sea]).Normalized();
				var shared = H3.CellToVertexes(ball.CellIds[i]).Intersect(H3.CellToVertexes(ball.CellIds[j])).ToArray();
				var a = ball.Vertices.PositionOf(shared[0]).Normalized();
				var b = ball.Vertices.PositionOf(shared[1]).Normalized();
				double lengthKm = World.Utils.SphericalFbmNoise.WorldRadiusKm * Math.Atan2(a.Cross(b).Length(), a.Dot(b));
				edges.Add(new Edge(sea, coast, lengthKm, Mathf.RadToDeg(Mathf.Asin(midpoint.Y)),
					inward.Dot(TangentField.NorthTangent(midpoint)), inward));
			}
			_edges = edges.ToArray();
		}

		public double[] Project(Vector3[] wind) =>
			_edges.Select(edge => (double)(wind[edge.Sea] + wind[edge.Land]).Dot(edge.Inward) / 2).ToArray();

		public (double Winter, double Summer, double Reversal, double LengthKm) Measure(double[] january, double[] july, Predicate<Edge> region)
		{
			double length = 0, winter = 0, summer = 0, reversed = 0;
			for (int i = 0; i < _edges.Length; i++)
			{
				var edge = _edges[i];
				if (!region(edge)) continue;
				double winterVelocity = edge.Latitude >= 0 ? january[i] : july[i];
				double summerVelocity = edge.Latitude >= 0 ? july[i] : january[i];
				length += edge.LengthKm;
				winter += edge.LengthKm * winterVelocity;
				summer += edge.LengthKm * summerVelocity;
				if (winterVelocity < 0 && summerVelocity > 0) reversed += edge.LengthKm;
			}
			return length > 0 ? (winter / length, summer / length, reversed / length, length) : (double.NaN, double.NaN, double.NaN, 0);
		}
	}
}
