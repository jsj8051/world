using System;
using Godot;
using NUnit.Framework;
using World.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 海陆结构护栏（阶段 1 大陆锚点 + 阶段 2 轮廓细化，决策 02）：
///   · 锚点层：确定性（同种子逐位同）、数量、蓝噪声最小间距下界（best-candidate 保证）、属性值域；
///   · 影响场：锚点中心影响 ≈ 权重、单调衰减、归属（加权 Voronoi）一致性；
///   · 海陆场：确定性、域扭曲开启改变场（但锚点归属用未扭方向）、有界。
/// 纪律（同 WorldGenFieldTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class ContinentLayoutTests
{
	static readonly int Seed = 42;
	static readonly int Count = 7;

	static ContinentLayout Make() => new(Seed, Count);

	[Test]
	public void SameSeed_AnchorsBitwiseIdentical()
	{
		var a = Make();
		var b = Make();
		Assert.That(b.Anchors.Length, Is.EqualTo(a.Anchors.Length));
		for (int i = 0; i < a.Anchors.Length; i++)
		{
			Assert.That(b.Anchors[i].Dir, Is.EqualTo(a.Anchors[i].Dir), $"锚点 {i} 中心须逐位同");
			Assert.That(b.Anchors[i].RadiusKm, Is.EqualTo(a.Anchors[i].RadiusKm));
			Assert.That(b.Anchors[i].AxisU, Is.EqualTo(a.Anchors[i].AxisU));
			Assert.That(b.Anchors[i].AxisV, Is.EqualTo(a.Anchors[i].AxisV));
			Assert.That(b.Anchors[i].StretchU, Is.EqualTo(a.Anchors[i].StretchU));
			Assert.That(b.Anchors[i].StretchV, Is.EqualTo(a.Anchors[i].StretchV));
			Assert.That(b.Anchors[i].CoastComplexity, Is.EqualTo(a.Anchors[i].CoastComplexity));
			Assert.That(b.Anchors[i].Weight, Is.EqualTo(a.Anchors[i].Weight));
		}
	}

	[Test]
	public void AnchorCount_MatchesRequest()
	{
		Assert.That(Make().Anchors.Length, Is.EqualTo(Count));
		Assert.That(new ContinentLayout(7, 1).Anchors.Length, Is.EqualTo(1));
	}

	[Test]
	public void BlueNoise_MinAngularGapHasLowerBound()
	{
		// best-candidate 的性质：第 c 个锚点的最小间距 ≥ 随机 32 候选的期望最大间距。
		// 球面上 N=7 点的"理想"最小间距量级 ~2/sqrt(N) rad ≈ 0.76；best-candidate 稳定拿到
		// 可观比例——断言 ≥ 0.4 rad（~2500 km，任何两块"大陆中心"不挤在一起）。
		var layout = Make();
		for (int i = 0; i < layout.Anchors.Length; i++)
			for (int j = i + 1; j < layout.Anchors.Length; j++)
			{
				float gap = MathF.Acos(Math.Clamp(layout.Anchors[i].Dir.Dot(layout.Anchors[j].Dir), -1f, 1f));
				Assert.That(gap, Is.GreaterThanOrEqualTo(0.4f),
					$"锚点 {i}/{j} 角距 {gap:F3} rad 过近——蓝噪声失效（中心挤簇）");
			}
	}

	[Test]
	public void AnchorProperties_InDeclaredRanges()
	{
		foreach (var a in Make().Anchors)
		{
			Assert.That(a.RadiusKm, Is.InRange(ContinentLayout.MinRadiusKm, ContinentLayout.MaxRadiusKm));
			Assert.That(a.StretchU, Is.InRange(1f, ContinentLayout.MaxStretch));
			Assert.That(a.StretchV, Is.InRange(1f, a.StretchU));                 // V ≤ U 纪律
			Assert.That(a.CoastComplexity, Is.InRange(ContinentLayout.MinCoastCx, ContinentLayout.MaxCoastCx));
			Assert.That(a.Weight, Is.InRange(0.8f, 1.25f));
			Assert.That(MathF.Abs(a.Dir.Length() - 1f), Is.LessThan(1e-4f));     // 单位方向
			Assert.That(MathF.Abs(a.AxisU.Dot(a.Dir)), Is.LessThan(1e-4f));      // 轴在切平面内
			Assert.That(MathF.Abs(a.AxisV.Dot(a.Dir)), Is.LessThan(1e-4f));
			Assert.That(MathF.Abs(a.AxisU.Dot(a.AxisV)), Is.LessThan(1e-4f));    // U⊥V
		}
	}

	[Test]
	public void Influence_AtAnchorCenter_AtLeastOwnWeight_DecaysOutward()
	{
		var layout = Make();
		var field = new ContinentInfluenceField(layout);
		foreach (var a in layout.Anchors)
		{
			float at = field.Sample(a.Dir);
			Assert.That(at, Is.GreaterThanOrEqualTo(a.Weight - 1e-4f),
				"锚点中心影响 ≥ 自身权重（自身贡献恒 = weight；max 场可被邻居尾巴加值）");
		}
		// 单调衰减：沿某方向外推，影响严格下降（取最远锚点避免近邻干扰——任意锚点沿自身长轴外推）
		var a0 = layout.Anchors[0];
		Vector3 outward = a0.Dir + a0.AxisU * 0.05f;   // 切向偏移一小步
		outward = outward.Normalized();
		float near = field.Sample(outward);
		Vector3 farther = a0.Dir + a0.AxisU * 0.15f;
		float far = field.Sample(farther.Normalized());
		Assert.That(far, Is.LessThanOrEqualTo(near), "远离锚点影响不得上升（局部单调）");
	}

	[Test]
	public void AnchorAt_SelfAnchor_ReturnsOwnIndex_AtLeastSomewhere()
	{
		var layout = Make();
		var field = new ContinentInfluenceField(layout);
		// 加权 Voronoi 覆盖：每个锚点在自己的中心处被自己（或更强近邻）归属——
		// best-candidate 间距下界 + 影响半径远大于间距 ⇒ 中心归属恒为自身。
		for (int i = 0; i < layout.Anchors.Length; i++)
			Assert.That(field.AnchorAt(layout.Anchors[i].Dir), Is.EqualTo(i),
				$"锚点 {i} 中心必须归属自己（影响顶点 = 权重最大处）");
	}

	[Test]
	public void LandSea_SameSeed_BitwiseIdentical_AndWarpChangesValue()
	{
		var layout = new ContinentLayout(Seed, Count);
		var a = new LandSeaField(layout, new LandSeaParams { Seed = Seed });
		var b = new LandSeaField(layout, new LandSeaParams { Seed = Seed });
		var dirs = SampleDirs(256);
		CollectionAssert.AreEqual(a.SampleAll(dirs), b.SampleAll(dirs), "同种子海陆场须逐位同");

		// 域扭曲关闭（幅度 0）须改变场（扭曲是形状的一部分）
		var noWarp = new LandSeaField(layout, new LandSeaParams { Seed = Seed, WarpAmplitudeKm = 0f });
		CollectionAssert.AreNotEqual(a.SampleAll(dirs), noWarp.SampleAll(dirs), "扭曲幅度 0 ≠ 默认（扭曲须实际生效）");
	}

	[Test]
	public void LandSea_BoundedByInfluencePlusNoiseAmplitudes()
	{
		var field = new LandSeaField(new ContinentLayout(Seed, Count), new LandSeaParams { Seed = Seed });
		var p = field.Params;
		float maxWeight = 0f;
		foreach (var a in field.Influence.Layout.Anchors) maxWeight = MathF.Max(maxWeight, a.Weight);
		float bound = maxWeight + p.LowAmplitude + p.MediumAmplitude * ContinentLayout.MaxCoastCx
			+ p.SmallAmplitude * ContinentLayout.MaxCoastCx + 1e-3f;
		foreach (float v in field.SampleAll(SampleDirs(256)))
			Assert.That(v, Is.InRange(-bound, bound), "海陆场必须被影响上界 + 噪声幅度包络");
	}

	static Vector3[] SampleDirs(int n)
	{
		var dirs = new Vector3[n];
		float golden = MathF.PI * (3f - MathF.Sqrt(5f));
		for (int i = 0; i < n; i++)
		{
			float y = 1f - 2f * (i + 0.5f) / n;
			float r = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
			float ang = golden * i;
			dirs[i] = new Vector3(r * MathF.Cos(ang), y, r * MathF.Sin(ang));
		}
		return dirs;
	}
}
