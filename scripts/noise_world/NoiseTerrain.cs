using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Utils;

namespace World.NoiseWorld;

// 噪声地形 · 逻辑层（"是什么就是什么"第一步，2026-09-30 重构）：
//   L0 大陆场（低频 fBm）→ L1 域扭曲（低频矢量场扭采样坐标）→ **段2 域内秩**（海陆由板块身份
//   定死——陆板全陆、洋板全洋；L0 噪声在陆/洋各自域内求秩得 h ∈[+0.03,1] / [−1,−0.03]）→
//   L2 山系带（ridged × 低秩衰减掩码，与碰撞带掩码取 max）→ L4 合成 → L5 单调样条 → 米域；
//   L3 细节按 w2 加在米域（陆侧渐入）。海拔符号在米域硬钳 ±1m ⇒ 恒等于板块身份（不变量测试钉死）。
// 目标陆地占比 = **陆板占比**口径（板块数；陆地面积 = 陆板面积之和，身份语义的固有特性）。
// 确定性红线：SphericalFbmNoise 同种子同方向逐位同、不耗 rng；层种子经 DeterministicRandom
//   按**固定次序**派生（L0→W1→W2→W3→山带→细节，开关状态不改变次序 ⇒ 开关不挪别的层的场）。
// 分层缓存（噪声地形-01 §4 数据轨）：原始场（含山带/细节噪声）→ 域内秩 → 高程 三段，
//   各段只在上游真变更时重算；段判据 = 逐字段快照对比（参数量少，诚实直写）。
public sealed class NoiseTerrain
{
	public const int LayerContinent = 0;
	public const int LayerWarp = 1;
	public const int LayerRidge = 2;
	public const int LayerDetail = 3;
	public const int LayerCount = 4;

	public static string LayerName(int layer) => layer switch
	{
		LayerContinent => "L0 大陆场",
		LayerWarp => "L1 域扭曲",
		LayerRidge => "L2 山系带",
		LayerDetail => "L3 细节层",
		_ => $"层{layer}",
	};

	public TerrainNoiseParams Params { get; } = new();

	readonly bool[] _layerOn = { true, true, true, true };   // 图层开关（预览组合；关 = 该层不参与合成）
	bool _dirty = true;
	float[] _elevationM = Array.Empty<float>();  // 逐格海拔（米），顺序与 Ball.CellDirs 对齐

	/// <summary>逐格海拔（米）。</summary>
	public float[] ElevationM => _elevationM;

	// ── 分层缓存只读暴露口（地图模式显示用；数组本体复用，零拷贝）──
	/// <summary>L0 大陆场原值 ≈[-1,1]（扭曲坐标上采样；L0 关 = 全 0）。</summary>
	public float[] ContinentRaw => _continentRaw;
	/// <summary>L2 山带 ridged 源噪声 ∈[-1,1]（整形 (1-|n|)^RidgePower 由显示侧按需算）。</summary>
	public float[] RidgeRaw => _ridgeRaw;
	/// <summary>L3 细节 fBm ∈[-1,1]。</summary>
	public float[] DetailRaw => _detailRaw;
	/// <summary>域内秩（带符号 ∈[-1,1]；陆 + / 洋 −，身份定侧；重映射流水线的中间真相）。</summary>
	public float[] DomainRank => _hNorm;

	/// <summary>参数/开关已变更待重算（组装器防抖后调 Generate）。</summary>
	public bool Dirty => _dirty;

	// ── 段缓存 ──
	float[] _continentRaw = Array.Empty<float>();   // 段1a：L0 原值 ≈[-1,1]（扭曲坐标上采样）
	float[] _ridgeRaw = Array.Empty<float>();       // 段1b：L2 ridged 源噪声 n ∈[-1,1]（同坐标）
	float[] _detailRaw = Array.Empty<float>();      // 段1c：L3 细节 fBm ∈[-1,1]（同坐标）
	float[] _hNorm = Array.Empty<float>();          // 段2：域内秩（带符号 ∈[-1,1]；陆 + / 洋 −，身份定侧）
	bool[] _cellIsLand = Array.Empty<bool>();       // 段2：逐格身份快照（段3 分支/钳制用——浮点符号在 ±0 附近不可靠）
	int[] _distToLand = Array.Empty<int>();         // 段2：离岸跳数（BFS；近岸浅水环 + 海沟离岸淡出用）
	float[] _plateBelt = Array.Empty<float>();      // 段1e：C 碰撞带·仰冲侧造山掩码 ∈[0,1]（强度随碰撞沿走向起伏）
	float[] _plateTrench = Array.Empty<float>();    // 段1e：C 碰撞带·俯冲洋侧海沟强度 ∈[0,1]（米域挖深）
	RawSnap _rawSnap;                               // 段1 重算判据快照
	DomainSnap _domainSnap;                         // 段2 重算判据快照
	BeltSnap _beltSnap;                             // 段1e 重算判据快照
	ElevSnap _elevSnap;                             // 段3 重算判据快照

