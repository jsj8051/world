using System;
using System.Threading.Tasks;
using Godot;                 // 仅 Vector3 结构体（纯值类型）
using World.Data;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>
/// 陆面基底：常量基线 + 三档变化场 + 自由板偏置（<see cref="FreeboardOps"/>）。采样方向先被陆面域扭曲推开，
/// 近岸再按 |高度 − 海面| 叠一层细节扭曲（让海岸不再沿 Voronoi 边界走直线）。只对陆壳格计算。
/// 同时给出大尺度场在采样方向上的值——它是“有多深入大陆”的判据，盾状台地要用。
/// </summary>
public static class LandFieldOps
{
	/// <summary>结果。<c>HeightM</c>：陆面基底（仅陆壳格有值）；<c>Inland</c>：大尺度场在陆面采样方向上的值。</summary>
	public readonly struct Field
	{
		public readonly float[] HeightM;
		public readonly float[] Inland;

		internal Field(float[] heightM, float[] inland)
		{
			HeightM = heightM;
			Inland = inland;
		}
	}

	/// <summary>铺陆面基底（只写陆壳格；非陆壳格留 0）。</summary>
	/// <param name="continental">逐格是否陆壳。</param>
	/// <param name="baseM">陆面基线（米；已按布局档调整过）。</param>
	public static Field Build(Ball ball, int seed, TerrainSpec spec, bool[] continental, float baseM)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (continental == null) throw new ArgumentNullException(nameof(continental));

		int n = ball.CellDirs.Length;
		if (continental.Length != n)
			throw new ArgumentException($"陆壳掩码长度 {continental.Length} ≠ 格数 {n}", nameof(continental));

		var dirs = ball.CellDirs;
		float seaM = spec.SeaLevelM;
		double minWavelengthKm = 3.0 * SpatialScale.Of(ball).NeighborCenterKm;

		var large = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Large), spec.LargeWavelengthKm,
			NoiseSampling.ClampOctaves(spec.LargeOctaves, spec.LargeWavelengthKm, minWavelengthKm));
		var medium = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Medium), spec.MediumWavelengthKm,
			NoiseSampling.ClampOctaves(spec.MediumOctaves, spec.MediumWavelengthKm, minWavelengthKm));
		var detail = new SphericalFbmNoise(
			SeedDerivation.Derive(seed, SeedDerivation.Terrain_Detail), spec.RegionalWavelengthKm,
			NoiseSampling.ClampOctaves(spec.RegionalOctaves, spec.RegionalWavelengthKm, minWavelengthKm));

		// 域扭曲：陆面大尺度 + 近岸细节各一层；两层都用 2 个倍频，最细层仍 ≥ 3 倍邻格中心距（采样下限）。
		var landWarp = DomainWarpOps.Create(seed,
			SeedDerivation.Terrain_LandWarpX, SeedDerivation.Terrain_LandWarpY, SeedDerivation.Terrain_LandWarpZ,
			spec.LandWarpKm, spec.LandWarpWavelengthKm,
			NoiseSampling.ClampOctaves(2, spec.LandWarpWavelengthKm, minWavelengthKm));
		var coastWarp = DomainWarpOps.Create(seed,
			SeedDerivation.Terrain_CoastWarpX, SeedDerivation.Terrain_CoastWarpY, SeedDerivation.Terrain_CoastWarpZ,
			spec.CoastWarpKm, spec.CoastWarpWavelengthKm,
			NoiseSampling.ClampOctaves(2, spec.CoastWarpWavelengthKm, minWavelengthKm));

		var landHeight = new float[n];
		var inland = new float[n];
		float[] freeboard = FreeboardOps.Build(ball, spec, continental);
		Parallel.For(0, n, i =>
		{
			if (!continental[i]) return;
			Vector3 q = landWarp.Apply(dirs[i]);
			float h = SampleLand(spec, baseM, large, medium, detail, q) + freeboard[i];
			if (spec.CoastWarpKm > 0f)
			{
				float nearCoast = 1f - MathOps.Smoothstep(
					spec.CoastWarpBandLowM, spec.CoastWarpBandHighM, MathF.Abs(h - seaM));
				if (nearCoast > 0f)
				{
					q = q.Lerp(coastWarp.Apply(q), nearCoast).Normalized();
					h = SampleLand(spec, baseM, large, medium, detail, q) + freeboard[i];
				}
			}
			landHeight[i] = h;
			inland[i] = large.Sample(q);
		});

		return new Field(landHeight, inland);
	}

	/// <summary>陆面基底 = 基线 + 三档幅值 × 各自变化场；不夹保底——基线可落到海面之下，
	/// 海岸线才能由最终高度穿过海面自然形成。</summary>
	static float SampleLand(TerrainSpec spec, float baseM, SphericalFbmNoise large, SphericalFbmNoise medium,
		SphericalFbmNoise detail, Vector3 dir) =>
			baseM
			+ spec.LargeVariationM * large.Sample(dir)
			+ spec.MediumVariationM * medium.Sample(dir)
			+ spec.RegionalNoiseM * detail.Sample(dir);
}
