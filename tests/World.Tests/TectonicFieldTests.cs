using System;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NoiseWorld.WorldGen;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 构造场护栏（决策 06「Field 层」）：
///   · 主轴：单位/零向量口径（退化陆块合法）；
///   · 强度场：∈[0,1]；MOUNTAIN 区域种子处强度 > 远洋处（"哪里容易发生什么"的判读口）；
///   · 锚点池：陆块归属过滤正确；确定性（同种子同构造）。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class TectonicFieldTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));
	static Ball Ball => SharedBall.Value;

	static (TectonicField t, GeologicalRegions g) Make(int seed = 42)
	{
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, new LandSeaParams { Seed = seed });
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, 0.29f);
		var g = new GeologicalRegions(seed);
		g.Generate(Ball, proj, 8_000_000f);
		var t = new TectonicField(seed, Ball, g);
		return (t, g);
	}

	[Test]
	public void Intensity_InUnitRange_AtMountainAboveOcean()
	{
		var (t, g) = Make();
		foreach (float v in SampleGrid(t))
			Assert.That(v, Is.InRange(0f, 1f), "构造强度必须 ∈[0,1]");

		// MOUNTAIN 区域种子处强度应高于远洋点（场方向性的可观察量）
		float mountainMax = 0f;
		foreach (var r in g.Regions)
			if (r.Type == RegionType.Mountain)
				mountainMax = MathF.Max(mountainMax, t.IntensityAt(r.Seed));
		Assert.That(mountainMax, Is.GreaterThan(0.4f), "造山带中心须读出高强度");
	}

	[Test]
	public void Axes_UnitOrZero()
	{
		var (t, _) = Make();
		for (int lm = 0; lm < 40; lm++)
		{
			var a = t.PrincipalAxis(lm);
			Assert.That(a.LengthSquared(), Is.InRange(0f, 1f + 1e-3f));
			if (a.LengthSquared() > 1e-6f)
				Assert.That(MathF.Abs(a.Length() - 1f), Is.LessThan(1e-3f), $"陆块 {lm} 主轴须单位化");
		}
	}

	[Test]
	public void AnchorPool_OnlyLandOfOwnLandmass()
	{
		var (t, g) = Make();
		int lm = g.Regions[0].Landmass;
		var pool = t.AnchorPool(lm, mountainOnly: false);
		Assert.That(pool.Count, Is.GreaterThan(0));
		foreach (int i in pool)
		{
			int r = g.RegionOfCell[i];
			Assert.That(r, Is.GreaterThanOrEqualTo(0), "锚点池不得含海格");
			Assert.That(g.Regions[r].Landmass, Is.EqualTo(lm), "锚点池不得跨陆块");
		}
	}

	[Test]
	public void SameSeed_SameField()
	{
		var a = Make(42);
		var b = Make(42);
		var dirs = Ball.CellDirs;
		for (int i = 0; i < dirs.Length; i += 37)
		{
			Assert.That(b.t.IntensityAt(dirs[i]), Is.EqualTo(a.t.IntensityAt(dirs[i])),
				$"格 {i}：强度场须逐位同（确定性红线）");
			Assert.That(b.t.PrincipalAxis(0), Is.EqualTo(a.t.PrincipalAxis(0)));
		}
	}

	static System.Collections.Generic.IEnumerable<float> SampleGrid(TectonicField t)
	{
		var dirs = Ball.CellDirs;
		for (int i = 0; i < dirs.Length; i += 11) yield return t.IntensityAt(dirs[i]);
	}
}
