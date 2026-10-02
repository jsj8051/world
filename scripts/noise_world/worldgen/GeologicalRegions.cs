using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.NewHexWorld;    // Ball（H3 球壳数据层）
using World.Utils;

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 地质区域层（阶段 3，决策 03）："地壳划分"思想的生成辅助版——**不是板块模拟**，
//   只是世界结构的分区标注（Northern Highlands / Central Plains / Basin…）。
//   两级 Voronoi：第一级 = 大陆锚点（阶段 1 的 ContinentId），第二级 = 大陆内区域种子。
//   区域种子从**本大陆的格集合**里 best-candidate 抽取（种子必落在大陆势力内 ⇒ 区域永不跨大陆），
//   数量按大陆面积定（大洲多区域）；归属 = 角距/weight 最小（乘法加权 Voronoi，与阶段 1 同哲学）。
// 类型分配 = **约束式打分**（决策明令"不要直接随机赋类型"），五因素：
//   ① 距离海岸（relCoast：沿岸 → COASTAL/PLAIN，内陆 → HIGHLAND/PLATEAU）
//   ② 区域大小（relSize：大区域偏 PLATEAU、小区域偏 BASIN）
//   ③ 大陆位置（质心纬度、相对锚点径向位置——框架版进纬度项）
//   ④ 邻接关系（pass3：邻接高地/高原的区域内陆侧加分 MOUNTAIN、山间洼地加分 BASIN——山脉连绵涌现）
//   ⑤ 随机权重（每区域每类型一份固定次序派生的扰动，tie-break 语义）
//   分配序：先算全表，再按 RegionId 升序**贪心定案**（邻接加分读邻居已定案类型 ⇒ 确定性）。
// 可见海拔 = 投影海拔 + 区域类型偏移（HIGHLAND/MOUNTAIN 抬、BASIN/RIFT 压）——区域感上画面；
//   仍是"辅助层"量级（±1.5 km），真实地形高度是后续阶段（03 §5 S3）。
/// <summary>地质区域类型（七类，决策 03 表）。</summary>
public enum RegionType { Plain, Highland, Basin, Mountain, Plateau, Rift, Coastal }

/// <summary>
/// 地质区域：大陆内两级 Voronoi 分区 + 约束式类型分配 + 区域海拔调制。
/// </summary>
public sealed class GeologicalRegions
{
	/// <summary>区域元数据（种子方向 / 大陆 / 类型 / 规模）。</summary>
	public sealed class Region
	{
		public Vector3 Centroid;          // 质心方向（归一化）
		public int Continent;             // 所属大陆（锚点号）
		public RegionType Type;
		public int CellCount;             // 格数（规模）
		public float AvgDistToCoast;      // 区域平均离海跳数（距海岸因素）
	}

	public Region[] Regions { get; private set; } = Array.Empty<Region>();
	/// <summary>逐格区域号（陆 = 区域下标；海 = −1）。</summary>
	public int[] RegionOfCell { get; private set; } = Array.Empty<int>();
	/// <summary>合成可见海拔（投影海拔 + 区域类型偏移；海格不变）。</summary>
	public float[] ElevationM { get; private set; } = Array.Empty<float>();

	// ── 分区旋钮 ──
	public const int CellsPerRegion = 4500;      // 每区域目标格数（res4 288k 格、29% 陆 ⇒ 全洲 ~18 区域）
	public const int MinRegionsPerContinent = 2;
	public const int MaxRegionsPerContinent = 8;

	// 区域类型海拔偏移（米；辅助层量级——真实高度在 S3）
	public static readonly float[] TypeElevOffsetM =
	{
		120f,    // Plain
		650f,    // Highland
		-450f,   // Basin
		1500f,   // Mountain
		950f,    // Plateau
		-280f,   // Rift
		0f,      // Coastal
	};

	static readonly string[] TypeNames = { "Plain", "Highland", "Basin", "Mountain", "Plateau", "Rift", "Coastal" };
	public static string TypeName(RegionType t) => TypeNames[(int)t];

	// 打分系数（调参入口集中在此；约束语义见各行的注释）
	const float CoastalCoastPull = 1.6f;     // COASTAL 沿岸拉力
	const float PlainLowPull = 0.8f;         // PLAIN 低地拉力
	const float HighlandInlandPull = 1.0f;   // HIGHLAND 内陆拉力
	const float PlateauInlandPull = 0.9f;    // PLATEAU 内陆拉力
	const float PlateauSizePull = 0.35f;     // PLATEAU 大区域偏好
	const float BasinBase = 0.45f;           // BASIN 基线（小区域/随机抬升后入榜）
	const float MountainBase = 0.3f;         // MOUNTAIN 基线（主要靠邻接调制入榜）
	const float RiftRarity = 0.18f;          // RIFT 稀有度（纯随机门槛，偶发）
	const float RandomWeightAmp = 0.35f;     // 随机权重幅度（tie-break 量级）
	const float AdjacentMountainPull = 0.6f; // 邻接高地/高原/山地 → MOUNTAIN 加分
	const float AdjacentBasinPull = 0.4f;    // 邻接高地 → BASIN 加分（山间盆地）

