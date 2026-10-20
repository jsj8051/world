using System;
using System.Collections.Generic;
using Godot;
using World.Data;
using World.H3Grid;
using World.Utils;
using World.Utils.H3;

namespace World.Logic;

/// <summary>
/// 板块划分：撒种子点（带速度）→ fBm 阻力场多源洪泛 → 派定壳型（陆壳 / 洋壳）。
/// 步骤由 <see cref="PlatePipeline"/> 编排，参数由 <see cref="PlateSpec"/> 给出。
/// 用洪泛而非加噪 argmin ⇒ 每块连通、不出飞地；边界是累积代价等值线，被阻力场扭成蜿蜒曲线。
/// 面积重尾是有意为之，不做 Lloyd 松弛。
/// 壳型是本阶段唯一的陆海产物：陆壳格由 ① 阶段铺陆面高度、洋壳铺海底；
/// 海岸线不由本阶段决定，而是 ① 阶段高度场穿过海面的结果。
/// </summary>
public sealed class PlateManager
{
	/// <summary>逐格板块号（长度 = 格数；每格恒有归属，取值 ∈ [0, 板块数)）。</summary>
	public int[] PlateOfCell { get; private set; } = Array.Empty<int>();
	/// <summary>板块表（号即下标；含每块壳型 <see cref="Plate.Crust"/>）。</summary>
	public Plate[] Plates { get; private set; } = Array.Empty<Plate>();
	/// <summary>逐块格数（长度 = 板块数；Σ = 格数）。</summary>
	public int[] SizeOfPlate { get; private set; } = Array.Empty<int>();

	/// <summary>板块邻接矩阵（<c>[a,b]</c> = 两块有直接相邻的格；对称、对角线 false）。
	/// 由洪泛后的逐格归属统计得到——「陆壳连成一片」靠它判定。</summary>
	public bool[,] PlateAdjacency { get; private set; } = new bool[0, 0];

	/// <summary>撒种子点：球面均匀随机撒 <c>plateCount</c> 个点，每点附随机速度与密度。
	/// 速度 = 种子点处切向速率 V ∈ [minSpeed, maxSpeed) + 切平面内随机方位 ψ；
	/// 由它反解欧拉矢量 ω（ω ⊥ 种子点方向 ⇒ 种子点速度恒等于该切向速度，其余格按刚体转动）。</summary>
	public void BuildSeeds(int seed, int plateCount, float minSpeed, float maxSpeed)
	{
		if (plateCount < 1)
			throw new ArgumentOutOfRangeException(nameof(plateCount),
				$"板块数须 ≥ 1（实得 {plateCount}）——0 块会让每格都没有归属，本阶段不产出那种世界");

		var rnd = new DeterministicRandom(SeedDerivation.Derive(seed, SeedDerivation.Plate_Seeds));
		var densRnd = new DeterministicRandom(SeedDerivation.Derive(seed, SeedDerivation.Plate_Density));
		Plates = new Plate[plateCount];
		for (int p = 0; p < plateCount; p++)
		{
			var dir = CoordUtil.UniformSphere(rnd);
			var (theta, phi) = CoordUtil.ToEuler(dir);
			float v = minSpeed + (maxSpeed - minSpeed) * (float)rnd.NextDouble();
			float psi = MathF.Tau * (float)rnd.NextDouble();     // 切平面内的运动方位
			float cosT = MathF.Cos(theta), sinT = MathF.Sin(theta);
			float cosP = MathF.Cos(phi), sinP = MathF.Sin(phi);
			var eTheta = new Vector3(cosT * cosP, -sinT, cosT * sinP);   // 沿经线向南
			var ePhi = new Vector3(-sinP, 0f, cosP);                     // 沿纬线向东
			var vTan = v * (MathF.Cos(psi) * eTheta + MathF.Sin(psi) * ePhi);
			Plates[p] = new Plate
			{
				PlateId = p,
				SeedTheta = theta,
				SeedPhi = phi,
				Omega = dir.Cross(vTan),    // ω ⊥ dir ⇒ `ω × dir` 恰为该切向速度
				Density = (float)densRnd.NextDouble(),
			};
		}
	}

