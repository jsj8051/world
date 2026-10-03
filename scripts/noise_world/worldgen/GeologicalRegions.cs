using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.NewHexWorld;    // Ball（H3 球壳数据层）
using World.Utils;

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 地质区域层 v2（阶段 3，决策 03 v2——「先划分区域，再决定区域类型」的完整 pipeline）：
//   大陆 → ② Poisson Disk 区域种子（种子数 = 面积/目标面积 × random(0.8,1.2)）→ ③ SizeNoise 空间连续权重
//   → ④⑤ **连续 Region Field**（扭曲坐标上 argmin 角距/weight 的加权 Voronoi）+ Lloyd Relaxation ×2
//   → ⑥ 边界域扭曲（内建于连续场：H3 格只是查询连续场，**不在格上改归属** ⇒ 区域不破碎）
//   → ⑦ 区域邻接图 → ⑧ Features（面积 km²/纬度/离海 km/邻接）→ ⑨⑩ 打分 + **全局配额修正** +
//   **Softmax 概率选型**（非 argmax：COASTAL 最高分也可能偶成 PLAIN，自然度）→ ⑪ 区域 → ⑫ 海拔偏移（占位）。
// 与 v1 的关键差异（判读时要知道）：
//   · 种子 = Poisson disk（最小角距约束）非普通随机 ⇒ 区域均匀不挤簇；
//   · 权重 = BaseSize × SizeNoise(seed 位置低频噪声) ⇒ 大小变化**空间连续**（相邻区域大小相关），
//     非 i.i.d. 随机（v1 的独立随机会产生"巨大区域贴着细条区域"的怪相）；
//   · 归属在**连续扭曲坐标**上判（Region Field → H3 Sampling）⇒ 边界自然弯曲，格级弧线不再生硬；
//   · 类型 = Softmax 概率抽样 + 世界级配额先验（防 70% 山区世界），非 argmax、非纯随机；
//   · RIFT 硬约束：必须邻接高地系（HIGHLAND/MOUNTAIN/BASIN）才允许——地质结构逻辑。
// 区域类型 ≠ 地形类型：本层只是"这个位置倾向出现什么大尺度地形"的**控制图**；
//   真实高度由 Height Generator（S3）按区域类型生成骨架（MOUNTAIN_REGION → 山链场等）。
//   当前 TypeElevOffsetM 常数偏移只是其占位表现。
/// <summary>地质区域类型（七类，决策 03 表）。</summary>
public enum RegionType { Plain, Highland, Basin, Mountain, Plateau, Rift, Coastal }

/// <summary>
/// 地质区域 v2：Poisson 种子 + 连续扭曲 Region Field + Lloyd + 配额 Softmax 类型分配。
/// </summary>
public sealed class GeologicalRegions
{
	/// <summary>区域元数据（决策 §十九 的 GeologicalRegion 记录）。</summary>
	public sealed class Region
	{
		public int Id;                    // 区域号（全局）
		public Vector3 Seed;              // 种子方向（Lloyd 后的收敛位置）
		public float Weight;              // SizeNoise 空间连续权重（score = 角距/weight 的分母）
		public int Landmass;              // 所属陆块（连通分量号）
		public RegionType Type;
		/// <summary>区域基础高度（米；类型位域 × 区域内随机——决策 05v2 §九/§十：
		///   不是最终高度，是 HeightField 的区域基座；经插值场展开）。</summary>
		public float BaseElevationM;
		// ── Features（决策 §十）──
		public int CellCount;             // 格数
		public float AreaKm2;             // 面积
		public float LatitudeRad;         // 质心纬度（绝对值）
		public float CoastDistanceKm;     // 质心平均离海（跳数 × 格宽近似 km）
		public float Continentality;      // 大陆度 = 离海归一（0 沿岸 … 1 内陆极深）
		public int[] Neighbors = Array.Empty<int>();   // 邻接区域号
	}