	struct RawSnap
	{
		public int Seed, ContOctaves, WarpOctaves;
		public float ContWl, WarpWl, WarpAmp, RidgeWl, DetailWl, Lacunarity, Gain;
		public bool WarpOn, RidgeOn, DetailOn, ContOn;
	}

	struct DomainSnap
	{
		public int SeedUsed, PlateCount;
		public float LandTarget;
	}

	struct BeltSnap
	{
		public int SeedUsed, PlateCount;
		public float ConvThreshold;
		public int BeltWidth;
	}

	struct ElevSnap
	{
		public float W0, W1, W2, Sharp, DetailAmpM, CoastWobble, BeltWeight;
		public float SplineAbyssM, SplineShelfM, SplinePlainM, SplineHillM, SplineHighlandM, SplinePeakM;
	}

	/// <summary>全量重算（Godot 侧入口）：段1a 原始场 → 段2 域内秩 → 段1e 碰撞带 → 段3 米域，
	/// 段按快照跳过。耗时由组装器打印（测量项①）。
	/// plates 必传——陆/洋板身份是海陆的唯一来源（"是什么就是什么"第一步）；neighbors 供碰撞带。</summary>
	public void Generate(Vector3[] cellDirs, NoisePlates plates, int[][] neighbors)
	{
		if (plates?.LandPlate == null || plates.LandPlate.Length != plates.NumPlates)
			throw new InvalidOperationException("板块身份缺失：Generate 需要已 Generate 的 NoisePlates（陆/洋板身份）");

		int n = cellDirs.Length;
		var p = Params;

		// 段1：原始场（L0/山带/细节三路噪声，同一扭曲坐标；L0 关 = 全 0，直接基准球出口）
		bool rawDirty = _dirty || _continentRaw.Length != n
			|| _rawSnap.Seed != p.Seed || _rawSnap.ContOctaves != p.ContinentOctaves
			|| _rawSnap.WarpOctaves != p.WarpOctaves
			|| _rawSnap.ContWl != p.ContinentWavelengthKm
			|| _rawSnap.WarpWl != p.WarpWavelengthKm || _rawSnap.WarpAmp != p.WarpAmplitudeKm
			|| _rawSnap.RidgeWl != p.RidgeWavelengthKm || _rawSnap.DetailWl != p.DetailWavelengthKm
			|| _rawSnap.Lacunarity != p.OctaveLacunarity || _rawSnap.Gain != p.OctaveGain
			|| _rawSnap.WarpOn != _layerOn[LayerWarp] || _rawSnap.ContOn != _layerOn[LayerContinent]
			|| _rawSnap.RidgeOn != _layerOn[LayerRidge] || _rawSnap.DetailOn != _layerOn[LayerDetail];
		if (rawDirty)
		{
			SampleRawFields(cellDirs, p);
			_rawSnap = new RawSnap
			{
				Seed = p.Seed, ContOctaves = p.ContinentOctaves, WarpOctaves = p.WarpOctaves,
				ContWl = p.ContinentWavelengthKm,
				WarpWl = p.WarpWavelengthKm, WarpAmp = p.WarpAmplitudeKm,
				RidgeWl = p.RidgeWavelengthKm, DetailWl = p.DetailWavelengthKm,
				Lacunarity = p.OctaveLacunarity, Gain = p.OctaveGain,
				WarpOn = _layerOn[LayerWarp], ContOn = _layerOn[LayerContinent],
				RidgeOn = _layerOn[LayerRidge], DetailOn = _layerOn[LayerDetail],
			};
		}

		if (!_layerOn[LayerContinent])
		{
			if (_elevationM.Length != n) _elevationM = new float[n];   // 基准球（海拔处处 0）
			_dirty = false;
			return;
		}

		// 段2：域内秩（第一步：海陆 = 板块身份定死；秩只在陆/洋各自域内分布起伏）
		int plateSeed = plates.SeedUsed;
		int plateCount = plates.NumPlates;
		bool domainDirty = rawDirty || _hNorm.Length != n
			|| _domainSnap.SeedUsed != plateSeed || _domainSnap.PlateCount != plateCount
			|| _domainSnap.LandTarget != p.LandFractionTarget;
		if (domainDirty)
		{
			BuildDomainRanks(plates, p, cellDirs, neighbors);
			_domainSnap = new DomainSnap { SeedUsed = plateSeed, PlateCount = plateCount, LandTarget = p.LandFractionTarget };
		}

		// 段1e：碰撞带场（C 方案；需邻接表——无邻接直接零场）
		bool beltDirty = _plateBelt.Length != n
			|| _beltSnap.SeedUsed != plateSeed || _beltSnap.PlateCount != plateCount
			|| _beltSnap.ConvThreshold != p.PlateConvThreshold || _beltSnap.BeltWidth != p.PlateBeltWidthCells;
		if (beltDirty)
		{
			BuildPlateBelt(plates, neighbors, cellDirs, p);
			_beltSnap = new BeltSnap { SeedUsed = plateSeed, PlateCount = plateCount, ConvThreshold = p.PlateConvThreshold, BeltWidth = p.PlateBeltWidthCells };
		}

		// 段3：域内秩 → 合成 → 样条 → 米域（廉价全场组合；任一造型参数变更即重算）
		bool elevDirty = _dirty || rawDirty || domainDirty || beltDirty
			|| _elevationM.Length != n || !_elevSnap.Equals(GetElevSnap(p));
		if (elevDirty)
		{
			MapQuantileToElevation(n, p);
			_elevSnap = GetElevSnap(p);
		}
		_dirty = false;
	}

