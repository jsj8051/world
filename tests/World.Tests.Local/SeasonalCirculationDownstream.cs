using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Godot;
using World.Data;
using World.H3Grid;
using World.Logic;

namespace World.Tests;

static partial class SeasonalCirculationExperiment
{
	static bool AuditFineBalance(Ball ball, ClimateGrid grid, ClimateSpec spec, LegacyWindSolver windSolver, int[] parent,
		int parentCount, float[][] monthlyTarget, Func<int, float[]> reconstruct,
		out Vector3[][] monthlyLowCorrected, out Vector3[] annualFreeCorrected, out double[] cellAreas)
	{
		monthlyLowCorrected = null;
		annualFreeCorrected = null;
		var setup = Stopwatch.StartNew();
		var fineOperator = new CirculationOperator(ball, grid, spec);
		cellAreas = fineOperator.CellAreas;
		fineOperator.SetRelaxation(ValidationRelaxationDays * 86400 * WaveSpeedMs * WaveSpeedMs);
		setup.Stop();
		var gradient = new Vector3[grid.Count];
		var velocity = new Vector3[grid.Count];
		var pressure = new float[grid.Count];
		var surface = new Vector3[grid.Count];
		var low = new Vector3[grid.Count];
		var free = new Vector3[grid.Count];
		var initialFine = new double[grid.Count];
		var corrected = new double[grid.Count];
		var correctedLow = new Vector3[monthlyTarget.Length][];
		var correctedAnnualFree = new Vector3[grid.Count];
		bool finite = true;
		bool allCorrected = true;
		double totalSolveMs = 0, worstCorrectedFace = 0, worstCorrectedWind = 0;
		int maxIterations = 0;
		Console.WriteLine($"  Fine mass audit: res4 edge geometry setup {setup.Elapsed.TotalMilliseconds:F0} ms.");
		Console.WriteLine("  month     shared face residual (raw/rebuilt/corrected)     cell-wind residual (raw/rebuilt/corrected)     parent-binned cell residual");
		for (int month = 0; month < monthlyTarget.Length; month++)
		{
			var target = ToZeroMeanPotential(fineOperator, monthlyTarget[month]);
			var rebuilt = reconstruct(month);
			var reconstructed = ToZeroMeanPotential(fineOperator, rebuilt);
			double pressureMeanShift = fineOperator.AreaMean(rebuilt.Select(value => (double)value).ToArray())
				- fineOperator.AreaMean(monthlyTarget[month].Select(value => (double)value).ToArray());
			for (int i = 0; i < initialFine.Length; i++) initialFine[i] = reconstructed[i] + pressureMeanShift * 100 / AirDensity;
			var baselineWind = Measure(monthlyTarget[month], target);
			var reconstructedWind = Measure(rebuilt, initialFine);
			double rawResidual = fineOperator.RelativeResidual(target, target);
			double rebuiltResidual = fineOperator.RelativeResidual(initialFine, target);

			long start = Stopwatch.GetTimestamp();
			int iterations = fineOperator.Solve(target, corrected, initialFine, tolerance: 1e-5, iterationLimit: 900);
			totalSolveMs += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
			double correctedResidual = fineOperator.RelativeResidual(corrected, target);
			bool solved = iterations >= 0 && correctedResidual <= 1e-5
				&& Math.Abs(fineOperator.AreaMean(corrected)) * AirDensity / 100 < 1e-3;
			allCorrected &= solved;
			maxIterations = Math.Max(maxIterations, iterations);
			worstCorrectedFace = Math.Max(worstCorrectedFace, correctedResidual);
			if (solved)
			{
				for (int i = 0; i < pressure.Length; i++)
					pressure[i] = monthlyTarget[month][i] + (float)((corrected[i] - target[i]) * AirDensity / 100);
				var correctedWind = Measure(pressure, corrected);
				worstCorrectedWind = Math.Max(worstCorrectedWind, correctedWind.Fine);
				TangentField.GradientField(grid, pressure, gradient);
				windSolver.SolveAll(gradient, surface, low, free);
				finite &= pressure.All(float.IsFinite) && low.All(value => value.IsFinite()) && free.All(value => value.IsFinite());
				correctedLow[month] = (Vector3[])low.Clone();
				for (int i = 0; i < free.Length; i++) correctedAnnualFree[i] += free[i];
				if (month is January or July)
				{
					Console.WriteLine($"  {MonthName(month),5}     {rawResidual,8:F3}/{rebuiltResidual,8:F3}/{correctedResidual,10:E2}" +
						$"                    {baselineWind.Fine,8:F3}/{reconstructedWind.Fine,8:F3}/{correctedWind.Fine,8:F3}" +
						$"                    {baselineWind.Parent,7:F3}/{reconstructedWind.Parent,7:F3}/{correctedWind.Parent,7:F3}");
				}
			}
			else
			{
				Console.WriteLine($"  month {month + 1}: fine face solve did not meet tolerance (iterations {iterations}, residual {correctedResidual:E3}).");
			}
			finite &= double.IsFinite(rawResidual) && double.IsFinite(rebuiltResidual)
				&& double.IsFinite(baselineWind.Fine) && double.IsFinite(reconstructedWind.Fine)
				&& double.IsFinite(baselineWind.Parent) && double.IsFinite(reconstructedWind.Parent);

			(double Fine, double Parent) Measure(float[] pressureHpa, double[] potential)
			{
				TangentField.GradientField(grid, pressureHpa, gradient);
				windSolver.SolveSurfacePhysical(gradient, velocity);
				return fineOperator.CellWindResidual(potential, target, velocity, parent, parentCount);
			}
		}
		Console.WriteLine($"  Direct res4 comparison: {totalSolveMs / 1000:F2} s for 12 solves, up to {maxIterations} iterations, " +
			$"worst face/cell-wind residual {worstCorrectedFace:E3}/{worstCorrectedWind:E3}.");
		if (allCorrected && finite)
		{
			for (int i = 0; i < correctedAnnualFree.Length; i++) correctedAnnualFree[i] /= monthlyTarget.Length;
			monthlyLowCorrected = correctedLow;
			annualFreeCorrected = correctedAnnualFree;
		}
		return finite && allCorrected;
	}

