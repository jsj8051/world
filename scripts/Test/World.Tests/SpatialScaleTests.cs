using System;
using System.Linq;
using NUnit.Framework;
using World.H3Grid;
using World.WorldGen;
using World.Utils.H3;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · **空间尺度 / 单位层**护栏（收口 §07 D-10，2026-10-04）。
///
/// 背景：res 只是**离散化精度**，它决定"一格多大"，不该改变物理量的意义。此前
/// "一格多大"在同一世界里有两个口径（LakeState 的 H3 平均表 vs 各处内联的 4πR²/n），
/// 而 `RiverThresholdCells = 40` 这类**格数阈值**在 res1 与 res4 下相差 34 倍。
///
/// 本文件钉住三件事：
///   ① **守恒恒等式**：`CellAreaKm2 × CellCount == 4πR²` —— 任何"格面积"读数先过这一关；
///   ② **H3 编号偏移**（实测坑）：本项目 res0 = 官方 res1（122 格），查官方表须 +1；
///   ③ **退化解**（永久架构原则）：格数口径与物理口径（km²）在同一 res 下判据**逐格等价**。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class SpatialScaleTests
{
	static readonly Lazy<Ball> Ball1 = new(() => new Ball(1, 1f));

	[Test]
	public void ProductionRes_IsRes4_Frozen()
	{
		// ★防架构倒退（用户 2026-10-04 二次拍板，§07 §5.5）：
		// **生产分辨率 = res4 冻结**。res4 是世界本身的空间离散尺度，
		// 各模拟系统（地质/地貌/水文/火山/人文）都应**适配这个尺度**。
		//
		// 实测当前水文模型的有效工作尺度集中在 res2 附近（res4 河流格仅占陆地 0.98%），
		// 但该差异被认定为**水文模型的尺度适配缺陷（D-11）** ⇒ 正确处置是让水文适配 res4，
		// **不得**把世界降级到 res2。否则会滑向：
		//   "某模块在 res4 不工作 → 换 res3 → 另一个不工作 → 换 res2"的架构倒退。
		//
		// ⇒ 这条测试钉住"生产档"不被子系统表现反向绑架。改动它必须用户解冻。
		var f = typeof(WorldGenPlanet).GetField("ProductionRes",
			System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
		Assert.That(f, Is.Not.Null, "WorldGenPlanet.ProductionRes 必须存在（生产分辨率是显式架构事实）");
		Assert.That(f.IsLiteral, Is.True, "ProductionRes 必须是编译期常量（防止运行期被改）");
		Assert.That((int)f.GetRawConstantValue(), Is.EqualTo(4),
			"生产分辨率 = res4 冻结；若要变更须用户解冻并同步 §07 §5.1 F");
	}

	/// <summary>
	/// ★**验收门槛测试**（用户 2026-10-04 拍板为固定门槛，非一次性）：
	/// `SpatialScale` 的**每一个**对外口径都必须有一条"与独立实现逐点对照"的验证。
	/// 本测试把四个口径的对照集中在一处跑通，作为"新增成员必须配对照"的模板——
	/// 将来加成员时在这里加一行，而不是另起一个只验证自身公式的测试。
	/// 依据见 `SpatialScale.cs` 头注释：`DistanceKm` 的 57 倍 bug 静态看公式完全正常。
	/// </summary>
	[Test]
	public void AllCalibers_HaveIndependentCrossValidation()
	{
		var ball = Ball1.Value;
		var sc = SpatialScale.Of(ball);

		// ① 等积口径 → 对照 H3 逐格精确（逐格面积求和应接近 1/122 球面）
		double exactSum = 0;
		foreach (ulong c in ball.CellIds)
			exactSum += SpatialScale.ExactCellAreaKm2(c);
		double exactPerCell = exactSum / ball.CellIds.Length;
		Assert.That(Math.Abs(exactPerCell - sc.CellAreaKm2) / sc.CellAreaKm2, Is.LessThan(0.03),
			$"① 等积口径 vs H3 精确平均：{sc.CellAreaKm2:N0} vs {exactPerCell:N0}（应 <3%）");

		// ② 守恒恒等式
		Assert.That(sc.AreaConserving, Is.True, "② 格面积 × 格数 必须等于总表面积");

		// ③ 距离 → 对照 H3.CellToLatLng 独立实现
		double worstDist = 0;
		for (int i = 0; i < Math.Min(200, ball.CellIds.Length); i++)
			foreach (int j in ball.CellNeighbors[i])
				worstDist = Math.Max(worstDist, Math.Abs(
					SpatialScale.DistanceKm(ball.CellDirs[i], ball.CellDirs[j])
					- SpatialScale.DistanceKm(ball.CellIds[i], ball.CellIds[j])));
		Assert.That(worstDist, Is.LessThan(1.0), $"③ 距离两口径最大差 {worstDist:N3} km（应 <1 m）");

		// ④ 跳数近似 → 对照真实邻格距离（有界偏差，且确认为"近似"而非精确）
		double worstHop = 0;
		bool anyExact = true;
		for (int i = 0; i < Math.Min(200, ball.CellDirs.Length); i++)
			foreach (int j in ball.CellNeighbors[i])
			{
				double real = SpatialScale.DistanceKm(ball.CellDirs[i], ball.CellDirs[j]);
				double approx = sc.HopDistanceKm(1);
				worstHop = Math.Max(worstHop, Math.Abs(approx - real) / real);
				if (Math.Abs(approx - real) > 1e-6) anyExact = false;
			}
		Assert.That(worstHop, Is.LessThan(0.5), $"④ 跳数近似最大偏差 {worstHop:P1}（应 <50%）");
		Assert.That(anyExact, Is.False,
			"④ HopDistanceKm 必须**不是**精确值——若与真实距离逐一相等，说明尺度层假设被改写，需重评");
	}

	[Test]
	public void CellArea_IsAreaConserving_ByConstruction()
	{
		// ① 守恒恒等式：等积口径的定义就是"总表面积均分到每格"。
		// 这条是所有面积类断言的地基——若它不成立，Σ 类统计全部不可信。
		foreach (int res in new[] { 1, 2, 3 })
		{
			var ball = new Ball(res, 1f);
			var sc = SpatialScale.Of(ball);
			Assert.That(sc.AreaConserving, Is.True, $"res{res}：格面积 × 格数必须等于总表面积");
			Assert.That(sc.CellAreaKm2 * ball.CellIds.Length, Is.EqualTo(sc.TotalAreaKm2).Within(1e-6));
		}
	}

	[Test]
	public void CellArea_MatchesSphereFormula_NotTheOldHardcodedTable()
	{
		// 防"顺手改回硬编码表"：旧表（res1=607,220.978）与等积口径（605,777）差 0.24%。
		// 差得不多，但**两个口径并存**本身就是 D-10 要消灭的问题——留一条钉子。
		var sc = SpatialScale.Of(Ball1.Value);
		double expected = 4.0 * Math.PI * 6371.0 * 6371.0 / Ball1.Value.CellIds.Length;
		Assert.That(sc.CellAreaKm2, Is.EqualTo(expected).Within(1e-6));
		Assert.That(H3.GetHexagonAreaAvgKm2(Ball1.Value.Res), Is.Not.EqualTo(sc.CellAreaKm2).Within(1.0),
			"等积口径与 H3 官方平均口径不应恰好相等（若相等说明有人把其中一个换成了另一个）");
	}

	[Test]
	public void Res_IsOffsetByOne_FromOfficialH3Tables()
	{
		// ② 编号偏移（实测 2026-10-04）：本项目 H3 封装的 res0 = 官方 res1 = 122 格。
		// 后果：任何人拿 H3 官方文档表格对照本项目读数都会**错位一整档**（面积差 7 倍）。
		// 这条钉子把"偏移"变成可验证事实——若将来换 H3 版本导致偏移变化，此测试立刻红。
		Assert.That(H3.GetRes0Cells().Length, Is.EqualTo(122), "本项目 res0 = 122 格（官方 res1）");
		foreach (int res in new[] { 1, 2, 3 })
		{
			var ball = new Ball(res, 1f);
			Assert.That(ball.CellIds.Length, Is.EqualTo(H3.GetNumCells(res)),
				$"res{res}：Ball 格数须等于 H3.GetNumCells（同编号）");
		}
		// 官方平均面积表按**项目编号**取值时与等积口径差 <1%（登记为已知口径差，不是 bug）
		double diff = Math.Abs(H3.GetHexagonAreaAvgKm2(1) - SpatialScale.Of(Ball1.Value).CellAreaKm2)
			/ SpatialScale.Of(Ball1.Value).CellAreaKm2;
		Assert.That(diff, Is.LessThan(0.01), $"口径差应 <1%，实测 {diff:P3}");
	}

	[Test]
	public void AreaCells_RoundTrips()
	{
		var sc = SpatialScale.Of(Ball1.Value);
		foreach (double km2 in new[] { 1.0, 100.0, 70_800.0, 1e6 })
		{
			int cells = sc.CellsForArea(km2);
			Assert.That(sc.AreaOfCells(cells), Is.GreaterThanOrEqualTo(km2),
				"CellsForArea 向上取整 ⇒ 覆盖面积不小于请求值");
			Assert.That(sc.AreaOfCells(cells - 1), Is.LessThan(km2),
				"且不应多取一格（否则阈值会被系统性放大）");
		}
	}

	[Test]
	public void DistanceKm_IsPhysical_NotHopTimesConstant()
	{
		// ③ 距离口径：同一对格子，其真实距离在 res 变化时**按格边长缩放**，
		// 而 hop 数几乎不变 ⇒ 证明"hops × 固定常数"不可能跨 res 当物理距离用。
		var a = SpatialScale.DistanceKm(Ball1.Value.CellDirs[0], Ball1.Value.CellDirs[1]);
		Assert.That(a, Is.GreaterThan(0), "不同格应有正距离");
		Assert.That(SpatialScale.DistanceKm(Ball1.Value.CellDirs[0], Ball1.Value.CellDirs[0]),
			Is.EqualTo(0).Within(1e-9), "同格距离为 0");

		// 邻格距离应落在"每跳距离实测包络"内（HopDistanceKmRange）
		var sc = SpatialScale.Of(Ball1.Value);
		var (minKm, maxKm) = sc.HopDistanceKmRange(1);
		Assert.That(a, Is.GreaterThanOrEqualTo(minKm * 0.99).And.LessThanOrEqualTo(maxKm * 1.01),
			$"邻格距离 {a:N0} km 应落在 1 跳实测包络 [{minKm:N0}, {maxKm:N0}] 内");
	}

	[Test]
	public void HopDistanceKm_IsDocumentedApproximation_NotExact()
	{
		// HopDistanceKm 是**有偏近似**（H3 邻接路径不在大圆上 + 12 个五边形胞畸变）。
		// 实测（2026-10-04 --probe）：真实邻格中心距 / 格边长 = res1 1.78 / res2 1.77 / res3 1.78，
		// 逐邻居最大偏差 18.7%~28.7%。钉住"误差有界"——若将来爆掉，说明拓扑/几何假设变了。
		var ball = Ball1.Value;
		var sc = SpatialScale.Of(ball);
		double worst = 0;
		for (int i = 0; i < Math.Min(200, ball.CellDirs.Length); i++)
			foreach (int j in ball.CellNeighbors[i])
			{
				double real = SpatialScale.DistanceKm(ball.CellDirs[i], ball.CellDirs[j]);
				double approx = sc.HopDistanceKm(1);
				worst = Math.Max(worst, Math.Abs(approx - real) / real);
			}
		Assert.That(worst, Is.LessThan(0.5),
			$"1 跳近似距离与真实距离偏差应 <50%，实测最差 {worst:P1}（超出说明尺度层假设失效）");
	}

	[Test]
	public void DistanceKm_MatchesH3Native_Exactly()
	{
		// 两条口径（球面方向反算 vs H3.CellToLatLng）必须一致——这是球面约定
		// （Y=北、x=cosφcosλ、z=cosφsinλ）正确性的活体验证。
		// 曾经踩过：反算已给弧度却又乘一次 d2r ⇒ 距离缩小 57 倍，静态看公式完全正常。
		// 容差 1 m：`CellDirs` 是 float（Godot Vector3），6371 km × 1e-7 ≈ 0.6 m 的固有误差。
		var ball = Ball1.Value;
		double worst = 0;
		for (int i = 0; i < Math.Min(300, ball.CellIds.Length); i++)
			foreach (int j in ball.CellNeighbors[i])
			{
				double byDir = SpatialScale.DistanceKm(ball.CellDirs[i], ball.CellDirs[j]);
				double byCell = SpatialScale.DistanceKm(ball.CellIds[i], ball.CellIds[j]);
				worst = Math.Max(worst, Math.Abs(byDir - byCell));
			}
		Assert.That(worst, Is.LessThan(1.0),
			$"两口径最大差 {worst:N3} km（应 <1 m，仅 float 精度）——球面约定错位会在这里暴露为数百 km");
	}

	[Test]
	public void RiverThreshold_PhysicsAndCellCriteria_AreEquivalentAtSameRes()
	{
		// ★退化解（永久架构原则）：格数阈值 T 与物理阈值 T×A（km²）在同一 res 下
		// 判据**逐格完全一致** ⇒ 迁移到物理口径零行为漂移，可以放心换。
		const int seed = 42;
		var ball = Ball1.Value;
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, seed, WorldPreset.Earth.LandSea);
		var proj = new H3LandSeaProjector();
		proj.Generate(ball, field, 0.29f);
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
		var regions = new GeologicalRegions(seed);
		regions.Generate(ball, proj, 8_000_000f);
		var mountains = new MountainSkeleton(seed);
		mountains.Generate(ball, regions, surface);
		var landforms = new RegionalLandforms(seed);
		landforms.Generate(ball, regions);
		var features = new System.Collections.Generic.List<FeatureField>
		{
			new(landforms, TerrainDomain.LandOnly),
			new(mountains, TerrainDomain.LandAndSea),
		};
		var composer = new HeightComposer(seed);
		composer.Generate(ball, surface, regions, features);
		var final = new FinalGeography();
		final.Generate(ball, composer, regions);

		// ① 纯格数口径（无降水：判据退化为 FlowAccum ≥ 格数）
		var byCells = new RiverNetwork();
		byCells.Generate(ball, final, composer, riverThresholdCells: 12, annualPrecipMm: null);
		// ② 物理口径：12 格 × 格面积
		double areaKm2 = 12 * SpatialScale.Of(ball).CellAreaKm2;
		var byArea = new RiverNetwork();
		byArea.Generate(ball, final, composer, annualPrecipMm: null, riverThresholdAreaKm2: areaKm2);

		CollectionAssert.AreEqual(byCells.IsRiver, byArea.IsRiver,
			"同一 res 下格数阈值与其等价面积阈值必须逐格一致（退化解）");
		Assert.That(byArea.RiverThresholdKm2, Is.EqualTo(areaKm2).Within(1e-6));
		Assert.That(byArea.RiverThresholdKm2, Is.EqualTo(byCells.RiverThresholdKm2).Within(1e-6));
	}

	[Test]
	public void FlowAccumKm2_IsCatchmentArea_AndScalesWithRes()
	{
		// 汇水面积 = FlowAccum × 格面积。验证它是**物理量**：res 变细时同一判据下的
		// 流域面积趋于稳定，而不是随格数漂移。
		const int seed = 42;
		double prevMax = 0;
		foreach (int res in new[] { 1, 2 })
		{
			var ball = new Ball(res, 1f);
			var layout = new ContinentLayout(seed, 7);
			var field = new LandSeaField(layout, seed, WorldPreset.Earth.LandSea);
			var proj = new H3LandSeaProjector();
			proj.Generate(ball, field, 0.29f);
			var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
			var regions = new GeologicalRegions(seed);
			regions.Generate(ball, proj, 8_000_000f);
			var mountains = new MountainSkeleton(seed);
			mountains.Generate(ball, regions, surface);
			var landforms = new RegionalLandforms(seed);
			landforms.Generate(ball, regions);
			var features = new System.Collections.Generic.List<FeatureField>
			{
				new(landforms, TerrainDomain.LandOnly),
				new(mountains, TerrainDomain.LandAndSea),
			};
			var composer = new HeightComposer(seed);
			composer.Generate(ball, surface, regions, features);
			var final = new FinalGeography();
			final.Generate(ball, composer, regions);
			var rivers = new RiverNetwork();
			// 用**物理阈值**，各 res 语义一致
			rivers.Generate(ball, final, composer, annualPrecipMm: null,
				riverThresholdAreaKm2: 70_800.0);

			double maxKm2 = 0;
			for (int i = 0; i < rivers.FlowAccumKm2.Length; i++)
			{
				Assert.That(rivers.FlowAccumKm2[i],
					Is.EqualTo(rivers.FlowAccum[i] * SpatialScale.Of(ball).CellAreaKm2).Within(1e-6));
				if (rivers.IsRiver[i]) maxKm2 = Math.Max(maxKm2, rivers.FlowAccumKm2[i]);
			}
			// 固定 70,800 km² 阈值下，最大河流汇水面积应 ≥ 阈值（存在达标的河）
			Assert.That(maxKm2, Is.GreaterThanOrEqualTo(70_800.0 * 0.999),
				$"res{res}：物理阈值下最大汇水面积应 ≥ 阈值");
			prevMax = maxKm2;
		}
		Assert.That(prevMax, Is.GreaterThan(0));
	}

	[Test]
	public void BasinAreaKm2_SumsToLandArea_NoDoubleCounting()
	{
		// 流域物理面积必须严格守恒：Σ AreaKm2 = 陆地总面积（km²）。这同时验证
		// "格数（拓扑）→ 面积（几何）"的换算没有引入重复或遗漏。
		const int seed = 42;
		var ball = Ball1.Value;
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, seed, WorldPreset.Earth.LandSea);
		var proj = new H3LandSeaProjector();
		proj.Generate(ball, field, 0.29f);
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
		var regions = new GeologicalRegions(seed);
		regions.Generate(ball, proj, 8_000_000f);
		var mountains = new MountainSkeleton(seed);
		mountains.Generate(ball, regions, surface);
		var landforms = new RegionalLandforms(seed);
		landforms.Generate(ball, regions);
		var features = new System.Collections.Generic.List<FeatureField>
		{
			new(landforms, TerrainDomain.LandOnly),
			new(mountains, TerrainDomain.LandAndSea),
		};
		var composer = new HeightComposer(seed);
		composer.Generate(ball, surface, regions, features);
		var final = new FinalGeography();
		final.Generate(ball, composer, regions);
		var rivers = new RiverNetwork();
		rivers.Generate(ball, final, composer, riverThresholdCells: 12, annualPrecipMm: null);
		var basins = new BasinGraph();
		basins.Generate(ball, final, rivers);

		double cellKm2 = SpatialScale.Of(ball).CellAreaKm2;
		int landCells = final.FinalLand.Count(b => b);
		double sumKm2 = 0;
		for (int b = 0; b < basins.BasinCount; b++)
		{
			Assert.That(basins.AreaKm2[b], Is.EqualTo(basins.Area[b] * cellKm2).Within(1e-6),
				$"流域 {b}：面积 = 格数 × 格面积");
			sumKm2 += basins.AreaKm2[b];
		}
		Assert.That(basins.BasinCount, Is.GreaterThan(0));
		Assert.That(sumKm2, Is.EqualTo(landCells * cellKm2).Within(1e-3),
			"Σ 流域面积必须等于陆地总面积（不重复、不遗漏）");
	}
}
