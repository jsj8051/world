using System;
using System.Collections.Generic;   // PriorityQueue（洪泛的优先队列）
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Data;            // Plate / ContinentAnchor / PlateSpec（数据载体与配置）
using World.H3Grid;          // Ball（H3 球壳数据层）
using World.Utils;           // CoordUtil（球面工具）+ SphericalFbmNoise（阻力场/陆地场）
using World.Utils.H3;        // H3（LatLngToCell：种子点找落格）

namespace World.Logic;

/// <summary>
/// 板块实现：撒种子点（带速度）→ fBm 阻力场洪泛划分 → 派定壳型（陆壳/洋壳）
/// → 只在陆壳上派生大陆锚点 → 逐格陆地掩码。
/// 步骤由 <see cref="PlatePipeline"/> 编排，参数从 <see cref="PlateSpec"/> 传入。
///
/// · 用洪泛而非加噪 argmin：保证连通，不出飞地。
/// · 边界 = 累积代价等值线，被阻力场扭成蜿蜒曲线。
/// · 面积重尾（大板块吞小块）是有意为之，不做 Lloyd 松弛。
/// · 壳型先于陆场：陆壳才发锚点 ⇒ 陆地长在陆壳上，洋壳是海。
/// </summary>
public sealed class PlateManager
{
	/// <summary>逐格板块号（长度 = 格数；每格恒有归属，取值 ∈ [0, 板块数)）。</summary>
	public int[] PlateOfCell { get; private set; } = Array.Empty<int>();
	/// <summary>板块表（号即下标）。</summary>
	public Plate[] Plates { get; private set; } = Array.Empty<Plate>();
	/// <summary>逐块格数（长度 = 板块数；Σ = 格数）。</summary>
	public int[] SizeOfPlate { get; private set; } = Array.Empty<int>();
	/// <summary>大陆锚点表（**陆壳一对一**：每块陆壳板块一个锚点，按板块号升序；由 <see cref="BuildAnchors"/> 填）。</summary>
	public ContinentAnchor[] Anchors { get; private set; } = Array.Empty<ContinentAnchor>();
	/// <summary>陆地掩码（海格 false / 陆格 true；由 <see cref="BuildLandMask"/> 填）。生成依据，非世界事实。</summary>
	public bool[] PlacementLand { get; private set; } = Array.Empty<bool>();

	// 锚点属性值域（地球量级）
	const float MinRadiusKm = 1800f, MaxRadiusKm = 4200f;
	const float MaxStretch = 2.2f;
	const float MinCoastCx = 0.5f, MaxCoastCx = 1.6f;
	const float MinWeight = 0.8f, MaxWeight = 1.25f;   // 影响权重（进影响场 ⇒ 加权 Voronoi）