	public Region[] Regions { get; private set; } = Array.Empty<Region>();
	/// <summary>逐格区域号（陆 = 区域下标；海 = −1）。</summary>
	public int[] RegionOfCell { get; private set; } = Array.Empty<int>();
	/// <summary>区域基础高度场（米；陆格）——每区域一枚基础高度经**归一化高斯插值**展开成
	///   连续场（决策 05v2 §十/§十一：大陆内部大尺度梯度，无色块拼接）。海格 0。</summary>
	public float[] BaseElevationField { get; private set; } = Array.Empty<float>();

	// ── 分区旋钮 ──
	public const float TargetRegionAreaKm2 = 4_000_000f;   // 目标区域面积（地球：~400 万 km²/区域）
	public const int MaxRegionsPerContinent = 14;
	public const int LloydIterations = 2;                  // 决策 §七：1~3 次，多了过度规则
	public const int PoissonMinGapKm = 900;                // 种子最小角距（km；Poisson disk 半径口径）

	// 世界级类型配额先验（决策 §十七）：统计控制非机械结果——配额缺口进打分，Softmax 保自然。
	//   BASIN 压到 0.06（决策 05 §七警告：盆地大量生成 ⇒ 河流系统出现大量孤立内流盆地）。
	//   区域类型不再直接加海拔偏移（2026-10-02 退役）——类型驱动大尺度场（MOUNTAIN→脊、
	//   PLATEAU→高原帽、BASIN→下挖），合成在 HeightComposer（决策 05 §六公式）。
	public static readonly float[] TypeQuota = { 0.35f, 0.15f, 0.06f, 0.15f, 0.10f, 0.05f, 0.10f };
	//                            类型:            Plain Highland Basin Mountain Plateau Rift Coastal

	static readonly string[] TypeNames = { "Plain", "Highland", "Basin", "Mountain", "Plateau", "Rift", "Coastal" };
	public static string TypeName(RegionType t) => TypeNames[(int)t];

	// 打分系数（五因素约束 + 邻接结构；随机权重只做基座）
	const float CoastalCoastPull = 1.6f;
	const float PlainLowPull = 0.8f;
	const float HighlandInlandPull = 1.0f;
	const float PlateauInlandPull = 0.9f;
	const float PlateauSizePull = 0.35f;
	const float BasinBase = 0.45f;
	const float MountainBase = 0.3f;
	const float RiftRarity = 0.18f;
	const float RandomWeightAmp = 0.35f;
	const float QuotaPull = 0.5f;            // 配额缺口权重（全局控制强度）
	const float AdjacentMountainPull = 0.6f; // 邻接高地系 → MOUNTAIN（山脉连绵）
	const float AdjacentBasinPull = 0.4f;    // 邻接高地系 → BASIN（山间盆地）
	const float SoftmaxTemperature = 0.35f;  // 决策 §十五：按概率而非硬选——T 越小越接近 argmax

	readonly int _seed;
	readonly SphericalFbmNoise _sizeNoise;   // Seed 权重的空间连续噪声（决策 §六）
	readonly SphericalFbmNoise _w1, _w2, _w3; // 区域边界域扭曲三路（独立于海陆场扭曲）

	public GeologicalRegions(int seed)
	{
		_seed = seed;
		var rnd = new DeterministicRandom(seed ^ 0x600D);
		_sizeNoise = new SphericalFbmNoise(rnd.Next(), 8000f, 2);   // 低频：相邻区域大小相关
		_w1 = new SphericalFbmNoise(rnd.Next(), 2500f, 3);
		_w2 = new SphericalFbmNoise(rnd.Next(), 2500f, 3);
		_w3 = new SphericalFbmNoise(rnd.Next(), 2500f, 3);
	}

	/// <summary>扭曲坐标（区域边界专用域扭曲；H3 格只是查询点——决策 §九）。</summary>
	public Vector3 WarpDir(Vector3 dir)
	{
		var w = new Vector3(_w1.Sample(dir), _w2.Sample(dir), _w3.Sample(dir));
		return (dir + w * (700f / SphericalFbmNoise.EarthRadiusKm)).Normalized();
	}

