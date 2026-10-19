using System;
using System.Collections.Generic;   // PriorityQueue（洪泛的优先队列）
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Data;            // Plate / PlateSpec（数据载体与配置）
using World.H3Grid;          // Ball（H3 球壳数据层）
using World.Utils;           // CoordUtil（球面工具）+ SphericalFbmNoise（阻力场）
using World.Utils.H3;        // H3（LatLngToCell：种子点找落格）

namespace World.Logic;

/// <summary>
/// 板块实现：撒种子点（带速度）→ fBm 阻力场洪泛划分 → 派定壳型（陆壳 / 洋壳）。
/// 步骤由 <see cref="PlatePipeline"/> 编排，参数从 <see cref="PlateSpec"/> 传入。
///
/// · 用洪泛而非加噪 argmin：保证连通，不出飞地。
/// · 边界 = 累积代价等值线，被阻力场扭成蜿蜒曲线。
/// · 面积重尾（大板块吞小块）是有意为之，不做 Lloyd 松弛。
/// · **壳型是本阶段唯一的陆海产物**：陆壳的格由 ① 阶段铺陆面高度、洋壳铺海面高度。
///   海岸线不由本阶段决定，而是 ① 阶段高度场穿过海面的结果。
/// </summary>
public sealed class PlateManager
{
	/// <summary>逐格板块号（长度 = 格数；每格恒有归属，取值 ∈ [0, 板块数)）。</summary>
	public int[] PlateOfCell { get; private set; } = Array.Empty<int>();
	/// <summary>板块表（号即下标；含每块壳型 <see cref="Plate.Crust"/>）。</summary>
	public Plate[] Plates { get; private set; } = Array.Empty<Plate>();
	/// <summary>逐块格数（长度 = 板块数；Σ = 格数）。</summary>
	public int[] SizeOfPlate { get; private set; } = Array.Empty<int>();

	/// <summary>撒种子点：球面均匀随机撒 `plateCount` 个点，每点附随机速度与密度。
	/// 速度 = 种子点处切向速率 V ∈ [minSpeed, maxSpeed) + 切平面内随机方位 ψ；
	/// 由它反解**欧拉矢量** ω（ω ⊥ 种子点方向 ⇒ 种子点速度恒等于该切向速度，其余格按刚体转动）。</summary>
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

	/// <summary>阻力场洪泛划分（加权测地 Voronoi）：全球格心一份 fBm 阻力场，
	/// 从所有种子点同时多源洪泛（Dijkstra，累积阻力×跨距最小优先），谁先漫到归谁。
	/// 边界 = 累积代价等值线，被阻力场扭成蜿蜒曲线；连通性由洪泛保证，不出飞地。</summary>
	/// <param name="cells">逐格格 id（种子点找落格用）。</param>
	/// <param name="res">H3 分辨率。</param>
	/// <param name="dirs">格心单位方向（阻力场按它采样）。</param>
	/// <param name="neighbors">逐格邻接表（洪泛靠它扩散）。</param>
	/// <param name="seed">世界种子。</param>
	/// <param name="amplitude">阻力起伏幅度。0 = 均匀阻力 ⇒ 退化为测地 Voronoi。</param>
	/// <param name="baseWavelengthKm">fBm 基波长（km）。</param>
	/// <param name="octaves">fBm 倍频层数。</param>
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

		// 阻力场（全局一份，不是每块一份）：每格一个 > 0 的**乘性**跨越代价
		float[] resistance = BuildResistanceField(dirs, seed, amplitude, baseWavelengthKm, octaves);

		// ── 多源洪泛：种子点入队（代价 0），逐格扩到代价最小的邻居 ──
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
				// 跨越代价 = 两格阻力的**几何均值** × 跨距——阻力高的地方"难长过去"，边界就绕开
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

		// 洪泛覆盖全球（网格连通 ⇒ 每格必被访问）；漏格说明网格不连通，明确报错而非静默留 −1
		for (int i = 0; i < n; i++)
			if (owner[i] < 0)
				throw new InvalidOperationException($"格 {i} 未被洪泛覆盖（邻接表不连通？）");

		// 统计
		var plateSize = new int[Plates.Length];
		for (int i = 0; i < n; i++) plateSize[owner[i]]++;

		PlateOfCell = owner;
		SizeOfPlate = plateSize;
	}

	/// <summary>派定壳型：每块板块按「格数 × 随机抖动」打分，取前 N 块当**陆壳**，其余**洋壳**。
	/// N = clamp(round(板块数 × <paramref name="spec"/>.ContinentalRatio), 1, 板块数)——
	/// 至少留一块陆壳，否则一个陆地平台也没有（⇒ 全球是海）。
	/// 抖动 ∈ [0.5,1.5) ⇒ 大板块更容易当陆壳，但不是必然。</summary>
	/// <param name="seed">世界种子（走 `Plate_Crust` 流）。</param>
	/// <param name="spec">板块配置（陆壳占比）。</param>
	public void AssignCrustTypes(int seed, PlateSpec spec)
	{
		int p = Plates.Length;
		if (p < 1) throw new InvalidOperationException("先 BuildSeeds 再 AssignCrustTypes（板块表为空）");
		if (SizeOfPlate.Length != p)
			throw new InvalidOperationException("先 GrowWithResistance 再 AssignCrustTypes（缺各板块格数）");

		var rnd = new DeterministicRandom(SeedDerivation.Derive(seed, SeedDerivation.Plate_Crust));
		int want = Math.Clamp((int)MathF.Round(p * spec.ContinentalRatio), 1, p);

		var order = new int[p];
		var score = new float[p];
		for (int k = 0; k < p; k++)
		{
			order[k] = k;
			score[k] = SizeOfPlate[k] * (0.5f + (float)rnd.NextDouble());
		}
		// 分高者在前；同分按板块号 ⇒ 与线程 / 调用次序无关的全序
		Array.Sort(order, (a, b) => score[a] != score[b] ? score[b].CompareTo(score[a]) : a.CompareTo(b));

		for (int k = 0; k < p; k++) Plates[k].Crust = CrustType.Oceanic;
		for (int i = 0; i < want; i++) Plates[order[i]].Crust = CrustType.Continental;
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
			Array.Fill(resistance, 1f);   // 均匀阻力 ⇒ 纯测地 Voronoi（省掉整场噪声采样）
			return resistance;
		}

		// 乘性 (1+amp)^noise：让噪声 ±1 造成成倍阻力差，边界才会被"挡"得绕行
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
