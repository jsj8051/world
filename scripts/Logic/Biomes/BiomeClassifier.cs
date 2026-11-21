using System;
using System.Threading;
using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>按仓库现有的柯本类型词表，将同轮温度与降水归类到未被水覆盖的陆格。</summary>
internal static class BiomeClassifier
{
	internal static BiomeType?[] Classify(Ball ball, TerrainPipeline terrain, ClimatePipeline climate,
		LakeState lakes, CancellationToken cancellation = default)
	{
		var result = new BiomeType?[ball.CellIds.Length];
		for (int cell = 0; cell < result.Length; cell++)
		{
			if ((cell & 1023) == 0) cancellation.ThrowIfCancellationRequested();
			if (!terrain.FinalLand[cell]) continue;
			int basin = lakes.DepressionIdOf[cell];
			int lake = basin < 0 ? -1 : lakes.LakeOfBasin[basin];
			if (lake >= 0 && terrain.HeightM[cell] < lakes.SurfaceElevationM[lake]) continue;
			result[cell] = ClassifyLand(climate.Temperature.AnnualC[cell], climate.Temperature.HotMonthC[cell],
				climate.Temperature.ColdMonthC[cell], climate.Precipitation.AnnualMm[cell],
				climate.DryMonthMm[cell], climate.Precipitation.MonthlyMm, cell, ball.CellDirs[cell].Y >= 0);
		}
		return result;
	}

	internal static BiomeType ClassifyLand(float annualC, float hotC, float coldC, float annualMm,
		float dryMonthMm, float[][] monthlyMm, int cell, bool northern)
	{
		if (hotC < 0) return BiomeType.IceCap;
		if (hotC < 10) return BiomeType.Tundra;

		float desertLimit = Math.Max(0, 10 * (annualC + 14));
		if (annualMm < desertLimit)
			return annualC >= 18 ? BiomeType.HotDesert : BiomeType.ColdDesertKoppen;
		if (annualMm < 2 * desertLimit)
			return annualC >= 18 ? BiomeType.HotSteppe : BiomeType.ColdSteppe;
		if (coldC >= 18)
		{
			if (dryMonthMm >= 60) return BiomeType.TropicalRainforest;
			return dryMonthMm >= 100 - annualMm / 25 ? BiomeType.TropicalMonsoon : BiomeType.TropicalSavanna;
		}

		float drySummer = float.PositiveInfinity, dryWinter = float.PositiveInfinity;
		float wetSummer = 0, wetWinter = 0;
		for (int month = 0; month < 12; month++)
		{
			float rain = monthlyMm[month][cell];
			if ((month >= 3 && month <= 8) == northern)
			{
				drySummer = Math.Min(drySummer, rain);
				wetSummer = Math.Max(wetSummer, rain);
			}
			else
			{
				dryWinter = Math.Min(dryWinter, rain);
				wetWinter = Math.Max(wetWinter, rain);
			}
		}

		bool hotSummer = hotC >= 22;
		bool winterDry = dryWinter < wetSummer / 10;
		if (coldC <= -3)
		{
			if (!hotSummer) return coldC <= -15 ? BiomeType.Subarctic : BiomeType.ContinentalWarm;
			return winterDry ? BiomeType.ContinentalDry : BiomeType.ContinentalHot;
		}
		if (drySummer < 40 && drySummer < wetWinter / 3)
			return hotSummer ? BiomeType.MediterraneanHot : BiomeType.MediterraneanCool;
		if (hotSummer)
			return winterDry ? BiomeType.MonsoonSubtropical : BiomeType.HumidSubtropical;
		return BiomeType.Oceanic;
	}
}
