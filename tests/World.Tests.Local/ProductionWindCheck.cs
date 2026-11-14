using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Godot;
using World.Data;
using World.Data.Generation;
using World.Logic;
using World.Params;
using World.Params.Generation;

namespace World.Tests;

static class ProductionWindCheck
{
	public static void ReadsRetiredTrialProfile()
	{
		var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
		var profile = JsonSerializer.SerializeToNode(new GenerationConfig(), options).AsObject();
		Program.True(!profile.ContainsKey("useConservativeLowWind"), "New profiles must omit the retired switch.");
		profile["useConservativeLowWind"] = true;
		var old = profile.Deserialize<GenerationConfig>(options);
		old.Validate();
		Program.True(!JsonSerializer.SerializeToNode(old, options).AsObject().ContainsKey("useConservativeLowWind"),
			"Re-exporting a trial profile must remove the retired switch.");
		var spec = WorldParams.ReadJson("{\"Climate\":{\"UseConservativeLowWind\":true}}", out var problems);
		Program.True(spec != null && problems.Count == 0, "An older world configuration must still load.");
	}

	public static int Run()
	{
		string json = WorldParamStore.ReadPresetText(out var ioProblems);
		var defaults = WorldParams.ReadJson(json, out var codecProblems);
		if (defaults == null || ioProblems.Count != 0 || codecProblems.Count != 0)
			throw new InvalidOperationException("Default world parameters are unavailable.");
		foreach (var sample in new[] { (Res: 0, Layout: ContinentLayout.Earthlike, Seed: 42),
			(Res: 2, Layout: ContinentLayout.Earthlike, Seed: 42), (Res: 3, Layout: ContinentLayout.Earthlike, Seed: 42),
			(Res: 4, Layout: ContinentLayout.Earthlike, Seed: 42), (Res: 4, Layout: ContinentLayout.Pangea, Seed: 7),
			(Res: 4, Layout: ContinentLayout.Archipelago, Seed: 123) })
		{
			var config = new GenerationConfig { Layout = sample.Layout, Seed = sample.Seed };
			var spec = GenerationConfigMapper.CreateWorldSpec(config, defaults);
			Console.WriteLine($"Generating res{sample.Res} {sample.Layout} seed {sample.Seed}...");
			var watch = Stopwatch.StartNew();
			var simulation = new WorldGenSimulation(sample.Res, 2f);
			simulation.Run(spec);
			double elapsed = watch.Elapsed.TotalMilliseconds;
			var facts = simulation.Facts;
			var grid = ClimateGrid.From(simulation.Ball, simulation.Terrain, facts.SeaLevelM);
			var legacy = LegacyWindSolver.Create(grid, spec.Climate);
			var gradient = new Vector3[grid.Count];
			var surface = new Vector3[grid.Count];
			var low = new Vector3[grid.Count];
			var free = new Vector3[grid.Count];
			var legacyAnnualFree = new Vector3[grid.Count];
			var delta = new double[3];
			var peakEq = new double[3];
			int wrongZonalSigns = 0;
			var latitude = Enumerable.Range(0, grid.Count).Select(grid.LatDeg).ToArray();
			var equator = Enumerable.Range(0, grid.Count).Where(i => Math.Abs(latitude[i]) <= 5).ToArray();
			var east = grid.Dirs.Select(dir => TangentField.EastTangent(dir, TangentField.NorthTangent(dir))).ToArray();
			var kinds = new[] { WindKind.Surface, WindKind.Low, WindKind.Free };
			int months = facts.PressureMonthlyHpa.Length;
			for (int month = 0; month < months; month++)
			{
				TangentField.GradientField(grid, facts.PressureMonthlyHpa[month], gradient);
				legacy.SolveAll(gradient, surface, low, free);
				for (int i = 0; i < grid.Count; i++) legacyAnnualFree[i] += free[i];
				var legacyWinds = new[] { surface, low, free };
				for (int kind = 0; kind < kinds.Length; kind++)
				{
					var wind = facts.Wind.Get(kinds[kind]).Monthly[month];
					Program.True(wind.Length == grid.Count, "Wind row has the wrong number of cells.");
					var zonal = new double[4];
					for (int i = 0; i < grid.Count; i++)
					{
						if (!wind[i].IsFinite() || wind[i].LengthSquared() > 1.00001f || Math.Abs(wind[i].Dot(grid.Dirs[i])) >= 1e-5f)
							throw new InvalidOperationException($"Invalid {kinds[kind]} wind at month {month + 1}, cell {i}: " +
								$"{wind[i]}, length² {wind[i].LengthSquared():R}, radial {wind[i].Dot(grid.Dirs[i]):R}.");
						delta[kind] += (wind[i] - legacyWinds[kind][i]).LengthSquared();
						int band = latitude[i] switch
						{
							>= 10 and < 25 => 0,
							<= -10 and > -25 => 1,
							>= 30 and < 50 => 2,
							<= -30 and > -50 => 3,
							_ => -1
						};
						if (band >= 0) zonal[band] += wind[i].Dot(east[i]);
					}
					if (sample.Res == 4)
						wrongZonalSigns += zonal.Where((value, band) => band < 2 ? value >= 0 : value <= 0).Count();
					if (equator.Length > 0)
					{
						var speeds = equator.Select(i => Math.Min(0.999999, wind[i].Length())).Order().ToArray();
						double p95 = -spec.Climate.WindSpeedRefMs * Math.Log(1 - speeds[(int)(0.95 * (speeds.Length - 1))]);
						peakEq[kind] = Math.Max(peakEq[kind], p95);
					}
				}
			}
			Program.True(delta.All(value => value > 0), "All three production closures must use the new solver.");
			Program.True(wrongZonalSigns == 0, "A res4 trade-wind or westerly band changed sign.");
			Program.True(facts.PrecipitationMm.All(value => float.IsFinite(value) && value >= 0), "Invalid rain.");
			Program.True(facts.OceanCurrentDirection.All(value => value.IsFinite())
				&& facts.OceanCurrentStrength.All(float.IsFinite), "Invalid ocean currents.");
			GeographySnapshotCapture.Capture(facts, Guid.NewGuid(), "Production wind", "", DateTimeOffset.UnixEpoch, "production-wind");
			for (int i = 0; i < grid.Count; i++) legacyAnnualFree[i] /= months;
			var relaxationCells = OceanCurrentModel.BuildRelaxationCells(grid);
			var initialOcean = new OceanCurrentModel();
			var climate = spec.Climate;
			initialOcean.Compute(grid, climate, PrevailingWind.BuildAll(grid.Dirs, climate.RotationSpeed, climate.Prograde),
				null, null, 120, relaxationCells, CancellationToken.None);
			var legacyOcean = new OceanCurrentModel();
			legacyOcean.Compute(grid, climate, legacyAnnualFree, facts.TemperatureC, initialOcean.Psi, 300,
				relaxationCells, CancellationToken.None);
			int activeOceanCells = 0, reversedOceanCells = 0;
			for (int i = 0; i < grid.Count; i++)
			{
				if (legacyOcean.Direction[i].LengthSquared() == 0 || facts.OceanCurrentDirection[i].LengthSquared() == 0) continue;
				activeOceanCells++;
				if (legacyOcean.Direction[i].Dot(facts.OceanCurrentDirection[i]) < 0) reversedOceanCells++;
			}
			double oceanReversal = activeOceanCells > 0 ? (double)reversedOceanCells / activeOceanCells : 0;
			Console.WriteLine($"  PASS {elapsed:F0} ms; normalized RMS vs retired wind (surface/low/free) " +
				$"{string.Join("/", delta.Select(value => Math.Sqrt(value / (grid.Count * months)).ToString("F3")))}; " +
				$"peak equatorial P95 {string.Join("/", peakEq.Select(value => value.ToString("F2")))} m/s; " +
				$"res4 wrong-sign band-months {wrongZonalSigns}; rain {facts.PrecipitationMm.Average():F1} mm; " +
				$"ocean turns >90° in {oceanReversal:P1} of cells with currents in both runs; " +
				$"ocean residual {simulation.Climate.OceanCurrents.Residual:E2}.");
		}
		return 0;
	}
}
