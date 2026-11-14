using System;
using System.Diagnostics;
using System.Linq;
using Godot;
using World.Data;
using World.H3Grid;
using World.Logic;
using static World.Logic.CirculationInterpolation;

namespace World.Tests;

static partial class SeasonalCirculationExperiment
{
	static bool CheckGeostrophicFaceWind(Ball ball)
	{
		var grid = BuildGrid(ball, new bool[ball.CellIds.Length]);
		var spec = new ClimateSpec { FrictionSurface = 1e-7f };
		var model = new CirculationOperator(ball, grid, spec);
		var transport = new ConservativeFaceTransport(ball, grid, model, model, Enumerable.Range(0, grid.Count).ToArray());
		// An eastward solid-body flow in geostrophic balance has Phi = -Omega*R*U*sin(latitude)^2.
		const double equatorialSpeed = 10;
		var potential = grid.Dirs.Select(dir => -7.2921e-5 * grid.RadiusKm * 1000 * equatorialSpeed * dir.Y * dir.Y).ToArray();
		var flux = new double[model.Edges.Length];
		model.BuildFaceFlux(potential, flux);
		var wind = transport.BuildWind(flux, spec.WindSpeedRefMs, physical: true);
		double error = 0, reference = 0;
		int westward = 0;
		for (int i = 0; i < grid.Count; i++)
		{
			if (Math.Abs(grid.LatDeg(i)) is < 20 or > 65) continue;
			var expected = -Vector3.Up.Cross(grid.Dirs[i]) * (float)equatorialSpeed;
			error += (wind[i] - expected).LengthSquared() * model.CellAreas[i];
			reference += expected.LengthSquared() * model.CellAreas[i];
			if (wind[i].Dot(expected) <= 0) westward++;
		}
		double relative = Math.Sqrt(error / reference);
		Console.WriteLine($"Geostrophic solid-body wind, both hemispheres 20-65°: vector relative RMS {relative:P2}, wrong-way cells {westward}.");
		return relative < 0.1 && westward == 0;
	}

