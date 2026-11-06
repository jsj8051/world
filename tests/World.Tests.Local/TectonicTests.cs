using System;
using System.Collections.Generic;
using Godot;
using World.Data;
using World.Logic;

namespace World.Tests;

/// <summary>
/// 板块构造来源与距离传播的纯托管测试：合成边界表 / 一维球面链，不启引擎、不碰 H3。
/// 钉的是分类规则（上下盘、混合壳型）与 Dijkstra 的来源归属，不是具体高度。
/// </summary>
static class TectonicTests
{
	// ── 合成边界：两块板块共享一条边（格 0 属板块 0，格 1 属板块 1）──

	static PlateBoundarySet SingleEdge(out int[] plateOfCell)
	{
		var edges = new[] { new PlateBoundaryEdge(0, 1, 0, 1) };
		var pairs = new PlateBoundaryPair[4];
		pairs[1] = new PlateBoundaryPair(0, 1, 0, 1);
		plateOfCell = new[] { 0, 1 };
		var set = new PlateBoundarySet(2, edges, pairs, new[] { 0, 1, 2 }, new[] { 0, 0 });
		set.Validate(2, plateOfCell);
		return set;
	}

	/// <summary>同一对板块的两段边界：格 0–1 与 2–3。</summary>
	static PlateBoundarySet TwoEdges(out int[] plateOfCell)
	{
		var edges = new[]
		{
			new PlateBoundaryEdge(0, 1, 0, 1),
			new PlateBoundaryEdge(2, 3, 0, 1),
		};
		var pairs = new PlateBoundaryPair[4];
		pairs[1] = new PlateBoundaryPair(0, 1, 0, 2);
		plateOfCell = new[] { 0, 1, 0, 1 };
		var set = new PlateBoundarySet(2, edges, pairs, new[] { 0, 1, 2, 3, 4 }, new[] { 0, 0, 1, 1 });
		set.Validate(4, plateOfCell);
		return set;
	}

	static CrustField CrustOf(params CrustType[] types)
	{
		var provinces = new List<CrustProvince>();
		var provinceId = new int[types.Length];
		Array.Fill(provinceId, -1);
		for (int cell = 0; cell < types.Length; cell++)
		{
			if (types[cell] != CrustType.Continental) continue;
			provinceId[cell] = provinces.Count;
			provinces.Add(new CrustProvince(provinces.Count, types[cell], cell, cell, 1));
		}
		return new CrustField(types, provinceId, provinces.ToArray());
	}

	static (Plate[] plates, CrustField crust) Pair(CrustType a, float densityA, CrustType b, float densityB)
	{
		var plates = new[]
		{
			new Plate { PlateId = 0, Density = densityA },
			new Plate { PlateId = 1, Density = densityB },
		};
		return (plates, CrustOf(a, b));
	}

	static TectonicSource[] Classify((Plate[] plates, CrustField crust) pair, float motion)
	{
		PlateBoundarySet set = SingleEdge(out _);
		return TectonicSourceOps.Build(set, pair.plates, pair.crust, new[] { motion });
	}

	static void Check(TectonicSource[] sources, int cell, TectonicChannel expected, string message)
	{
		foreach (TectonicSource source in sources)
		{
			if (source.SideCell != cell) continue;
			Program.True(source.Channel == expected, $"{message}：实得 {source.Channel}，期望 {expected}");
			return;
		}
		throw new Exception($"{message}：没有格 {cell} 的来源");
	}

	// ── 分类 ──

	public static void ContinentalCollisionHasBothSides()
	{
		TectonicSource[] sources = Classify(Pair(CrustType.Continental, 1f, CrustType.Continental, 2f), -1f);
		Program.True(sources.Length == 2, $"陆陆汇聚应两侧成源，实得 {sources.Length}");
		Check(sources, 0, TectonicChannel.ContinentalCollision, "陆陆 A 侧");
		Check(sources, 1, TectonicChannel.ContinentalCollision, "陆陆 B 侧");
	}

	public static void MixedCrustPutsArcOnContinentalSide()
	{
		// 陆侧密度更大也必须上盘：混合壳型不看密度
		TectonicSource[] sources = Classify(Pair(CrustType.Continental, 9f, CrustType.Oceanic, 1f), -1f);
		Check(sources, 0, TectonicChannel.ContinentalArc, "陆洋陆侧");
		Check(sources, 1, TectonicChannel.Subduction, "陆洋洋侧");
	}

	public static void DenseOceanPlateSubducts()
	{
		TectonicSource[] sources = Classify(Pair(CrustType.Oceanic, 3f, CrustType.Oceanic, 1f), -1f);
		Check(sources, 0, TectonicChannel.Subduction, "重洋壳俯冲");
		Check(sources, 1, TectonicChannel.OceanicArc, "轻洋壳成弧");
	}

	public static void EqualDensityTieBreakFavorsLowerPlateId()
	{
		TectonicSource[] sources = Classify(Pair(CrustType.Oceanic, 1f, CrustType.Oceanic, 1f), -1f);
		Check(sources, 0, TectonicChannel.OceanicArc, "等密度小号上盘");
		Check(sources, 1, TectonicChannel.Subduction, "等密度大号俯冲");
	}

