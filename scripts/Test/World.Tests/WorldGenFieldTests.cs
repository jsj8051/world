using System;
using Godot;
using NUnit.Framework;
using World.Logic;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 连续场层护栏（决策原典 docs/newdecision/world.md）：
///   · 确定性红线：同种子同方向**逐位同**；采样无状态 ⇒ 次序无关（交错采样不改结果）；
///   · 组合子不变量：ridged 值域 [0,1]、正瓣值域 [0,+∞)、域扭曲 amp=0 逐位等价直采、
///     加权和 = 手工线性对照；
///   · 米域口径：海拔组合场在默认参数下值域有界（振幅之和包络）。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class WorldGenFieldTests
{
	static readonly Vector3[] Dirs = MakeDirs(512);   // 固定方向集（斐波那契球带 + 两极）

	static Vector3[] MakeDirs(int n)
	{
		var dirs = new Vector3[n];
		float golden = MathF.PI * (3f - MathF.Sqrt(5f));
		for (int i = 0; i < n; i++)
		{
			float y = 1f - 2f * (i + 0.5f) / n;
			float r = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
			float a = golden * i;
			dirs[i] = new Vector3(r * MathF.Cos(a), y, r * MathF.Sin(a));
		}
		return dirs;
	}

	// ★测试夹具（决策 08 §9 第三层）：下面这些测试测的是 **SphericalField 族**的
	//   确定性与无状态性，不是 `ElevationFieldStack` 这个组合层。
	//   它只被借来"造一个可采样的场"，该职责由测试层夹具承担。
	static SphericalField MakeField(int seed) => TestElevationFieldBuilder.Build(seed);

	[Test]
	public void SameSeed_SamplesBitwiseIdentical()
	{
		var a = MakeField(42);
		var b = MakeField(42);
		CollectionAssert.AreEqual(a.SampleAll(Dirs), b.SampleAll(Dirs),
			"同种子同方向须逐位同（噪声线确定性红线）");
	}

	[Test]
	public void DifferentSeed_GeneratesDifferentField()
	{
		var a = MakeField(42);
		var b = MakeField(43);
		CollectionAssert.AreNotEqual(a.SampleAll(Dirs), b.SampleAll(Dirs),
			"不同种子须给出不同的场（防止种子未接线）");
	}

	[Test]
	public void SamplingIsStateless_OrderIndependent()
	{
		var f = MakeField(7);
		var baseline = f.SampleAll(Dirs);
		// 交错乱采样不改结果（Sample 无状态、不耗 rng ⇒ 采样次序无关）
		var junk = new Vector3(0.3f, 0.5f, 0.81f);
		for (int i = 0; i < 10; i++) f.Sample(new Vector3(junk.X * i + 0.1f, junk.Y, junk.Z));
		CollectionAssert.AreEqual(baseline, f.SampleAll(Dirs),
			"Sample 必须无状态：任意交错采样不得改变后续结果");
	}

	[Test]
	public void WarpedField_ZeroAmplitude_EqualsInnerDirectSample()
	{
		var inner = new FbmField(11, 4000f, 4);
		var warped = new WarpedField(inner,
			new FbmField(1, 4000f, 2), new FbmField(2, 4000f, 2), new FbmField(3, 4000f, 2), 0f);
		var innerVals = inner.SampleAll(Dirs);
		var warpedVals = warped.SampleAll(Dirs);
		CollectionAssert.AreEqual(innerVals, warpedVals, "扭曲幅度 0 = 域扭曲组合子必须逐位退化为直采");
	}

	[Test]
	public void WarpedField_WithAmplitude_DiffersFromDirect()
	{
		var inner = new FbmField(11, 4000f, 4);
		var warped = new WarpedField(inner,
			new FbmField(1, 4000f, 2), new FbmField(2, 4000f, 2), new FbmField(3, 4000f, 2), 900f);
		CollectionAssert.AreNotEqual(inner.SampleAll(Dirs), warped.SampleAll(Dirs),
			"扭曲幅度非 0 须实际改变采样坐标");
	}

	[Test]
	public void RidgedField_RangeInUnitInterval()
	{
		var ridge = new RidgedField(new FbmField(5, 350f, 4), 2f);
		foreach (float v in ridge.SampleAll(Dirs))
			Assert.That(v, Is.InRange(0f, 1f), "ridged 整形 (1-|n|)^k 值域必须落在 [0,1]");
	}

	[Test]
	public void PositiveField_ClampsNegativesToZero()
	{
		var pos = new PositiveField(new FbmField(6, 3000f, 2));
		var vals = pos.SampleAll(Dirs);
		Assert.That(vals, Has.Some.GreaterThan(0f), "测试种子下须出现正瓣（防止恒 0 的假通过）");
		foreach (float v in vals)
			Assert.That(v, Is.GreaterThanOrEqualTo(0f), "正瓣整形不得输出负值");
	}

	[Test]
	public void WeightedSum_MatchesManualLinearCombination()
	{
		var f1 = new FbmField(21, 5000f, 3);
		var f2 = new RidgedField(new FbmField(22, 700f, 3), 2f);
		var sum = new WeightedSumField((f1, 3000f), (f2, -1500f));
		var vals = sum.SampleAll(Dirs);
		for (int i = 0; i < Dirs.Length; i++)
		{
			float expect = 3000f * f1.Sample(Dirs[i]) - 1500f * f2.Sample(Dirs[i]);
			Assert.That(vals[i], Is.EqualTo(expect).Within(1e-3f),
				$"方向 {i}：加权和必须等于手工线性对照（负权重 = 减法）");
		}
	}

	/// <summary>米域振幅包络：**加权组合的输出必须被各项振幅之和包络**（无未加权来源）。
	/// <para>★原测试挂在 `ElevationFieldStack`（已清退的框架期组合层）上。
	/// 该不变量属于**组合子语义**而非那个组合层的私有实现 ⇒改挂`WeightedSumField`
	/// 多项组合，测的是同一个不变量，且不再依赖被清退的结构。</para></summary>
	[Test]
	public void WeightedCombination_RangeBoundedByAmplitudeSum()
	{
		const float a1 = 3000f, a2 = 2500f, a3 = 1200f, a4 = 1500f;
		var combo = new WeightedSumField(
			(new FbmField(31, 9000f, 4), a1),
			(new RidgedField(new FbmField(32, 350f, 4), 2f), a2),
			(new FbmField(33, 2500f, 2), a3),
			(new PositiveField(new FbmField(34, 3000f, 2)), -a4));
		float bound = a1 + a2 + a3 + a4 + 1f;
		foreach (float v in combo.SampleAll(Dirs))
			Assert.That(MathF.Abs(v), Is.LessThan(bound),
				"米域海拔必须被各项振幅之和包络（无未加权来源）");
	}
}
