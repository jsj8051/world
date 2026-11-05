using System;
using World.Data;

namespace World.Logic;

/// <summary>
/// 气压场（三项叠加）：纬向气压带 + 海陆热力异常（粗层 650 km 卷积）+ 副高东西不对称（静态）。
/// 只产状态，不认识风：风是它的响应（见 设计-季风气压场架构.md）。
///
/// <code>
/// p(φ,λ) = p_z(φ − Δc)                      纬向带（13 节点 PCHIP；Δc = 0.3×太阳赤纬，随季节南北平移）
///        − α · G_650[(T_sl − T̄_λ − bias)·eff(φ)]   海陆热力（bias = 年均偏置，先扣掉）
///        + A · B(φ) · G_300[E]               副高东西位置（E = 大陆东侧洋面指示场）
/// </code>
///
/// 中心不是"放"出来的：三项叠加后的局部极值就是中心。静态部分（E / 两套核 / p_z 斜率 /
/// 年均偏置 / 纬度查表）在 <see cref="Create"/> 一次算好；<see cref="BuildMonth"/> 只跑月度项。
///
/// @example
/// <code>
/// var model = PressureFieldModel.Create(grid, spec, coarse, annualSeaLevelC);
/// model.BuildMonth(seaLevelC[m], declinationRad[m], pressureMonthly[m]);
/// </code>
/// </summary>
public sealed class PressureFieldModel
{
	// p_z 的 13 个节点：赤道低 / 副高 / 副极低 / 极地高（hPa，年均纬向带）。
	// 数值是标定锚点，不是旋钮——换行星要重标并登记（见方案 §十一）。
	static readonly float[] ZonalLat = { -90f, -75f, -60f, -45f, -30f, -15f, 0f, 15f, 30f, 45f, 60f, 75f, 90f };
	static readonly float[] ZonalHpa = { 1014f, 1002f, 990f, 1002f, 1018f, 1014f, 1009f, 1014f, 1019f, 1008f, 996f, 1000f, 1012f };

	/// <summary>E 指示场的纬度窗口（度）：同纬度带内才算"大陆东侧"。</summary>
	const float EastOceanLatWindowDeg = 8f;

	/// <summary>E 指示场的经度窗口（度，向西）：12–24° 有陆 = 大洋东部。</summary>
	const float EastOceanLonMinDeg = 12f;
	const float EastOceanLonMaxDeg = 24f;

	/// <summary>上采样后的收尾平滑遍数：抹掉粗层父格边界在细层留下的六边形台阶。</summary>
	const int UpsampleSmoothPasses = 2;

	readonly ClimateGrid _grid;
	readonly CoarseLayer _coarse;
	readonly CoarseKernel _thermalKernel;
	readonly CoarseKernel _asymmetryKernel;

	readonly float[] _latDeg;
	readonly int[] _bucket;
	readonly float[] _zonalSlopes;
	readonly float[] _thermalGain;      // α · eff(|lat|)
	readonly float[] _asymmetryHpa;     // A · B(|lat|) · G_300[E]
	readonly float[] _biasC;            // 年均海陆热力偏置（先扣掉，避免年均常年一个符号）

	readonly int[] _coarseCount;
	readonly float[] _coarseSum;
	readonly float[] _coarseAnomaly;
	readonly float[] _coarseSmooth;
	readonly int[] _interpStart;
	readonly int[] _interpIndex;
	readonly float[] _interpWeight;
	readonly float[] _anomaly;
	readonly float[] _buffer;
	readonly int[] _bandCount;
	readonly double[] _bandSum;
	readonly float[] _bandMean;
	readonly float _beltShiftFactor;

