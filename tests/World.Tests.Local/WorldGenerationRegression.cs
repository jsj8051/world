using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using World.Data;
using World.Data.Maps;
using World.Data.Work;
using World.Logic;

namespace World.Tests;

internal static class WorldGenerationRegression
{
	static readonly JsonSerializerOptions Json = new()
	{
		WriteIndented = true,
		NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
		Converters = { new JsonStringEnumConverter() }
	};

	static readonly Sample[] Samples =
	{
		new("earthlike-42", 42, ContinentLayout.Earthlike),
		new("pangea-7", 7, ContinentLayout.Pangea),
		new("archipelago-123", 123, ContinentLayout.Archipelago)
	};

	internal static int Run(int resolution, string reportPath)
	{
		if (resolution is not (2 or 4))
		{
			Console.Error.WriteLine("World regression supports resolution 2 (quick) or 4 (production).");
			return 2;
		}
		var results = new List<SampleResult>();
		foreach (Sample sample in Samples)
		{
			var result = new SampleResult { Id = sample.Id, Spec = new WorldSpec { Seed = sample.Seed, Layout = sample.Layout } };
			results.Add(result);
			for (int repeat = 0; repeat < 2; repeat++)
			{
				var run = new RunResult();
				result.Runs.Add(run);
				try
				{
					Generate(result.Spec, resolution, run);
					if (repeat == 1) CompareHashes(result.Runs[0].Hashes, run.Hashes);
					Console.WriteLine($"[WORLD-REGRESSION] {sample.Id} run={repeat + 1} PASS "
						+ $"build={run.BuildMilliseconds:F0}ms land={run.Metrics["landFraction"]:F4} "
						+ $"temp={run.Metrics["temperatureMeanC"]:F2} rain={run.Metrics["precipitationMeanMm"]:F2}");
				}
				catch (Exception error)
				{
					run.Error = error.ToString();
					Console.Error.WriteLine($"[WORLD-REGRESSION] {sample.Id} run={repeat + 1} FAIL stage={run.Stage}: {error}");
					break;
				}
			}
		}
		bool passed = results.All(result => result.Runs.Count == 2 && result.Runs.All(run => run.Error == null));
		string path = Path.GetFullPath(reportPath);
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		File.WriteAllText(path, JsonSerializer.Serialize(new
		{
			SchemaVersion = 1, CreatedUtc = DateTimeOffset.UtcNow, Passed = passed, Resolution = resolution,
			Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
			Architecture = RuntimeInformation.ProcessArchitecture.ToString(), ProcessorCount = Environment.ProcessorCount,
			Samples = results
		}, Json));
		Console.WriteLine($"[WORLD-REGRESSION] report={path} {(passed ? "PASS" : "FAIL")}");
		return passed ? 0 : 1;
	}

	static void Generate(WorldSpec spec, int resolution, RunResult run)
	{
		var watch = Stopwatch.StartNew();
		run.Stage = "grid";
		var simulation = new WorldGenSimulation(resolution, 2f);
		run.StageMilliseconds.Add("grid", watch.Elapsed.TotalMilliseconds);
		var stageWatch = Stopwatch.StartNew();
		var stageStarts = new Dictionary<string, long>(StringComparer.Ordinal);
		run.Stage = "plate";
		simulation.Signal += signal =>
		{
			if (signal.Kind == WorkSignalKind.StageStarted)
			{
				run.Stage = signal.StageId;
				stageStarts[signal.StageId] = Stopwatch.GetTimestamp();
				return;
			}
			if (signal.Kind != WorkSignalKind.UnitCompleted || signal.Done != signal.Total) return;
			if (stageStarts.TryGetValue(signal.StageId, out long timestamp))
				run.StageMilliseconds[signal.StageId] = (Stopwatch.GetTimestamp() - timestamp) * 1000d / Stopwatch.Frequency;
			stageWatch.Restart();
			run.Stage = signal.StageId == "plate" ? "terrain" : signal.StageId;
		};
		// Each repeat owns a new grid and simulation; cached stage state cannot hide drift.
		var input = new WorldSpec();
		spec.CopyTo(input);
		simulation.Run(input);
		run.BuildMilliseconds = watch.Elapsed.TotalMilliseconds;
		run.Stage = "snapshot-validation";
		var snapshot = GeographySnapshotCapture.Capture(simulation.Facts, Guid.Parse("00000000-0000-0000-0000-000000000001"), "Regression", "",
			DateTimeOffset.UnixEpoch, "world-regression-v1");
		run.Stage = "metrics";
		run.Metrics = Measure(simulation.Facts);
		ValidateMetrics(run.Metrics);
		if (spec.Layout != ContinentLayout.Archipelago)
			Require(run.Metrics["riverCells"] > 0, "hydrology: continental samples must contain rivers");
		ValidateAnnualClimate(simulation.Facts);
		run.Stage = "fingerprints";
		run.Hashes = new Dictionary<string, string>
		{
			["grid"] = Fingerprint(snapshot.Grid),
			["plate"] = Fingerprint(new { snapshot.Surface, snapshot.Tectonics }),
			["terrain"] = Fingerprint(snapshot.Terrain),
			["climate"] = Fingerprint(snapshot.Climate),
			["hydrology"] = Fingerprint(snapshot.Hydrology),
			["index"] = Fingerprint(snapshot.QueryIndex)
		};
		run.Stage = "determinism";
	}

