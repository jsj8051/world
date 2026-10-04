using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;                            // 仅 Vector3 值类型
using World.NewHexWorld;
using World.NoiseWorld.WorldGen;
using World.Utils;                       // SphericalFbmNoise
using World.Utils.H3;                    // H3

namespace World.PerfBench;

// 世界生成空间 · **性能基线**（收口 §07 §4）。
// 目的：把"各阶段耗时 / 内存峰值 / res 档位对比"变成**实测数字**，而不是文档里的印象。
// 本工具只读世界生成栈，不写文件、不改任何生成逻辑、不触碰 GD.* / LogService。
//
// 用法：dotnet run --project tests/PerfBench -c Release -- 1 4
//      （默认 res1 res4；res5 约 200 万格，单独跑且耗时较长）
internal static class Program
{
	const int Seed = 42;
	const int ContinentCount = 7;
	const float LandFraction = 0.29f;
	const float TargetRegionAreaKm2 = 8_000_000f;
	const int RiverThresholdCells = 12;

	static void Main(string[] args)
	{
		if (args.Length > 0 && args[0] == "--probe") { Probe(); return; }
		if (args.Length > 0 && args[0] == "--converge") { Converge(args.Skip(1).ToArray()); return; }
		if (args.Length > 0 && args[0] == "--d11") { D11(args.Skip(1).ToArray()); return; }

		var resList = args.Length > 0
			? args.Select(a => int.Parse(a)).ToArray()
			: new[] { 1, 4 };

		var rows = new List<(int res, string stage, double ms)>();
		var mem = new List<(int res, long cells, long managedMb, long peakWorkingSetMb)>();

		foreach (int res in resList)
			Run(res, rows, mem);

		Print(rows, mem);
	}

	// Resolution convergence 实测（收口 D-10）：验证"提高 res ⇒ 宏观统计量趋于稳定"。
	// ★关键：河流阈值必须用**物理口径**（km²），否则格数阈值会让统计量随 res 漂移
	//   （40 格在 res1 = 2,431 万 km²，在 res4 = 7.08 万 km²，差 34 倍），
	//   那样测到的"不收敛"是参数口径造成的，不是离散化造成的。
	static void Converge(string[] resArgs)
	{
		var resList = resArgs.Length > 0 ? resArgs.Select(int.Parse).ToArray() : new[] { 1, 2, 3, 4 };
		Console.WriteLine("| 统计量 | " + string.Join(" | ", resList.Select(r => $"res{r}")) + " |");
		Console.WriteLine("|---" + string.Join("|", resList.Select(_ => "---")) + "|---|");

		var landFrac = new List<double>();
		var basinMax = new List<double>();
		var basinMed = new List<double>();
		var basinN = new List<double>();
		var lakeN = new List<double>();
		var lakeArea = new List<double>();
		var lakeVol = new List<double>();
		var endoFrac = new List<double>();
		var riverLen = new List<double>();
		var riverN = new List<double>();
		var volcanoN = new List<double>();
		var cells = new List<double>();

		foreach (int res in resList)
		{
			var ball = new Ball(res, 1f);
			var sc = SpatialScale.Of(ball);
			var layout = new ContinentLayout(Seed, ContinentCount);
			var field = new LandSeaField(layout, new LandSeaParams { Seed = Seed });
			var proj = new H3LandSeaProjector();
			proj.Generate(ball, field, LandFraction);
			var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
			var regions = new GeologicalRegions(Seed);
			regions.Generate(ball, proj, TargetRegionAreaKm2);
			var mountains = new MountainSkeleton(Seed);
			mountains.Generate(ball, regions, surface);
			var landforms = new RegionalLandforms(Seed);
			landforms.Generate(ball, regions);
			var volcanoes = new VolcanoField();
			volcanoes.Place(ball, regions, mountains.Tectonic, Seed, mountains.RangeAnchors());
			var features = new List<FeatureField>
			{
				new(landforms, TerrainDomain.LandOnly),
				new(mountains, TerrainDomain.LandAndSea),
				new(volcanoes, TerrainDomain.LandAndSea),
			};
			var composer = new HeightComposer(Seed);
			composer.Generate(ball, surface, regions, features);
			var final = new FinalGeography();
			final.Generate(ball, composer, regions);
			var precip = new PrecipitationModel();
			precip.Generate(ball, final);
			var rivers = new RiverNetwork();
			// ★物理阈值：跨 res 语义一致（黄河流域量级）
			rivers.Generate(ball, final, composer, annualPrecipMm: precip.AnnualMm,
				riverThresholdAreaKm2: 70_800.0);
			var graph = new RiverGraph();
			graph.Generate(ball, final, rivers);
			var geom = new RiverGeometry();
			geom.Generate(ball, graph, rivers);
			var basins = new BasinGraph();
			basins.Generate(ball, final, rivers);
			// D-16：LakeState 消费**原始地形**的内流洼地分区（不是路由表面上的 basins）
			var rawHydro = new H3Hydrology();
			rawHydro.Generate(ball, composer.HeightM, 0f, precip.AnnualMm);
			var depBasins = new BasinGraph();
			depBasins.Generate(ball, final, rawHydro.Downstream, rawHydro.WeightedAccum);
			var lakes = new LakeState();
			lakes.Generate(ball, final, composer.HeightM, depBasins, precip.AnnualMm);

			int land = 0;
			for (int i = 0; i < final.FinalLand.Length; i++) if (final.FinalLand[i]) land++;
			landFrac.Add(100.0 * land / ball.CellDirs.Length);
			cells.Add(ball.CellIds.Length);

			basinMax.Add(basins.AreaKm2.Length > 0 ? basins.AreaKm2.Max() : 0);
			var sorted = basins.AreaKm2.OrderBy(x => x).ToArray();
			basinMed.Add(sorted.Length > 0 ? sorted[sorted.Length / 2] : 0);
			basinN.Add(basins.BasinCount);
			basinN[^1] = basins.BasinCount;
			endoFrac.Add(basins.BasinCount > 0 ? 100.0 * basins.EndorheicBasinCount / basins.BasinCount : 0);

			lakeN.Add(lakes.LakeCount);
			lakeArea.Add(lakes.AreaKm2.Sum());
			lakeVol.Add(lakes.VolumeKm3.Sum());

			// 河流总长度：按 RiverGeometry 的折线累加**真实大圆距离**（不是 hops × 常数）
			double len = 0;
			foreach (var line in geom.Lines)
				for (int k = 1; k < line.Length; k++)
					len += SpatialScale.DistanceKm(line[k - 1], line[k]);
			riverLen.Add(len);
			int riverCells = 0;
			for (int i = 0; i < rivers.IsRiver.Length; i++) if (rivers.IsRiver[i]) riverCells++;
			riverN.Add(riverCells);
			volcanoN.Add(volcanoes.Volcanoes.Count);
		}

		void Row(string name, List<double> v, string fmt = "N0")
			=> Console.WriteLine($"| {name} | " + string.Join(" | ", v.Select(x => x.ToString(fmt))) + " |");

		Row("格数", cells);
		Row("陆地占比 %", landFrac, "N2");
		Row("流域数", basinN);
		Row("最大流域 km²", basinMax);
		Row("中位流域 km²", basinMed);
		Row("内流流域占比 %", endoFrac, "N2");
		Row("河流格数", riverN);
		Row("河流总长 km", riverLen);
		Row("湖数", lakeN);
		Row("湖泊总面积 km²", lakeArea);
		Row("湖泊总体积 km³", lakeVol, "N2");
		Row("火山数", volcanoN);
	}