	/// <summary>全量生成。proj 须已 Generate；field 仅取扭曲系一致性（同参种子亦可独立）。</summary>
	public void Generate(Ball ball, H3LandSeaProjector proj, float targetRegionAreaKm2 = TargetRegionAreaKm2)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (proj == null) throw new ArgumentNullException(nameof(proj));
		int n = ball.CellDirs.Length;
		var rnd = new DeterministicRandom(_seed);
		float cellAreaKm2 = 4f * MathF.PI * SphericalFbmNoise.EarthRadiusKm * SphericalFbmNoise.EarthRadiusKm / n;

		// ── ① 大陆格集合 + 面积口径区域数（×random(0.8,1.2) 抖动，决策 §四）──
		var continentCells = new List<int>[proj.LandmassCount];
		for (int c = 0; c < continentCells.Length; c++) continentCells[c] = new List<int>();
		for (int i = 0; i < n; i++)
			if (proj.Land[i]) continentCells[proj.LandmassId[i]].Add(i);

		int continentN = continentCells.Length;
		var kPerContinent = new int[continentN];
		var regionBase = new int[continentN];
		int regionCount = 0;
		float minGapRad = PoissonMinGapKm / SphericalFbmNoise.EarthRadiusKm;
		for (int c = 0; c < continentN; c++)
		{
			regionBase[c] = regionCount;
			var cells = continentCells[c];
			if (cells.Count == 0) continue;
			float area = cells.Count * cellAreaKm2;
			float k = area / targetRegionAreaKm2 * (0.8f + 0.4f * (float)rnd.NextDouble());
			kPerContinent[c] = Math.Clamp((int)MathF.Round(k), 1, MaxRegionsPerContinent);
			regionCount += kPerContinent[c];
		}

		// ── ② Poisson Disk 种子（决策 §三：最小角距拒绝；候选从本大陆格集合抽 ⇒ 种子必在大陆内）──
		var seedDir = new Vector3[regionCount];
		var seedWeight = new float[regionCount];
		for (int c = 0; c < continentN; c++)
		{
			var cells = continentCells[c];
			if (kPerContinent[c] == 0) continue;
			var chosen = new List<Vector3>(kPerContinent[c]);
			int attempts = 0;
			while (chosen.Count < kPerContinent[c] && attempts < kPerContinent[c] * 200)
			{
				attempts++;
				var d = ball.CellDirs[cells[rnd.Next(cells.Count)]];
				bool ok = true;
				foreach (var e in chosen)
					if (MathF.Acos(Math.Clamp(d.Dot(e), -1f, 1f)) < minGapRad) { ok = false; break; }
				if (!ok) continue;
				chosen.Add(d);
				// ③ SizeNoise 空间连续权重（决策 §六：BaseSize × SizeNoise(position)）
				seedWeight[regionBase[c] + chosen.Count - 1] = 0.7f + 0.6f * (_sizeNoise.Sample(d) + 1f) * 0.5f;
			}
			// 拒绝上限兜底：放宽间距补足（极小大陆/种子挤时）
			while (chosen.Count < Math.Min(kPerContinent[c], 2))
			{
				var d = ball.CellDirs[cells[rnd.Next(cells.Count)]];
				chosen.Add(d);
				seedWeight[regionBase[c] + chosen.Count - 1] = 0.7f + 0.6f * (_sizeNoise.Sample(d) + 1f) * 0.5f;
			}
			for (int s = 0; s < chosen.Count; s++) seedDir[regionBase[c] + s] = chosen[s];
		}

		// ── ④⑤ 连续 Region Field 归属 + Lloyd Relaxation（种子 → 区域质心，×2 不过度规则）──
		RegionOfCell = new int[n];
		var regionCells = new List<int>[regionCount];
		for (int r = 0; r < regionCount; r++) regionCells[r] = new List<int>();