	static Dictionary<string, double> Measure(WorldFacts facts)
	{
		int cells = facts.HeightM.Length;
		var heights = (float[])facts.HeightM.Clone();
		Array.Sort(heights);
		return new Dictionary<string, double>
		{
			["cells"] = cells,
			["landFraction"] = facts.FinalLand.Count(land => land) / (double)cells,
			["landmasses"] = facts.FinalLandmassCount,
			["heightP10M"] = heights[(int)((cells - 1) * .1)],
			["heightP50M"] = heights[(cells - 1) / 2],
			["heightP90M"] = heights[(int)((cells - 1) * .9)],
			["temperatureMeanC"] = facts.TemperatureC.Average(value => (double)value),
			["precipitationMeanMm"] = facts.PrecipitationMm.Average(value => (double)value),
			["riverCells"] = facts.Rivers.IsRiver.Count(value => value),
			["riverOutlets"] = facts.RiverTopology.Outlets.Count,
			["basinCount"] = facts.Basins.BasinCount,
			["lakeCount"] = facts.Lakes.LakeCount,
			["spatialLandSources"] = facts.SpatialIndex.NearestLandmassId.Distinct().Count(value => value >= 0),
			["spatialRiverSources"] = facts.SpatialIndex.NearestRiverCell.Distinct().Count(value => value >= 0)
		};
	}

	static void ValidateMetrics(IReadOnlyDictionary<string, double> metrics)
	{
		foreach (var metric in metrics)
			Require(double.IsFinite(metric.Value), $"metrics.{metric.Key}: nonfinite value {metric.Value}");
		// These are broad sanity bounds, not an Earth calibration or an automatically accepted golden baseline.
		InRange(metrics["landFraction"], .01, .95, "terrain.landFraction");
		InRange(metrics["temperatureMeanC"], -80, 80, "climate.temperatureMeanC");
		InRange(metrics["precipitationMeanMm"], 1, 20000, "climate.precipitationMeanMm");
		Require(metrics["basinCount"] > 0 && metrics["lakeCount"] >= 0, "hydrology: basin/lake counts are invalid");
		Require(metrics["spatialLandSources"] > 0 && metrics["spatialRiverSources"] >= 0,
			"index: land/river source labels are invalid");
		Require(metrics["riverCells"] >= 0 && metrics["riverOutlets"] >= 0
			&& ((metrics["riverCells"] == 0) == (metrics["riverOutlets"] == 0)),
			"hydrology: river cells and outlets must both be empty or both present");
	}

	static void ValidateAnnualClimate(WorldFacts facts)
	{
		for (int cell = 0; cell < facts.HeightM.Length; cell++)
		{
			double rain = 0, temperature = 0, pressure = 0;
			for (int month = 0; month < 12; month++)
			{
				rain += facts.PrecipitationMonthlyMm[month][cell];
				temperature += facts.TemperatureMonthlyC[month][cell];
				pressure += facts.PressureMonthlyHpa[month][cell];
			}
			InRange(facts.PrecipitationMm[cell] - rain, -.02, .02, $"climate.precipitationSum[{cell}]");
			InRange(facts.TemperatureC[cell] - temperature / 12, -.001, .001, $"climate.temperatureMean[{cell}]");
			InRange(facts.PressureAnnualHpa[cell] - pressure / 12, -.001, .001, $"climate.pressureMean[{cell}]");
		}
	}

	static void InRange(double value, double minimum, double maximum, string name)
		=> Require(double.IsFinite(value) && value >= minimum && value <= maximum,
			$"{name}: actual={value:R}, allowed=[{minimum:R}, {maximum:R}]");

	static void CompareHashes(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
	{
		foreach (var stage in expected)
			Require(actual.TryGetValue(stage.Key, out string hash) && hash == stage.Value,
				$"determinism.{stage.Key}: first={stage.Value}, repeat={hash ?? "missing"}");
	}

	static string Fingerprint<T>(T value)
	{
		using var hash = SHA256.Create();
		using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
		// Hash logical snapshot data only; generated IDs, timestamps and timings are deliberately excluded.
		JsonSerializer.Serialize(stream, value, Json);
		stream.FlushFinalBlock();
		return Convert.ToHexString(hash.Hash);
	}

	static void Require(bool condition, string message)
	{
		if (!condition) throw new InvalidOperationException(message);
	}

	internal static void RejectsInvalidMetricsAndFingerprintDrift()
	{
		var metrics = new Dictionary<string, double>
		{
			["landFraction"] = .3, ["temperatureMeanC"] = 15,
			["precipitationMeanMm"] = 900, ["riverCells"] = 5, ["riverOutlets"] = 1,
			["basinCount"] = 2, ["lakeCount"] = 0, ["spatialLandSources"] = 1, ["spatialRiverSources"] = 5
		};
		ValidateMetrics(metrics);
		foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, 0, 1 })
		{
			metrics["landFraction"] = invalid;
			Program.Throws(() => ValidateMetrics(metrics), "Invalid land fraction accepted.");
		}
		string first = Fingerprint(new[] { 1f, 2f });
		Program.True(first == Fingerprint(new[] { 1f, 2f }), "Identical fields have different fingerprints.");
		Program.True(first != Fingerprint(new[] { 1f, 3f }), "A changed field kept its fingerprint.");
		Program.Throws(() => CompareHashes(new Dictionary<string, string> { ["terrain"] = first },
			new Dictionary<string, string> { ["terrain"] = "changed" }), "Drift did not fail verification.");
	}

	sealed record Sample(string Id, int Seed, ContinentLayout Layout);
	sealed class SampleResult
	{
		public string Id { get; init; }
		public WorldSpec Spec { get; init; }
		public List<RunResult> Runs { get; } = new();
	}
	sealed class RunResult
	{
		public string Stage { get; set; }
		public string Error { get; set; }
		public double BuildMilliseconds { get; set; }
		public Dictionary<string, double> StageMilliseconds { get; } = new();
		public Dictionary<string, double> Metrics { get; set; } = new();
		public Dictionary<string, string> Hashes { get; set; } = new();
	}
}