	// H3 尺度探针（2026-10-04 收口 D-10）：判定"格面积"到底该信谁。
	// 疑点：LakeState 硬编码表 res1=607,221 km²，但 4πR²/842 = 605,777 ⇒ 差 0.24%（可接受）；
	// 而 H3 官方 getHexagonAreaAvgKm2 文档值（res2=86,745）与"覆盖全球"的口径差约 7 倍。
	// 到底是①表错位一档 ②官方 API 口径不同 ③本项目 native 是修改版？—— 用实测判决。
	static void Probe()
	{
		const double rKm = SphericalFbmNoise.EarthRadiusKm;
		double total = 4.0 * Math.PI * rKm * rKm;
		Console.WriteLine($"地球总表面积 4πR² = {total:N0} km²  (R={rKm})");
		Console.WriteLine();
		Console.WriteLine("| res | GetNumCells | 官方 HexagonAreaAvg | 官方 EdgeKm | 4πR²/n | n×官方面积 |");
		Console.WriteLine("|---|---|---|---|---|---|");
		for (int res = 0; res <= 5; res++)
		{
			long n = H3.GetNumCells(res);
			double area = H3.GetHexagonAreaAvgKm2(res);
			double edge = H3.GetHexagonEdgeLengthAvgKm(res);
			Console.WriteLine($"| {res} | {n:N0} | {area:N0} | {edge:N1} | {total / n:N0} | {n * area / 1e6:N0} M km² |");
		}

		Console.WriteLine();
		for (int res = 0; res <= 3; res++)
		{
			ulong[] cells = H3.GetRes0Cells();
			ulong[] one = H3.CellToChildren(cells[0], res);
			double sum = 0;
			foreach (var c in one) sum += H3.CellAreaKm2(c);
			Console.WriteLine($"[res{res}] 取 res0 的**一个**胞 {cells[0]:X} 的全部子孙：{one.Length:N0} 格，" +
				$"逐格 H3.CellAreaKm2 求和 = {sum:N0} km²（= 1/122 球面应 ≈ {total / 122:N0}）");
			if (res == 0) Console.WriteLine($"        GetRes0Cells().Length = {cells.Length}，五边形数 = {cells.Count(H3.IsPentagon)}");
		}

		// 距离口径自检：格心方向 → 大圆距离，与 H3 官方 edge/area 推出的期望值比
		Console.WriteLine();
		for (int res = 1; res <= 3; res++)
		{
			var ball = new Ball(res, 1f);
			var sc = SpatialScale.Of(ball);
			double edge = H3.GetHexagonEdgeLengthAvgKm(res);
			double expectNeighbour = Math.Sqrt(3.0) * edge;   // 六边形中心距 = √3·边长
			Console.WriteLine($"[res{res}] 格数={ball.CellIds.Length:N0}  官方边长={edge:N1} km  " +
				$"⇒ 期望邻格中心距≈{expectNeighbour:N0} km  等积格边长={sc.CellEdgeKm:N1} km  " +
				$"⇒ 期望邻格中心距≈{Math.Sqrt(3) * sc.CellEdgeKm:N0} km");
			Console.WriteLine($"        格0={ball.CellIds[0]:X} dirs=({ball.CellDirs[0].X:F4},{ball.CellDirs[0].Y:F4},{ball.CellDirs[0].Z:F4})");
			Console.WriteLine($"        格1={ball.CellIds[1]:X} dirs=({ball.CellDirs[1].X:F4},{ball.CellDirs[1].Y:F4},{ball.CellDirs[1].Z:F4})");
			Console.WriteLine($"        真实距离(CellDirs 0→1) = {SpatialScale.DistanceKm(ball.CellDirs[0], ball.CellDirs[1]):N1} km" +
				$"   |  H3.CellToLatLng 口径 = {SpatialScale.DistanceKm(ball.CellIds[0], ball.CellIds[1]):N1} km" +
				$"   |  是否邻居 = {(ball.CellNeighbors[0].Contains(1) ? "是" : "否")}");
			// 逐邻居实测最差/最好
			double worst = 0, best = double.MaxValue;
			for (int i = 0; i < Math.Min(500, ball.CellIds.Length); i++)				foreach (int j in ball.CellNeighbors[i])
				{
					double real = SpatialScale.DistanceKm(ball.CellIds[i], ball.CellIds[j]);
					best = Math.Min(best, real);
					worst = Math.Max(worst, Math.Abs(real - expectNeighbour) / expectNeighbour);
				}
			Console.WriteLine($"        逐邻居实测：最短 {best:N0} km，对期望值的最大偏差 {worst:P1}");
		}
	}

	static void Run(int res, List<(int, string, double)> rows,
		List<(int res, long cells, long managedMb, long peakWorkingSetMb)> mem)
	{
		var swAll = Stopwatch.StartNew();
		var proc = Process.GetCurrentProcess();

		var ball = Stage(rows, res, "Ball 构造", () => new Ball(res, 1f));
		Console.Error.WriteLine($"[bench] res={res} cells={ball.CellIds.Length}");

		var layout = Stage(rows, res, "ContinentLayout", () => new ContinentLayout(Seed, ContinentCount));
		var field = Stage(rows, res, "LandSeaField", () => new LandSeaField(layout, new LandSeaParams { Seed = Seed }));

		var proj = new H3LandSeaProjector();
		Stage(rows, res, "H3LandSeaProjector", () => proj.Generate(ball, field, LandFraction));
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);

		var regions = new GeologicalRegions(Seed);
		Stage(rows, res, "GeologicalRegions", () => regions.Generate(ball, proj, TargetRegionAreaKm2));

		var mountains = new MountainSkeleton(Seed);
		Stage(rows, res, "MountainSkeleton", () => mountains.Generate(ball, regions, surface));

		var landforms = new RegionalLandforms(Seed);
		Stage(rows, res, "RegionalLandforms", () => landforms.Generate(ball, regions));

		var volcanoes = new VolcanoField();
		Stage(rows, res, "VolcanoField", () =>
			volcanoes.Place(ball, regions, mountains.Tectonic, Seed, mountains.RangeAnchors()));

		var features = new List<FeatureField>
		{
			new(landforms, TerrainDomain.LandOnly),
			new(mountains, TerrainDomain.LandAndSea),
			new(volcanoes, TerrainDomain.LandAndSea),
		};
		var composer = new HeightComposer(Seed);
		Stage(rows, res, "HeightComposer", () => composer.Generate(ball, surface, regions, features));

		var final = new FinalGeography();
		Stage(rows, res, "FinalGeography", () => final.Generate(ball, composer, regions));

		var precip = new PrecipitationModel();
		Stage(rows, res, "PrecipitationModel", () => precip.Generate(ball, final));

		var rivers = new RiverNetwork();
		Stage(rows, res, "RiverNetwork", () =>
			rivers.Generate(ball, final, composer, RiverThresholdCells, precip.AnnualMm));

		var graph = new RiverGraph();
		Stage(rows, res, "RiverGraph", () => graph.Generate(ball, final, rivers));

		var geom = new RiverGeometry();
		Stage(rows, res, "RiverGeometry", () => geom.Generate(ball, graph, rivers));

		var basins = new BasinGraph();
		Stage(rows, res, "BasinGraph", () => basins.Generate(ball, final, rivers));

		var rawHydroP = new H3Hydrology();
		rawHydroP.Generate(ball, composer.HeightM, 0f, precip.AnnualMm);
		var depBasinsP = new BasinGraph();
		depBasinsP.Generate(ball, final, rawHydroP.Downstream, rawHydroP.WeightedAccum);
		var lakes = new LakeState();
		Stage(rows, res, "LakeState", () => lakes.Generate(ball, final, composer.HeightM, depBasinsP, precip.AnnualMm));

		var water = new WaterTopology();
		Stage(rows, res, "WaterTopology", () => water.Generate(ball, final, rivers, graph, basins, lakes));

		Stage(rows, res, "FinalSpatialIndex", () =>
		{
			var mountainAnchors = new List<Vector3>();
			foreach (var sys in mountains.Systems) mountainAnchors.Add(sys.Anchor);
			var volcanoAnchors = volcanoes.Volcanoes.ConvertAll(v => v.Anchor);
			var riverCells = new List<int>();
			for (int i = 0; i < rivers.IsRiver.Length; i++)
				if (rivers.IsRiver[i]) riverCells.Add(i);
			var index = new FinalSpatialIndex(ball, mountainAnchors, volcanoAnchors);
			index.Generate(final, riverCells);
			return index;
		});

		rows.Add((res, "**全链合计**", swAll.Elapsed.TotalMilliseconds));

		// 内存：强制一次完整 GC 后的托管堆 + 进程峰值工作集（后者是累计峰值，跨档位单调递增）
		GC.Collect();
		GC.WaitForPendingFinalizers();
		GC.Collect();
		proc.Refresh();
		mem.Add((res, ball.CellIds.Length, GC.GetTotalMemory(true) / (1024 * 1024),
			proc.PeakWorkingSet64 / (1024 * 1024)));

