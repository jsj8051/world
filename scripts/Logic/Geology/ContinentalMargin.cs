using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using World.Data;
using World.H3Grid;
using World.Utils;

namespace World.Logic;

/// <summary>第 0 轮陆面与大陆架共享的边缘距离及海岸过渡剖面。</summary>
internal sealed class ContinentalMargin(float[] distanceKm, float[] widthScale, float cellSpacingKm,
	float joinHeightM)
{
	const float LandTransitionKm = 520f;
	const float BayWavelengthKm = 1800f;
	const float CoveWavelengthKm = 540f;

	internal float JoinHeightM { get; } = joinHeightM;

	internal static ContinentalMargin Build(Ball ball, int seed, TerrainSpec spec, bool[] continental)
	{
		float spacing = (float)SpatialScale.Of(ball).NeighborCenterKm;
		float cap = Math.Max(spec.FreeboardSpanKm, Math.Max(2 * LandTransitionKm, 3 * spacing));
		cap = Math.Max(cap, 2.5f * Math.Max(spec.ShelfMinWidthKm, spec.ShelfMaxWidthKm) + 1.5f * spec.SlopeSpanKm);
		var seeds = new List<DistanceFieldOps.Seed>();
		for (int cell = 0; cell < continental.Length; cell++)
		{
			double distance = double.PositiveInfinity;
			foreach (int neighbor in ball.CellNeighbors[cell])
				if (continental[cell] != continental[neighbor])
					distance = Math.Min(distance, .5 * SpatialScale.DistanceKm(ball.CellDirs[cell], ball.CellDirs[neighbor]));
			if (double.IsFinite(distance)) seeds.Add(new DistanceFieldOps.Seed(cell, cell, distance));
		}
		if (seeds.Count == 0) return null;

		var distanceField = DistanceFieldOps.BuildSeeds(ball, seeds, cap,
			(from, to) => continental[from] == continental[to]);
		var noise = new SphericalFbmNoise(SeedDerivation.Derive(seed, SeedDerivation.Terrain_ContinentalMargin),
			Math.Max(BayWavelengthKm, 3 * spacing), 1);
		var details = new SphericalFbmNoise(SeedDerivation.Derive(seed, SeedDerivation.Terrain_ContinentalMarginDetail),
			Math.Max(CoveWavelengthKm, 3 * spacing), 1);
		var width = new float[continental.Length];
		Parallel.For(0, width.Length, cell =>
		{
			var direction = ball.CellDirs[cell];
			float shape = .7f * noise.Sample(direction) + .3f * details.Sample(direction);
			width[cell] = .4f + 1.4f * MathOps.Smoothstep(-.42f, .42f, shape);
		});
		float joint = spec.ShelfShallowestM + .45f * (spec.ShelfDeepestM - spec.ShelfShallowestM);
		return new ContinentalMargin(distanceField.DistKm, width, spacing, joint);
	}

	internal float FreeboardAt(TerrainSpec spec, int cell)
		=> spec.FreeboardSpanKm > 0 && spec.FreeboardLiftM > 0
			? spec.FreeboardLiftM * MathOps.Smoothstep(0, spec.FreeboardSpanKm, distanceKm[cell])
			: 0;

	internal float JoinLand(int cell, float inlandHeightM)
	{
		float width = Math.Max(2 * cellSpacingKm, LandTransitionKm * widthScale[cell]);
		float weight = MathOps.Smoothstep(0, width, distanceKm[cell]);
		return JoinHeightM + (inlandHeightM - JoinHeightM) * weight;
	}

	internal float ShelfHeight(int cell, float widthKm, TerrainSpec spec, float deepM)
	{
		float distance = distanceKm[cell];
		float widening = spec.MarginWidenKm > 0 ? 1f - MathOps.Smoothstep(0, spec.MarginWidenKm, distance) : 0;
		float width = Math.Max(1, widthKm * widthScale[cell] * (.85f + .3f * widening));
		if (distance < width)
		{
			float weight = MathOps.Smoothstep(0, width, distance);
			return JoinHeightM + (spec.ShelfDeepestM - JoinHeightM) * weight;
		}
		float slopeSpan = Math.Max(1, spec.SlopeSpanKm);
		float beyondShelf = distance - width;
		float toe = Math.Max(deepM, spec.SlopeBottomM);
		if (beyondShelf < slopeSpan)
			return spec.ShelfDeepestM + (toe - spec.ShelfDeepestM) * MathOps.Smoothstep(0, slopeSpan, beyondShelf);
		return toe + (deepM - toe) * MathOps.Smoothstep(0, .5f * slopeSpan, beyondShelf - slopeSpan);
	}
}
