using System;
using System.Linq;
using Godot;
using World.Data;
using World.Logic;
using static World.Logic.CirculationInterpolation;

namespace World.Tests;

static partial class SeasonalCirculationExperiment
{
	readonly record struct ThermalCase(string Name, bool OceanReference, float Gain);
	readonly record struct ThermalWindMonth(Vector3[] Velocity, double[] CoastalSpeed);

	static bool AuditThermalForcing(WorldGenSimulation simulation, ClimateGrid grid, ClimateGrid coarseGrid, WorldSpec worldSpec,
		CirculationOperator fine, CirculationOperator coarse, ConservativeFaceTransport transport,
		InterpolationWeight[][] interpolation, int[] parent, double[] parentArea, double fluxScale, bool fullYear)
	{
		var facts = simulation.Facts;
		var spec = worldSpec.Climate;
		var coarseLayer = CoarseLayer.Parent(simulation.Ball, 2);
		var insolation = MonthlyInsolation.BuildYear(facts.PressureMonthlyHpa.Length, spec.ObliquityDeg);
		var noThermal = new ClimateSpec();
		spec.CopyTo(noThermal);
		noThermal.PressureThermalGain = 0;
		var backgrounds = new float[insolation.Length][];
		var matchedOcean = new float[insolation.Length][];
		double minRatio = double.PositiveInfinity, maxRatio = 0, controlError = 0;
		var seaLevelTemperature = new float[grid.Count];
		var annualSeaLevel = new float[grid.Count];
		for (int i = 0; i < grid.Count; i++)
		{
			float annualSum = 0f;
			foreach (var row in facts.TemperatureMonthlyC) annualSum += row[i];
			annualSeaLevel[i] = annualSum / insolation.Length + ElevationCorrection(i);
		}
		var control = PressureFieldModel.Create(grid, spec, coarseLayer, annualSeaLevel);
		var backgroundModel = PressureFieldModel.Create(grid, noThermal, coarseLayer, annualSeaLevel);
		var controlPressure = new float[grid.Count];
		for (int month = 0; month < insolation.Length; month++)
		{
			var baseline = facts.PressureMonthlyHpa[month];
			var temperature = facts.TemperatureMonthlyC[month];
			float declination = insolation[month].DeclinationRad;
			for (int i = 0; i < grid.Count; i++) seaLevelTemperature[i] = temperature[i] + ElevationCorrection(i);
			control.BuildMonth(seaLevelTemperature, declination, controlPressure);
			controlError = Math.Max(controlError, MaxDifference(controlPressure, baseline));
			var background = backgrounds[month] = new float[grid.Count];
			backgroundModel.BuildMonth(seaLevelTemperature, declination, background);
		}

		if (!fullYear)
		{
			var oceanGrid = ClimateGrid.FromArrays(grid.Dirs, grid.Neighbors, new bool[grid.Count], new float[grid.Count],
				grid.RadiusKm, grid.NeighborKm);
			var oceanTemperatures = BuildTemperatures(oceanGrid, spec, insolation);
			var oceanAnnual = AnnualMean(oceanTemperatures);
			var actualAnnual = AnnualMean(facts.TemperatureMonthlyC);
			var workspace = control.CreateWorkspace();
			for (int month = 0; month < insolation.Length; month++)
			{
				var temperature = facts.TemperatureMonthlyC[month];
				var background = backgrounds[month];
				var anomaly = new float[grid.Count];
				for (int i = 0; i < grid.Count; i++)
					anomaly[i] = (temperature[i] - actualAnnual[i]) - (oceanTemperatures[month][i] - oceanAnnual[i]);
				var oceanTarget = new float[grid.Count];
				control.BuildMonth(temperature, insolation[month].DeclinationRad, oceanTarget, workspace, anomaly);
				double legacyRms = CenteredRms(facts.PressureMonthlyHpa[month], background);
				double oceanRms = CenteredRms(oceanTarget, background);
				double ratio = oceanRms > 0 ? legacyRms / oceanRms : 1;
				minRatio = Math.Min(minRatio, ratio);
				maxRatio = Math.Max(maxRatio, ratio);
				matchedOcean[month] = oceanTarget.Zip(background, (full, belt) => (float)((full - belt) * ratio)).ToArray();
			}
		}
		Console.WriteLine($"  Production pressure replay max error {controlError:E2} hPa.");
		var cases = new[] { new ThermalCase("legacy x1", false, 1), new ThermalCase("legacy x1.5", false, 1.5f),
			new ThermalCase("ocean ref x1", true, 1), new ThermalCase("ocean ref x1.5", true, 1.5f),
			new ThermalCase("legacy x2", false, 2), new ThermalCase("ocean ref x2", true, 2) };
		var coast = fine.Edges.Select((edge, index) => (Edge: edge, Index: index))
			.Where(face => grid.Land[face.Edge.Left] != grid.Land[face.Edge.Right]).Select(face =>
		{
			var (edge, index) = face;
			var midpoint = (grid.Dirs[edge.Left] + grid.Dirs[edge.Right]).Normalized();
			int sign = grid.Land[edge.Right] ? 1 : -1;
			return new ThermalCoast(edge, index, Mathf.RadToDeg(Mathf.Asin(midpoint.Y)),
				InwardNorth: sign * edge.Normal.Dot(TangentField.NorthTangent(midpoint)), Sign: sign);
		}).ToArray();
		var latitude = Enumerable.Range(0, grid.Count).Select(grid.LatDeg).ToArray();
		var east = grid.Dirs.Select(dir => TangentField.EastTangent(dir, TangentField.NorthTangent(dir))).ToArray();
		var coarseFlux = new double[coarse.Edges.Length];
		var fineFlux = new double[fine.Edges.Length];
		var provisional = new double[grid.Count];
		var desired = new double[grid.Count];
		var solution = new double[coarseGrid.Count];
		var coarseTarget = new double[coarseGrid.Count];
		var pressure = new float[grid.Count];
		var monthIndices = new[] { January, July };
		double worstResidual = 0;
		bool passed = controlError == 0;
		if (fullYear)
		{
			foreach (var choice in cases.Take(2))
				passed &= ReportAnnualLowWind(choice.Name, latitude, east, fine.CellAreas, coast,
					month => SolveMonth(month, choice), insolation.Length);
			Console.WriteLine($"  Worst face budget/crossing relative residual {worstResidual:E2}. {(passed ? "PASS" : "FAIL")}");
			return passed;
		}
		passed &= minRatio > 0 && double.IsFinite(maxRatio);
		Console.WriteLine($"  Ocean-reference RMS matching factors {minRatio:F2}-{maxRatio:F2}.");
		Console.WriteLine("  Jan/Jul experiment. Coast columns: summer inward m/s / winter-offshore-summer-onshore share (both >0.5 m/s).");
		Console.WriteLine("  case            equator P95 J/J    N/S trade J+J m/s    N-facing-south     S-facing-north      N 0-25             S 0-25");
		foreach (var choice in cases)
		{
			var season = monthIndices.Select(month => SolveMonth(month, choice)).ToArray();
			double eqJan = EquatorialP95(season[0].Velocity), eqJul = EquatorialP95(season[1].Velocity);
			double northTrades = TradeMean(season, north: true), southTrades = TradeMean(season, north: false);
			Console.WriteLine($"  {choice.Name,-15} {eqJan,6:F2}/{eqJul,-6:F2}     {northTrades,6:F2}/{southTrades,-6:F2}" +
				$"    {CoastStats(season, lat => lat is >= 0 and < 50, 1),-16}" +
				$"   {CoastStats(season, lat => lat is >= -50 and < 0, -1),-16}" +
				$"   {CoastStats(season, lat => lat is >= 0 and < 25),-16}" +
				$"   {CoastStats(season, lat => lat is >= -25 and < 0)}");
		}
		Console.WriteLine($"  Worst face budget/crossing relative residual {worstResidual:E2}. {(passed ? "PASS" : "FAIL")}");
		return passed;

		float ElevationCorrection(int cell) => grid.Land[cell] && grid.HeightM[cell] > 0
			? spec.LapseRateCPerKm / 1000f * grid.HeightM[cell] : 0;

		double CenteredRms(float[] total, float[] background)
		{
			double area = 0, sum = 0, square = 0;
			for (int i = 0; i < grid.Count; i++)
			{
				double anomaly = total[i] - background[i];
				double weight = fine.CellAreas[i];
				area += weight;
				sum += weight * anomaly;
				square += weight * anomaly * anomaly;
			}
			return Math.Sqrt(Math.Max(0, square / area - (sum / area) * (sum / area)));
		}

		ThermalWindMonth SolveMonth(int month, ThermalCase choice)
		{
			var baseline = facts.PressureMonthlyHpa[month];
			var background = backgrounds[month];
			for (int i = 0; i < pressure.Length; i++)
			{
				float thermal = choice.OceanReference ? matchedOcean[month][i] : baseline[i] - background[i];
				pressure[i] = background[i] + choice.Gain * thermal;
			}
			var target = ToZeroMeanPotential(fine, pressure);
			Array.Clear(coarseTarget);
			for (int i = 0; i < target.Length; i++) coarseTarget[parent[i]] += target[i] * fine.CellAreas[i];
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
			fine.BuildFaceFlux(provisional, fineFlux);
			var error = transport.Correct(coarseFlux, fineFlux, desired, redistributionPasses: 3);
			worstResidual = Math.Max(worstResidual, Math.Max(error.Fine, Math.Max(error.Parent, error.Crossing)));
			var velocity = transport.BuildWind(fineFlux, spec.WindSpeedRefMs, physical: true);
			passed &= worstResidual < 1e-9 && velocity.All(value => value.IsFinite());
			return new ThermalWindMonth(velocity, coast.Select(face => face.Sign * fineFlux[face.Index] / face.Edge.LengthM).ToArray());
		}

		double EquatorialP95(Vector3[] wind) =>
			Percentile(wind.Where((_, i) => Math.Abs(latitude[i]) <= 5).Select(value => value.Length()), 0.95);

		double TradeMean(ThermalWindMonth[] season, bool north)
		{
			double sum = 0, area = 0;
			for (int i = 0; i < latitude.Length; i++)
			{
				double lat = north ? latitude[i] : -latitude[i];
				if (lat is < 10 or >= 25) continue;
				sum += fine.CellAreas[i] * (season[0].Velocity[i] + season[1].Velocity[i]).Dot(east[i]) / 2;
				area += fine.CellAreas[i];
			}
			return sum / area;
		}

		string CoastStats(ThermalWindMonth[] season, Predicate<double> includes, int facing = 0)
		{
			double sum = 0, length = 0, reversed = 0;
			for (int i = 0; i < coast.Length; i++)
			{
				var face = coast[i];
				if (!includes(face.Latitude) || (facing != 0 && face.InwardNorth * facing <= 0.5)) continue;
				int summer = face.Latitude >= 0 ? 1 : 0;
				double speed = season[summer].CoastalSpeed[i];
				length += face.Edge.LengthM;
				sum += speed * face.Edge.LengthM;
				if (speed > 0.5 && season[1 - summer].CoastalSpeed[i] < -0.5) reversed += face.Edge.LengthM;
			}
			return length > 0 ? $"{sum / length,5:F2}/{reversed / length,6:P1}" : "n/a";
		}
	}
}