		// 判读读数（与 [WORLDGEN-TIMING] 口径一致，便于和游戏内日志对表）
		Console.Error.WriteLine($"[bench] res={res} regions={regions.Regions.Length} " +
			$"ridges={mountains.Ridges.Length} basins={basins.BasinCount}" +
			$"(endorheic {basins.EndorheicBasinCount}) lakes={lakes.LakeCount} " +
			$"waterNodes={water.NodeCount} thr={proj.ThresholdUsed:F3}");
	}

	static T Stage<T>(List<(int, string, double)> rows, int res, string name, Func<T> f)
	{
		var sw = Stopwatch.StartNew();
		T r = f();
		rows.Add((res, name, sw.Elapsed.TotalMilliseconds));
		return r;
	}

	static void Stage(List<(int, string, double)> rows, int res, string name, Action f)
		=> Stage(rows, res, name, () => { f(); return 0; });

	static void Print(List<(int res, string stage, double ms)> rows,
		List<(int res, long cells, long managedMb, long peakWorkingSetMb)> mem)
	{
		var stages = rows.Select(r => r.stage).Distinct().ToList();
		var resList = rows.Select(r => r.res).Distinct().OrderBy(x => x).ToList();

		Console.WriteLine();
		Console.WriteLine("| 阶段 | " + string.Join(" | ", resList.Select(r => $"res{r} (ms)")) + " |");
		Console.WriteLine("|---" + string.Join("|", resList.Select(_ => "---")) + "|---");
		foreach (var s in stages)
		{
			var cells = resList.Select(r =>
			{
				var hit = rows.FirstOrDefault(x => x.res == r && x.stage == s);
				return hit.stage == null ? "—" : $"{hit.ms:F0}";
			});
			Console.WriteLine($"| {s} | " + string.Join(" | ", cells) + " |");
		}

		Console.WriteLine();
		Console.WriteLine("| 档位 | 格数 | 托管堆 (MB) | 峰值工作集 (MB) |");
		Console.WriteLine("|---|---|---|---|");
		foreach (var m in mem)
			Console.WriteLine($"| res{m.res} | {m.cells} | {m.managedMb} | {m.peakWorkingSetMb} |");
	}

	// ══════════════════════════════════════════════════════════════════════════
	// D-11 诊断探针（2026-10-04，用户指定逐层诊断链）
	//
	//   降水/径流场空间相关长度 → res4 采样密度 → 汇流图拓扑 → FlowAccum → 河网形成
	//
	// ★纪律（用户拍板，不得违反）：
	//   · **不碰** 70,800 km² 阈值
	//   · **不碰** ProductionRes = 4
	//   · 本探针**只读**，不写文件、不改任何生成逻辑、不触碰 GD.*/LogService
	//   要问的是"在 res4 采样尺度下水文模型如何保持空间连续的水文结构"，
	//   **不是**"怎么让 res4 多出一些河流"。
	//
	// 用法：dotnet run --project tests/PerfBench -c Release -- --d11 1 2 3 4
	// ══════════════════════════════════════════════════════════════════════════
	/// <summary>
	/// 第 6 层（判据语义矩阵）的逐档结果——跨 res 汇总表在循环结束后统一打印。
	/// ★纯诊断数据结构：PerfBench 私有，生产代码零改动。
	/// </summary>
	sealed class CriteriaRow
	{
		public int Res;
		public int LandCells;
		public double CellKm2;
		/// <summary>五种判据的通过率（占陆格 %）。</summary>
		public double C1, C2, C4, C5;
		/// <summary>C3（纯累积径流量）在各**绝对量级**阈值下的通过率（km³/a → %）。</summary>
		public Dictionary<double, double> C3 = new();
		/// <summary>阈值弹性 |ε| = |dln(通过率)/dln(阈值)|；越大 = 分布越陡 = 越敏感。</summary>
		public double E1, E2, E4, E5;
		public Dictionary<double, double> E3 = new();
		/// <summary>等效阈值反解：固定通过率下所需的阈值（km² / km³）。</summary>
		public Dictionary<double, (double areaKm2, double volKm3)> Inverse = new();
		/// <summary>ln(面积) 与 ln(水量) 的 Pearson 相关（陆格）。</summary>
		public double AreaVolCorr;
		/// <summary>C2 中不满足 C1 的比例（"汇水够大但水不够"），以及反向（"水够但面积不够"）。</summary>
		public double BigButDry, WetButSmall;
		/// <summary>现行判据 C1 的阈值换算成 km³/a（本 res）——看它落在 C3 扫描的哪一段。</summary>
		public double C1ThresholdKm3;
		/// <summary>C1′：把降水换成 **D-14 前口径**（海岸加权恒 1）后、同一生产路径下的通过率。
		/// 用途：把"D-14 到底改变了什么"测准——第 4 层是填洼 OFF，不能代表生产路径。</summary>
		public double C1Pre;
		/// <summary>绝对河流格数（C1 / C2）——6.6 维数检验用。</summary>
		public int C1Cells, C2Cells;
		/// <summary>本 res 的相邻格中心距（km）与陆域总面积（km²）。</summary>
		public double NeighbourKm, LandAreaKm2;
	}

	static void D11(string[] resArgs)
	{
		var resList = resArgs.Length > 0 ? resArgs.Select(int.Parse).ToArray() : new[] { 1, 2, 3, 4 };
		Console.WriteLine("世界生成空间 · D-11 逐层诊断（只读探针，不调参）");
		Console.WriteLine("固定：阈值 = 70,800 km²；生产档 = res4。本探针只做测量。");
		Console.WriteLine();

		// 第 6 层跨档汇总（D-11 第三层：判据语义诊断）
		var criteriaRows = new List<CriteriaRow>();
		// C3 的绝对量级扫描（km³/a）——**不是标定**，只是量级扫描，横跨 4 个数量级
		var volScan = new[] { 1.0, 3.0, 10.0, 30.0, 100.0, 300.0, 1000.0 };
		// 等效反解的目标通过率（占陆格 %）
		var inverseTargets = new[] { 1.0, 5.0, 10.0 };

		foreach (int res in resList)
		{
			Console.WriteLine(new string('═', 78));
			var ball = new Ball(res, 1f);
			var sc = SpatialScale.Of(ball);
			int n = ball.CellDirs.Length;

			var layout = new ContinentLayout(Seed, ContinentCount);
			var field = new LandSeaField(layout, new LandSeaParams { Seed = Seed });
			var proj = new H3LandSeaProjector();
			proj.Generate(ball, field, LandFraction);
			var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
			var regions = new GeologicalRegions(Seed);
			regions.Generate(ball, proj, TargetRegionAreaKm2);
			var mountains = new MountainSkeleton(Seed);
			mountains.Generate(ball, regions, surface);
			var landforms = new RegionalLandforms(Seed);
			landforms.Generate(ball, regions);
			var volcanoes = new VolcanoField();
			volcanoes.Place(ball, regions, mountains.Tectonic, Seed, mountains.RangeAnchors());
			var features = new List<FeatureField>
			{
				new(landforms, TerrainDomain.LandOnly),
				new(mountains, TerrainDomain.LandAndSea),
				new(volcanoes, TerrainDomain.LandAndSea),
			};
			var composer = new HeightComposer(Seed);
			composer.Generate(ball, surface, regions, features);
			var final = new FinalGeography();
			final.Generate(ball, composer, regions);
			var precip = new PrecipitationModel();
			precip.Generate(ball, final);
			// ★基线侧**显式关闭**填洼（生产默认已翻为 true，不显式关就测不到对照）
			var rivers = new RiverNetwork();
			rivers.Generate(ball, final, composer, annualPrecipMm: precip.AnnualMm,
				riverThresholdAreaKm2: 70_800.0, useDepressionFill: false);

			int landCells = 0;
			for (int i = 0; i < n; i++) if (final.FinalLand[i]) landCells++;

			double neighbourKm = Math.Sqrt(3.0) * sc.CellEdgeKm;   // 六边形中心距
			Console.WriteLine($"res{res}  格数={n:N0}  陆格={landCells:N0}  格边长={sc.CellEdgeKm:N2} km  " +
				$"格面积={sc.CellAreaKm2:N0} km²  邻格中心距≈{neighbourKm:N1} km");
			Console.WriteLine($"        阈值 70,800 km² = {70_800.0 / sc.CellAreaKm2:N1} 格（等效格数）");
			Console.WriteLine();

			// ── 第 1 层：场的空间相关长度 vs 采样密度 ──────────────────────────
			Console.WriteLine("【第 1 层】场空间相关长度 vs 采样密度");
			Console.WriteLine("  r(1hop) = 相邻格相关系数；L = 相关衰减到 1/e 的物理距离；");
			Console.WriteLine("  L/邻距 = 一个相关长度内约有几个格（<2 即**采样不足**）。");
			Console.WriteLine();
			Console.WriteLine("  | 场 | r(1hop) | L (km) | L/邻距 |");
			Console.WriteLine("  |---|---|---|---|");
			int maxHops = res switch { 1 => 6, 2 => 8, 3 => 12, _ => 16 };
			AutoCorr(ball, "高度场 HeightM（陆）", i => composer.HeightM[i],
				i => final.FinalLand[i], maxHops, neighbourKm);
			AutoCorr(ball, "降水场 AnnualMm（陆）", i => precip.AnnualMm[i],
				i => final.FinalLand[i], maxHops, neighbourKm);
			AutoCorr(ball, "径流场 RunoffAccum（陆）", i => rivers.RunoffAccum[i],
				i => final.FinalLand[i], maxHops, neighbourKm);
			Console.WriteLine();

			// ── 第 2 层：汇流图拓扑 ───────────────────────────────────────────
			Console.WriteLine("【第 2 层】汇流图拓扑（单流向 D8）");
			var plen = PathLengths(rivers.Downstream, n);
			double meanPath = 0, maxPath = 0; int direct = 0, landWithPath = 0;
			for (int i = 0; i < n; i++)
			{
				if (!final.FinalLand[i]) continue;
				meanPath += plen[i]; landWithPath++;
				maxPath = Math.Max(maxPath, plen[i]);
				if (plen[i] <= 1) direct++;
			}
			meanPath /= Math.Max(1, landWithPath);
			Console.WriteLine($"  陆格到出口的路径长度（跳）：均值 {meanPath:N2}  最大 {maxPath:N0}  " +
				$"**直排入海(≤1跳)占比 {100.0 * direct / Math.Max(1, landWithPath):N1}%**");
			Console.WriteLine($"  洼地/无出口陆格 SinkCount = {rivers.SinkCount:N0}  " +
				$"（占陆格 {100.0 * rivers.SinkCount / Math.Max(1, landCells):N2}%）");

			// ── 第 2 层补：陆地形态到底是不是"实心大陆"────────────────────────
			// 判据：若陆地是少数几个大连通块，则问题在水文；若碎成几千块或离岸距离
			// 全挤在 1~3 跳，则问题在**陆地形态**，修水文是找错了层。
			var (compCount, compMax, compTop5) = LandComponents(ball, final);
			Console.WriteLine($"  陆地连通块：{compCount:N0} 个  最大块 {compMax:N0} 格 " +
				$"（占陆格 {100.0 * compMax / Math.Max(1, landCells):N1}%）  " +
				$"前 5 大合计占 {100.0 * compTop5 / Math.Max(1, landCells):N1}%");
			var dll = new List<int>();
			for (int i = 0; i < n; i++) if (final.FinalLand[i]) dll.Add(final.FinalDistToCoast[i]);
			dll.Sort();
			double MedI(List<int> s) => s.Count > 0 ? s[s.Count / 2] : 0;
			int PctI(List<int> s, double p) => s.Count > 0 ? s[Math.Min(s.Count - 1, (int)(s.Count * p))] : 0;
			// ⚠️ 语义核对（2026-10-04）：`FinalDistToLand` 是 **BFS from 陆格**
			//   ⇒ 陆格恒 0（它回答"海格离陆地多远"）。陆格的**离岸距离**是
			//   `FinalDistToCoast`（BFS from 海格）。降水模型用的却是前者 ⇒ 见下。
			Console.WriteLine($"  内陆深度 FinalDistToCoast（跳）：中位 {MedI(dll)}  " +
				$"p90 {PctI(dll, 0.9)}  p99 {PctI(dll, 0.99)}  最大 {(dll.Count > 0 ? dll[^1] : 0)}");
			Console.WriteLine($"  ⇒ 路径均值 {meanPath:N2} 跳 vs 内陆深度中位 {MedI(dll)} 跳 / p99 {PctI(dll, 0.99)} 跳：" +
				$"若路径**远小于**内陆深度，说明水没走到海就中断了");

		// ── D-14 修复后的降水海岸加权自检 ─────────────────────────────────────
		// 修复前：读 FinalDistToLand（陆格恒 0）⇒ coastFactor 恒 1.0 ⇒ 降水 = 纯纬度函数。
		// 修复后应看到：coastFactor 在陆格上**有分布**，且临海 > 内陆。
		var cf = new List<float>();
		for (int i = 0; i < n; i++)
			if (final.FinalLand[i]) cf.Add(PrecipitationModel.CoastFactor(final.FinalDistToCoast[i], neighbourKm));
		cf.Sort();
		double MedF(List<float> s) => s.Count > 0 ? s[s.Count / 2] : 0;
		float cfMin = cf.Count > 0 ? cf[0] : 0, cfMax = cf.Count > 0 ? cf[^1] : 0;
		Console.WriteLine($"  ★降水海岸加权（D-14 修复后）：陆格 coastFactor " +
			$"最小 {cfMin:N3}  中位 {MedF(cf):N3}  最大 {cfMax:N3}" +
			$"  ⇒ {(cfMax - cfMin < 0.01f ? "⚠️**仍然失效**（权重恒为常数）" : "✅生效（权重有分布）")}");
		Console.WriteLine($"  衰减长度 {PrecipitationModel.CoastDecayKm:N0} km = " +
			$"{PrecipitationModel.CoastDecayKm / neighbourKm:N2} 跳（本 res）" +
			$"；若按**旧的 hop 口径**写死 10 跳 ⇒ {10 * neighbourKm:N0} km（跨 res 漂移对照：" +
			$"res1≈8,364 / res4≈452 km，差 18× ⇒ 已改为 km 口径）");

			// ── 第 2 层核心：地形在格尺度上到底有没有"汇聚结构" ────────────────
			// 判据：正常斜坡地形上，一个陆格应有 ~3 个更低的邻居（6 邻居的一半），
			// 且入度分布应重尾（少数谷线格入度大）⇒ 汇流成树。
			// 若"入度=0"占一半且"更低邻居数"很小 ⇒ 地形在格尺度上**不汇聚**。
			var lowN = new int[7];
			var indeg = new int[n];   // 逐格入度，故长度 = 格数（不是 7）
			for (int i = 0; i < n; i++)
			{
				if (!final.FinalLand[i]) continue;
				if (rivers.Downstream[i] >= 0) indeg[rivers.Downstream[i]]++;
				int c = 0;
				foreach (int j in ball.CellNeighbors[i])
					if (composer.HeightM[j] < composer.HeightM[i]) c++;
				lowN[Math.Min(6, c)]++;
			}
			int landTot = Math.Max(1, landCells);
			Console.WriteLine($"  更低邻居数分布（0/1/2/3/4+/6）：" +
				string.Join("  ", Enumerable.Range(0, 7).Select(k =>
					$"{k}:{100.0 * lowN[k] / landTot:N1}%")));
			var ind = new int[8];
			for (int i = 0; i < n; i++)
				if (final.FinalLand[i]) ind[Math.Min(7, indeg[i])]++;
			Console.WriteLine($"  入度分布（上游汇入数 0/1/2/3/4/5/6/7+）：" +
				string.Join("  ", Enumerable.Range(0, 8).Select(k =>
					$"{k}{(k == 7 ? "+" : "")}:{100.0 * ind[k] / landTot:N1}%")));
			Console.WriteLine();

			// ── 第 3 层：FlowAccum 分布 ───────────────────────────────────────
			Console.WriteLine("【第 3 层】FlowAccum 分布（汇水面积 km²）");
			var landAccum = new List<double>();
			for (int i = 0; i < n; i++)
				if (final.FinalLand[i]) landAccum.Add(rivers.FlowAccumKm2[i]);
			landAccum.Sort();
			double Med(List<double> s) => s.Count > 0 ? s[s.Count / 2] : 0;
			double Pct(List<double> s, double p) => s.Count > 0 ? s[Math.Min(s.Count - 1, (int)(s.Count * p))] : 0;
			int overThr = landAccum.Count(a => a >= 70_800.0);
			Console.WriteLine($"  中位 {Med(landAccum):N0}  p90 {Pct(landAccum, 0.9):N0}  " +
				$"p99 {Pct(landAccum, 0.99):N0}  最大 {(landAccum.Count > 0 ? landAccum[^1] : 0):N0}");
			Console.WriteLine($"  达到 70,800 km² 的陆格：{overThr:N0} / {landCells:N0} = " +
				$"{100.0 * overThr / Math.Max(1, landCells):N3}%");
			Console.WriteLine($"  单格流域（FlowAccum==1）占陆格：" +
				$"{100.0 * landAccum.Count(a => a <= sc.CellAreaKm2 * 1.5) / Math.Max(1, landCells):N1}%");
			Console.WriteLine();

			// ── 第 4 层：河网形成 ─────────────────────────────────────────────
			Console.WriteLine("【第 4 层】河网形成");
			int riverCells = 0;
			for (int i = 0; i < n; i++) if (rivers.IsRiver[i]) riverCells++;
			Console.WriteLine($"  河流格 {riverCells:N0}  占陆格 {100.0 * riverCells / Math.Max(1, landCells):N3}%  " +
				$"占全格 {100.0 * riverCells / n:N3}%");
			var graph = new RiverGraph();
			graph.Generate(ball, final, rivers);
			var basins = new BasinGraph();
			basins.Generate(ball, final, rivers);
			// D-16：LakeState 消费**原始地形**的内流洼地分区（不是路由表面上的 basins）
			var rawHydro = new H3Hydrology();
			rawHydro.Generate(ball, composer.HeightM, 0f, precip.AnnualMm);
			var depBasins = new BasinGraph();
			depBasins.Generate(ball, final, rawHydro.Downstream, rawHydro.WeightedAccum);
			var lakes = new LakeState();
			lakes.Generate(ball, final, composer.HeightM, depBasins, precip.AnnualMm);
			Console.WriteLine($"  流域数 {basins.BasinCount:N0}  内流 {basins.EndorheicBasinCount:N0}  " +
				$"最大流域 {(basins.AreaKm2.Length > 0 ? basins.AreaKm2.Max() : 0):N0} km²  " +
				$"湖数 {lakes.LakeCount:N0}");
			Console.WriteLine();

			double maxH = 0;
			for (int i = 0; i < n; i++) if (composer.HeightM[i] > maxH) maxH = composer.HeightM[i];

			// ── 第 5 层：A/B —— 关/开「水文路由填洼」（走**生产路径** `RiverNetwork.Routing`）──
			// ★Batch A 已落地：填洼是 `HydrologyRoutingSurface` 派生的**路由表面**，
			//   Final 地形（composer.HeightM）**不动**；开关关闭 ⇒ 与旧行为逐点相同。
			//   目的：验证"汇流树浅是洼地截断造成的"，并给出是否翻生产默认的依据。
			// 走**完整的生产链路**（不是只跑 H3Hydrology）：RiverNetwork → RiverGraph
			// → BasinGraph → LakeState，这样下游副作用（内流湖语义）才测得到。
			var riversOn = new RiverNetwork();
			riversOn.Generate(ball, final, composer, annualPrecipMm: precip.AnnualMm,
				riverThresholdAreaKm2: 70_800.0, useDepressionFill: true);
			var routing = riversOn.Routing;
			var p2 = PathLengths(riversOn.Downstream, n);
			double mp2 = 0; int l2 = 0;
			for (int i = 0; i < n; i++) if (final.FinalLand[i]) { mp2 += p2[i]; l2++; }
			mp2 /= Math.Max(1, l2);
			var acc2 = new List<double>();
			for (int i = 0; i < n; i++)
				if (final.FinalLand[i]) acc2.Add(riversOn.FlowAccumKm2[i]);
			acc2.Sort();
			int over2 = acc2.Count(a => a >= 70_800.0);

			var basins2 = new BasinGraph();
			basins2.Generate(ball, final, riversOn);
			var lakes2 = new LakeState();
			lakes2.Generate(ball, final, composer.HeightM, depBasins, precip.AnnualMm);
			Console.WriteLine("【第 5 层·A/B】水文路由填洼：关 → 开（走生产路径 `RiverNetwork.Routing`）");
			Console.WriteLine($"  ★原始洼地事实 DepressionCount = {routing.DepressionCount:N0}（与开关无关，不被抹掉）");
			Console.WriteLine($"  填洼格数 {routing.FilledCellCount:N0}  最大填深 {routing.MaxFillDepthM:N1} m " +
				$"（地形量程 {maxH:N0} m ⇒ 占比 {100.0 * routing.MaxFillDepthM / Math.Max(1, maxH):N2}%）");
			Console.WriteLine($"  路由侧洼地 SinkCount：{rivers.SinkCount:N0} → {riversOn.SinkCount:N0}");
			// ⚠️ 副作用 1：**内流湖语义**。填洼后内流流域归零 ⇒ LakeState 的内流湖会消失。
			//   这正是"不要让填洼抹掉内流语义"的量化证据，必须盯着。
			Console.WriteLine($"  流域数 {basins.BasinCount:N0} → {basins2.BasinCount:N0}   " +
				$"**内流 {basins.EndorheicBasinCount:N0} → {basins2.EndorheicBasinCount:N0}**  " +
				$"最大流域 {(basins.AreaKm2.Length > 0 ? basins.AreaKm2.Max() : 0):N0} → " +
				$"{(basins2.AreaKm2.Length > 0 ? basins2.AreaKm2.Max() : 0):N0} km²");
			Console.WriteLine($"  湖数 {lakes.LakeCount:N0} → {lakes2.LakeCount:N0}   " +
				$"湖面积 {lakes.AreaKm2.Sum():N0} → {lakes2.AreaKm2.Sum():N0} km²   " +
				$"湖体积 {lakes.VolumeKm3.Sum():N2} → {lakes2.VolumeKm3.Sum():N2} km³");
			// ⚠️ 副作用 2：**填深分布**。若存在深填（数百米以上），说明被填的不只是
			//   "微型/数值洼地"，可能包含真实内流盆地——这是用户明确警告不要抹掉的东西。
			int f10 = 0, f100 = 0, f500 = 0;
			for (int i = 0; i < n; i++)
			{
				float d = routing.FillDepthM[i];
				if (d > 10f) f10++;
				if (d > 100f) f100++;
				if (d > 500f) f500++;
			}
			Console.WriteLine($"  **填深分布**：>10m {f10:N0} 格  >100m {f100:N0} 格  >500m {f500:N0} 格" +
				$"（占陆格 {100.0 * f10 / Math.Max(1, landCells):N2}% / " +
				$"{100.0 * f100 / Math.Max(1, landCells):N2}% / {100.0 * f500 / Math.Max(1, landCells):N2}%）");
			Console.WriteLine($"  汇流路径（跳）：{meanPath:N2} → {mp2:N2}   最大 {maxPath:N0} → {p2.Max():N0}");
			Console.WriteLine($"  FlowAccum 中位：{(landAccum.Count > 0 ? landAccum[landAccum.Count / 2] : 0):N0} → " +
				$"{(acc2.Count > 0 ? acc2[acc2.Count / 2] : 0):N0} km²   " +
				$"p99 {Pct(landAccum, 0.99):N0} → {(acc2.Count > 0 ? acc2[(int)(acc2.Count * 0.99)] : 0):N0} km²");
			// ⚠️ 口径声明：这里比的是**纯汇水面积口径**（与第 3 层 overThr 同口径），
			//   不是第 4 层的 `IsRiver`（那个是降水加权 RunoffAccum 口径）。
			//   两者不能并列——res2 上 IsRiver 只有 81.8% 而汇水面积口径已达 100%，
			//   差别来自"降水纬度带"，与汇流无关。
			Console.WriteLine($"  达阈值陆格（**纯汇水面积口径**，同第 3 层）：" +
				$"{100.0 * overThr / Math.Max(1, landCells):N3}% → " +
				$"{100.0 * over2 / Math.Max(1, landCells):N3}%");
			Console.WriteLine();

			// ── 第 6 层：判据语义矩阵（D-11 **第三层**；★纯诊断，生产参数一个不动）──
			// 用户拍板（2026-10-04）：前两层已修（洼地路由 / 湖泊语义 / 降水字段），
			//   残留的 2.43× 很可能意味着 **IsRiver 的定义本身**还没跨尺度稳定。
			//   本层**不改任何生产参数**（70,800 km² 不动、CoastDecayKm 不动、
			//   DefaultUseDepressionFill 不动、ProductionRes 不动），只并行比较五种"河流是什么"的定义：
			//     C1 现行      = RunoffAccum ≥ 全球均雨 × 阈值格数      （阈值锚全球均雨）
			//     C2 纯面积    = FlowAccumKm2 ≥ 70,800
			//     C3 纯水量    = RunoffAccumKm3 ≥ T km³/a                （真正的物理量）
			//     C4 面积∧水量 = C2 ∧ C1（零新参数，直接用两个既有判据的交）
			//     C5 局地归一  = RunoffAccum ≥ **当地**降水 × 阈值格数   （对照：验证"当地降水该参与阈值"吗）
			//   评估面 = **填洼 ON**（生产路径 `riversOn`）的陆格。
			Console.WriteLine("【第 6 层】判据语义矩阵（★只读诊断；生产参数一个不动）");
			Console.WriteLine("  生产条件冻结：ProductionRes=4 / DefaultUseDepressionFill=true / " +
				$"CoastDecayKm={PrecipitationModel.CoastDecayKm:N0} / 70,800 km² 不动 / D-14 不动");
			Console.WriteLine("  评估面 = **填洼 ON**（生产路径）上的陆格。");

			var landIdx = new List<int>();
			for (int i = 0; i < n; i++) if (final.FinalLand[i]) landIdx.Add(i);
			int nl = landIdx.Count;
			var areaKm2 = new double[nl];
			var volKm3 = new double[nl];
			var runMm = new double[nl];
			var locP = new double[nl];
			for (int k = 0; k < nl; k++)
			{
				int i = landIdx[k];
				areaKm2[k] = riversOn.FlowAccumKm2[i];
				volKm3[k] = riversOn.RunoffAccumKm3[i];
				runMm[k] = riversOn.RunoffAccum[i];
				locP[k] = precip.AnnualMm[i];
			}

			double meanP = 0;
			for (int i = 0; i < n; i++) meanP += precip.AnnualMm[i];
			meanP /= Math.Max(1, n);
			double thrCellsEq = 70_800.0 / sc.CellAreaKm2;
			double thrMm = meanP * thrCellsEq;                 // C1 阈值（mm）
			double c1Km3 = thrMm * sc.CellAreaKm2 / 1e6;       // C1 阈值换算成 km³/a（本 res）

			// 通过率（占陆格 %）
			double Rate(Func<int, bool> ok)
			{
				int c = 0;
				foreach (int i in landIdx) if (ok(i)) c++;
				return 100.0 * c / Math.Max(1, nl);
			}
			// 阈值弹性：ε = dln(通过率)/dln(阈值)，用 ±10% 数值求导
			double Elast(Func<double, double> rateAt, double t)
			{
				double r0 = rateAt(t), up = rateAt(t * 1.1), dn = rateAt(t / 1.1);
				if (r0 <= 0 || up <= 0 || dn <= 0) return double.NaN;
				return Math.Log(up / dn) / (2.0 * Math.Log(1.1));
			}
			// 等效阈值反解：使通过率 = p% 所需的阈值（数组升序后自顶取分位）
			double InverseThr(double[] v, double pct)
			{
				if (v.Length == 0) return double.NaN;
				var s = (double[])v.Clone();
				Array.Sort(s);
				int idx = s.Length - (int)Math.Round(pct / 100.0 * s.Length);
				return s[Math.Clamp(idx, 0, s.Length - 1)];
			}

			bool OkC1(int i) => riversOn.RunoffAccum[i] >= thrMm;
			bool OkC2(int i) => riversOn.FlowAccumKm2[i] >= 70_800.0;
			bool OkC4(int i) => OkC1(i) && OkC2(i);
			bool OkC5(int i) => riversOn.RunoffAccum[i] >= precip.AnnualMm[i] * thrCellsEq;

			var row = new CriteriaRow
			{
				Res = res, LandCells = nl, CellKm2 = sc.CellAreaKm2,
				C1 = Rate(OkC1), C2 = Rate(OkC2), C4 = Rate(OkC4), C5 = Rate(OkC5),
				C1ThresholdKm3 = c1Km3,
			};
			foreach (double t in volScan)
			{
				row.C3[t] = Rate(i => riversOn.RunoffAccumKm3[i] >= t);
				row.E3[t] = Elast(x => Rate(i => riversOn.RunoffAccumKm3[i] >= x), t);
			}
			row.E1 = Elast(x => Rate(i => riversOn.RunoffAccum[i] >= x), thrMm);
			row.E2 = Elast(x => Rate(i => riversOn.FlowAccumKm2[i] >= x), 70_800.0);
			// C4 是双条件，弹性按"两个阈值同时缩放"测（面积阈值 ×x、mm 阈值 ×x）
			row.E4 = Elast(x => Rate(i => riversOn.FlowAccumKm2[i] >= 70_800.0 * x
									&& riversOn.RunoffAccum[i] >= thrMm * x), 1.0);
			row.E5 = Elast(x => Rate(i => riversOn.RunoffAccum[i] >= precip.AnnualMm[i] * thrCellsEq * x), 1.0);
			foreach (double p in inverseTargets)
				row.Inverse[p] = (InverseThr(areaKm2, p), InverseThr(volKm3, p));

			// ln(面积) 与 ln(水量) 的相关（"物理量之间是不是同一件事"）
			{
				double sa = 0, sv = 0;
				for (int k = 0; k < nl; k++) { sa += Math.Log(areaKm2[k] + 1); sv += Math.Log(volKm3[k] + 1e-9); }
				sa /= Math.Max(1, nl); sv /= Math.Max(1, nl);
				double cov = 0, va = 0, vv = 0;
				for (int k = 0; k < nl; k++)
				{
					double da = Math.Log(areaKm2[k] + 1) - sa, dv = Math.Log(volKm3[k] + 1e-9) - sv;
					cov += da * dv; va += da * da; vv += dv * dv;
				}
				row.AreaVolCorr = (va > 0 && vv > 0) ? cov / Math.Sqrt(va * vv) : double.NaN;
			}
			{
				int bigDry = 0, wetSmall = 0, c2n = 0, c1n = 0;
				foreach (int i in landIdx)
				{
					bool a = OkC2(i), w = OkC1(i);
					if (a) { c2n++; if (!w) bigDry++; }
					if (w) { c1n++; if (!a) wetSmall++; }
				}
				row.BigButDry = 100.0 * bigDry / Math.Max(1, c2n);
				row.WetButSmall = 100.0 * wetSmall / Math.Max(1, c1n);
			}
			// ── C1′（D-14 前口径对照）───────────────────────────────────────────
			// 第 4 层是**填洼 OFF**，它给出的"D-14 前后 13.14×→16.14×"不能代表生产路径。
			// 这里在**同一条生产路径（填洼 ON）**上，只把降水场换成 D-14 前的形态
			// （海岸加权恒 1.0 = 纯纬度带），从而把"D-14 到底改变了什么"测准。
			// ★纯诊断：PerfBench 本地构造，生产代码零改动。
			{
				var flat = new float[n];
				for (int i = 0; i < n; i++)
				{
					float latDeg = MathF.Abs(MathF.Asin(Math.Clamp(ball.CellDirs[i].Y, -1f, 1f)) * 180f / MathF.PI);
					flat[i] = PrecipitationModel.MaxMm * PrecipitationModel.LatFactor(latDeg);
				}
				var riversFlat = new RiverNetwork();
				riversFlat.Generate(ball, final, composer, annualPrecipMm: flat,
					riverThresholdAreaKm2: 70_800.0, useDepressionFill: true);
				int c = 0;
				foreach (int i in landIdx) if (riversFlat.IsRiver[i]) c++;
				row.C1Pre = 100.0 * c / Math.Max(1, nl);
			}

			row.C1Cells = (int)Math.Round(row.C1 / 100.0 * nl);
			row.C2Cells = (int)Math.Round(row.C2 / 100.0 * nl);
			row.NeighbourKm = neighbourKm;
			row.LandAreaKm2 = nl * sc.CellAreaKm2;

			criteriaRows.Add(row);

			// ── 逐档明细：分布形状 ────────────────────────────────────────────
			var sa2 = (double[])areaKm2.Clone(); Array.Sort(sa2);
			var sv2 = (double[])volKm3.Clone(); Array.Sort(sv2);
			double Q(double[] s, double p) => s.Length > 0 ? s[Math.Min(s.Length - 1, (int)(s.Length * p))] : 0;
			Console.WriteLine("  ── 6.1 判据量的分布（陆格）");
			Console.WriteLine("  | 量 | p50 | p90 | p99 | p99.9 | max |");
			Console.WriteLine("  |---|---|---|---|---|---|");
			Console.WriteLine($"  | 汇水面积 FlowAccumKm2 (km²) | {Q(sa2, 0.5):N0} | {Q(sa2, 0.9):N0} | " +
				$"{Q(sa2, 0.99):N0} | {Q(sa2, 0.999):N0} | {(sa2.Length > 0 ? sa2[^1] : 0):N0} |");
			Console.WriteLine($"  | 累积水量 RunoffAccumKm3 (km³/a) | {Q(sv2, 0.5):N3} | {Q(sv2, 0.9):N3} | " +
				$"{Q(sv2, 0.99):N3} | {Q(sv2, 0.999):N3} | {(sv2.Length > 0 ? sv2[^1] : 0):N3} |");
			Console.WriteLine($"  ★ln(面积)~ln(水量) 相关 r = {row.AreaVolCorr:N3}" +
				$"（≈1 ⇒ 两者是同一件事的两套单位；明显 <1 ⇒ 水量带独立信息）");
			Console.WriteLine($"  现行 C1 阈值 = {thrMm:N0} mm = **{c1Km3:N2} km³/a**（本 res）" +
				$"；全球均雨 {meanP:N0} mm");
			Console.WriteLine($"  交叉：汇水够大但水不够（C2∧¬C1）占 C2 的 {row.BigButDry:N1}%；" +
				$"水够但汇水不够（C1∧¬C2）占 C1 的 {row.WetButSmall:N1}%");
			Console.WriteLine();
		}

		PrintCriteriaMatrix(criteriaRows, volScan, inverseTargets);
	}

	/// <summary>第 6 层跨档汇总：判据语义矩阵（在 res 循环结束后打印）。</summary>
	static void PrintCriteriaMatrix(List<CriteriaRow> rows, double[] volScan, double[] inverseTargets)
	{
		if (rows.Count == 0) return;
		var ress = rows.Select(r => r.Res).ToArray();
		double RatioOf(Func<CriteriaRow, double> f)
		{
			var r3 = rows.FirstOrDefault(x => x.Res == 3);
			var r4 = rows.FirstOrDefault(x => x.Res == 4);
			if (r3 == null || r4 == null) return double.NaN;
			double a = f(r3), b = f(r4);
			return b > 0 ? a / b : double.NaN;
		}
		static string F(double v) => double.IsNaN(v) ? "—" : v.ToString("N3");

		Console.WriteLine(new string('═', 78));
		Console.WriteLine("【第 6 层·跨档汇总】判据语义矩阵（★只读诊断，生产参数一个不动）");
		Console.WriteLine();
		Console.WriteLine("### 6.2 五种判据的通过率（占陆格 %）与 res3/res4 比值");
		Console.WriteLine($"| 判据 | 定义 | {string.Join(" | ", ress.Select(r => "res" + r))} | res3/res4 |");
		Console.WriteLine("|---|---|---:" + string.Join("|---:", ress.Select(_ => "")) + "|---:|");
		Console.WriteLine($"| **C1 现行 IsRiver** | RunoffAccum ≥ **全球均雨** × 阈值格数 | " +
			$"{string.Join(" | ", rows.Select(r => F(r.C1)))} | {F(RatioOf(r => r.C1))} |");
		Console.WriteLine($"| **C2 纯汇水面积** | FlowAccumKm2 ≥ 70,800 | " +
			$"{string.Join(" | ", rows.Select(r => F(r.C2)))} | {F(RatioOf(r => r.C2))} |");
		foreach (double t in volScan)
			Console.WriteLine($"| **C3 纯累积水量** | RunoffAccumKm3 ≥ {t:N0} km³/a | " +
				$"{string.Join(" | ", rows.Select(r => F(r.C3[t])))} | {F(RatioOf(r => r.C3[t]))} |");
		Console.WriteLine($"| **C4 面积 ∧ 水量** | C2 ∧ C1（零新参数） | " +
			$"{string.Join(" | ", rows.Select(r => F(r.C4)))} | {F(RatioOf(r => r.C4))} |");
		Console.WriteLine($"| **C5 局地降水归一** | RunoffAccum ≥ **当地**降水 × 阈值格数 | " +
			$"{string.Join(" | ", rows.Select(r => F(r.C5)))} | {F(RatioOf(r => r.C5))} |");
		Console.WriteLine();

		Console.WriteLine("### 6.3 阈值附近分布形状 = 弹性 |ε| = |dln(通过率)/dln(阈值)|");
		Console.WriteLine("（越大 ⇒ 分布越陡 ⇒ 判据对阈值位移越敏感；同一位移下通过率变化更大）");
		Console.WriteLine($"| 判据 | {string.Join(" | ", ress.Select(r => "res" + r))} |");
		Console.WriteLine("|---|---:" + string.Join("|---:", ress.Select(_ => "")) + "|");
		Console.WriteLine($"| C1 现行 | {string.Join(" | ", rows.Select(r => F(Math.Abs(r.E1))))} |");
		Console.WriteLine($"| C2 纯面积 | {string.Join(" | ", rows.Select(r => F(Math.Abs(r.E2))))} |");
		foreach (double t in volScan)
			Console.WriteLine($"| C3 纯水量 @{t:N0} km³ | {string.Join(" | ", rows.Select(r => F(Math.Abs(r.E3[t]))))} |");
		Console.WriteLine($"| C4 面积∧水量 | {string.Join(" | ", rows.Select(r => F(Math.Abs(r.E4))))} |");
		Console.WriteLine($"| C5 局地归一 | {string.Join(" | ", rows.Select(r => F(Math.Abs(r.E5))))} |");
		Console.WriteLine();

		Console.WriteLine("### 6.4 ★等效阈值反解：固定通过率下各 res **需要的阈值**（漂移小 = 判据本身尺度不变）");
		Console.WriteLine("（不引入任何新阈值常数：直接问『要筛出同样比例的河，这个量得取多少』）");
		Console.WriteLine("| 目标通过率 | 量 | " + string.Join(" | ", ress.Select(r => "res" + r)) + " | 漂移 max/min |");
		Console.WriteLine("|---|---|---:" + string.Join("|---:", ress.Select(_ => "")) + "|---:|");
		foreach (double p in inverseTargets)
		{
			var av = rows.Select(r => r.Inverse[p].areaKm2).ToArray();
			var vv = rows.Select(r => r.Inverse[p].volKm3).ToArray();
			Console.WriteLine($"| {p:N0}% | A_km2（km²） | {string.Join(" | ", av.Select(v => v.ToString("N0")))} | " +
				$"{(av.Min() > 0 ? (av.Max() / av.Min()).ToString("N1") + "×" : "—")} |");
			Console.WriteLine($"| {p:N0}% | T_km3（km³/a） | {string.Join(" | ", vv.Select(v => v.ToString("N3")))} | " +
				$"{(vv.Min() > 0 ? (vv.Max() / vv.Min()).ToString("N1") + "×" : "—")} |");
		}
		Console.WriteLine();

		Console.WriteLine("### 6.5 物理量关系与交叉");
		Console.WriteLine($"| 量 | {string.Join(" | ", ress.Select(r => "res" + r))} |");
		Console.WriteLine("|---|---:" + string.Join("|---:", ress.Select(_ => "")) + "|");
		Console.WriteLine($"| ln(面积)~ln(水量) 相关 r | {string.Join(" | ", rows.Select(r => F(r.AreaVolCorr)))} |");
		Console.WriteLine($"| C2∧¬C1「够大但太干」占 C2 | " +
			$"{string.Join(" | ", rows.Select(r => F(r.BigButDry)))} |");
		Console.WriteLine($"| C1∧¬C2「够湿但太小」占 C1 | " +
			$"{string.Join(" | ", rows.Select(r => F(r.WetButSmall)))} |");
		Console.WriteLine($"| C1 阈值换算 km³/a | {string.Join(" | ", rows.Select(r => F(r.C1ThresholdKm3)))} |");
		Console.WriteLine();

		// ── 6.6 ★维数检验：河流是**一维**对象 ──────────────────────────────────
		// 若河流是 1D 线特征，则"河流格占陆格的百分比"必然 ∝ 格间距 ⇒ 随 res 升高而下降，
		// 下降比 ≈ 格边长之比。此时用**面积型统计量**做收敛判据本身就用错了量纲；
		// 正确的量是**河长**（格数 × 每格代表的河段长度）或**河长密度**（km 河长 / 陆地面积）。
		Console.WriteLine("### 6.6 ★维数检验：河流是**一维**线特征，不是面特征");
		Console.WriteLine("（同一条河在更细的网格上占**更少**的陆格比例 ⇒ '占陆格%' 必然随 res 下降；");
		Console.WriteLine("  若下降比 ≈ 格间距之比，则残差是**量纲用错**，不是模型缺陷）");
		Console.WriteLine("  ★本指标由既有事实**现场算出**（IsRiver 掩码 × √3·SpatialScale.CellEdgeKm），");
		Console.WriteLine("    **不新增任何生产字段**——诊断指标留在诊断工具里（§10.7.6）。");
		Console.WriteLine($"| 量 | {string.Join(" | ", ress.Select(r => "res" + r))} | res3/res4 |");
		Console.WriteLine("|---|---:" + string.Join("|---:", ress.Select(_ => "")) + "|---:|");
		Console.WriteLine($"| 陆格数 | {string.Join(" | ", rows.Select(r => r.LandCells.ToString("N0")))} | — |");
		Console.WriteLine($"| 格间距（km） | {string.Join(" | ", rows.Select(r => r.NeighbourKm.ToString("N1")))} | " +
			$"{F(RatioOf(r => r.NeighbourKm))} |");
		Console.WriteLine($"| C2 河流**格数**（绝对） | {string.Join(" | ", rows.Select(r => r.C2Cells.ToString("N0")))} | " +
			$"{F(RatioOf(r => r.C2Cells))} |");
		Console.WriteLine($"| C2 河**长** = 格数 × 格间距（km） | " +
			$"{string.Join(" | ", rows.Select(r => (r.C2Cells * r.NeighbourKm).ToString("N0")))} | " +
			$"{F(RatioOf(r => r.C2Cells * r.NeighbourKm))} |");
		Console.WriteLine($"| C2 **河长密度**（km 河长 / 10⁶ km² 陆地） | " +
			$"{string.Join(" | ", rows.Select(r => (r.C2Cells * r.NeighbourKm / r.LandAreaKm2 * 1e6).ToString("N1")))} | " +
			$"{F(RatioOf(r => r.C2Cells * r.NeighbourKm / r.LandAreaKm2))} |");
		Console.WriteLine($"| C1 河流**格数**（绝对） | {string.Join(" | ", rows.Select(r => r.C1Cells.ToString("N0")))} | " +
			$"{F(RatioOf(r => r.C1Cells))} |");
		Console.WriteLine($"| C1 河**长**（km） | " +
			$"{string.Join(" | ", rows.Select(r => (r.C1Cells * r.NeighbourKm).ToString("N0")))} | " +
			$"{F(RatioOf(r => r.C1Cells * r.NeighbourKm))} |");
		Console.WriteLine($"| C1 **河长密度**（km / 10⁶ km²） | " +
			$"{string.Join(" | ", rows.Select(r => (r.C1Cells * r.NeighbourKm / r.LandAreaKm2 * 1e6).ToString("N1")))} | " +
			$"{F(RatioOf(r => r.C1Cells * r.NeighbourKm / r.LandAreaKm2))} |");
		Console.WriteLine();
		Console.WriteLine("### 6.7 D-14 的净效应（**同一条生产路径**上比，不是第 4 层的填洼 OFF 侧）");
		Console.WriteLine($"| 判据 | {string.Join(" | ", ress.Select(r => "res" + r))} | res3/res4 |");
		Console.WriteLine("|---|---:" + string.Join("|---:", ress.Select(_ => "")) + "|---:|");
		Console.WriteLine($"| C1′ D-14 **前**（海岸加权恒 1） | {string.Join(" | ", rows.Select(r => F(r.C1Pre)))} | " +
			$"{F(RatioOf(r => r.C1Pre))} |");
		Console.WriteLine($"| C1  D-14 **后**（现行） | {string.Join(" | ", rows.Select(r => F(r.C1)))} | " +
			$"{F(RatioOf(r => r.C1))} |");
		Console.WriteLine();
		Console.WriteLine("**读法**：6.2 看『同一定义下跨 res 稳不稳』；6.3 看『不稳是不是被陡尾放大』；");
		Console.WriteLine("6.4 去掉阈值选取这个混淆项，直接问『判据的定义本身是不是尺度不变的量』。");
		Console.WriteLine();
	}

	/// <summary>
	/// 空间自相关：以 hop 为滞后，算相邻滞后上所有格对的 Pearson r，
	/// 并把滞后换算成**物理距离**（km），插值出 r = 1/e 的相关长度 L。
	/// 只统计 <paramref name="inDomain"/> 内的格（如"陆格"），避免海陆混合把 r 拉高。
	/// </summary>
	static void AutoCorr(Ball ball, string name, Func<int, double> f, Func<int, bool> inDomain,
		int maxHops, double neighbourKm)
	{
		int n = ball.CellDirs.Length;
		var dirs = ball.CellDirs;

		// 域内均值/方差
		double mean = 0; int m = 0;
		for (int i = 0; i < n; i++) if (inDomain(i)) { mean += f(i); m++; }
		if (m == 0) { Console.WriteLine($"  | {name} | — | — | — |"); return; }
		mean /= m;
		double var = 0;
		for (int i = 0; i < n; i++) if (inDomain(i)) { double d = f(i) - mean; var += d * d; }
		var /= m;
		if (var <= 1e-12) { Console.WriteLine($"  | {name} | 常量场 | — | — |"); return; }

		var sXY = new double[maxHops + 1];
		var sX2 = new double[maxHops + 1];
		var sY2 = new double[maxHops + 1];
		var distSum = new double[maxHops + 1];
		var cnt = new int[maxHops + 1];

		// 抽样 BFS：以固定 LCG 保证可复现（不用 Random，避免版本差异）
		var visited = new int[n];
		var queue = new int[n];
		int samples = Math.Min(200, Math.Max(1, m / 4));
		uint seed = 12345u;
		int stamp = 0, tried = 0;
		while (tried < samples * 40 && stamp < samples)
		{
			seed = seed * 1664525u + 1013904223u;
			int src = (int)(seed % (uint)n);
			tried++;
			if (!inDomain(src) || visited[src] != 0) continue;
			stamp++;
			visited[src] = stamp;
			int qh = 0, qt = 0;
			queue[qt++] = src;
			int levelSize = 1, hop = 0;
			double ax = f(src) - mean;
			while (qh < qt && hop < maxHops)
			{
				int nextSize = 0;
				for (int k = 0; k < levelSize; k++)
				{
					int u = queue[qh++];
					foreach (int v in ball.CellNeighbors[u])
					{
						if (visited[v] == stamp) continue;
						visited[v] = stamp;
						queue[qt++] = v;
						nextSize++;
						if (!inDomain(v)) continue;
						int h = hop + 1;
						double by = f(v) - mean;
						sXY[h] += ax * by;
						sX2[h] += ax * ax;
						sY2[h] += by * by;
						distSum[h] += SpatialScale.DistanceKm(dirs[src], dirs[v]);
						cnt[h]++;
					}
				}
				levelSize = nextSize;
				hop++;
			}
		}

		var r = new double[maxHops + 1];
		var lagKm = new double[maxHops + 1];
		for (int h = 1; h <= maxHops; h++)
		{
			if (cnt[h] < 8) { r[h] = double.NaN; lagKm[h] = double.NaN; continue; }
			double den = Math.Sqrt(sX2[h] * sY2[h]);
			r[h] = den > 0 ? sXY[h] / den : double.NaN;
			lagKm[h] = distSum[h] / cnt[h];
		}

		// 相关长度 L：r 首次跌破 1/e 的两点间线性插值（按物理距离）
		const double target = 1.0 / Math.E;   // ≈ 0.3679
		double L = double.NaN;
		for (int h = 1; h <= maxHops; h++)
		{
			if (h + 1 > maxHops) break;
			if (double.IsNaN(r[h]) || double.IsNaN(r[h + 1])) continue;
			if (r[h] >= target && r[h + 1] < target)
			{
				double t = (r[h] - target) / (r[h] - r[h + 1]);
				L = lagKm[h] + t * (lagKm[h + 1] - lagKm[h]);
				break;
			}
		}

		string r1 = double.IsNaN(r[1]) ? "—" : r[1].ToString("N3");
		string ls = double.IsNaN(L) ? (r[maxHops] > target ? $">{lagKm[maxHops]:N0}（未衰减）" : "—") : L.ToString("N0");
		string ratio = double.IsNaN(L) ? (r[maxHops] > target ? $">{lagKm[maxHops] / neighbourKm:N1}" : "—")
									  : (L / neighbourKm).ToString("N2");
		Console.WriteLine($"  | {name} | {r1} | {ls} | {ratio} |");
	}

	/// <summary>
	/// 陆地连通块分析（第 2 层补）：返回 (块数, 最大块格数, 前 5 大块格数合计)。
	/// 用于判定"陆地是实心大陆"还是"破碎群岛"——这决定 D-11 该修水文还是修形态。
	/// </summary>
	static (int count, int max, int top5) LandComponents(Ball ball, FinalGeography final)
	{
		int n = ball.CellDirs.Length;
		var comp = new int[n];
		for (int i = 0; i < n; i++) comp[i] = -1;
		var sizes = new List<int>();
		var stack = new List<int>();
		int id = 0;
		for (int i = 0; i < n; i++)
		{
			if (!final.FinalLand[i] || comp[i] != -1) continue;
			stack.Clear();
			stack.Add(i);
			comp[i] = id;
			int size = 0;
			while (stack.Count > 0)
			{
				int u = stack[^1]; stack.RemoveAt(stack.Count - 1);
				size++;
				foreach (int v in ball.CellNeighbors[u])
					if (v >= 0 && v < n && final.FinalLand[v] && comp[v] == -1)
					{
						comp[v] = id;
						stack.Add(v);
					}
			}
			sizes.Add(size);
			id++;
		}
		sizes.Sort();
		sizes.Reverse();
		return (sizes.Count, sizes.Count > 0 ? sizes[0] : 0, sizes.Take(5).Sum());
	}

	/// <summary>每个格到出口的下游跳数（0 = 本身即出口；-1 下游者记为 0）。带记忆化。</summary>
	static int[] PathLengths(int[] downstream, int n)
	{
		var plen = new int[n];
		for (int i = 0; i < n; i++) plen[i] = -2;    // -2 = 未算
		var stack = new List<int>();
		for (int i = 0; i < n; i++)
		{
			if (plen[i] != -2) continue;
			stack.Clear();
			int cur = i;
			while (cur >= 0 && plen[cur] == -2)
			{
				stack.Add(cur);
				plen[cur] = -3;                      // 标记"正在算"，防环
				cur = downstream[cur];
			}
			int baseLen = cur < 0 ? 0 : Math.Max(0, plen[cur]);
			for (int k = stack.Count - 1; k >= 0; k--) plen[stack[k]] = ++baseLen;
		}
		for (int i = 0; i < n; i++) if (plen[i] < 0) plen[i] = 0;
		return plen;
	}
}