	PressureFieldModel(ClimateGrid grid, ClimateSpec spec, CoarseLayer coarse, float[] annualSeaLevelC)
	{
		_grid = grid;
		_coarse = coarse;

		int n = grid.Count;
		int buckets = Math.Max(1, spec.LatitudeBuckets);

		_latDeg = new float[n];
		_bucket = new int[n];
		for (int i = 0; i < n; i++)
		{
			_latDeg[i] = grid.LatDeg(i);
			_bucket[i] = LatitudeBands.BucketOf(_latDeg[i], buckets);
		}

		_zonalSlopes = MonotoneCubic.Slopes(ZonalLat, ZonalHpa);

		_thermalGain = new float[n];
		for (int i = 0; i < n; i++)
			_thermalGain[i] = spec.PressureThermalGain * LatitudeClosure(_latDeg[i]);

		_biasC = BuildAnnualBias(grid, annualSeaLevelC, buckets, _latDeg);

		int thermalHops = KernelRadiusHops(spec.PressureThermalSmoothKm, coarse.NeighborKm);
		int asymmetryHops = KernelRadiusHops(spec.PressureAsymSmoothKm, coarse.NeighborKm);
		_thermalKernel = CoarseKernel.Build(coarse, spec.PressureThermalSmoothKm, thermalHops);
		_asymmetryKernel = CoarseKernel.Build(coarse, spec.PressureAsymSmoothKm, asymmetryHops);

		var upsampler = BuildUpsampler(grid, coarse);
		_interpStart = upsampler.Start;
		_interpIndex = upsampler.Index;
		_interpWeight = upsampler.Weight;

		_asymmetryHpa = BuildAsymmetry(grid, spec, coarse, _asymmetryKernel,
			_interpStart, _interpIndex, _interpWeight);

		_coarseCount = new int[coarse.Count];
		for (int i = 0; i < n; i++) _coarseCount[coarse.CoarseOfCell[i]]++;
		_coarseSum = new float[coarse.Count];
		_coarseAnomaly = new float[coarse.Count];
		_coarseSmooth = new float[coarse.Count];
		_anomaly = new float[n];
		_buffer = new float[n];

		_bandCount = new int[buckets];
		for (int i = 0; i < n; i++) _bandCount[_bucket[i]]++;
		_bandSum = new double[buckets];
		_bandMean = new float[buckets];
		_beltShiftFactor = spec.PressureBeltShiftFactor;
	}

	/// <summary>建模型（含全部静态场：E、两套核、p_z 斜率、年均偏置）。年均 T_sl 由月温累积得到。</summary>
	public static PressureFieldModel Create(ClimateGrid grid, ClimateSpec spec, CoarseLayer coarse,
		float[] annualSeaLevelC)
	{
		if (grid == null) throw new ArgumentNullException(nameof(grid));
		if (spec == null) throw new ArgumentNullException(nameof(spec));
		if (coarse == null) throw new ArgumentNullException(nameof(coarse));
		if (annualSeaLevelC == null) throw new ArgumentNullException(nameof(annualSeaLevelC));
		if (annualSeaLevelC.Length != grid.Count) throw new ArgumentException("年均温度与格数不一致", nameof(annualSeaLevelC));
		return new PressureFieldModel(grid, spec, coarse, annualSeaLevelC);
	}

	/// <summary>最近一次 <see cref="BuildMonth"/> 用的气压带季节平移（度；诊断用）。</summary>
	public float LastBeltShiftDeg { get; private set; }

	/// <summary>算一个月的气压（hPa，绝对量）。<paramref name="declinationRad"/> = 该月太阳赤纬。</summary>
	public void BuildMonth(float[] seaLevelC, float declinationRad, float[] destinationHpa)
	{
		if (seaLevelC == null) throw new ArgumentNullException(nameof(seaLevelC));
		if (destinationHpa == null) throw new ArgumentNullException(nameof(destinationHpa));
		int n = _grid.Count;
		if (seaLevelC.Length != n) throw new ArgumentException("温度场与格数不一致", nameof(seaLevelC));
		if (destinationHpa.Length != n) throw new ArgumentException("目标场与格数不一致", nameof(destinationHpa));

		BuildBandMeans(seaLevelC);
		var bandMean = SmoothBandMeans(_bandMean, passes: 2);

		// ① 热力异常：相对同纬带均值（纬度插值，不是阶梯），再扣掉年均偏置
		Array.Clear(_coarseSum, 0, _coarseSum.Length);
		for (int i = 0; i < n; i++)
		{
			float mean = InterpolateBand(bandMean, _latDeg[i]);
			float anomaly = seaLevelC[i] - mean - _biasC[i];
			anomaly *= _thermalGain[i];
			_anomaly[i] = anomaly;
			_coarseSum[_coarse.CoarseOfCell[i]] += anomaly;
		}

		for (int c = 0; c < _coarse.Count; c++)
			_coarseAnomaly[c] = _coarseCount[c] > 0 ? _coarseSum[c] / _coarseCount[c] : 0f;

		// ② 粗层卷积（固定核、单遍）→ 距离加权上采样 → 细层收尾平滑
		_thermalKernel.Convolve(_coarseAnomaly, _coarseSmooth);
		for (int i = 0; i < n; i++)
		{
			float value = 0f;
			int end = _interpStart[i + 1];
			for (int k = _interpStart[i]; k < end; k++)
				value += _interpWeight[k] * _coarseSmooth[_interpIndex[k]];
			_anomaly[i] = value;
		}
		SmoothFine(_anomaly, _buffer);

		// ③ 三项叠加：纬向带（随太阳季移）− 热力异常 + 副高东西不对称
		float beltShift = declinationRad * (180f / MathF.PI) * _beltShiftFactor;
		LastBeltShiftDeg = beltShift;

		for (int i = 0; i < n; i++)
		{
			float zonal = MonotoneCubic.Evaluate(ZonalLat, ZonalHpa, _zonalSlopes, _latDeg[i] - beltShift);
			destinationHpa[i] = zonal - _anomaly[i] + _asymmetryHpa[i];
		}
	}

