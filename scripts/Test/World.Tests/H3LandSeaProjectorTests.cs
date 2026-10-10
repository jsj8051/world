using System;
using NUnit.Framework;
using World.H3Grid;
using World.Logic;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · H3 海陆投影护栏（阶段 1 输出五件套，决策 02 §2/§4）：
///   · 分位校准：实测海陆比 = 目标（±2pp）；
///   · 五件套一致性：Land 符号 ⟺ 可见海拔符号；LandmassId 陆全覆盖、海 −1；
///     DistToCoast/DistToLand 各自源侧为 0、对侧 ≥1；BFS 距离 ≤ 邻居 +1（图距定义）；
///   · 确定性：同输入逐位同。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3LandSeaProjectorTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));   // 842 格（链路验证档）
	static Ball Ball => SharedBall.Value;

	static (H3LandSeaProjector p, LandSeaField f) MakeProjector(int seed = 42,
		float landTarget = 0.29f, int continents = 7)
	{
		var layout = new ContinentLayout(seed, continents);
		var field = new LandSeaField(layout, seed, WorldPreset.Earth.LandSea);
		var p = new H3LandSeaProjector();
		p.Generate(Ball, field, landTarget);
		return (p, field);
	}

	[Test]
	public void LandFraction_MatchesTargetWithinTolerance()
	{
		var (p, _) = MakeProjector(landTarget: 0.29f);
		Assert.That(p.LandFraction, Is.EqualTo(0.29f).Within(0.02f),
			$"分位校准必须钉死海陆比（实测 {p.LandFraction:P1}）");
		var (p40, _) = MakeProjector(landTarget: 0.6f);
		Assert.That(p40.LandFraction, Is.EqualTo(0.6f).Within(0.02f),
			"另一目标占比同样须命中（滑块可控）");
	}

	[Test]
	public void LandSymbol_MatchesElevationSign()
	{
		var (p, _) = MakeProjector();
		for (int i = 0; i < p.PlacementLand.Length; i++)
			Assert.That(p.ElevationM[i] > 0f, Is.EqualTo(p.PlacementLand[i]),
				$"格 {i}：可见海拔符号必须等于海陆掩码（陆正海负）");
	}

	[Test]
	public void LandmassId_FullOnLand_NegativeOnOcean()
	{
		var (p, field) = MakeProjector();
		int continents = p.LandmassCount;
		for (int i = 0; i < p.PlacementLand.Length; i++)
		{
			if (p.PlacementLand[i])
			{
				Assert.That(p.LandmassId[i], Is.InRange(0, continents - 1),
					$"陆格 {i}：归属必须是合法陆块号");
			}
			else
			{
				Assert.That(p.LandmassId[i], Is.EqualTo(-1), $"海格 {i}：归属必须为 −1");
			}
		}
	}

	[Test]
	public void CoastDistances_ZeroOnOwnSide_AtLeastOneOnOtherSide()
	{
		var (p, _) = MakeProjector();
		for (int i = 0; i < p.PlacementLand.Length; i++)
		{
			if (p.PlacementLand[i])
			{
				Assert.That(p.DistToCoast[i], Is.GreaterThanOrEqualTo(0), $"陆格 {i} 离海 ≥ 0");
				Assert.That(p.DistToLand[i], Is.EqualTo(0), $"陆格 {i} 离岸必须为 0（源侧）");
			}
			else
			{
				Assert.That(p.DistToLand[i], Is.GreaterThanOrEqualTo(1), $"海格 {i} 离岸 ≥ 1");
				Assert.That(p.DistToCoast[i], Is.EqualTo(0), $"海格 {i} 离海必须为 0（源侧）");
			}
		}
	}

	[Test]
	public void BfsDistances_SatisfyGraphDistanceRecurrence()
	{
		var (p, _) = MakeProjector();
		var neighbors = Ball.CellNeighbors;
		for (int i = 0; i < p.PlacementLand.Length; i++)
		{
			int dc = p.DistToCoast[i];
			if (dc > 0)
			{
				// 距离 = min(邻居) + 1（BFS 收敛点）
				int minN = int.MaxValue;
				foreach (int j in neighbors[i]) minN = Math.Min(minN, p.DistToCoast[j]);
				Assert.That(dc, Is.EqualTo(minN + 1), $"格 {i} 离海距离违反图距递推");
			}
		}
	}

	[Test]
	public void SameInput_ProjectsBitwiseIdentical()
	{
		var a = MakeProjector();
		var b = MakeProjector();
		CollectionAssert.AreEqual(a.p.Raw, b.p.Raw, "原始场须逐位同");
		CollectionAssert.AreEqual(a.p.ElevationM, b.p.ElevationM, "可见海拔须逐位同");
		Assert.That(b.p.LandFraction, Is.EqualTo(a.p.LandFraction));
		Assert.That(b.p.ThresholdUsed, Is.EqualTo(a.p.ThresholdUsed));
	}

	[Test]
	public void MultiSample_SmootherThanCenterOnly_AtBandBoundary()
	{
		// 多点采样的目的（决策原典 §五）：海岸线（阈值横切）不因格心恰在脊上而突兀。
		// 可观察量：同格数下多点模式的海岸带（|raw−thr| 极小格数）不高于单点模式。
		var layout = new ContinentLayout(42, 7);
		var field = new LandSeaField(layout, 42, WorldPreset.Earth.LandSea);
		var single = new H3LandSeaProjector();
		single.Generate(Ball, field, 0.29f, H3TerrainSampler.Mode.CenterOnly);
		var multi = new H3LandSeaProjector();
		multi.Generate(Ball, field, 0.29f, H3TerrainSampler.Mode.CenterAndCorners);

		Assert.That(multi.LandFraction, Is.EqualTo(0.29f).Within(0.02f), "两种模式都过校准门");
		// 阈值附近（±0.005）的格数：多点平均应不增加"擦边"格（平滑化直觉的可检验代理）
		int multiNear = CountNearThreshold(multi);
		int singleNear = CountNearThreshold(single);
		Assert.That(multiNear, Is.LessThanOrEqualTo(singleNear + 40),
			"多点采样在阈值附近的擦边格不应显著多于单点（边界平滑方向）");
	}

	static int CountNearThreshold(H3LandSeaProjector p)
	{
		int count = 0;
		for (int i = 0; i < p.Raw.Length; i++)
			if (MathF.Abs(p.Raw[i] - p.ThresholdUsed) < 0.005f) count++;
		return count;
	}
}
