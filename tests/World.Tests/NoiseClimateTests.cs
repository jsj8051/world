using System;
using System.Linq;
using NUnit.Framework;
using World.NewHexWorld;
using World.NoiseWorld;

namespace World.Tests;

/// <summary>
/// 气候层护栏（自老树 ClimateGenerator/WindField 迁移，"是什么就是什么"第二步）：
///   · 确定性：同种子两次生成**逐位同**；
///   · 温度：赤道带暖于极地带（纬度基准）、高海拔冷于低海拔（直减 −6°C/km）、值域有界；
///   · 降水：ITCZ（赤道带）湿于副热带（26° 干旱带）、洋面湿润顶格、值域非负；
///   · 生物群系：格 i 的 biome 必须等于 BiomeFor(T[i], P[i], isLand) 重算结果（单一事实源）。
/// 纪律：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class NoiseClimateTests
{
	static readonly Lazy<Ball> Ball2 = new(() => new Ball(2, 1f));   // 5882 格

	static (NoiseClimate c, NoiseTerrain t, NoisePlates plates) MakeClimate(int seed, int numPlates = 8)
	{
		var ball = Ball2.Value;
		var plates = new NoisePlates();
		plates.Generate(ball, numPlates, seed, 0.29f);
		var t = new NoiseTerrain();
		t.Params.Seed = seed;
		t.Generate(ball.CellDirs, plates, ball.CellNeighbors);
		var c = new NoiseClimate();
		c.Generate(ball, t, seed);
		return (c, t, plates);
	}

	[Test]
	public void SameSeed_GeneratesBitwiseIdentical()
	{
		var a = MakeClimate(42);
		var b = MakeClimate(42);
		CollectionAssert.AreEqual(a.c.TemperatureC, b.c.TemperatureC, "温度同种子须逐位同");
		CollectionAssert.AreEqual(a.c.PrecipMmYear, b.c.PrecipMmYear, "降水同种子须逐位同");
		CollectionAssert.AreEqual(a.c.Biome, b.c.Biome, "生物群系同种子须逐位同");
	}

	[Test]
	public void Temperature_PolesColderThanEquator()
	{
		var ball = Ball2.Value;
		var (c, _, _) = MakeClimate(42);
		var dirs = ball.CellDirs;
		float tropicalAvg = 0; int nT = 0;
		float polarAvg = 0; int nP = 0;
		for (int i = 0; i < dirs.Length; i++)
		{
			float latDeg = MathF.Abs(MathF.Asin(Math.Clamp(dirs[i].Y, -1f, 1f)) * 180f / MathF.PI);
			if (latDeg < 10f) { tropicalAvg += c.TemperatureC[i]; nT++; }
			if (latDeg > 80f) { polarAvg += c.TemperatureC[i]; nP++; }
		}
		tropicalAvg /= Math.Max(nT, 1);
		polarAvg /= Math.Max(nP, 1);
		Assert.That(tropicalAvg, Is.GreaterThan(15f), "赤道带均温应 >15°C（纬度基准 ≈+30）");
		Assert.That(tropicalAvg - polarAvg, Is.GreaterThan(25f),
			"赤道-极地温差应 >25°C（纬度基准曲线 52cos^1.1−22 的标定）");
	}

	[Test]
	public void Temperature_LapseRate_HighElevationColder()
	{
		// 直减率：同纬度带（|lat|<30，排除纬度混淆）内，高海拔格应显著冷于低海拔格
		var ball = Ball2.Value;
		var (c, t, _) = MakeClimate(42);
		var dirs = ball.CellDirs;
		float highSum = 0; int highN = 0;
		float lowSum = 0; int lowN = 0;
		for (int i = 0; i < dirs.Length; i++)
		{
			float latDeg = MathF.Abs(MathF.Asin(Math.Clamp(dirs[i].Y, -1f, 1f)) * 180f / MathF.PI);
			if (latDeg > 30f) continue;
			float e = t.ElevationM[i];
			if (e > 2000f) { highSum += c.TemperatureC[i]; highN++; }
			else if (e < 200f) { lowSum += c.TemperatureC[i]; lowN++; }
		}
		Assert.That(highN, Is.GreaterThan(0), "测试有效性：热带带内应有高海拔格");
		Assert.That(lowN, Is.GreaterThan(0), "测试有效性：热带带内应有低海拔格");
		Assert.That(highSum / highN, Is.LessThan(lowSum / lowN - 5f),
			"高海拔均温应比低海拔低 >5°C（直减 −6°C/km × 数 km）");
	}

	[Test]
	public void Temperature_ValueBounds()
	{
		var (c, _, _) = MakeClimate(42);
		Assert.That(c.TemperatureC.All(x => x is >= -75f and <= 45f), Is.True,
			"年均温应在 [−75, +45]（纬度基准 + 噪声 ±7 + 直减下限，公式值域）");
	}

	[Test]
	public void Precipitation_ItczWetterThanSubtropical()
	{
		// ITCZ：赤道带（|lat|<10）洋面均降水应显著高于副热带干旱带（20~35°）洋面
		var ball = Ball2.Value;
		var (c, t, _) = MakeClimate(42);
		var dirs = ball.CellDirs;
		float itczSum = 0; int nI = 0;
		float subSum = 0; int nS = 0;
		for (int i = 0; i < dirs.Length; i++)
		{
			float e = c.PrecipMmYear[i];
			if (t.ElevationM[i] > 0f) continue;                   // 洋格（免雨影/抬升混淆）
			float latDeg = MathF.Abs(MathF.Asin(Math.Clamp(dirs[i].Y, -1f, 1f)) * 180f / MathF.PI);
			if (latDeg < 10f) { itczSum += e; nI++; }
			else if (latDeg is > 20f and < 35f) { subSum += e; nS++; }
		}
		itczSum /= Math.Max(nI, 1);
		subSum /= Math.Max(nS, 1);
		Assert.That(itczSum, Is.GreaterThan(subSum * 1.5f),
			"ITCZ 带降水应 >1.5× 副热带干旱带（σ12° 高斯 × 0.85 压制的经典结构）");
	}

	[Test]
	public void Precipitation_Nonnegative()
	{
		var (c, _, _) = MakeClimate(42);
		Assert.That(c.PrecipMmYear.All(x => x >= 0f), Is.True, "降水非负");
	}

	[Test]
	public void Biome_MatchesClassifier_SingleSource()
	{
		// 生物群系 = BiomeFor(T, P, isLand) 的纯函数结果（单一事实源，无隐状态）
		var ball = Ball2.Value;
		var (c, t, plates) = MakeClimate(42);
		for (int i = 0; i < t.ElevationM.Length; i++)
		{
			bool isLand = t.ElevationM[i] > 0f;
			Assert.That(isLand, Is.EqualTo(plates.LandPlate[plates.PlateOfCell[i]]),
				$"格 {i}：海拔符号必须等于板块身份（「是什么就是什么」不变量）");
			byte expected = NoiseClimate.BiomeFor(c.TemperatureC[i], c.PrecipMmYear[i], isLand);
			Assert.That(c.Biome[i], Is.EqualTo(expected), $"格 {i}：biome 必须匹配分类器重算");
		}
	}

	[Test]
	public void BiomeTable_Complete()
	{
		// 生物群系表完备性：每个 byte id 在 [0, Names.Length) 内且表长一致（地图着色不越界）
		Assert.That(NoiseClimate.BiomeNames.Length, Is.EqualTo(NoiseClimate.BiomeColors.Length), "名称/颜色表等长");
		Assert.That((int)NoiseClimate.BiomeTropicalRainforest, Is.EqualTo(NoiseClimate.BiomeNames.Length - 1),
			"最高 id = 表尾（热带雨林）");
	}
}