	readonly int _seed;

	public GeologicalRegions(int seed) => _seed = seed;

	/// <summary>全量生成：分区 → 特征 → 类型 → 海拔合成。proj 须已 Generate（用 ContinentId/DistToCoast/ElevationM）。</summary>
	public void Generate(Ball ball, H3LandSeaProjector proj, float cellsPerRegion = CellsPerRegion)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (proj == null) throw new ArgumentNullException(nameof(proj));
		int n = ball.CellDirs.Length;
		var rnd = new DeterministicRandom(_seed);

		// ── ① 按大陆收集陆格，逐大陆 best-candidate 抽区域种子（种子 = 本大陆真实格心 ⇒ 永在大陆内）──
		var continentCells = new List<int>[proj.ContinentCount];
		for (int c = 0; c < continentCells.Length; c++) continentCells[c] = new List<int>();
		for (int i = 0; i < n; i++)
			if (proj.Land[i]) continentCells[proj.ContinentId[i]].Add(i);

		// 每大陆区域数（按面积）+ 区域号基址前缀（区域号 = 全局递增，大陆序）
		int continentN = continentCells.Length;
		var kPerContinent = new int[continentN];
		var regionBase = new int[continentN];
		int regionCount = 0;
		for (int c = 0; c < continentN; c++)
		{
			regionBase[c] = regionCount;
			var cells = continentCells[c];
			if (cells.Count == 0) continue;
			kPerContinent[c] = Math.Clamp((int)MathF.Round(cells.Count / cellsPerRegion),
				Math.Min(MinRegionsPerContinent, cells.Count), MaxRegionsPerContinent);
			regionCount += kPerContinent[c];
		}

		var seedDirs = new List<Vector3>(regionCount);
		var seedWeight = new List<float>(regionCount);
		for (int c = 0; c < continentN; c++)
		{
			var cells = continentCells[c];
			if (kPerContinent[c] == 0) continue;
			var chosen = new List<Vector3>(kPerContinent[c]);
			const int candidates = 24;
			for (int s = 0; s < kPerContinent[c]; s++)
			{
				Vector3 best = default;
				float bestGap = -1f;
				for (int q = 0; q < candidates; q++)
				{
					var d = ball.CellDirs[cells[rnd.Next(cells.Count)]];   // 从本大陆格集合抽候选
					float gap = float.PositiveInfinity;
					foreach (var e in chosen)
						gap = MathF.Min(gap, MathF.Acos(Math.Clamp(d.Dot(e), -1f, 1f)));
					if (gap > bestGap) { bestGap = gap; best = d; }
				}
				chosen.Add(best);
				seedDirs.Add(best);
				seedWeight.Add(0.8f + 0.4f * (float)rnd.NextDouble());   // 区域影响权重 ∈[0.8,1.2]
			}
		}

		// ── ② 逐陆格归属（本大陆种子中 角距/weight 最小 ⇒ 乘法加权 Voronoi；海格 −1）──
		RegionOfCell = new int[n];
		var regionDirs = new List<Vector3>[regionCount];
		var regionDist = new List<int>[regionCount];
		for (int r = 0; r < regionCount; r++) { regionDirs[r] = new List<Vector3>(); regionDist[r] = new List<int>(); }
		for (int i = 0; i < n; i++)
		{
			if (!proj.Land[i]) { RegionOfCell[i] = -1; continue; }
			int c = proj.ContinentId[i];
			int end = regionBase[c] + kPerContinent[c];
			int best = regionBase[c];
			float bestCost = float.PositiveInfinity;
			for (int s = regionBase[c]; s < end; s++)
			{
				float d = MathF.Acos(Math.Clamp(ball.CellDirs[i].Dot(seedDirs[s]), -1f, 1f));
				float cost = d / seedWeight[s];
				if (cost < bestCost) { bestCost = cost; best = s; }
			}
			RegionOfCell[i] = best;
			regionDirs[best].Add(ball.CellDirs[i]);
			regionDist[best].Add(proj.DistToCoast[i]);
		}