	public static void SamePlatePairUsesPerEdgeCrust()
	{
		// 同一对板块：一段陆陆、一段陆洋 ⇒ 角色必须逐边不同
		PlateBoundarySet set = TwoEdges(out _);
		var plates = new[]
		{
			new Plate { PlateId = 0, Density = 1f },
			new Plate { PlateId = 1, Density = 2f },
		};
		var crust = CrustOf(CrustType.Continental, CrustType.Continental,
			CrustType.Continental, CrustType.Oceanic);
		TectonicSource[] sources = TectonicSourceOps.Build(set, plates, crust, new[] { -1f, -1f });
		Program.True(sources.Length == 4, $"两段边界应各有两侧来源，实得 {sources.Length}");
		Check(sources, 0, TectonicChannel.ContinentalCollision, "陆陆段 A");
		Check(sources, 1, TectonicChannel.ContinentalCollision, "陆陆段 B");
		Check(sources, 2, TectonicChannel.ContinentalArc, "陆洋段陆侧");
		Check(sources, 3, TectonicChannel.Subduction, "陆洋段洋侧");
	}

	public static void CrustFieldRejectsWrongProvinceCount()
	{
		Program.Throws(() => new CrustField(
			new[] { CrustType.Continental },
			new[] { 0 },
			new[] { new CrustProvince(0, CrustType.Continental, 0, 0, 2) }), "省格数不符");
	}

	public static void ContinentalRiftUsesLocalCrust()
	{
		TectonicSource[] sources = Classify(Pair(CrustType.Continental, 1f, CrustType.Continental, 1f), 1f);
		Check(sources, 0, TectonicChannel.ContinentalRift, "陆壳张裂 A");
		Check(sources, 1, TectonicChannel.ContinentalRift, "陆壳张裂 B");
	}

	public static void MixedDivergenceOnlyRidgesOceanSide()
	{
		// 陆洋张裂 = 大陆被动边缘：陆侧不产通道，洋侧成脊
		TectonicSource[] sources = Classify(Pair(CrustType.Continental, 1f, CrustType.Oceanic, 1f), 1f);
		Program.True(sources.Length == 1, $"陆洋张裂应只有洋侧来源，实得 {sources.Length}");
		Check(sources, 1, TectonicChannel.OceanicRidge, "混合张裂洋侧");
	}

	public static void TransformYieldsNoSources()
	{
		Program.True(Classify(Pair(CrustType.Oceanic, 1f, CrustType.Oceanic, 2f), 0.1f).Length == 0,
			"低于阈值应无来源");
		Program.True(Classify(Pair(CrustType.Oceanic, 1f, CrustType.Oceanic, 2f), 0.25f).Length == 0,
			"恰在阈值上应算不活动");
	}

	public static void BadMotionIsRejected()
	{
		PlateBoundarySet set = SingleEdge(out _);
		var pair = Pair(CrustType.Oceanic, 1f, CrustType.Oceanic, 2f);
		Program.Throws(() => TectonicSourceOps.Build(set, pair.plates, pair.crust, new float[0]),
			"逐边运动长度不符");
		Program.Throws(() => TectonicSourceOps.Build(set, pair.plates, pair.crust, new[] { float.NaN }),
			"非有限运动");
	}

	// ── 距离传播 ──

	static Vector3 At(float deg)
	{
		float r = deg * MathF.PI / 180f;
		return new Vector3(MathF.Cos(r), 0f, MathF.Sin(r));
	}

	static readonly Vector3[] LineDirs = { At(0f), At(10f), At(40f) };
	static readonly int[][] LineNeighbors = { new[] { 1 }, new[] { 0, 2 }, new[] { 1 } };

	public static void NearestSeedWins()
	{
		var field = DistanceFieldOps.BuildSeeds(LineDirs, LineNeighbors, new[]
		{
			new DistanceFieldOps.Seed(0, 7),
			new DistanceFieldOps.Seed(2, 9),
		}, 5000);
		Program.True(field.SeedId[1] == 7, $"格 1 应归近端源，实得 {field.SeedId[1]}");
		Program.Near(field.DistKm[1], 6371.0 * 10.0 * Math.PI / 180.0, 2.0, "10° 弧长");
	}

	public static void PassableBlocksPropagation()
	{
		var field = DistanceFieldOps.BuildSeeds(LineDirs, LineNeighbors, new[]
		{
			new DistanceFieldOps.Seed(0, 3),
		}, 5000, (i, j) => j != 1);
		Program.True(field.SeedId[1] == -1 && field.SeedId[2] == -1, "被拒的边不应穿越");
		Program.Near(field.DistKm[1], 5000, 1e-3, "够不到的格取截断距离");
	}

	public static void CapStopsPropagation()
	{
		var field = DistanceFieldOps.BuildSeeds(LineDirs, LineNeighbors, new[]
		{
			new DistanceFieldOps.Seed(0, 3),
		}, 1500);
		Program.True(field.SeedId[1] == 3, "截断内应可达");
		Program.Near(field.DistKm[1], 6371.0 * 10.0 * Math.PI / 180.0, 2.0, "截断内距离");
		Program.True(field.SeedId[2] == -1, "截断外应不可达");
		Program.Near(field.DistKm[2], 1500, 1e-3, "截断外取截断值");
	}

	public static void SameCellSeedKeepsSmallerDistanceAndId()
	{
		var field = DistanceFieldOps.BuildSeeds(LineDirs, LineNeighbors, new[]
		{
			new DistanceFieldOps.Seed(1, 5, 10),
			new DistanceFieldOps.Seed(1, 4, 20),
		}, 5000);
		Program.True(field.SeedId[1] == 5, "同格多源应取更短起始距离");
		Program.Near(field.DistKm[1], 10, 1e-6, "同格起始距离");
	}
}
