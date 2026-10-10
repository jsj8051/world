using System;
using Godot;
using NUnit.Framework;
using World.Logic;
using World.Utils;

using World.Data;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · LandformFeature 口径护栏（决策 06/07 职责契约——T6 迁移后）：
///   · ITerrainField 语义：影响度 ∈[0,1] 且锚点=1、沿距离单调衰减、目标高在标称值 ±12% Flatness 带内；
///   · Scale3 三元组已填充（身份层不再是仪式数据）；
///   · 组合口（高原→盆地 lerp）顺序语义：盆地后位覆盖高原。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class TerrainFeatureTests
{
	static PlateauFeature MakePlateau() => new()
	{
		Anchor = Vector3.Up,
		OrientationRad = 0f,
		Intensity = 1f,
		Scale = new Scale3(2400f, 2400f, 2050f),
		CapCenter = Vector3.Up,
		CapRadiusRad = 1.2f,
		SigmaKm = 1200f,
		TargetElevM = 2050f,
		TopNoise = new SphericalFbmNoise(7, 350f, 2),
	};

	static BasinFeature MakeBasin() => new()
	{
		Anchor = Vector3.Up,
		OrientationRad = 0f,
		Intensity = 1f,
		Scale = new Scale3(2000f, 2000f, 350f),
		CapCenter = Vector3.Up,
		CapRadiusRad = 1.1f,
		SigmaKm = 1000f,
		TargetElevM = 350f,
		TopNoise = new SphericalFbmNoise(7, 350f, 2),
	};

	[Test]
	public void Influence_OneAtAnchor_DecaysWithDistance()
	{
		var f = MakePlateau();
		var (infAt, _) = f.SampleAt(f.Anchor);
		Assert.That(infAt, Is.EqualTo(1f).Within(1e-4f), "锚点处影响度 = 1");
		float prev = infAt;
		var t1 = Vector3.Right;
		for (int step = 1; step <= 4; step++)
		{
			float ang = step * 0.2f;
			var q = (f.Anchor * MathF.Cos(ang) + t1 * MathF.Sin(ang)).Normalized();
			var (inf, _) = f.SampleAt(q);
			Assert.That(inf, Is.LessThanOrEqualTo(prev + 1e-5f), $"距锚 {ang:F1} rad 影响度须不增");
			prev = inf;
		}
	}

	[Test]
	public void Target_WithinFlatnessBand()
	{
		var f = MakePlateau();
		float golden = MathF.PI * (3f - MathF.Sqrt(5f));
		for (int i = 0; i < 64; i++)
		{
			float y = 1f - 2f * (i + 0.5f) / 64f;
			float r = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
			float a = golden * i;
			var dir = new Vector3(r * MathF.Cos(a), y, r * MathF.Sin(a));
			var (_, tgt) = f.SampleAt(dir);
			Assert.That(tgt, Is.InRange(f.TargetElevM * 0.88f, f.TargetElevM * 1.12f),
				"目标高必须落在台面标称值 ±12% Flatness 带内");
		}
	}

	[Test]
	public void Scale3_FilledOnRegistration()
	{
		// 身份层不再是仪式数据：Scale3 三元组须有真实量级（决策 06/07 职责契约）
		var plateau = MakePlateau();
		Assert.That(plateau.Scale.LongitudinalKm, Is.GreaterThan(0f));
		Assert.That(plateau.Scale.TransversalKm, Is.GreaterThan(0f));
		Assert.That(plateau.Scale.VerticalM, Is.EqualTo(2050f));
		var basin = MakeBasin();
		Assert.That(basin.Scale.VerticalM, Is.EqualTo(350f));
		Assert.That(basin.Scale.TransversalKm, Is.LessThan(plateau.Scale.TransversalKm),
			"盆地横向尺度 < 高原（尺度表：不同地貌不同尺度）");
	}
}
