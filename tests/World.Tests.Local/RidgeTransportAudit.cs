using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using World.Data;
using World.H3Grid;
using World.Logic;
using World.Utils;

namespace World.Tests;

/// <summary>单板刚体共转实验。在球面板块坐标里量狭窄增厚带的扩散。</summary>
static class RidgeTransportAudit
{
	const double CenterLat = -58.5 * Math.PI / 180;
	const double CenterLng = 119 * Math.PI / 180;
	const double PeakM = 2500;
	const double WidthKm = 100;
	const double LengthKm = 700;
	const double SpeedKmPerMa = 35;
	const double FwhmFactor = 2.3548200450309493;
	const int MapWidth = 360;
	const int MapHeight = 520;
	const double CrossHalfKm = 450;
	const double AlongHalfKm = 650;

	readonly record struct Metrics(double Peak, double EquivalentWidth, double Centroid);

	public static void Run(string directory)
	{
		Directory.CreateDirectory(directory);
		var ball = new Ball(4, PlanetGeometry.ProductionRadius);
		var grid = EvolutionGrid.FromBall(ball, SphericalFbmNoise.WorldRadiusKm);
		var world = new WorldSpec();
		world.Plate.PlateCount = 1;
		var spec = world.Evolution;
		spec.StepYears = 100_000;
		double radius = grid.RadiusKm;
		double parallelRadius = radius * Math.Cos(CenterLat);
		double buoyancy = 1 - spec.ContinentalDensityKgM3 / spec.MantleDensityKgM3;
		var plates = new[] { new Plate
		{
			PlateId = 0, Density = (float)spec.ContinentalDensityKgM3,
			Omega = new Vector3(0, (float)(SpeedKmPerMa / parallelRadius / spec.LegacyOmegaScaleRadPerMa), 0),
		} };
		int n = grid.CellCount;
		var owners = new int[n];
		var types = new CrustType[n];
		Array.Fill(types, CrustType.Continental);
		var crust = new CrustField(types, new int[n], new[] { new CrustProvince(0, CrustType.Continental, 0, 0, n) });
		var heights = new float[n];
		var simulation = EvolutionSimulation.Create(world, grid, owners, crust, heights, plates, out var zero);
		double omega = zero.Motions[0].OmegaRadPerMa.Y;
		var relief = new EvolutionReliefGeometry(grid, spec);
		var rows = CrustRows.Build(n, 2, null, (cell, row) =>
		{
			double area = grid.AreasKm2[cell];
			var slot = CrustSlot.Create(area, spec.InitialContinentalThicknessKm, 0, null, 0);
			double excess = ExactHeight(Cross(ball.Geo.LngRad[cell], 0), Along(ball.Geo.LatRad[cell])) * area / (1000 * buoyancy);
			slot.Volume += excess;
			slot.CollisionVolume = excess;
			row.Set((int)CrustType.Continental, slot);
		});
		var material = new EvolutionCrust(grid.AreasKm2, 1, rows, 0, spec.CoverageRelativeTolerance);
		var projection = CrustProjectionBuilder.Build(grid, relief, spec, 0, 0, material, zero.Motions, plates,
			crust.Provinces, new double[n], world.Terrain.SeaLevelM);
		var frame = new EvolutionFrame(zero.Definition, zero.Clock, zero.Motions, material, zero.Mantle,
			projection, EvolutionBudget.CreateInitial(material.VolumeKm3, zero.Mantle.StoredVolumeKm3));
		frame.Validate();

		using var metrics = File.CreateText(Path.Combine(directory, "metrics.csv"));
		metrics.WriteLine("ma,peak_m,gaussian_equivalent_fwhm_km,volume_km3,centroid_km,exact_grid_peak_m,exact_grid_width_km");
		double initialVolume = 0;
		foreach (int ma in new[] { 0, 10, 20 })
		{
			frame = simulation.CalculateToStep(frame, frame.Clock.StepsFor(ma * 1_000_000L));
			double rotation = omega * ma;
			var height = frame.Projection.HeightM;
			Metrics measured = Measure(height, rotation);
			Metrics exact = Measure(null, rotation);
			double volume = 0;
			for (int cell = 0; cell < n; cell++)
				volume += frame.Material.Get(cell, 0, CrustType.Continental).CollisionVolume;
			if (ma == 0) initialVolume = volume;
			Program.Near(volume, initialVolume, initialVolume * 1e-8, "共转山带增厚体积守恒");
			Program.True(frame.Budget.TotalSubducted == 0 && frame.Budget.TotalNewOceanic == 0
				&& frame.Budget.TotalVolcanic == 0 && frame.Projection.PlateBoundaries.Edges.Length == 0,
				"单板共转应隔离全部构造过程");
			metrics.WriteLine(FormattableString.Invariant(
				$"{ma},{measured.Peak:R},{measured.EquivalentWidth:R},{volume:R},{measured.Centroid:R},{exact.Peak:R},{exact.EquivalentWidth:R}"));
			metrics.Flush();
			WriteRaster(Path.Combine(directory, $"ridge_{ma:00}.f32"), height, rotation);
			Console.WriteLine(FormattableString.Invariant(
				$"[RIDGE] Ma={ma} peak={measured.Peak:F1}m width={measured.EquivalentWidth:F1}km excess={volume:F3}km³ centroid={measured.Centroid:F2}km exact-peak={exact.Peak:F1}m exact-width={exact.EquivalentWidth:F1}km"));
		}

		double Cross(double lng, double rotation) => parallelRadius * Math.IEEERemainder(lng - CenterLng + rotation, 2 * Math.PI);
		double Along(double lat) => radius * (lat - CenterLat);

		Metrics Measure(IReadOnlyList<float> height, double rotation)
		{
			double peak = 0, weighted = 0, first = 0, second = 0;
			for (int cell = 0; cell < n; cell++)
			{
				double x = Cross(ball.Geo.LngRad[cell], rotation);
				double h = height == null ? ExactHeight(x, Along(ball.Geo.LatRad[cell])) : height[cell];
				peak = Math.Max(peak, h);
				double weight = h * grid.AreasKm2[cell];
				weighted += weight;
				first += weight * x;
				second += weight * x * x;
			}
			double centroid = first / weighted;
			return new Metrics(peak, FwhmFactor * Math.Sqrt(second / weighted - centroid * centroid), centroid);
		}

		void WriteRaster(string path, IReadOnlyList<float> height, double rotation)
		{
			using var writer = new BinaryWriter(File.Create(path));
			writer.Write(MapWidth);
			writer.Write(MapHeight);
			for (int y = 0; y < MapHeight; y++)
			{
				double lat = CenterLat + (1 - 2 * (y + 0.5) / MapHeight) * AlongHalfKm / radius;
				for (int x = 0; x < MapWidth; x++)
				{
					double lng = CenterLng - rotation + (2 * (x + 0.5) / MapWidth - 1) * CrossHalfKm / parallelRadius;
					int cell = BallGeoIndex.CellAt(ball, CoordUtil.LatLngToSphere(new World.Utils.H3.LatLng(lat, lng), 1f));
					writer.Write(height[cell]);
				}
			}
		}
	}

	static double ExactHeight(double crossKm, double alongKm)
	{
		double x = crossKm / WidthKm, y = alongKm / LengthKm;
		return PeakM * Math.Exp(-4 * Math.Log(2) * (x * x + y * y));
	}
}