	ElevSnap GetElevSnap(TerrainNoiseParams p) => new()
	{
		W0 = p.WeightContinent, W1 = p.WeightRidge, W2 = p.WeightDetail,
		Sharp = p.RidgeMaskSharpness, DetailAmpM = p.DetailAmplitudeM, CoastWobble = p.CoastWobble,
		BeltWeight = p.PlateBeltWeight,
		SplineAbyssM = p.SplineAbyssM, SplineShelfM = p.SplineShelfM, SplinePlainM = p.SplinePlainM,
		SplineHillM = p.SplineHillM, SplineHighlandM = p.SplineHighlandM, SplinePeakM = p.SplinePeakM,
	};

	// 段1：层种子固定次序派生（与开关状态无关）+ 同一扭曲坐标采三路噪声。
	void SampleRawFields(Vector3[] cellDirs, TerrainNoiseParams p)
	{
		int n = cellDirs.Length;
		if (_continentRaw.Length != n) _continentRaw = new float[n];
		if (_ridgeRaw.Length != n) _ridgeRaw = new float[n];
		if (_detailRaw.Length != n) _detailRaw = new float[n];

		var rnd = new DeterministicRandom(p.Seed);
		int seedL0 = rnd.Next();
		int seedW1 = rnd.Next(), seedW2 = rnd.Next(), seedW3 = rnd.Next();
		int seedRidge = rnd.Next(), seedDetail = rnd.Next();

		var nL0 = new SphericalFbmNoise(seedL0, p.ContinentWavelengthKm, p.ContinentOctaves,
			p.OctaveLacunarity, p.OctaveGain);
		bool warp = _layerOn[LayerWarp];
		// 扭曲倍频 = 走势旋钮：4 起位移场带 500-1000km 结构 ⇒ 海岸出现半岛/海湾的性格。
		var nW1 = warp ? new SphericalFbmNoise(seedW1, p.WarpWavelengthKm, p.WarpOctaves, p.OctaveLacunarity, p.OctaveGain) : null;
		var nW2 = warp ? new SphericalFbmNoise(seedW2, p.WarpWavelengthKm, p.WarpOctaves, p.OctaveLacunarity, p.OctaveGain) : null;
		var nW3 = warp ? new SphericalFbmNoise(seedW3, p.WarpWavelengthKm, p.WarpOctaves, p.OctaveLacunarity, p.OctaveGain) : null;
		// 山带 4 倍频（链的走向+复杂度）；细节 3 倍频（碎质感）。
		bool ridgeOn = _layerOn[LayerRidge];
		var nRidge = ridgeOn ? new SphericalFbmNoise(seedRidge, p.RidgeWavelengthKm, 4, p.OctaveLacunarity, p.OctaveGain) : null;
		bool detailOn = _layerOn[LayerDetail];
		var nDetail = detailOn ? new SphericalFbmNoise(seedDetail, p.DetailWavelengthKm, 3, p.OctaveLacunarity, p.OctaveGain) : null;
		float warpRad = p.WarpAmplitudeKm / SphericalFbmNoise.EarthRadiusKm;

		bool contOn = _layerOn[LayerContinent];
		for (int i = 0; i < n; i++)
		{
			Vector3 d = cellDirs[i];
			if (warp)
			{
				var w = new Vector3(nW1.Sample(d), nW2.Sample(d), nW3.Sample(d));
				d = (d + w * warpRad).Normalized();
			}
			_continentRaw[i] = contOn ? nL0.Sample(d) : 0f;
			_ridgeRaw[i] = ridgeOn ? nRidge.Sample(d) : 0f;
			_detailRaw[i] = detailOn ? nDetail.Sample(d) : 0f;
		}
	}