	/// <summary>紧支撑加权上采样：候选集 = 父格的 2 环邻域，权重只看距离、在 R 处归零。
	/// 之前用"父格 + 一邻域"的高斯/Shepard 权重：候选集会随父格切换而跳变，粗层边界上留下
	/// 0.2 hPa 级的值跳变——风场把它放大成环状条纹。紧支撑核在 R 处权重与导数都为 0，
	/// 候选集变化时权重连续，插值面连续。</summary>
	static (int[] Start, int[] Index, float[] Weight) BuildUpsampler(ClimateGrid grid, CoarseLayer coarse)
	{
		int n = grid.Count;
		var start = new int[n + 1];
		var index = new System.Collections.Generic.List<int>(n * 8);
		var weight = new System.Collections.Generic.List<float>(n * 8);

		// 恒等层（合成网格 / 小世界）：没有跨分辨率重建，保持逐格原值
		if (coarse.Count == n)
		{
			var map = new int[n];
			var w1 = new float[n];
			for (int i = 0; i < n; i++) { start[i] = i; map[i] = i; w1[i] = 1f; }
			start[n] = n;
			return (start, map, w1);
		}

		var ring = BuildRing(coarse, maxHops: 3);
		float radius = 1.35f * (float)coarse.NeighborKm;

		for (int i = 0; i < n; i++)
		{
			int parent = coarse.CoarseOfCell[i];
			var dir = grid.Dirs[i];
			float sum = 0f;

			// 第一遍：求和
			foreach (int c in ring[parent])
			{
				float d = CoarseLayer.GreatCircleKm(dir, coarse.Dirs[c], coarse.RadiusKm);
				if (d >= radius) continue;
				float t = d / radius;
				float w = (1f - t * t) * (1f - t * t);
				sum += w;
			}

			start[i] = index.Count;
			if (sum <= 1e-12f) { index.Add(parent); weight.Add(1f); continue; }

			// 第二遍：归一化 + 剪枝
			foreach (int c in ring[parent])
			{
				float d = CoarseLayer.GreatCircleKm(dir, coarse.Dirs[c], coarse.RadiusKm);
				if (d >= radius) continue;
				float t = d / radius;
				float w = (1f - t * t) * (1f - t * t) / sum;
				if (w < 0.02f) continue;
				index.Add(c);
				weight.Add(w);
			}

			float norm = 0f;
			for (int k = start[i]; k < index.Count; k++) norm += weight[k];
			for (int k = start[i]; k < index.Count; k++) weight[k] /= norm;
		}
		start[n] = index.Count;
		return (start, index.ToArray(), weight.ToArray());
	}