	/// <summary>撒种子点：球面均匀随机撒 `plateCount` 个点，每点附随机速度。
	/// 速度 = 速率 V ∈ [minSpeed, maxSpeed) + 随机方向 ψ，分解成 (VTheta, VPhi) 两个分量。</summary>
	public void BuildSeeds(int seed, int plateCount, float minSpeed, float maxSpeed)
	{
		if (plateCount < 1)
			throw new ArgumentOutOfRangeException(nameof(plateCount),
				$"板块数须 ≥ 1（实得 {plateCount}）——0 块会让每格都没有归属，本阶段不产出那种世界");

		var rnd = new DeterministicRandom(SeedDerivation.Derive(seed, SeedDerivation.Plate_Seeds));
		Plates = new Plate[plateCount];
		for (int p = 0; p < plateCount; p++)
		{
			var (theta, phi) = CoordUtil.ToEuler(CoordUtil.UniformSphere(rnd));
			float v = minSpeed + (maxSpeed - minSpeed) * (float)rnd.NextDouble();
			float psi = MathF.Tau * (float)rnd.NextDouble();     // 切平面内的运动方位
			float sinTheta = MathF.Max(MathF.Abs(MathF.Sin(theta)), 1e-3f);   // 夹下界防极区发散
			Plates[p] = new Plate
			{
				PlateId = p,
				SeedTheta = theta,
				SeedPhi = phi,
				V = v,
				VTheta = v * MathF.Cos(psi),
				VPhi = v * MathF.Sin(psi) / sinTheta,
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
	/// 至少留一块陆壳，否则一个锚点也发不出来（⇒ 全球是海）。
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

	/// <summary>派生大陆锚点（**陆壳一对一**）：每块陆壳板块 → 一个锚点，位置 = 板块种子点方向，
	/// 属性（半径/拉伸/朝向/海岸复杂度/权重）按固定次序随机派生；**洋壳板块不发锚点**。</summary>
	/// <param name="seed">世界种子（属性派生用）。</param>
	public void BuildAnchors(int seed)
	{
		if (Plates.Length < 1) throw new InvalidOperationException("先 BuildSeeds 再 BuildAnchors（板块表为空）");

		int cont = 0;
		for (int k = 0; k < Plates.Length; k++) if (Plates[k].Crust == CrustType.Continental) cont++;
		if (cont < 1)
			throw new InvalidOperationException("先 AssignCrustTypes 再 BuildAnchors（没有陆壳板块 ⇒ 一个锚点也发不出来）");

		var rnd = new DeterministicRandom(seed);
		var list = new List<ContinentAnchor>(cont);

		// 属性：固定次序（半径→拉伸→轴→海岸→权重）逐锚点派生——增删锚点不影响前面锚点的属性
		for (int c = 0; c < Plates.Length; c++)
		{
			// 洋壳板块跳过 ⇒ 陆地只产在陆壳上
			if (Plates[c].Crust != CrustType.Continental) continue;
			var center = CoordUtil.LatLngToSphere(
				CoordUtil.EulerToLatLng(Plates[c].SeedTheta, Plates[c].SeedPhi), 1f).Normalized();
			float radiusKm = MinRadiusKm + (MaxRadiusKm - MinRadiusKm) * (float)rnd.NextDouble();
			float stretchU = 1f + (MaxStretch - 1f) * (float)rnd.NextDouble();
			float stretchV = 1f + (stretchU - 1f) * (float)rnd.NextDouble();   // V ≤ U：单轴为主的长条，避免双轴乱拉伸
			float rot = MathF.PI * (float)rnd.NextDouble();
			var (u, v) = TangentBasis(center);
			var axisU = (u * MathF.Cos(rot) + v * MathF.Sin(rot)).Normalized();
			var axisV = center.Cross(axisU);                                   // 切平面内与 U 正交（右手系）
			float coastCx = MinCoastCx + (MaxCoastCx - MinCoastCx) * (float)rnd.NextDouble();
			float weight = MinWeight + (MaxWeight - MinWeight) * (float)rnd.NextDouble();
			list.Add(new ContinentAnchor
			{
				Dir = center,
				RadiusKm = radiusKm,
				AxisU = axisU,
				AxisV = axisV,
				StretchU = stretchU,
				StretchV = stretchV,
				CoastComplexity = coastCx,
				Weight = weight,
			});
		}
		Anchors = list.ToArray();
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

	/// <summary>中心方向的切平面正交基（与 dir 构成右手系；极轴退化回退 Right）。</summary>
	static (Vector3 u, Vector3 v) TangentBasis(Vector3 dir)
	{
		var refUp = MathF.Abs(dir.Y) < 0.9f ? Vector3.Up : Vector3.Right;
		var u = refUp.Cross(dir).Normalized();
		var v = dir.Cross(u);
		return (u, v);
	}

	/// <summary>造逐格陆地掩码：逐格标量 = 锚点影响场 + 域扭曲 + 大/中/小三尺度噪声，
	/// &gt; <paramref name="spec"/>.SeaLevel 即陆。
	/// 陆地标量直接在 H3 上采（<see cref="H3TerrainSampler"/> 多点采样），无连续场。</summary>
	/// <param name="ball">球壳数据层。</param>
	/// <param name="seed">世界种子。</param>
	/// <param name="spec">板块配置（阈值/噪声）。</param>
	public void BuildLandMask(Ball ball, int seed, PlateSpec spec)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (Anchors.Length < 1) throw new InvalidOperationException("先 BuildAnchors 再 BuildLandMask（锚点表为空）");

		// ① 逐格陆地标量：域扭曲 → 影响场 + 大/中/小三尺度噪声
		Func<Vector3, float> landField = BuildLandField(seed, spec);
		float[] raw = new H3TerrainSampler(ball).SampleField(landField);

		// ② 判陆（> SeaLevel 即陆）
		int n = ball.CellDirs.Length;
		PlacementLand = new bool[n];
		for (int i = 0; i < n; i++)
			PlacementLand[i] = raw[i] > spec.SeaLevel;
	}

	/// <summary>逐点陆地标量函数：域扭曲后的方向 → 影响场 + 大/中/小三尺度噪声
	/// （中/小尺度幅度乘归属锚点的 CoastComplexity）。</summary>
	/// <param name="seed">世界种子。</param>
	/// <param name="spec">板块配置。</param>
	Func<Vector3, float> BuildLandField(int seed, PlateSpec spec)
	{
		var anchors = Anchors;

		// 噪声派生：固定次序（三路扭曲 → 大 → 中 → 小）
		var rnd = new DeterministicRandom(seed);
		var w1 = new SphericalFbmNoise(rnd.Next(), spec.WarpWavelengthKm, spec.WarpOctaves);
		var w2 = new SphericalFbmNoise(rnd.Next(), spec.WarpWavelengthKm, spec.WarpOctaves);
		var w3 = new SphericalFbmNoise(rnd.Next(), spec.WarpWavelengthKm, spec.WarpOctaves);
		var low = new SphericalFbmNoise(rnd.Next(), spec.LowWavelengthKm, spec.LowOctaves);
		var med = new SphericalFbmNoise(rnd.Next(), spec.MediumWavelengthKm, 3);
		var small = new SphericalFbmNoise(rnd.Next(), spec.SmallWavelengthKm, 2);

		return dir =>
		{
			Vector3 d = Warp(dir, spec, w1, w2, w3);

			int a = NearestAnchor(anchors, d, out float influence);
			var anchor = anchors[a];
			float v = influence;
			v += spec.LowAmplitude * low.Sample(d);
			v += spec.MediumAmplitude * anchor.CoastComplexity * med.Sample(d);
			v += spec.SmallAmplitude * anchor.CoastComplexity * small.Sample(d);
			return Math.Clamp(v, 0f, 1f);   // 陆地标量值域 [0,1]
		};
	}

	/// <summary>域扭曲：方向加三路噪声偏移后归一化。幅度 ≤ 0 ⇒ 原样返回。</summary>
	static Vector3 Warp(Vector3 dir, PlateSpec spec,
		SphericalFbmNoise w1, SphericalFbmNoise w2, SphericalFbmNoise w3)
	{
		if (spec.WarpAmplitudeKm <= 0f) return dir;
		var w = new Vector3(w1.Sample(dir), w2.Sample(dir), w3.Sample(dir));
		return (dir + w * (spec.WarpAmplitudeKm / SphericalFbmNoise.EarthRadiusKm)).Normalized();
	}

	/// <summary>锚点影响场：逐点 = 各锚点「椭圆角距衰减 × 权重」的最大值 ÷ 最大权重 ⇒ 值域 [0,1]
	/// （锚点中心 = 1，影响半径外 = 0）；归属 = argmax 平滑度（归一化 ⇒ 每个锚点中心恒归属自己）。</summary>
	static int NearestAnchor(ContinentAnchor[] anchors, Vector3 dir, out float influence)
	{
		int best = 0;
		float bestInf = -1f, bestSite = -1f;
		for (int i = 0; i < anchors.Length; i++)
		{
			var a = anchors[i];
			// 椭圆角距：p−c 的切平面分量投到 U/V 轴、按拉伸缩小 ⇒ 长轴方向影响更远
			Vector3 delta = dir - a.Dir * dir.Dot(a.Dir);
			float du = delta.Dot(a.AxisU) / a.StretchU;
			float dv = delta.Dot(a.AxisV) / a.StretchV;
			float dRad = MathF.Sqrt(du * du + dv * dv);
			float rRad = a.RadiusKm / SphericalFbmNoise.EarthRadiusKm;
			float t = Math.Clamp(1f - dRad / rRad, 0f, 1f);
			float smooth = t * t * (3f - 2f * t);
			float inf = a.Weight / MaxWeight * smooth;   // 除以最大权重 ⇒ 值域 [0,1]
			if (inf > bestInf) bestInf = inf;
			if (smooth > bestSite) { bestSite = smooth; best = i; }
		}
		influence = bestInf;
		return best;
	}
}