	/// <summary>阻力场洪泛划分（加权测地 Voronoi）：全球一份 fBm 阻力场，所有种子点同时多源洪泛
	/// （Dijkstra，累积阻力×跨距最小优先），谁先漫到归谁。</summary>
	/// <param name="cells">逐格格 id（种子点找落格用）。</param>
	/// <param name="res">H3 分辨率。</param>
	/// <param name="dirs">格心单位方向（阻力场按它采样）。</param>
	/// <param name="neighbors">逐格邻接表（洪泛靠它扩散）。</param>
	/// <param name="amplitude">阻力起伏幅度。0 = 均匀阻力 ⇒ 退化为测地 Voronoi。</param>
	/// <param name="baseWavelengthKm">fBm 基波长（km）。</param>
	/// <param name="octaves">fBm 倍频层数（按采样下限截断）。</param>
	public void GrowWithResistance(ulong[] cells, int res, Vector3[] dirs, int[][] neighbors, int seed,
		float amplitude, float baseWavelengthKm, int octaves)
	{
		if (cells == null) throw new ArgumentNullException(nameof(cells));
		if (dirs == null) throw new ArgumentNullException(nameof(dirs));
		if (neighbors == null) throw new ArgumentNullException(nameof(neighbors));
		if (Plates.Length < 1) throw new InvalidOperationException("先 BuildSeeds 再 Grow（种子表为空）");
		if (neighbors.Length != cells.Length || dirs.Length != cells.Length)
			throw new ArgumentException($"邻接表/方向表长度须等于格数 {cells.Length}", nameof(neighbors));

		int n = cells.Length;

		// 阻力场全局一份（不是每块一份）：每格一个 > 0 的乘性跨越代价；
		// 层数按采样下限截断，与 ① 的噪声场同一条纪律。
		double minWavelengthKm = 3.0 * SpatialScale.ForRes(res, n).NeighborCenterKm;
		int safeOctaves = NoiseSampling.ClampOctaves(octaves, baseWavelengthKm, minWavelengthKm);
		float[] resistance = BuildResistanceField(dirs, seed, amplitude, baseWavelengthKm, safeOctaves);

		// ── 多源洪泛：种子代价 0，逐格扩到代价最小的邻居 ──
		var owner = new int[n];
		var cost = new double[n];
		var visited = new bool[n];
		Array.Fill(owner, -1);
		Array.Fill(cost, double.PositiveInfinity);

		var pq = new PriorityQueue<int, double>();
		for (int p = 0; p < Plates.Length; p++)
		{
			int cell = SeedCellOf(cells, res, Plates[p].SeedTheta, Plates[p].SeedPhi);
			if (owner[cell] >= 0) continue;      // 两块种子落同格 ⇒ 后者作废（先到先得）
			owner[cell] = p;
			cost[cell] = 0.0;
			pq.Enqueue(cell, 0.0);
		}

		while (pq.TryDequeue(out int i, out double c))
		{
			if (visited[i]) continue;          // 惰性删除（同格可能被多次入队）
			visited[i] = true;

			foreach (int j in neighbors[i])
			{
				if (visited[j]) continue;
				// 跨越代价 = 两格阻力的几何均值 × 跨距：阻力高处难长过去，边界绕开
				double step = Math.Sqrt((double)resistance[i] * resistance[j])
					* SpatialScale.DistanceKm(dirs[i], dirs[j]);
				double nc = c + step;
				if (nc < cost[j])
				{
					cost[j] = nc;
					owner[j] = owner[i];
					pq.Enqueue(j, nc);
				}
			}
		}

		// 洪泛覆盖全球（网格连通 ⇒ 每格必被访问）；漏格说明邻接表不连通，须报错而非静默留 −1
		for (int i = 0; i < n; i++)
			if (owner[i] < 0)
				throw new InvalidOperationException($"格 {i} 未被洪泛覆盖（邻接表不连通？）");

		var plateSize = new int[Plates.Length];
		for (int i = 0; i < n; i++) plateSize[owner[i]]++;

		// 板块邻接（跨板块相邻格对）：壳型分配要判两块陆壳是否挨着
		var adjacency = new bool[Plates.Length, Plates.Length];
		for (int i = 0; i < n; i++)
		{
			int a = owner[i];
			foreach (int j in neighbors[i])
			{
				int b = owner[j];
				if (a == b || adjacency[a, b]) continue;
				adjacency[a, b] = true;
				adjacency[b, a] = true;
			}
		}

		PlateOfCell = owner;
		SizeOfPlate = plateSize;
		PlateAdjacency = adjacency;
	}