	static bool AuditLowWind(ClimateGrid grid, ClimateSpec spec, WorldFacts facts, CirculationOperator fine,
		CirculationOperator coarse, ConservativeFaceTransport transport, InterpolationWeight[][] interpolation,
		int[] parent, double[] parentArea, double fluxScale)
	{
		string[] labels = { "current", "before correction", "local correction", "redistributed" };
		var east = grid.Dirs.Select(dir => TangentField.EastTangent(dir, TangentField.NorthTangent(dir))).ToArray();
		var latitude = Enumerable.Range(0, grid.Count).Select(grid.LatDeg).ToArray();
		var solver = LegacyWindSolver.Create(grid, spec);
		var gradient = new Vector3[grid.Count];
		var season = new double[2][][];
		var coast = fine.Edges.Select((edge, index) => (Edge: edge, Index: index))
			.Where(face => grid.Land[face.Edge.Left] != grid.Land[face.Edge.Right]).ToArray();
		var coarseFlux = new double[coarse.Edges.Length];
		var provisional = new double[grid.Count];
		var solution = new double[parentArea.Length];
		var initialFlux = new double[fine.Edges.Length];
		var desired = new double[grid.Count];
		bool passed = true;
		double worstResidual = 0, worstWorkRatio = 0, localMs = 0, optimizedMs = 0;
		int months = facts.PressureMonthlyHpa.Length;
		var bandSums = labels.Select(_ => new double[6]).ToArray();
		var bandAreas = new double[6];
		for (int i = 0; i < grid.Count; i++)
		{
			int band = Band(latitude[i]);
			if (band < 6) bandAreas[band] += fine.CellAreas[i];
		}
		Console.WriteLine("  Physical low wind (m/s), P50/P95 global, P95 at ±5°, P95 neighbor vector jump across/within parents.");
		for (int month = 0; month < months; month++)
		{
			var pressure = facts.PressureMonthlyHpa[month];
			var target = ToZeroMeanPotential(fine, pressure);
			var coarseTarget = new double[parentArea.Length];
			for (int i = 0; i < grid.Count; i++) coarseTarget[parent[i]] += target[i] * fine.CellAreas[i];
			for (int i = 0; i < coarseTarget.Length; i++) coarseTarget[i] /= parentArea[i];
			int iterations = coarse.Solve(coarseTarget, solution);
			passed &= iterations >= 0 && coarse.RelativeResidual(solution, coarseTarget) <= ResidualTolerance;
			coarse.BuildFaceFlux(solution, coarseFlux);
			for (int i = 0; i < target.Length; i++)
			{
				double adjustment = 0;
				foreach (var entry in interpolation[i])
					adjustment += entry.Weight * (solution[entry.Cell] - coarseTarget[entry.Cell]);
				provisional[i] = target[i] + adjustment;
				desired[i] = -adjustment * fine.CellAreas[i] / fluxScale;
			}
			fine.BuildFaceFlux(provisional, initialFlux);
			var local = (double[])initialFlux.Clone();
			var redistributed = (double[])initialFlux.Clone();
			long start = Stopwatch.GetTimestamp();
			var localError = transport.Correct(coarseFlux, local, (double[])desired.Clone());
			localMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
			start = Stopwatch.GetTimestamp();
			var error = transport.Correct(coarseFlux, redistributed, (double[])desired.Clone(), redistributionPasses: 3);
			optimizedMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
			worstResidual = Math.Max(worstResidual, Math.Max(Math.Max(error.Fine, error.Parent), error.Crossing));
			passed &= localError.Fine < 1e-9 && worstResidual < 1e-9;

			var current = new Vector3[grid.Count];
			TangentField.GradientField(grid, pressure, gradient);
			solver.SolveLowPhysical(gradient, current);
			var winds = new[] { current, transport.BuildWind(initialFlux, spec.WindSpeedRefMs, physical: true),
				transport.BuildWind(local, spec.WindSpeedRefMs, physical: true),
				transport.BuildWind(redistributed, spec.WindSpeedRefMs, physical: true) };
			double workRatio = ChangeEnergy(redistributed) / ChangeEnergy(local);
			worstWorkRatio = Math.Max(worstWorkRatio, workRatio);
			passed &= workRatio <= 1 + 1e-10;
			for (int variant = 0; variant < labels.Length; variant++)
			{
				for (int i = 0; i < grid.Count; i++)
				{
					int band = Band(latitude[i]);
					if (band < 6) bandSums[variant][band] += fine.CellAreas[i] * winds[variant][i].Dot(east[i]);
				}
				passed &= winds[variant].All(wind => wind.IsFinite());
			}
			if (month is not (January or July)) continue;
			Console.WriteLine($"  {MonthName(month)}. Redistribution/local change energy: {workRatio:P1}.");
			season[month == January ? 0 : 1] = new double[labels.Length][];
			for (int variant = 0; variant < labels.Length; variant++)
			{
				var wind = winds[variant];
				var across = fine.Edges.Where(edge => parent[edge.Left] != parent[edge.Right]);
				var within = fine.Edges.Where(edge => parent[edge.Left] == parent[edge.Right]);
				double across95 = Percentile(across.Select(edge => (wind[edge.Left] - wind[edge.Right]).Length()), 0.95);
				double within95 = Percentile(within.Select(edge => (wind[edge.Left] - wind[edge.Right]).Length()), 0.95);
				double normalError = FaceFitError(wind, variant == 1 ? initialFlux : variant == 2 ? local : redistributed);
				Console.WriteLine($"    {labels[variant],-19} {Percentile(wind.Select(value => value.Length()), 0.5),6:F2}/" +
					$"{Percentile(wind.Select(value => value.Length()), 0.95),6:F2}, " +
					$"eq {Percentile(wind.Where((_, i) => Math.Abs(latitude[i]) <= 5).Select(value => value.Length()), 0.95),6:F2}, " +
					$"jump {across95:F2}/{within95:F2} ({across95 / within95:F2}x)" +
					(variant > 0 ? $", face-fit error {normalError:P1}" : ""));
				season[month == January ? 0 : 1][variant] = coast.Select(face =>
				{
					var edge = face.Edge;
					double normalSpeed = variant == 0
						? (wind[edge.Left] + wind[edge.Right]).Dot(edge.Normal) / 2
						: (variant == 1 ? initialFlux : variant == 2 ? local : redistributed)[face.Index] / edge.LengthM;
					return normalSpeed * (grid.Land[edge.Right] ? 1 : -1);
				}).ToArray();
			}
		}
		Console.WriteLine("  Annual zonal wind, eastward positive, S50-70/S30-50/S10-25/N10-25/N30-50/N50-70 (m/s).");
		for (int variant = 0; variant < labels.Length; variant++)
			Console.WriteLine($"    {labels[variant],-19} " + string.Join(" / ", bandSums[variant].Take(6).Select((sum, i) => $"{sum / (months * bandAreas[i]):F2}")));
		PrintCoast("N 0-25", lat => lat >= 0 && lat < 25);
		PrintCoast("N 25-50", lat => lat >= 25 && lat < 50);
		PrintCoast("S 0-25", lat => lat < 0 && lat >= -25);
		PrintCoast("S 25-50", lat => lat < -25 && lat >= -50);
		PrintCoast("N 0-50 south-facing", lat => lat >= 0 && lat < 50, facing: 1);
		PrintCoast("S 0-50 north-facing", lat => lat < 0 && lat >= -50, facing: -1);
		Console.WriteLine($"  Worst redistribution/local change energy {worstWorkRatio:P1}; worst budget residual {worstResidual:E2}. " +
			$"12 transfers local {localMs:F0} ms, redistributed {optimizedMs:F0} ms. {(passed ? "PASS" : "FAIL")}");
		return passed;

		double ChangeEnergy(double[] flux)
		{
			double sum = 0;
			for (int i = 0; i < flux.Length; i++)
			{
				double change = flux[i] - initialFlux[i];
				sum += change * change / fine.Edges[i].LengthM;
			}
			return sum;
		}

		double FaceFitError(Vector3[] wind, double[] flux)
		{
			double error = 0, reference = 0;
			for (int i = 0; i < flux.Length; i++)
			{
				var edge = fine.Edges[i];
				double fit = (wind[edge.Left] + wind[edge.Right]).Dot(edge.Normal) / 2;
				double expected = flux[i] / edge.LengthM;
				error += (fit - expected) * (fit - expected) * edge.LengthM;
				reference += expected * expected * edge.LengthM;
			}
			return Math.Sqrt(error / reference);
		}

		void PrintCoast(string label, Predicate<double> includes, int facing = 0)
		{
			var values = new (double Winter, double Summer, double Reversed)[labels.Length];
			double length = 0;
			for (int i = 0; i < coast.Length; i++)
			{
				var edge = coast[i].Edge;
				double lat = (latitude[edge.Left] + latitude[edge.Right]) / 2;
				if (!includes(lat)) continue;
				if (facing != 0)
				{
					var midpoint = (grid.Dirs[edge.Left] + grid.Dirs[edge.Right]).Normalized();
					double inwardNorth = edge.Normal.Dot(TangentField.NorthTangent(midpoint))
						* (grid.Land[edge.Right] ? 1 : -1);
					if (facing * inwardNorth <= 0.5) continue;
				}
				length += edge.LengthM;
				for (int variant = 0; variant < values.Length; variant++)
				{
					double winter = season[lat >= 0 ? 0 : 1][variant][i];
					double summer = season[lat >= 0 ? 1 : 0][variant][i];
					values[variant].Winter += winter * edge.LengthM;
					values[variant].Summer += summer * edge.LengthM;
					if (winter < -0.5 && summer > 0.5) values[variant].Reversed += edge.LengthM;
				}
			}
			if (length == 0) return;
			Console.WriteLine($"  Coast {label} winter/summer m/s and reversal with both >0.5 m/s: " +
				string.Join(" | ", values.Select(value => $"{value.Winter / length:F2}/{value.Summer / length:F2}/{value.Reversed / length:P1}")));
		}
	}

	static int Band(float latitude)
	{
		float absolute = Math.Abs(latitude);
		int north = absolute is >= 10 and < 25 ? 3 : absolute is >= 30 and < 50 ? 4 : absolute is >= 50 and < 70 ? 5 : 6;
		return north == 6 || latitude >= 0 ? north : 5 - north;
	}

	static float Percentile(System.Collections.Generic.IEnumerable<float> values, double fraction)
	{
		var sorted = values.ToArray();
		Array.Sort(sorted);
		return sorted[(int)(fraction * (sorted.Length - 1))];
	}
}
