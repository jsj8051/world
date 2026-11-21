using System.Linq;
using World.Data;
using World.Logic;

namespace World.Tests;

static class BiomeClassifierTests
{
	internal static void ClassifiesRepresentativeClimatesAndSeasonality()
	{
		static BiomeType Classify(float annual, float hot, float cold, float rain, float dry,
			float[] monthly, bool northern = true) => BiomeClassifier.ClassifyLand(annual, hot, cold, rain, dry,
				monthly.Select(amount => new[] { amount }).ToArray(), 0, northern);

		float[] uniform = Enumerable.Repeat(100f, 12).ToArray();
		Program.True(Classify(-20, -1, -35, 0, 0, uniform) == BiomeType.IceCap
			&& Classify(4, 9, -3, 240, 20, uniform) == BiomeType.Tundra, "按最热月区分冰原和苔原");
		Program.True(Classify(26, 31, 22, 200, 1, uniform) == BiomeType.HotDesert
			&& Classify(26, 31, 22, 600, 1, uniform) == BiomeType.HotSteppe
			&& Classify(26, 31, 22, 1600, 100, uniform) == BiomeType.TropicalRainforest,
			"采用仓库的干旱阈值和热带降水边界");
		float[] rain = { 150, 150, 150, 20, 20, 20, 20, 20, 20, 150, 150, 150 };
		Program.True(Classify(16, 26, 5, 1020, 20, rain) == BiomeType.MediterraneanHot
			&& Classify(16, 26, 5, 1020, 20, rain, northern: false) == BiomeType.HumidSubtropical,
			"同一降水序列按南北半球的当地季节判别");
	}
}