	/// <summary>
	/// 派定壳型：每块按「格数 × 随机抖动」打分，按布局档挑出陆壳板块，其余洋壳。
	/// 陆壳块数 = clamp(round(板块数 × <c>ContinentalRatio</c>), 1, 板块数)，至少一块，否则全球是海。
	/// 抖动 ∈ [0.5,1.5) ⇒ 大板块更易当陆壳，但不是必然（面积重尾，实际接近按面积取前几大）。
	/// 各档只改排列（多大陆 / 群岛档额外多选几块），判据一律是板块邻接：Earthlike / ManyContinents
	/// 优先取不与已选相邻的板块（之间必夹洋壳 ⇒ 大陆被大洋隔开；容不下时按分补齐）；
	/// Pangea 只取与已选相邻者（陆壳连成一片）；Archipelago 块数最多、不做邻接约束
	/// （"碎"由 ⓪ 的基线压低、① 应用负责）。
	/// </summary>
	/// <param name="seed">世界种子（走 <c>Plate_Crust</c> 流）。</param>
	/// <param name="spec">板块配置（陆壳占比）。</param>
	/// <param name="layout">陆块布局档（<see cref="ContinentLayout.Auto"/> 须由调用方先定档）。</param>
	public void AssignCrustTypes(int seed, PlateSpec spec, ContinentLayout layout)
	{
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		int p = Plates.Length;
		if (p < 1) throw new InvalidOperationException("先 BuildSeeds 再 AssignCrustTypes（板块表为空）");
		if (SizeOfPlate.Length != p)
			throw new InvalidOperationException("先 GrowWithResistance 再 AssignCrustTypes（缺各板块格数）");
		if (PlateAdjacency.GetLength(0) != p)
			throw new InvalidOperationException("先 GrowWithResistance 再 AssignCrustTypes（缺板块邻接）");

		var rnd = new DeterministicRandom(SeedDerivation.Derive(seed, SeedDerivation.Plate_Crust));
		int baseWant = Math.Clamp((int)MathF.Round(p * spec.ContinentalRatio), 1, p);

		int want;
		switch (layout)
		{
			case ContinentLayout.ManyContinents:
				want = Math.Min(p, baseWant + ContinentLayouts.ManyExtraPlates);
				break;
			case ContinentLayout.Archipelago:
				want = Math.Min(p, baseWant + ContinentLayouts.ArchipelagoExtraPlates);
				break;
			default:                                   // Earthlike / Pangea（Auto 已在外层定档）
				want = baseWant;
				break;
		}

		var score = new float[p];
		for (int k = 0; k < p; k++)
			score[k] = SizeOfPlate[k] * (0.5f + (float)rnd.NextDouble());

		// 候选顺序：分高者在前；同分按板块号 ⇒ 与线程 / 调用次序无关的全序
		var order = new int[p];
		for (int k = 0; k < p; k++) order[k] = k;
		Array.Sort(order, (a, b) => score[a] != score[b] ? score[b].CompareTo(score[a]) : a.CompareTo(b));

		var chosen = new bool[p];
		var picked = new List<int>(want);
		switch (layout)
		{
			case ContinentLayout.Pangea:      PickContiguous(order, chosen, picked, want); break;
			case ContinentLayout.Archipelago: PickByScore(order, chosen, picked, want); break;
			default:                          PickSplit(order, chosen, picked, want); break;
		}

		for (int k = 0; k < p; k++) Plates[k].Crust = CrustType.Oceanic;
		foreach (int k in picked) Plates[k].Crust = CrustType.Continental;
	}