		void AssignRegions()
		{
			for (int r = 0; r < regionCount; r++) regionCells[r].Clear();
			for (int i = 0; i < n; i++)
			{
				if (!proj.Land[i]) { RegionOfCell[i] = -1; continue; }
				RegionOfCell[i] = RegionAt(ball.CellDirs[i], proj.LandmassId[i], regionBase, kPerContinent, seedDir, seedWeight);
				regionCells[RegionOfCell[i]].Add(i);
			}
		}

		AssignRegions();
		for (int iter = 0; iter < LloydIterations; iter++)
		{
			bool moved = false;
			for (int c = 0; c < continentN; c++)
				for (int s = 0; s < kPerContinent[c]; s++)
				{
					int r = regionBase[c] + s;
					if (regionCells[r].Count == 0) continue;
					var centroid = Vector3.Zero;
					foreach (int i in regionCells[r]) centroid += ball.CellDirs[i];
					if (centroid.Length() < 1e-9f) continue;
					var next = centroid.Normalized();
					if ((next - seedDir[r]).Length() > 1e-4f) moved = true;
					seedDir[r] = next;   // 种子 ← 质心（weight 不动——大小场不因松弛改变）
				}
			if (!moved) break;
			AssignRegions();
		}

		// ── ⑦⑧ 邻接图 + Features ──
		var neighbors = ball.CellNeighbors;
		var adj = new HashSet<int>[regionCount];
		for (int r = 0; r < regionCount; r++) adj[r] = new HashSet<int>();
		for (int i = 0; i < n; i++)
		{
			int ri = RegionOfCell[i];
			if (ri < 0) continue;
			foreach (int j in neighbors[i])
			{
				int rj = RegionOfCell[j];
				if (rj >= 0 && rj != ri) { adj[ri].Add(rj); adj[rj].Add(ri); }
			}
		}

		int maxDist = 1;
		for (int i = 0; i < n; i++) maxDist = Math.Max(maxDist, proj.DistToCoast[i]);
		Regions = new Region[regionCount];
		for (int c = 0; c < continentN; c++)
			for (int s = 0; s < kPerContinent[c]; s++)
			{
				int r = regionBase[c] + s;
				var centroid = Vector3.Zero;
				float distSum = 0f;
				foreach (int i in regionCells[r]) { centroid += ball.CellDirs[i]; distSum += proj.DistToCoast[i]; }
				int count = Math.Max(regionCells[r].Count, 1);
				Regions[r] = new Region
				{
					Id = r,
					Seed = seedDir[r],
					Weight = seedWeight[r],
					Landmass = c,
					Type = RegionType.Plain,
					CellCount = regionCells[r].Count,
					AreaKm2 = regionCells[r].Count * cellAreaKm2,
					LatitudeRad = MathF.Abs(MathF.Asin(Math.Clamp((centroid.Length() > 1e-9f ? centroid.Normalized() : seedDir[r]).Y, -1f, 1f))),
					CoastDistanceKm = distSum / count * cellWidthKm(cellAreaKm2),
					Continentality = Math.Clamp(distSum / count / maxDist, 0f, 1f),
					Neighbors = new int[adj[r].Count],
				};
			adj[r].CopyTo(Regions[r].Neighbors);
		}