	// 段2：域内场（"是什么就是什么"第一步 + 海洋深度剖面二步）。
	//   陆格：L0 噪声在**陆域内**求秩 → h0 = +rank^γ（大陆坡/山地起伏）。
	//   洋格：**离岸距离深度剖面**（岸棚→大陆坡→深渊）+ 深海丘噪声 + 洋中脊抬升（离散边界为源）。
	//   海陆由板块身份定死 ⇒ 陆板整块是陆、洋板整块是洋；深度结构跟离岸远近走，与真实 Bathymetry 同构。
	void BuildDomainRanks(NoisePlates plates, TerrainNoiseParams p, Vector3[] cellDirs, int[][] neighbors)
	{
		int n = _continentRaw.Length;
		if (_hNorm.Length != n) _hNorm = new float[n];
		if (_cellIsLand.Length != n) _cellIsLand = new bool[n];
		var land = plates.LandPlate;
		var plateOf = plates.PlateOfCell;

		int landN = 0;
		for (int i = 0; i < n; i++) if (land[plateOf[i]]) landN++;
		int seaN = n - landN;
		var landVals = new float[landN];
		int li = 0;
		for (int i = 0; i < n; i++)
			if (land[plateOf[i]]) landVals[li++] = _continentRaw[i];
		Array.Sort(landVals);
		float landDenom = Math.Max(landN - 1, 1);

		// ① 离岸距离 BFS（陆格为源，细格图；洋格深度剖面的自变量 + 近岸浅水环/海沟淡出口径）
		var dist = new int[n];
		Array.Fill(dist, -1);
		_distToLand = dist;                            // 留存（段3 海沟淡出用；陆格 = 0）
		var bfsQueue = new Queue<int>(n);
		for (int i = 0; i < n; i++)
		{
			if (!land[plateOf[i]]) continue;
			dist[i] = 0;
			bfsQueue.Enqueue(i);
		}
		while (bfsQueue.Count > 0)
		{
			int i = bfsQueue.Dequeue();
			foreach (int j in neighbors[i])
				if (dist[j] < 0) { dist[j] = dist[i] + 1; bfsQueue.Enqueue(j); }
		}
		float cellKm = SphericalFbmNoise.EarthRadiusKm * MathF.Sqrt(4f * MathF.PI / n);

		// ② 洋中脊源：**离散边界**（接近速度为负的异板边，双端皆洋）——板块背向运动处张开中脊
		var omega = plates.Omega;
		var ridgeSrc = new float[n];
		float maxDiv = 0f;
		for (int i = 0; i < n; i++)
		{
			if (_cellIsLand[i]) continue;
			Vector3 vi = omega[plateOf[i]].Cross(cellDirs[i]);
			foreach (int j in neighbors[i])
			{
				if (_cellIsLand[j] || plateOf[j] == plateOf[i]) continue;
				Vector3 nij = cellDirs[j] - cellDirs[i];
				nij -= cellDirs[i] * nij.Dot(cellDirs[i]);
				float len = nij.Length();
				if (len < 1e-9f) continue;
				float div = -(vi - omega[plateOf[j]].Cross(cellDirs[j])).Dot(nij) / len;   // <0 的接近速度取反
				if (div > maxDiv) maxDiv = div;
			}
		}
		if (maxDiv > 0f)
		{
			for (int i = 0; i < n; i++)
			{
				if (_cellIsLand[i]) continue;
				Vector3 vi = omega[plateOf[i]].Cross(cellDirs[i]);
				foreach (int j in neighbors[i])
				{
					if (_cellIsLand[j] || plateOf[j] == plateOf[i]) continue;
					Vector3 nij = cellDirs[j] - cellDirs[i];
					nij -= cellDirs[i] * nij.Dot(cellDirs[i]);
					float len = nij.Length();
					if (len < 1e-9f) continue;
					float div = -(vi - omega[plateOf[j]].Cross(cellDirs[j])).Dot(nij) / len;
					float c = Math.Clamp(div / maxDiv, 0f, 1f);
					c = c * c * (3f - 2f * c);
					ridgeSrc[i] = MathF.Max(ridgeSrc[i], c);
					ridgeSrc[j] = MathF.Max(ridgeSrc[j], c);
				}
			}
		}
		var ridge = PropagateMax(ridgeSrc, neighbors, p.PlateBeltWidthCells, BeltDecayPerHop);

		// ③ 合成（**地球实际曲线**）：陆 = 面积分位 → Earth hypsometry LUT → 样条反解；
		//    洋 = 离岸距离 → Earth 深度 LUT（+中脊抬升/深海丘噪声/近岸环，米域）→ 样条反解。
		//    样条反解 = 用当前样条结点把"目标米数"反解成归一化 h——结点可调，地球分布不变。
		var y = ComputeKnots(p);
		float oceanNoiseM = _layerOn[LayerDetail] ? OceanNoiseM : 0f;
		for (int i = 0; i < n; i++)
		{
			bool isLand = _cellIsLand[i] = land[plateOf[i]];
			if (isLand)
			{
				int pos = Array.BinarySearch(landVals, _continentRaw[i]);
				if (pos < 0) pos = ~pos;           // 理论不触（值取自同数组）；浮点并列取最左秩，确定性
				float rank = Math.Clamp(pos, 0, landVals.Length - 1) / landDenom;
				float targetM = LutSample(LandHypsoRank, LandHypsoM, rank);
				_hNorm[i] = Math.Max(SplineInverse(targetM, y), 0.03f);
			}
			else
			{
				float depthM = LutSample(OceanDepthLutKm, OceanDepthLutM, dist[i] * cellKm);
				depthM += _detailRaw[i] * oceanNoiseM;                     // 深海丘
				depthM = MathF.Max(depthM, depthM + (RidgeCrestM - depthM) * ridge[i]);   // 洋中脊抬升
				if (dist[i] <= 2) depthM = MathF.Max(depthM, -80f);        // 近岸浅水环（2 跳）
				depthM = Math.Clamp(depthM, y[0] + 1f, y[5] - 1f);
				_hNorm[i] = MathF.Min(SplineInverse(depthM, y), -0.03f);   // 顶钳（脊不露出水面）
			}
		}
	}