	/// <summary>连片挑选：第一块取分最高者，之后每轮只从与已选板块相邻的候选里取分最高者
	/// ⇒ 选出的陆壳板块彼此连通。邻接图不连通时兜底取分最高者（否则块数不达标）。</summary>
	void PickContiguous(int[] order, bool[] chosen, List<int> picked, int want)
	{
		while (picked.Count < want)
		{
			int best = -1;
			foreach (int c in order)              // order 已按分降序 ⇒ 第一个命中即最高
			{
				if (chosen[c]) continue;
				if (picked.Count > 0 && !TouchesChosen(c, picked)) continue;
				best = c;
				break;
			}
			if (best < 0)                          // 兜底：邻接图上够不到那么远
			{
				foreach (int c in order)
					if (!chosen[c]) { best = c; break; }
			}
			if (best < 0) break;
			chosen[best] = true;
			picked.Add(best);
		}
	}

	/// <summary>分开挑选：先按分降序取不与已选陆壳相邻的板块；邻接图容不下时再按分补齐
	/// ⇒ 少数几块会连在一起，像欧亚与非洲。块数一定达标，优先保分散。</summary>
	void PickSplit(int[] order, bool[] chosen, List<int> picked, int want)
	{
		foreach (int c in order)
		{
			if (picked.Count >= want) break;
			if (chosen[c] || TouchesChosen(c, picked)) continue;
			chosen[c] = true;
			picked.Add(c);
		}
		foreach (int c in order)
		{
			if (picked.Count >= want) break;
			if (chosen[c]) continue;
			chosen[c] = true;
			picked.Add(c);
		}
	}

	/// <summary>只看分数：按分降序取满（不做邻接约束）。</summary>
	void PickByScore(int[] order, bool[] chosen, List<int> picked, int want)
	{
		foreach (int c in order)
		{
			if (picked.Count >= want) break;
			if (chosen[c]) continue;
			chosen[c] = true;
			picked.Add(c);
		}
	}

	/// <summary>该板块是否与任一块已选陆壳相邻。</summary>
	bool TouchesChosen(int candidate, List<int> picked)
	{
		foreach (int q in picked)
			if (PlateAdjacency[candidate, q]) return true;
		return false;
	}

	/// <summary>造全球阻力场：每格一个 > 0 的乘性跨越代价（噪声 ∈ [−1,1] ⇒ 阻力恒正）。
	/// 单位阻力（amplitude ≤ 0）⇒ 纯测地 Voronoi（边界是直线弧）。</summary>
	/// <param name="dirs">格心单位方向。</param>
	/// <param name="seed">世界种子（走 `Plate_Distort` 噪声流）。</param>
	/// <param name="amplitude">阻力起伏幅度。≤ 0 = 均匀阻力。</param>
	/// <param name="baseWavelengthKm">fBm 基波长（km）。</param>
	/// <param name="octaves">fBm 倍频层数。</param>
	static float[] BuildResistanceField(Vector3[] dirs, int seed,
		float amplitude, float baseWavelengthKm, int octaves)
	{
		var resistance = new float[dirs.Length];
		if (!(amplitude > 0f))
		{
			Array.Fill(resistance, 1f);   // 省掉整场噪声采样
			return resistance;
		}

		// 乘性 (1+amp)^noise：噪声 ±1 造成成倍阻力差，边界才会被"挡"得绕行
		int noiseSeed = SeedDerivation.Derive(seed, SeedDerivation.Plate_Distort);
		var noise = new SphericalFbmNoise(noiseSeed, baseWavelengthKm, octaves);
		for (int i = 0; i < resistance.Length; i++)
			resistance[i] = MathF.Pow(1f + amplitude, noise.Sample(dirs[i]));
		return resistance;
	}

	/// <summary>种子点 (θ,φ) 所在格（`LatLngToCell` 映射成 id，再查格表）。</summary>
	static int SeedCellOf(ulong[] cells, int res, float theta, float phi)
	{
		LatLng g = CoordUtil.EulerToLatLng(theta, phi);
		ulong id = H3.LatLngToCell(g, res);
		for (int i = 0; i < cells.Length; i++)
			if (cells[i] == id) return i;
		throw new InvalidOperationException($"种子点 ({theta:F4}, {phi:F4}) 落到的格不在格表（res{res}）");
	}
}
