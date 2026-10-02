using System;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NoiseWorld.WorldGen;
using World.Utils.H3;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 离散化层护栏（决策原典 §四/§五）：
///   · 单点模式 ≡ 逐格直采格心；
///   · 多点采样：格值必须落在「格心 + 角点」采样点的 min/max 包络内（加权平均的精确不变量）；
///     CenterWeight=1 逐位退化为单点；
///   · 确定性：同种子两次采样逐位同；
///   · 解耦红线：场不依赖 Ball——同一场采样同方向与格网无关（换 res 不动生成算法的地基）。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3TerrainSamplerTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));   // 842 格（含 12 五边形）
	static Ball Ball => SharedBall.Value;

	static ElevationFieldStack MakeStack(int seed) =>
		new(new WorldGenParams { Seed = seed });

	[Test]
	public void CenterOnly_MatchesDirectCellDirSampling()
	{
		var field = MakeStack(42).Elevation;
		var sampler = new H3TerrainSampler(Ball);
		var elev = sampler.SampleField(field, H3TerrainSampler.Mode.CenterOnly);
		var dirs = Ball.CellDirs;
		Assert.That(elev.Length, Is.EqualTo(dirs.Length));
		for (int i = 0; i < elev.Length; i++)
			Assert.That(elev[i], Is.EqualTo(field.Sample(dirs[i])),
				$"格 {i}：单点模式必须逐位等于格心直采");
	}

	[Test]
	public void MultiSample_ValueWithinSamplePointsEnvelope()
	{
		var field = MakeStack(42).Elevation;
		var sampler = new H3TerrainSampler(Ball) { CenterWeight = 0.5f };
		var elev = sampler.SampleField(field, H3TerrainSampler.Mode.CenterAndCorners);
		var dirs = Ball.CellDirs;

		for (int i = 0; i < elev.Length; i++)
		{
			// 重建该格采样点集（心 + 实际角数），验证格值 ∈ [min, max]（加权平均的精确不变量）
			ulong[] vids = H3.CellToVertexes(Ball.CellIds[i]);
			int cornerCount = Math.Min(vids.Length, 6);
			float min = field.Sample(dirs[i]);
			float max = min;
			for (int k = 0; k < cornerCount; k++)
			{
				float v = field.Sample(Ball.VertexPositions[Ball.VertexIndexOf(vids[k])].Normalized());
				min = MathF.Min(min, v);
				max = MathF.Max(max, v);
			}
			Assert.That(elev[i], Is.InRange(min, max),
				$"格 {i}：多点采样值必须落在采样点包络内");
		}
	}

	[Test]
	public void CenterWeightOne_DegradesToCenterOnly()
	{
		var field = MakeStack(42).Elevation;
		var multi = new H3TerrainSampler(Ball) { CenterWeight = 1f };
		var single = new H3TerrainSampler(Ball);
		CollectionAssert.AreEqual(
			single.SampleField(field, H3TerrainSampler.Mode.CenterOnly),
			multi.SampleField(field, H3TerrainSampler.Mode.CenterAndCorners),
			"格心权重 1 = 多点模式必须逐位退化为单点（角点权重归零）");
	}

	[Test]
	public void SameSeed_SamplesBitwiseIdentical()
	{
		var field = MakeStack(42).Elevation;
		var a = new H3TerrainSampler(Ball).SampleField(field, H3TerrainSampler.Mode.CenterAndCorners);
		var b = new H3TerrainSampler(Ball).SampleField(field, H3TerrainSampler.Mode.CenterAndCorners);
		CollectionAssert.AreEqual(a, b, "同种子同格网须逐位同（确定性红线）");
	}

	[Test]
	public void FieldIndependentOfBall_SameCellSameValue()
	{
		// 解耦红线：场是无状态空间函数——采样值只由方向决定，与 Ball 构造（半径/实例）无关。
		// 同 res 两个 Ball 拓扑相同；半径不同 ⇒ 球面投影的舍入模式不同，CellDirs 本身就带
		// ~1e-5 rad 级的方向差（×3 km 振幅 → dm 级场值差，实测 0.016 m）——断言容差 0.1 m：
		// 采样管线不得把 Ball 的半径/实例状态泄漏进采样值（归一化末位差之外的任何放大都算泄漏）。
		var field = MakeStack(42).Elevation;
		var a = new H3TerrainSampler(new Ball(1, 1f)).SampleField(field);
		var b = new H3TerrainSampler(new Ball(1, 5f)).SampleField(field);
		Assert.That(a.Length, Is.EqualTo(b.Length));
		for (int i = 0; i < a.Length; i++)
			Assert.That(b[i], Is.EqualTo(a[i]).Within(0.1f),
				$"格 {i}：采样值不得依赖 Ball 半径/实例（容差 = 方向浮点差 × 振幅的量级论证）");
	}
}