	// L5 样条结点高度（米）：递增链（用户把高档拉低于低档时兜底 +1m，防回折）。
	// 段2（样条反解）与段3（样条正算）共用同一份结点 ⇒ 单源。
	static float[] ComputeKnots(TerrainNoiseParams p)
	{
		var y = new float[]
		{
			p.SplineAbyssM, p.SplineShelfM, p.SplinePlainM,
			p.SplineHillM, p.SplineHighlandM, p.SplinePeakM,
		};
		for (int k = 1; k < y.Length; k++)
			if (y[k] < y[k - 1] + 1f) y[k] = y[k - 1] + 1f;
		return y;
	}

	// LUT 分段线性采样（xs 升序；越界钳两端）。
	static float LutSample(float[] xs, float[] ys, float x)
	{
		if (x <= xs[0]) return ys[0];
		for (int i = 1; i < xs.Length; i++)
			if (x <= xs[i])
				return ys[i - 1] + (ys[i] - ys[i - 1]) * (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
		return ys[^1];
	}

	// 样条反函数（结点分段线性近似——单调样条的单调反解）：目标海拔(米) → 归一化 h ∈[-1,1]。
	// 地球拟合 LUT 以米为域，经本函数换算到样条域；结点可调，地球分布不变。
	static float SplineInverse(float elevM, float[] y)
	{
		for (int k = 0; k < 5; k++)
			if (elevM <= y[k + 1] || k == 4)
				return SplineX[k] + (SplineX[k + 1] - SplineX[k])
					* Math.Clamp((elevM - y[k]) / MathF.Max(y[k + 1] - y[k], 1f), 0f, 1f);
		return 1f;
	}

	// smoothstep(a, b, x)：a→b 线性插值后三次平滑（0..1）。
	static float Smoothstep(float a, float b, float x)
	{
		float t = Math.Clamp((x - a) / MathF.Max(b - a, 1e-4f), 0f, 1f);
		return t * t * (3f - 2f * t);
	}

	// 段1e：碰撞带场（C 细化，2026-09-30"碰撞带造山细化"）：
	//   ① 逐异板边接近速度 s = (v_i−v_j)·n_ij（n = i→j 切向；v = ω×r），按全球最大归一；
	//   ② **软阈值** smoothstep(死区, 1, s/max) ⇒ 无硬切一刀——弱碰撞=丘陵、强碰撞=主峰，
	//      山链沿走向自然起伏（旧版线性带 + 二值死区的"均匀带子"观感退役）；
	//   ③ **单侧造山**：陆板压洋板 → 陆侧仰冲造山、洋侧俯冲（密度规则）；同型板 → 推进量小的一侧
	//      为仰冲 backstop、大的一侧俯冲；同速并列 → 双侧对称。俯冲侧若为洋 → 海沟源；
	//   ④ **max 传播**（每跳 ×BeltDecayPerHop、≤ 宽跳）——max 松弛的收敛点与传播序无关 ⇒ 确定性。
	//   输出：_plateBelt（仰冲侧造山掩码，陆格用）、_plateTrench（俯冲洋侧海沟，洋格米域挖深）。
	//   方向全部用**未扭曲**格心方向（板块运动是刚体，扭曲只是采样坐标的观感手段）。
	void BuildPlateBelt(NoisePlates plates, int[][] neighbors, Vector3[] cellDirs, TerrainNoiseParams p)
	{
		int n = cellDirs.Length;
		if (_plateBelt.Length != n) _plateBelt = new float[n];
		if (_plateTrench.Length != n) _plateTrench = new float[n];
		Array.Clear(_plateBelt, 0, n);
		Array.Clear(_plateTrench, 0, n);
		if (neighbors == null || plates.NumPlates == 0 || p.PlateBeltWeight <= 0f) return;

		var land = plates.LandPlate;
		var plateOf = plates.PlateOfCell;
		var omega = plates.Omega;
		var conv = new float[n];      // 仰冲侧造山源强度
		var trench = new float[n];    // 俯冲洋侧海沟源强度
		float maxS = 0f;

		// pass1：全局最大接近速度（标量，与遍历序无关）
		for (int i = 0; i < n; i++)
		{
			Vector3 vi = omega[plateOf[i]].Cross(cellDirs[i]);
			foreach (int j in neighbors[i])
			{
				if (plateOf[j] == plateOf[i]) continue;
				Vector3 nij = cellDirs[j] - cellDirs[i];
				nij -= cellDirs[i] * nij.Dot(cellDirs[i]);
				float len = nij.Length();
				if (len < 1e-9f) continue;
				float s = (vi - omega[plateOf[j]].Cross(cellDirs[j])).Dot(nij) / len;
				if (s > maxS) maxS = s;
			}
		}
		if (maxS <= 0f) return;

		// pass2：软阈值 + 单侧裁决 → 源强度
		float thr = Math.Clamp(p.PlateConvThreshold, 0f, 0.95f);
		for (int i = 0; i < n; i++)
		{
			int pi = plateOf[i];
			Vector3 vi = omega[pi].Cross(cellDirs[i]);
			foreach (int j in neighbors[i])
			{
				int pj = plateOf[j];
				if (pj == pi) continue;
				Vector3 nij = cellDirs[j] - cellDirs[i];
				nij -= cellDirs[i] * nij.Dot(cellDirs[i]);
				float len = nij.Length();
				if (len < 1e-9f) continue;
				nij /= len;
				float s = (vi - omega[pj].Cross(cellDirs[j])).Dot(nij);
				if (s <= 0f) continue;
				float t01 = Math.Clamp((s / maxS - thr) / (1f - thr), 0f, 1f);
				float c = t01 * t01 * (3f - 2f * t01);             // smoothstep 软阈值
				if (c <= 0f) continue;

				float a = vi.Dot(nij);                             // i 侧推进量
				float b = -omega[pj].Cross(cellDirs[j]).Dot(nij);  // j 侧推进量
				bool iOver, jOver;
				if (land[pi] && !land[pj]) { iOver = true; jOver = false; }
				else if (!land[pi] && land[pj]) { iOver = false; jOver = true; }
				else if (a > b) { iOver = false; jOver = true; }
				else if (b > a) { iOver = true; jOver = false; }
				else { iOver = true; jOver = true; }

				if (iOver) conv[i] = MathF.Max(conv[i], c);
				else if (!land[pi]) trench[i] = MathF.Max(trench[i], c);   // 俯冲侧为洋 → 海沟
				if (jOver) conv[j] = MathF.Max(conv[j], c);
				else if (!land[pj]) trench[j] = MathF.Max(trench[j], c);
			}
		}

		// pass3：max 传播扩散（每跳衰减；造山与海沟各传各的）
		_plateBelt = PropagateMax(conv, neighbors, p.PlateBeltWidthCells, BeltDecayPerHop);
		_plateTrench = PropagateMax(trench, neighbors, p.PlateBeltWidthCells, BeltDecayPerHop);
	}

	// max 传播：源强度按每跳 ×decay 向外扩散 ≤width 跳，取各源贡献的最大值。
	// max 松弛的收敛点与处理序无关 ⇒ 确定性；carry 逐跳严格缩减 ⇒ 必然终止。
	static float[] PropagateMax(float[] source, int[][] neighbors, int width, float decayPerHop)
	{
		int n = source.Length;
		var carry = (float[])source.Clone();
		var hop = new int[n];
		var queue = new Queue<int>(n);
		for (int i = 0; i < n; i++)
			if (carry[i] > 0f) queue.Enqueue(i);
		while (queue.Count > 0)
		{
			int i = queue.Dequeue();
			if (carry[i] <= 0f || hop[i] >= width) continue;
			foreach (int j in neighbors[i])
			{
				float cand = carry[i] * decayPerHop;
				if (cand > carry[j])
				{
					carry[j] = cand;
					hop[j] = hop[i] + 1;
					queue.Enqueue(j);
				}
			}
		}
		return carry;
	}

	// L5 样条结点位置口径（固定）：深海@-1 → 陆架@-0.35 → 海岸@0 → 丘陵@0.35 → 高原@0.7 → 山脉@1。
	// 位置是造型口径不进面板；高度值 = 参数（MapQuantileToElevation 里强制递增防回折）。
	static readonly float[] SplineX = { -1f, -0.35f, 0f, 0.35f, 0.7f, 1f };

	// 大陆坡顶格 = 高原结点（位置 0.7）：山脉结点（1.0）保留给 L2 山系带 ⇒ "山只长在该长的地方"。
	const float LandTopFraction = 0.7f;

	// 碰撞带细化（C 二阶）参数：
	public const float BeltDecayPerHop = 0.8f;   // 碰撞带 max 传播的每跳衰减（0.8^4 ≈ 0.41 @宽4）
	public const float TrenchMeters = 2500f;     // 海沟挖深上限（米；俯冲洋侧碰撞带外缘下探）
	public const float MountainM = 3500f;        // 造山加成上限（米；碰撞带上的山链在地球基线之上叠加）

	// 海洋深度剖面（**地球实际拟合**，2026-10-02）：数据源 = OpenTopodata ETOPO1 1° 全球采样
	//（64800 点，NOAA 公有领域）+ Natural Earth 110m 海岸线离岸距离（BFS 跳数×111km）。
	// 实测：岸边均值 −523m → −3116m(555km) → 渐近 −4300m（远洋均值）；浅水（−200m 内）仅占洋面 10.2%。
	// 近岸 2 跳由浅水环钳制（≥ −80m）——"所有陆地边上都是浅水"不变量优先于实测均值。
	public static readonly float[] OceanDepthLutKm = { 0f, 100f, 200f, 300f, 450f, 650f, 900f, 1200f, 1600f, 2100f, 2600f, 3200f, 4000f };
	public static readonly float[] OceanDepthLutM  = { -60f, -250f, -600f, -1200f, -1900f, -2500f, -3000f, -3500f, -3800f, -4050f, -4250f, -4300f, -4300f };

	// 陆地高程分布（**地球实际拟合**，同源数据）：面积分位 → 海拔(m)。实测：25% 陆地 <200m、
	// 46.5% <500m、长尾到 6094m（珠峰量级）。秩（L0 噪声在陆域内的秩）= 面积分位。
	public static readonly float[] LandHypsoRank = { 0f, 0.0833f, 0.1667f, 0.25f, 0.3333f, 0.4167f, 0.5f, 0.5833f, 0.6667f, 0.75f, 0.8333f, 0.9167f, 1f };
	public static readonly float[] LandHypsoM    = { 1f, 56f, 122f, 201f, 303f, 418f, 582f, 855f, 1257f, 1887f, 2593f, 3052f, 6094f };

	public const float RidgeCrestM = -2500f;     // 洋中脊顶深度（米；地球中脊脊顶 ≈ −2500m）
	public const float OceanNoiseM = 250f;       // 深海丘噪声（米；地球深海丘起伏量级）

	// 海岸摆动的门控半宽（归一化场单位）：岸带内外渐收，深海/内陆不扰动。
	const float CoastGateBand = 0.12f;

	// 段3：域内场 → 海拔（米）。（"是什么就是什么"第一步 + **地球实际分布**：基线海拔由地球拟合
	//   LUT 经样条给出——陆 = hypsometry 分布、洋 = 离岸深度剖面；构造运动在米域**加成**：
	//   山链 = ridged 形状 × 碰撞带掩码 × MountainM（地球：山只在板块边界）、海沟 = 俯冲洋侧下探。
	//   身份硬钳（陆 ≥ +1m / 洋 ≤ −1m）保证海拔符号恒等于板身份。）
	void MapQuantileToElevation(int n, TerrainNoiseParams p)
	{
		if (_elevationM.Length != n) _elevationM = new float[n];

		// 结点高度：与段2 的样条反解共用同一份（单源，含递增兜底）
		var y = ComputeKnots(p);

		// Fritsch–Carlson 单调三次切线（结点严格递增 ⇒ 斜率恒正，无除零）
		var dx = new float[5]; var slope = new float[5]; var tan = new float[6];
		for (int k = 0; k < 5; k++) { dx[k] = SplineX[k + 1] - SplineX[k]; slope[k] = (y[k + 1] - y[k]) / dx[k]; }
		tan[0] = slope[0]; tan[5] = slope[4];
		for (int k = 1; k < 5; k++)
		{
			if (slope[k - 1] * slope[k] <= 0f) { tan[k] = 0f; continue; }
			float hw1 = 2f * dx[k] + dx[k - 1], hw2 = dx[k] + 2f * dx[k - 1];
			tan[k] = (hw1 + hw2) / (hw1 / slope[k - 1] + hw2 / slope[k]);
		}

		float w2 = p.WeightDetail;
		bool ridgeOn = _layerOn[LayerRidge];
		bool detailOn = _layerOn[LayerDetail];

		for (int i = 0; i < n; i++)
		{
			bool isLand = _cellIsLand[i];                              // 身份分支（浮点符号在 ±0 不可靠）
			float h = _hNorm[i];
			float elev = EvalSpline(h, y, tan);                        // 地球基线（米）

			if (isLand)
			{
				// 造山（C 细化）：山链 = ridged 形状 × 碰撞带掩码，米域加成叠在地球基线上——
				// 基线（地球 hypsometry）管"陆地一般多高"，构造运动管"山在哪里、多高"。
				if (ridgeOn)
				{
					float shape = MathF.Pow(1f - MathF.Abs(_ridgeRaw[i]), p.RidgePower);   // [0,1]
					elev += shape * _plateBelt[i] * p.PlateBeltWeight * MountainM;
				}
				// 陆地细节纹理（米域，低地渐入）
				if (detailOn)
				{
					float fade = Smoothstep(50f, 350f, elev);
					elev += w2 * _detailRaw[i] * p.DetailAmplitudeM * fade;
				}
				// 近岸平原摆动（米域；仅低地，高原/山不受扰动）
				if (detailOn)
				{
					float gate = Smoothstep(400f, 100f, elev);
					elev += _detailRaw[i] * p.CoastWobble * 1000f * gate;
				}
				elev = MathF.Max(elev, 1f);                            // 身份硬钳（陆 ≥ +1m）
			}
			else
			{
				// 海沟：俯冲洋侧碰撞带下探——**离岸淡出**（≤2 跳零海沟、≥5 跳满强度，与近岸浅水环一致；
				// 海沟轴线让到离岸之外，"所有陆地边上都是浅水"不被破坏）
				if (ridgeOn)
				{
					float trenchFade = Smoothstep(2f, 5f, _distToLand[i]);
					elev -= _plateTrench[i] * TrenchMeters * trenchFade;
				}
				elev = MathF.Min(elev, -1f);                           // 身份硬钳（洋 ≤ −1m）
			}
			_elevationM[i] = elev;
		}
	}

	// 单调分段三次 Hermite 求值（段定位用固定口径直接比较，无查表）
	static float EvalSpline(float t, float[] y, float[] tan)
	{
		t = Math.Clamp(t, -1f, 1f);
		int s = t < -0.5f ? 0 : t < 0f ? 1 : t < 0.35f ? 2 : t < 0.7f ? 3 : 4;
		float h = SplineX[s + 1] - SplineX[s];
		float u = (t - SplineX[s]) / h, u2 = u * u, u3 = u2 * u;
		return (2f * u3 - 3f * u2 + 1f) * y[s] + (u3 - 2f * u2 + u) * h * tan[s]
			+ (-2f * u3 + 3f * u2) * y[s + 1] + (u3 - u2) * h * tan[s + 1];
	}

	// ── 参数口（面板改完参数后调；重算统一走 Generate，段缓存自动跳过未变段）──

	/// <summary>参数对象被就地修改后的通知（本类只持有 Params 引用，无法自己察觉）。</summary>
	public void NotifyParamsChanged() => _dirty = true;

	/// <summary>图层开关（预览组合：关掉的层不参与合成，参数值不动）。</summary>
	public void SetLayerEnabled(int layer, bool on)
	{
		if (_layerOn[layer] == on) return;
		_layerOn[layer] = on;
		_dirty = true;
	}

	public bool IsLayerEnabled(int layer) => _layerOn[layer];
}