		AssignTypes(rnd, adj);
		BuildBaseElevationField(ball, regionBase, kPerContinent);
	}

	// ── 区域基础高度（决策 05v2 §九/§十）──

	// 类型位域（米）：不是最终高度，是区域基座——插值展开后叠加系统场。
	//   MOUNTAIN 区基座高（山区本身是高地），BASIN 位域低（叠加下挖更深）。
	static readonly (float lo, float hi)[] BaseElevBandM =
	{
		(80f, 450f),     // Plain
		(500f, 1100f),   // Highland
		(150f, 600f),    // Basin
		(900f, 1600f),   // Mountain
		(900f, 1700f),   // Plateau
		(100f, 500f),    // Rift
		(20f, 120f),     // Coastal
	};

	/// <summary>逐格基础高度场：每区域一枚 BaseElevationM 经**归一化高斯帽插值**（Shepard 式）
	/// 展开成连续场——区域间平滑过渡，无色块拼接（决策 05v2 §十一）。</summary>
	void BuildBaseElevationField(Ball ball, int[] regionBase, int[] kPerContinent)
	{
		int n = ball.CellDirs.Length;
		var rnd = new DeterministicRandom(_seed ^ 0xB4E5);
		foreach (var region in Regions)
		{
			var (lo, hi) = BaseElevBandM[(int)region.Type];
			region.BaseElevationM = lo + (hi - lo) * (float)rnd.NextDouble();
		}

		// 每区域一帽（σ = 等效半径 ×1.4：覆盖全区并向邻区溢出——插值权重重叠才能平滑过渡）
		var caps = new (Vector3 anchor, float sigmaRad, float baseM, float capR2)[Regions.Length];
		for (int i = 0; i < Regions.Length; i++)
		{
			float equivKm = MathF.Sqrt(Regions[i].AreaKm2 / MathF.PI);
			float sigmaKm = equivKm * 1.4f;
			float sigmaRad = sigmaKm / SphericalFbmNoise.EarthRadiusKm;
			// 帽截断角距 = 2.5σ（exp(−6.25)≈0.002，插值权重足够小）
			float cutRad = 2.5f * sigmaRad;
			caps[i] = (Regions[i].Seed, sigmaRad, Regions[i].BaseElevationM, cutRad * cutRad);
		}

		BaseElevationField = new float[n];
		var dirs = ball.CellDirs;
		var regionOfCell = RegionOfCell;
		for (int i = 0; i < n; i++)
		{
			if (regionOfCell[i] < 0) continue;   // 海格 0（合成器海侧不读本场）
			float wSum = 0f, acc = 0f;
			for (int c = 0; c < caps.Length; c++)
			{
				float d2 = (dirs[i] - caps[c].anchor).LengthSquared();   // 弦距² ∝ 小角距（排序/截断用）
				if (d2 > caps[c].capR2) continue;
				float dRad = MathF.Acos(Math.Clamp(dirs[i].Dot(caps[c].anchor), -1f, 1f));
				float w = MathF.Exp(-(dRad * dRad) / (2f * caps[c].sigmaRad * caps[c].sigmaRad));
				if (w < 1e-4f) continue;
				wSum += w;
				acc += w * caps[c].baseM;
			}
			// 无帽覆盖（孤格）：回落本区域基础高度
			BaseElevationField[i] = wSum > 1e-3f ? acc / wSum : Regions[regionOfCell[i]].BaseElevationM;
		}
	}

	/// <summary>连续 Region Field 查询：本大陆种子中 argmin(扭曲角距/weight)——H3 格只是采样点（决策 §九）。</summary>
	int RegionAt(Vector3 dir, int continent, int[] regionBase, int[] kPerContinent, Vector3[] seedDir, float[] seedWeight)
	{
		var d = WarpDir(dir);
		int lo = regionBase[continent], hi = lo + kPerContinent[continent];
		int best = lo;
		float bestCost = float.PositiveInfinity;
		for (int s = lo; s < hi; s++)
		{
			float dist = MathF.Acos(Math.Clamp(d.Dot(seedDir[s]), -1f, 1f));
			float cost = dist / seedWeight[s];   // 决策 §五：score = distance / weight
			if (cost < bestCost) { bestCost = cost; best = s; }
		}
		return best;
	}

	/// <summary>类型分配 v2：五因素打分 + 邻接结构 + 配额缺口修正 + Softmax 概率抽样（决策 §十五/§十七）。</summary>
	void AssignTypes(DeterministicRandom rnd, HashSet<int>[] adj)
	{
		int regionCount = Regions.Length;
		var scores = new float[regionCount, 7];
		var rndType = new DeterministicRandom(_seed ^ 0x5EED);
		for (int r = 0; r < regionCount; r++)
		{
			var g = Regions[r];
			for (int t = 0; t < 7; t++)
				scores[r, t] = RandomWeightAmp * (float)rndType.NextDouble();   // ⑤ 随机基座
			// ① 距海岸 / ② 面积 / ③ 纬度（轻微风格不主导——决策 §十三）
			scores[r, (int)RegionType.Coastal] += CoastalCoastPull * (1f - g.Continentality) * (1f - g.Continentality);
			scores[r, (int)RegionType.Plain] += PlainLowPull + PlainLowPull * (1f - g.Continentality);
			scores[r, (int)RegionType.Highland] += HighlandInlandPull * g.Continentality;
			scores[r, (int)RegionType.Plateau] += PlateauInlandPull * g.Continentality
				+ PlateauSizePull * (AreaRelSize(r) - 1f)
				+ 0.4f * (1f - g.LatitudeRad / (MathF.PI / 2f)) * g.Continentality;
			scores[r, (int)RegionType.Basin] += BasinBase + 0.3f * (2f - AreaRelSize(r));
			scores[r, (int)RegionType.Mountain] += MountainBase + 0.5f * g.Continentality;
			scores[r, (int)RegionType.Rift] += RiftRarity * (float)rndType.NextDouble();
		}

		// ④ 邻接结构：按区域号序贪心（邻接加分读已定案邻居 ⇒ 确定性；山脉连绵/山间盆地涌现）
		for (int r = 0; r < regionCount; r++)
		{
			int high = 0;
			foreach (int a in adj[r])
				if (Regions[a].Type is RegionType.Highland or RegionType.Plateau or RegionType.Mountain) high++;
			scores[r, (int)RegionType.Mountain] += AdjacentMountainPull * high;
			scores[r, (int)RegionType.Basin] += AdjacentBasinPull * high;

			// ⑩ 全局配额缺口修正（统计先验，非机械：缺口权重随分配动态更新）
			int remaining = regionCount - r;
			for (int t = 0; t < 7; t++)
			{
				float quota = TypeQuota[t] * regionCount;
				int assigned = CountAssigned(t, r);
				float gap = MathF.Max(0f, quota - assigned);
				scores[r, t] += QuotaPull * gap / Math.Max(1, remaining);
			}

			// RIFT 硬约束（决策 §十四）：必须邻接高地系——否则概率置零（地质结构逻辑）
			if (high == 0) scores[r, (int)RegionType.Rift] = 0f;

			// ⑨ Softmax 概率抽样（决策 §十五：COASTAL 最高分也可能偶成 PLAIN）
			float maxS = float.NegativeInfinity;
			for (int t = 0; t < 7; t++) maxS = MathF.Max(maxS, scores[r, t]);
			float sum = 0f;
			var prob = new float[7];
			for (int t = 0; t < 7; t++) { prob[t] = MathF.Exp((scores[r, t] - maxS) / SoftmaxTemperature); sum += prob[t]; }
			float pick = (float)rndType.NextDouble() * sum;
			int chosen = 6;
			float acc = 0f;
			for (int t = 0; t < 7; t++) { acc += prob[t]; if (pick <= acc) { chosen = t; break; } }
			Regions[r].Type = (RegionType)chosen;
		}
	}

	float AreaRelSize(int r)
	{
		var g = Regions[r];
		float sum = 0f; int cnt = 0;
		for (int i = 0; i < Regions.Length; i++)
			if (Regions[i].Landmass == g.Landmass) { sum += Regions[i].AreaKm2; cnt++; }
		float avg = cnt > 0 ? sum / cnt : 1f;
		return avg > 0 ? g.AreaKm2 / avg : 1f;
	}

	int CountAssigned(int type, int upToExclusive)
	{
		int c = 0;
		for (int r = 0; r < upToExclusive; r++)
			if ((int)Regions[r].Type == type) c++;
		return c;
	}

	static float cellWidthKm(float cellAreaKm2) => MathF.Sqrt(cellAreaKm2);   // 跳数→km 的方格近似（BFS 跳数量级口径）
}