	/// <summary>粗层每格的 maxHops 环邻域（含自身），构建期一次。</summary>
	static int[][] BuildRing(CoarseLayer coarse, int maxHops)
	{
		int count = coarse.Count;
		var result = new int[count][];
		var stamp = new int[count];
		var hop = new int[count];
		var queue = new int[count];

		for (int origin = 0; origin < count; origin++)
		{
			int mark = origin + 1;
			int head = 0, tail = 0;
			queue[tail++] = origin;
			stamp[origin] = mark;
			hop[origin] = 0;

			while (head < tail)
			{
				int c = queue[head++];
				if (hop[c] >= maxHops) continue;
				foreach (int nb in coarse.Neighbors[c])
				{
					if (stamp[nb] == mark) continue;
					stamp[nb] = mark;
					hop[nb] = hop[c] + 1;
					queue[tail++] = nb;
				}
			}

			var list = new int[tail];
			Array.Copy(queue, list, tail);
			result[origin] = list;
		}
		return result;
	}

	/// <summary>纬向带均值的降噪（二项 [1,2,1]/4，两遍）：去掉 5° 分桶在纬度上留下的台阶。</summary>
	static float[] SmoothBandMeans(float[] means, int passes)
	{
		int n = means.Length;
		var current = (float[])means.Clone();
		FillNaNBands(current);
		var buffer = new float[n];

		for (int pass = 0; pass < passes; pass++)
		{
			for (int b = 0; b < n; b++)
			{
				float left = b > 0 ? current[b - 1] : current[b];
				float right = b < n - 1 ? current[b + 1] : current[b];
				buffer[b] = (left + 2f * current[b] + right) * 0.25f;
			}
			(current, buffer) = (buffer, current);
		}
		return current;
	}

	/// <summary>空带（合成网格的极区）用最近合法带补齐，避免 NaN 在平滑里扩散。</summary>
	static void FillNaNBands(float[] means)
	{
		int last = -1;
		for (int b = 0; b < means.Length; b++)
		{
			if (!float.IsNaN(means[b])) last = b;
			else if (last >= 0) means[b] = means[last];
		}

		last = -1;
		for (int b = means.Length - 1; b >= 0; b--)
		{
			if (!float.IsNaN(means[b])) last = b;
			else if (last >= 0) means[b] = means[last];
		}
	}

	/// <summary>在带心之间线性插值（带心 = −90 + (b+0.5)·180/N）。</summary>
	static float InterpolateBand(float[] means, float latDeg)
	{
		int buckets = means.Length;
		float x = (latDeg + 90f) / 180f * buckets - 0.5f;
		int b0 = (int)MathF.Floor(x);
		if (b0 < 0) return means[0];
		if (b0 >= buckets - 1) return means[buckets - 1];
		return means[b0] + (means[b0 + 1] - means[b0]) * (x - b0);
	}

	/// <summary>一次遍历得纬向带均值（热力异常以它为零点）。</summary>
	void BuildBandMeans(float[] seaLevelC)
	{
		Array.Clear(_bandSum, 0, _bandSum.Length);
		for (int i = 0; i < seaLevelC.Length; i++) _bandSum[_bucket[i]] += seaLevelC[i];
		for (int b = 0; b < _bandCount.Length; b++)
			_bandMean[b] = _bandCount[b] > 0 ? (float)(_bandSum[b] / _bandCount[b]) : float.NaN;
	}

	/// <summary>纬度闭合 eff(|φ|)：低纬被环流抹平、高纬冷气团浅薄，中纬最清楚（0.5 → 1 → 0.5）。</summary>
	static float LatitudeClosure(float latDeg)
	{
		float x = (MathF.Abs(latDeg) - 45f) / 30f;
		return 0.5f + 0.5f * MathF.Exp(-x * x);
	}

	/// <summary>副高纬度权重 B(|φ|)：只在副热带给东西不对称留位置。</summary>
	static float SubtropicalWeight(float latDeg)
	{
		float x = (MathF.Abs(latDeg) - 30f) / 14f;
		return MathF.Exp(-x * x);
	}

	/// <summary>核半径（跳）：高斯取 2σ，截断误差 e^(−2)≈0.14，尾部由行归一化补回。</summary>
	static int KernelRadiusHops(float sigmaKm, double neighborKm)
	{
		if (neighborKm <= 0) return 1;
		return Math.Max(1, (int)MathF.Ceiling(2f * sigmaKm / (float)neighborKm));
	}