	static bool CompareDownstream(ClimateGrid grid, WorldSpec worldSpec, WorldFacts facts, Vector3[][] monthlyLow,
		Vector3[] annualFree, Vector3[][] monthlyLowCorrected, Vector3[] annualFreeCorrected, double[] cellAreas)
	{
		var spec = worldSpec.Climate;
		var relaxation = OceanCurrentModel.BuildRelaxationCells(grid);
		var initial = new OceanCurrentModel();
		var prevailing = PrevailingWind.BuildAll(grid.Dirs, spec.RotationSpeed, spec.Prograde);
		initial.Compute(grid, spec, prevailing, null, null, 120, relaxation, CancellationToken.None);
		var warmth = initial.BuildCoastalWarmth(grid);
		var oceanControl = ComputeOcean(facts.Wind.Free.Annual);
		double controlOceanError = Enumerable.Range(0, grid.Count).Max(i => (oceanControl.Direction[i] - facts.OceanCurrentDirection[i]).Length());
		bool controlsMatch = controlOceanError == 0
			&& MaxDifference(oceanControl.Strength, facts.OceanCurrentStrength) == 0
			&& MaxDifference(oceanControl.Warmth, facts.OceanCurrentWarmth) == 0;
		Console.WriteLine($"  Published ocean replay: {(controlsMatch ? "PASS" : "FAIL")} " +
			$"(max direction error {controlOceanError:E2}). Rain comparisons use published face-transport rain.");
		bool finite = Report("coarse reconstruction", monthlyLow, annualFree);
		if (monthlyLowCorrected != null) finite &= Report("direct fine face solve", monthlyLowCorrected, annualFreeCorrected);
		return controlsMatch && finite;

		OceanCurrentModel ComputeOcean(Vector3[] wind)
		{
			var ocean = new OceanCurrentModel();
			ocean.Compute(grid, spec, wind, facts.TemperatureC, initial.Psi, 300, relaxation, CancellationToken.None);
			return ocean;
		}

		bool Report(string label, Vector3[][] low, Vector3[] free)
		{
			var rain = MonthlyClimate.ComputePrecipitationFromWind(grid, spec, warmth, worldSpec.Seed, low);
			var ocean = ComputeOcean(free);
			Console.WriteLine($"  Downstream {label}:");
			PrintRain("all land", i => grid.Land[i]);
			PrintRain("tropical land", i => grid.Land[i] && Math.Abs(grid.LatDeg(i)) < 25);
			PrintRain("inland land", i => grid.Land[i] && grid.DistToCoast[i] >= 10);
			int sea = 0, reversed = 0;
			double oldStrength = 0, newStrength = 0, change = 0, warmthChange = 0;
			for (int i = 0; i < grid.Count; i++)
			{
				if (grid.Land[i]) continue;
				sea++;
				oldStrength += oceanControl.Strength[i];
				newStrength += ocean.Strength[i];
				var previous = oceanControl.Direction[i] * oceanControl.Strength[i];
				var updated = ocean.Direction[i] * ocean.Strength[i];
				change += (updated - previous).LengthSquared();
				warmthChange += Math.Abs(ocean.Warmth[i] - oceanControl.Warmth[i]);
				if (previous.Dot(updated) < 0) reversed++;
			}
			Console.WriteLine($"    Ocean: mean strength {oldStrength / sea:F3} -> {newStrength / sea:F3}, " +
				$"vector RMS change {Math.Sqrt(change / sea):F3}, >90 deg {reversed / (double)sea:P1}, " +
				$"mean warmth change {warmthChange / sea:F3}, SOR residual {oceanControl.Residual:E2} -> {ocean.Residual:E2}.");
			return rain.All(row => row.All(value => float.IsFinite(value) && value >= 0))
				&& ocean.Direction.All(value => value.IsFinite()) && ocean.Strength.All(float.IsFinite) && ocean.Warmth.All(float.IsFinite);

			void PrintRain(string region, Predicate<int> includes)
			{
				double before = 0, after = 0, summerBefore = 0, summerAfter = 0, totalArea = 0, changedArea = 0;
				for (int i = 0; i < grid.Count; i++)
				{
					if (!includes(i)) continue;
					double area = cellAreas[i];
					double oldAnnual = facts.PrecipitationMm[i];
					double newAnnual = 0;
					for (int month = 0; month < rain.Length; month++) newAnnual += rain[month][i];
					int summer = grid.LatRad(i) >= 0 ? July : January;
					before += area * oldAnnual;
					after += area * newAnnual;
					summerBefore += area * facts.PrecipitationMonthlyMm[summer][i];
					summerAfter += area * rain[summer][i];
					if (Math.Abs(newAnnual - oldAnnual) > 0.2 * oldAnnual) changedArea += area;
					totalArea += area;
				}
				if (totalArea == 0) return;
				Console.WriteLine($"    Rain {region}: {before / totalArea:F0} -> {after / totalArea:F0} mm/yr, " +
					$"summer month {summerBefore / totalArea:F1} -> {summerAfter / totalArea:F1} mm, >20% annual change {changedArea / totalArea:P1}.");
			}
		}
	}

	static double MaxDifference(float[] first, float[] second) =>
		first.Select((value, i) => Math.Abs((double)value - second[i])).Max();
}