		// ── ③ 区域特征（质心/规模/平均离海；大陆号按前缀回填）──
		Regions = new Region[regionCount];
		var relCoast = new float[regionCount];   // 0 = 沿岸 … 1 = 内陆极深（按全球最大离海归一）
		int maxDist = 1;
		for (int i = 0; i < n; i++) maxDist = Math.Max(maxDist, proj.DistToCoast[i]);
		for (int c = 0; c < continentN; c++)
			for (int r = regionBase[c]; r < regionBase[c] + kPerContinent[c]; r++)
			{
				var centroid = Vector3.Zero;
				float distSum = 0f;
				for (int k = 0; k < regionDirs[r].Count; k++)
				{
					centroid += regionDirs[r][k];
					distSum += regionDist[r][k];
				}
				int count = Math.Max(regionDirs[r].Count, 1);
				Regions[r] = new Region
				{
					Centroid = centroid.Length() > 1e-9f ? centroid.Normalized() : seedDirs[r],
					Continent = c,
					CellCount = regionDirs[r].Count,
					AvgDistToCoast = distSum / count,
					Type = RegionType.Plain,
				};
				relCoast[r] = Math.Clamp(Regions[r].AvgDistToCoast / maxDist, 0f, 1f);
			}
		// relSize：区域内格数 / 所在大陆的区域均格数（大区域 > 1）
		var relSize = new float[regionCount];
		for (int c = 0; c < continentN; c++)
		{
			float sum = 0f; int cnt = 0;
			for (int r = regionBase[c]; r < regionBase[c] + kPerContinent[c]; r++) { sum += Regions[r].CellCount; cnt++; }
			float avg = cnt > 0 ? sum / cnt : 1f;
			for (int r = regionBase[c]; r < regionBase[c] + kPerContinent[c]; r++)
				relSize[r] = avg > 0 ? Regions[r].CellCount / avg : 1f;
		}

		// ── ④ 打分全表（五因素之 ①②③⑤；邻接 ④ 留 pass3）──
		var scores = new float[regionCount, 7];
		var rndType = new DeterministicRandom(_seed ^ 0x5EED);   // 类型扰动种子（与分区种子解耦）
		for (int r = 0; r < regionCount; r++)
		{
			float latAbs = MathF.Abs(Vector3.Up.Dot(Regions[r].Centroid));   // 大陆位置：纬度因子（极 → 1）
			for (int t = 0; t < 7; t++)
				scores[r, t] = RandomWeightAmp * (float)rndType.NextDouble();   // ⑤ 随机权重（基座）
			scores[r, (int)RegionType.Coastal] += CoastalCoastPull * (1f - relCoast[r]) * (1f - relCoast[r]);
			scores[r, (int)RegionType.Plain] += PlainLowPull + PlainLowPull * (1f - relCoast[r]);
			scores[r, (int)RegionType.Highland] += HighlandInlandPull * relCoast[r];
			scores[r, (int)RegionType.Plateau] += PlateauInlandPull * relCoast[r]
				+ PlateauSizePull * (relSize[r] - 1f)
				+ 0.4f * (1f - latAbs) * relCoast[r];                        // ③ 低纬内陆更可能高原
			scores[r, (int)RegionType.Basin] += BasinBase + 0.3f * (2f - relSize[r]);
			scores[r, (int)RegionType.Mountain] += MountainBase + 0.5f * relCoast[r];
			scores[r, (int)RegionType.Rift] += RiftRarity * (float)rndType.NextDouble();   // 稀有：双重随机门槛
		}

		// ── ⑤ pass3 邻接调制 + 贪心定案（按 RegionId 序；邻接加分读已定案邻居 ⇒ 确定性）──
		// 邻接对：陆格边两侧区域不同（H3 邻接图一遍扫描）
		var adjacent = new HashSet<int>[regionCount];
		for (int r = 0; r < regionCount; r++) adjacent[r] = new HashSet<int>();
		var neighbors = ball.CellNeighbors;
		for (int i = 0; i < n; i++)
		{
			int ri = RegionOfCell[i];
			if (ri < 0) continue;
			foreach (int j in neighbors[i])
			{
				int rj = RegionOfCell[j];
				if (rj >= 0 && rj != ri) { adjacent[ri].Add(rj); adjacent[rj].Add(ri); }
			}
		}
		for (int r = 0; r < regionCount; r++)
		{
			int high = 0;   // 邻接中已定案的高地/高原/山地数
			foreach (int a in adjacent[r])
				if (Regions[a].Type is RegionType.Highland or RegionType.Plateau or RegionType.Mountain) high++;
			scores[r, (int)RegionType.Mountain] += AdjacentMountainPull * high;   // ④ 山脉连绵
			scores[r, (int)RegionType.Basin] += AdjacentBasinPull * high;         // ④ 山间盆地
			int bestT = 0;
			for (int t = 1; t < 7; t++)
				if (scores[r, t] > scores[r, bestT]) bestT = t;
			Regions[r].Type = (RegionType)bestT;
		}

		// ── ⑥ 合成可见海拔（投影海拔 + 区域偏移；海格不变）──
		ElevationM = new float[n];
		for (int i = 0; i < n; i++)
			ElevationM[i] = RegionOfCell[i] >= 0
				? proj.ElevationM[i] + TypeElevOffsetM[(int)Regions[RegionOfCell[i]].Type]
				: proj.ElevationM[i];
	}
}
