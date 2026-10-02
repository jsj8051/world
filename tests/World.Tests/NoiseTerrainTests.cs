using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using World.NewHexWorld;
using World.NoiseWorld;

namespace World.Tests;

/// <summary>
/// 噪声地形护栏（"是什么就是什么"第一步 + L0-L5 全栈）：
///   · **身份不变量**：海拔符号 ⟺ 板块陆/洋身份（陆板全陆、洋板全洋——每格严丝合缝）；
///   · 确定性：同种子两次生成**逐位同**（噪声地形-01 §8 红线 1）；
///   · 图层开关：关任一层改变场且可复现；关 L0 = 基准球（海拔处处 0，身份测试不适用）；
///   · L5 护栏：单调样条 ⇒ 海拔值域被结点包络（海侧不破深海结点、山不超顶格）。
/// 纪律（同 H3RiversTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class NoiseTerrainTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));   // 842 格
	static readonly Lazy<Ball> Ball2 = new(() => new Ball(2, 1f));       // 5882 格（山带链尺度判读用）
	static Ball Ball => SharedBall.Value;

	static (NoiseTerrain t, NoisePlates plates) MakeTerrain(int seed, float landTarget = 0.29f,
		bool warpOn = true, bool contOn = true, bool ridgeOn = true, bool detailOn = true,
		int numPlates = 8, Ball ball = null)
	{
		ball ??= Ball;
		var plates = new NoisePlates();
		plates.Generate(ball, numPlates, seed, landTarget);
		var t = new NoiseTerrain();
		t.Params.Seed = seed;
		t.Params.LandFractionTarget = landTarget;
		t.SetLayerEnabled(NoiseTerrain.LayerWarp, warpOn);
		t.SetLayerEnabled(NoiseTerrain.LayerContinent, contOn);
		t.SetLayerEnabled(NoiseTerrain.LayerRidge, ridgeOn);
		t.SetLayerEnabled(NoiseTerrain.LayerDetail, detailOn);
		t.Generate(ball.CellDirs, plates, ball.CellNeighbors);
		return (t, plates);
	}

	[Test]
	public void SameSeed_GeneratesBitwiseIdenticalField()
	{
		var a = MakeTerrain(42);
		var b = MakeTerrain(42);
		CollectionAssert.AreEqual(a.t.ElevationM, b.t.ElevationM, "同种子同方向须逐位同（§8 红线 1）");
	}

	[Test]
	public void IdentityDecidesLandSea_EveryCell()
	{
		// **"是什么就是什么"不变量**：海拔符号 ⟺ 板块陆/洋身份——陆板全陆、洋板全洋，一格不差
		var (t, plates) = MakeTerrain(42);
		for (int i = 0; i < t.ElevationM.Length; i++)
		{
			bool isLandPlate = plates.LandPlate[plates.PlateOfCell[i]];
			Assert.That(t.ElevationM[i] > 0f, Is.EqualTo(isLandPlate),
				$"格 {i}：海拔符号必须等于板块身份（陆板 {isLandPlate}）");
		}
	}

	[Test]
	public void LandPlateCount_FollowsTarget()
	{
		// 目标陆地占比 = **陆板占比**口径：陆板数 = round(板数 × 目标)，clamp [1, P−1]
		var ball = Ball2.Value;
		var plates = new NoisePlates();
		plates.Generate(ball, 12, 42, 0.29f);
		Assert.That(plates.LandPlate.Count(x => x), Is.EqualTo(3),
			"12 板 × 0.29 ⇒ 3.48 → round = 3 块陆板");
		Assert.That(plates.LandPlate.Count(x => x), Is.GreaterThanOrEqualTo(1), "至少一陆");
		Assert.That(plates.LandPlate.Count(x => !x), Is.GreaterThanOrEqualTo(1), "至少一洋");
	}

	[Test]
	public void LayerToggles_ChangeField_Reproducibly()
	{
		var (full, _) = MakeTerrain(42);
		CollectionAssert.AreNotEqual(full.ElevationM, MakeTerrain(42, warpOn: false).t.ElevationM,
			"关 L1（域扭曲）应改变场（海岸线弯曲的来源）");
		CollectionAssert.AreNotEqual(full.ElevationM, MakeTerrain(42, ridgeOn: false).t.ElevationM,
			"关 L2（山系带）应改变场（山脉的来源）");
		CollectionAssert.AreNotEqual(full.ElevationM, MakeTerrain(42, detailOn: false).t.ElevationM,
			"关 L3（细节层）应改变场（碎质感的来源）");

		// 开关不挪别的层的种子：同参数重复生成须逐位同
		CollectionAssert.AreEqual(full.ElevationM, MakeTerrain(42).t.ElevationM, "同参数重复生成须逐位同");
	}

	[Test]
	public void ContinentOff_FlatBall()
	{
		var (flat, _) = MakeTerrain(42, contOn: false);
		Assert.That(flat.ElevationM.All(x => x == 0f), Is.True, "关 L0 = 基准球（图层开关的退化出口）");
	}

	[Test]
	public void Elevation_StaysWithinSplineKnotEnvelope()
	{
		var p = new NoiseTerrain().Params;
		float detailMax = p.WeightDetail * p.DetailAmplitudeM;
		float mountainMax = NoiseTerrain.MountainM * p.PlateBeltWeight;
		var elev = MakeTerrain(42).t.ElevationM;
		Assert.That(elev.All(x => x >= p.SplineAbyssM - NoiseTerrain.TrenchMeters - 1f), Is.True,
			"海侧不得穿破深海结点-海沟挖深上限（单调样条无过冲 + 海沟有界）");
		Assert.That(elev.All(x => x <= p.SplinePeakM + mountainMax + detailMax + 1f), Is.True,
			"山不得穿破山脉结点+造山加成+细节上探");
		Assert.That(elev.Min(), Is.LessThan(-3000f), "深海平原应实际出现（样条下段被用到；洋板存在 ⇒ 必有深海）");
		Assert.That(elev.Any(x => x is > -500f and <= -1f), Is.True, "岸棚/浅海应实际出现（离岸距离剖面的近岸档）");
		Assert.That(elev.Max(), Is.GreaterThan(0f), "陆板应实际出露（身份定海陆 ⇒ 陆板全部 > 0）");

		// 山地/高原档由 L2 山系带 + 碰撞带顶上去——res1（~250km 格）链分辨不出 ⇒ 用 res2 判。
		// 门槛 = 高原结点（软阈值碰撞带细化后峰值强度沿走向分布，不再绑死具体种子出极值）。
		var (t2, _) = MakeTerrain(42, numPlates: 8, ball: Ball2.Value);
		Assert.That(t2.ElevationM.Max(), Is.GreaterThan(p.SplineHighlandM),
			"山系带应把板缘顶过高原结点（样条上段被用到）");
	}

	// ── 板块划分 + 联动（粗格吞并七修 + 碰撞带 C）──

	[Test]
	public void CoastRing_AlwaysShallow()
	{
		// **"所有陆地边上都是浅水"不变量**：与陆格相邻的洋格必为浅水（近岸浅水环 + 海沟离岸淡出）
		var ball = Ball2.Value;
		var (t, _) = MakeTerrain(42, numPlates: 8, ball: ball);
		int checkedCells = 0;
		for (int i = 0; i < ball.CellIds.Length; i++)
		{
			if (t.ElevationM[i] > 0f) continue;                       // 陆格跳过
			if (!ball.CellNeighbors[i].Any(j => t.ElevationM[j] > 0f)) continue;   // 非海岸洋格
			checkedCells++;
			Assert.That(t.ElevationM[i], Is.GreaterThan(-500f),
				$"海岸洋格 {i} 必须是浅水（岸棚/浅海档）");
		}
		Assert.That(checkedCells, Is.GreaterThan(0), "测试有效性：应存在海岸洋格");
	}

	[Test]
	public void PlateCoupling_CoversEveryCell()
	{
		// 覆盖率护栏：划分后每格必有合法板号（Dijkstra 出堆认领漏行 = 全图 -1 的教训，2026-09-30）
		var ball = Ball2.Value;
		var plates = new NoisePlates();
		plates.Generate(ball, 7, 42, 0.29f);
		Assert.That(plates.PlateOfCell.Length, Is.EqualTo(ball.CellIds.Length), "板号表与格表对齐");
		Assert.That(plates.PlateOfCell.All(p => p >= 0 && p < plates.NumPlates), Is.True,
			"每格板号 ∈ [0, NumPlates)——不许有未认领格");
	}

	[Test]
	public void ShapeGuard_DetectsHourglass_SparsCompact()
	{
		// 形状护栏直测（合成图，含外圈海洋板——真实球面拓扑）：
		//   板 0 = 上叶（16×6）+ 2 格宽竖走廊 + 下叶（16×6）= 沙漏；走廊两侧 = 板 1 海洋
		int W = 18, H = 20;
		int n = W * H;
		var plate = new int[n];
		var nbList = new List<int>[n];
		for (int i = 0; i < n; i++) nbList[i] = new List<int>();
		for (int y = 0; y < H; y++)
		for (int x = 0; x < W; x++)
		{
			int i = y * W + x;
			if (x + 1 < W) { nbList[i].Add(i + 1); nbList[i + 1].Add(i); }
			if (y + 1 < H) { nbList[i].Add(i + W); nbList[i + W].Add(i); }
			bool ring = x == 0 || x == W - 1 || y == 0 || y == H - 1;
			bool upper = y >= 1 && y <= 6;                          // 上叶带
			bool lower = y >= 13 && y <= 18;                        // 下叶带
			bool corridor = x >= 8 && x <= 9 && y >= 7 && y <= 12;  // 2 格宽竖走廊
			plate[i] = (byte)((ring || !(upper || lower || corridor)) || (upper || lower) || corridor ? (upper || lower || corridor ? 0 : 1) : 1);
		}
		// 明确重标：外圈 = 板 1；上/下叶带 = 板 0；走廊 = 板 0；中带其余（走廊两侧）= 板 1
		for (int y = 0; y < H; y++)
		for (int x = 0; x < W; x++)
		{
			int i = y * W + x;
			bool ring = x == 0 || x == W - 1 || y == 0 || y == H - 1;
			bool upper = y >= 1 && y <= 6;
			bool lower = y >= 13 && y <= 18;
			bool corridor = x >= 8 && x <= 9 && y >= 7 && y <= 12;
			plate[i] = (byte)((upper || lower || corridor) && !ring ? 0 : 1);
		}
		var counts = new int[2];
		foreach (int p in plate) counts[p]++;
		Assert.That(counts[0], Is.GreaterThan(100), "测试有效性：板 0 应有两叶 + 走廊");
		Assert.That(NoisePlates.HasFineBottleneck(plate, counts, nbList.Select(l => l.ToArray()).ToArray()), Is.True,
			"沙漏（两块大叶 + 2 格宽走廊）必须检出");

		// 紧凑块：16×16 板 0 满块 + 外圈板 1 → 单深核，不误报
		int W2 = 18, H2 = 18, n2 = W2 * H2;
		var plate2 = new int[n2];
		var nbList2 = new List<int>[n2];
		for (int i = 0; i < n2; i++) nbList2[i] = new List<int>();
		for (int y = 0; y < H2; y++)
		for (int x = 0; x < W2; x++)
		{
			int i = y * W2 + x;
			if (x + 1 < W2) { nbList2[i].Add(i + 1); nbList2[i + 1].Add(i); }
			if (y + 1 < H2) { nbList2[i].Add(i + W2); nbList2[i + W2].Add(i); }
			bool ring = x == 0 || x == W2 - 1 || y == 0 || y == H2 - 1;
			plate2[i] = (byte)(ring ? 1 : 0);
		}
		var counts2 = new int[2];
		foreach (int p in plate2) counts2[p]++;
		Assert.That(NoisePlates.HasFineBottleneck(plate2, counts2, nbList2.Select(l => l.ToArray()).ToArray()), Is.False,
			"紧凑板块（单深核）无瓶颈——半岛/海湾不受影响");
	}

	[Test]
	public void PlateCoupling_IsDeterministic_AndSeedSensitive()
	{
		var ball = Ball2.Value;
		var a = new NoisePlates();
		a.Generate(ball, 7, 42, 0.29f);
		var b = new NoisePlates();
		b.Generate(ball, 7, 42, 0.29f);
		CollectionAssert.AreEqual(a.PlateOfCell, b.PlateOfCell, "同 seed 划分须逐位同");
		CollectionAssert.AreEqual(a.LandPlate, b.LandPlate, "陆/洋身份同 seed 须逐位同");

		var c = new NoisePlates();
		c.Generate(ball, 7, 7, 0.29f);
		CollectionAssert.AreNotEqual(a.PlateOfCell, c.PlateOfCell, "换种子划分应不同（防退化为常量）");

		var ta = new NoiseTerrain();
		ta.Params.Seed = 42;
		ta.Generate(ball.CellDirs, a, ball.CellNeighbors);
		var tb = new NoiseTerrain();
		tb.Params.Seed = 42;
		tb.Generate(ball.CellDirs, a, ball.CellNeighbors);
		CollectionAssert.AreEqual(ta.ElevationM, tb.ElevationM, "板块联动路径同 seed 须逐位同（§8 红线 1）");
	}

	[Test]
	public void PlateCoupling_BeltMakesInlandMountains()
	{
		// C 的判读：碰撞带掩码与海岸掩码取 max ⇒ 板缘碰撞带也应把海拔顶起来。
		// 门槛 = 高原结点（软阈值细化后峰值沿走向分布，绑死极值会种子脆弱）。
		var ball = Ball2.Value;
		var plates = new NoisePlates();
		plates.Generate(ball, 5, 42, 0.29f);
		var t = new NoiseTerrain();
		t.Params.Seed = 42;
		t.Params.PlateBeltWeight = 1.5f;
		t.Generate(ball.CellDirs, plates, ball.CellNeighbors);
		Assert.That(t.ElevationM.Max(), Is.GreaterThan(t.Params.SplineHighlandM),
			"碰撞带造山应实际出现（板缘顶过高原结点；样条上段被用到）");
	}
}