	/// <summary>年均偏置：逐格年均 T_sl 减同纬带均值（带宽平滑 + 纬度插值，避免 5° 台阶）。
	/// 月异常减掉它 ⇒ 年均异常逐格为 0。</summary>
	static float[] BuildAnnualBias(ClimateGrid grid, float[] annualSeaLevelC, int buckets, float[] latDeg)
	{
		var mean = LatitudeBands.MeanByBand(grid, annualSeaLevelC, buckets);
		var smooth = SmoothBandMeans(mean, passes: 2);
		var bias = new float[grid.Count];
		for (int i = 0; i < grid.Count; i++)
			bias[i] = annualSeaLevelC[i] - InterpolateBand(smooth, latDeg[i]);
		return bias;
	}

	/// <summary>
	/// 副高东西不对称：A·B(|φ|)·G_300[E]。E 是几何量（大陆东侧 12–24° 的洋面），
	/// 不做扩散近似——经度环绕与高纬经线收敛都用经纬度直接算。
	/// </summary>
	static float[] BuildAsymmetry(ClimateGrid grid, ClimateSpec spec, CoarseLayer coarse, CoarseKernel kernel,
		int[] interpStart, int[] interpIndex, float[] interpWeight)
	{
		int n = grid.Count;
		var landCount = new int[coarse.Count];
		var total = new int[coarse.Count];
		for (int i = 0; i < n; i++)
		{
			int c = coarse.CoarseOfCell[i];
			total[c]++;
			if (grid.Land[i]) landCount[c]++;
		}

		var coarseLand = new bool[coarse.Count];
		for (int c = 0; c < coarse.Count; c++) coarseLand[c] = landCount[c] * 2 >= total[c];

		var indicator = BuildEastOceanIndicator(coarse, coarseLand);
		var smoothed = new float[coarse.Count];
		kernel.Convolve(indicator, smoothed);

		var asymmetry = new float[n];
		for (int i = 0; i < n; i++)
		{
			float east = 0f;
			int end = interpStart[i + 1];
			for (int k = interpStart[i]; k < end; k++) east += interpWeight[k] * smoothed[interpIndex[k]];
			asymmetry[i] = spec.PressureAsymGain * SubtropicalWeight(grid.LatDeg(i)) * east;
		}
		return asymmetry;
	}

	/// <summary>E：海格向西 12–24°、同纬度 ±8° 内存在陆地 ⇒ 1（= 大陆东侧的洋面），否则 0。</summary>
	static float[] BuildEastOceanIndicator(CoarseLayer layer, bool[] coarseLand)
	{
		var indicator = new float[layer.Count];
		var land = new System.Collections.Generic.List<int>();
		for (int i = 0; i < layer.Count; i++) if (coarseLand[i]) land.Add(i);
		if (land.Count == 0) return indicator;

		for (int i = 0; i < layer.Count; i++)
		{
			if (coarseLand[i]) continue;
			float lat = layer.LatDeg[i];
			float lon = layer.LonDeg[i];
			foreach (int j in land)
			{
				if (MathF.Abs(layer.LatDeg[j] - lat) > EastOceanLatWindowDeg) continue;
				float west = WrapDeg(lon - layer.LonDeg[j]);   // 陆地在西侧 ⇒ 正
				if (west >= EastOceanLonMinDeg && west <= EastOceanLonMaxDeg) { indicator[i] = 1f; break; }
			}
		}
		return indicator;
	}

	/// <summary>经度差折到 [−180, 180]。</summary>
	static float WrapDeg(float degrees)
	{
		float d = degrees % 360f;
		if (d > 180f) d -= 360f;
		if (d < -180f) d += 360f;
		return d;
	}

	/// <summary>细层收尾平滑（自身 + 六邻居平均，固定遍数）。</summary>
	void SmoothFine(float[] field, float[] buffer)
	{
		float[] source = field;
		float[] destination = buffer;
		for (int pass = 0; pass < UpsampleSmoothPasses; pass++)
		{
			for (int i = 0; i < _grid.Count; i++)
			{
				float sum = source[i];
				int count = 1;
				foreach (int nb in _grid.Neighbors[i]) { sum += source[nb]; count++; }
				destination[i] = sum / count;
			}
			(source, destination) = (destination, source);
		}

		// 遍数为奇数时结果在 buffer 里；搬回 field，调用方（BuildMonth）永远读 _anomaly。
		if (!ReferenceEquals(source, field)) Array.Copy(source, field, field.Length);
	}
}
