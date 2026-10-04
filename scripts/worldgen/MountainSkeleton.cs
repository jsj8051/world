using System;
using System.Linq;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.NewHexWorld;    // Ball（H3 球壳数据层）
using World.Utils;

namespace World.WorldGen;

// 世界生成空间 · 山脉骨架 v3（决策 04 v2 + 06 Feature/Morphology 化）：
//   本类 = **MountainRange Feature 的 Morphology + 放置器**——环境场（主轴/强度/锚点池）
//   已上收 TectonicField（决策 06：Field 回答"哪里容易"，本类只回答"长成什么样"）。
//   v2 的三缺陷（脊线全线雪白 / 支脉死直辐条 / 平行双 Range 无地质逻辑）在此一次性消除：
//   ① **主脊纵向高度曲线**：HeightM 常数 → 控制点 + Catmull-Rom 沿轴插值（峰-垭-峰），
//      多尺度 = 宏观曲线（主起伏）+ 轴向中噪声 ±15% + detail ±8%（detail 不再负责主起伏）；
//   ② **支脉曲线生长**：固定角度+直线 → **相关随机游走**（heading 动量 0.8 + 小随机转角），
//      曲率尺度与支脉长度匹配；支脉分级（一级长/二级短）、起点按主脊高程加权、长度分布化；
//   ③ **方向场 + 排斥**：主 Range 方位 = 陆块主轴（PCA 主成分）± 扰动，不再独立随机；
//      System 间近平行排斥（间距 < 1400 km 且角差 < 25° → 重掷）。
// 采样语义不变：HeightAddAt(dir) 连续口；逐格数组 = 其 H3 采样。
// 雪线不在本层——渲染器按 SnowOverlay 渐变叠加（雪线属表现层，且必须在最终高度之后）。
/// <summary>一条山脊：曲线行走点列（~20 km 步长）+ **逐点高度**（轴向 profile 的离散载体）。</summary>
public sealed class MountainRidge
{
	public Vector3[] Points = Array.Empty<Vector3>();
	public float[] PointHeightM = Array.Empty<float>();   // 沿轴高度（含宏观 profile 与支脉衰减）
	public bool IsBranch;
	public float SigmaKm;
	public Vector3 Center = Vector3.Zero;
	public float CapRadiusRad;
}

/// <summary>
/// 山脉骨架 v3：方向场主 Range（轴向高度曲线）+ 曲线生长支脉树 → 逐格高斯包络加成。
/// </summary>
public sealed class MountainSkeleton : ITerrainField
{
	public MountainRidge[] Ridges { get; private set; } = Array.Empty<MountainRidge>();
	/// <summary>山脉系统 Feature（Field+Feature+Morphology 抽象的第一个完整实现；决策 06）。</summary>
	public List<MountainSystemFeature> Systems { get; } = new();
	/// <summary>主 Range 锚点与方位（其他 Feature 的间距排斥参照；v3.2 冻结口）。</summary>
	public IReadOnlyList<(Vector3 anchor, float azRad)> RangeAnchors() =>
		Systems.Select(f => (f.Anchor, f.OrientationRad)).ToList();
	/// <summary>构造场（Field 层：主轴/强度/锚点池——决策 06 上收的环境背景）。</summary>
	public TectonicField Tectonic { get; private set; }
	/// <summary>逐格山脉影响度 ∈[0,1]（InfluenceAt 的 H3 采样）。</summary>
	public float[] MountainInfluence { get; private set; } = Array.Empty<float>();
	/// <summary>逐格山脉目标绝对高度（米；影响度 0 处无意义）。</summary>
	public float[] MountainTargetM { get; private set; } = Array.Empty<float>();

	// ── 骨架性格旋钮（地球量级；测试球可经构造覆写）──
	public const float BaseHeightM = 4200f;        // 主 Range **目标绝对高度**基准（决策 07 步骤⑤：lerp 语义——峰高单一来源；高度多样性/小陆块缩放在其上乘）
	public const float BaseSigmaKm = 160f;         // 主 Range 高斯宽度（v3.1：250→160，山带变薄）
	public const float MeanderAmpKm = 260f;        // （保留常量兼容；v3 蜿蜒由行走转角承担）
	public const int PointStepKm = 20;             // 行走步长
	public const float MinRangeSpacingKm = 1400f;  // System 间最小间距（近平行排斥）
	public const float ParallelAngleDeg = 25f;     // 排斥的角差阈值

	readonly int _seed;
	readonly SphericalFbmNoise _rugged;            // ridged 细化（±8%——不再负责主起伏）
	readonly SphericalFbmNoise _axialMedium;       // 轴向中噪声（±15%，350 km）
	readonly float _baseSigmaKm, _baseHeightM;

	public MountainSkeleton(int seed, float? baseLengthKm = null, float? baseHeightM = null, float? baseSigmaKm = null)
	{
		_seed = seed;
		_baseSigmaKm = baseSigmaKm ?? BaseSigmaKm;
		_baseHeightM = baseHeightM ?? BaseHeightM;
		var rnd = new DeterministicRandom(SeedDerivation.Derive(seed, SeedDerivation.Mountain_Noise));
		_rugged = new SphericalFbmNoise(rnd.Next(), 90f, 3);
		_axialMedium = new SphericalFbmNoise(rnd.Next(), 350f, 2);
	}

		SurfaceResolver _surface;   // 唯一海陆口径（决策 07 步骤③）

	/// <summary>生成骨架与目标场。regions 须已 Generate；surface = 唯一海陆口径
	/// （海洋表达式收归 resolver——决策 07 步骤③）。</summary>
	public void Generate(Ball ball, GeologicalRegions regions, SurfaceResolver surface)
	{
		_surface = surface ?? throw new ArgumentNullException(nameof(surface));
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (regions == null) throw new ArgumentNullException(nameof(regions));
		var regionOfCell = regions.RegionOfCell;
		int n = ball.CellDirs.Length;
		var rnd = new DeterministicRandom(_seed);
		_walkMain = new DeterministicRandom(SeedDerivation.Derive(_seed, SeedDerivation.Mountain_WalkMain));
		_walkBranch = new DeterministicRandom(SeedDerivation.Derive(_seed, SeedDerivation.Mountain_WalkBranch));
		var dirs = ball.CellDirs;
		// 格面积统一走 SpatialScale（收口 D-10）
		float cellAreaKm2 = (float)SpatialScale.Of(ball).CellAreaKm2;

		// ── Field 层：构造场（主轴/强度/锚点池——决策 06，从本类上收）──
		Tectonic = new TectonicField(_seed, ball, regions);

		// ── 陆块分组（System 数量口径仍按陆块面积）──
		var landmassCells = new SortedDictionary<int, List<int>>();
		for (int i = 0; i < n; i++)
		{
			int r = regionOfCell[i];
			if (r < 0) continue;
			int lm = regions.Regions[r].Landmass;
			if (!landmassCells.TryGetValue(lm, out var cells)) landmassCells[lm] = cells = new List<int>();
			cells.Add(i);
		}

		// ── Feature 放置：数量由陆块尺度定；锚点 = 强度加权（“为什么长在这里”）；近平行排斥 ──
		var ridges = new List<MountainRidge>();
		var placedRanges = new List<(Vector3 mid, float azRad)>();
		float radPerKm = 1f / SphericalFbmNoise.EarthRadiusKm;
		foreach (var (lm, cells) in landmassCells)
		{
			float areaKm2 = cells.Count * cellAreaKm2;
			float sideKm = MathF.Sqrt(areaKm2);
			int kSystems = Math.Clamp((int)MathF.Round(sideKm / 2000f * (0.8f + 0.4f * (float)rnd.NextDouble())), 0, 4);
			var mountainPool = Tectonic.AnchorPool(lm, mountainOnly: true);
			var axis = Tectonic.PrincipalAxis(lm);

			for (int s = 0; s < kSystems; s++)
			{
				var pool = mountainPool.Count > 0 ? mountainPool : cells;
				int anchorCell = Tectonic.PickAnchorWeighted(pool, rnd);
				var anchor = dirs[anchorCell];

				// 方位 = 陆块主轴方位 ± 方向场扰动（±15° 低频 + ±8° 局部）；退化轴 → 随机
				float az;
				var (t1, t2) = TangentBasis(anchor);
				if (axis.LengthSquared() > 1e-6f)
				{
					float axisAz = MathF.Atan2(axis.Dot(t2), axis.Dot(t1));
					az = axisAz + ((float)rnd.NextDouble() - 0.5f) * (MathF.PI / 180f) * 30f
						+ Tectonic.OrientationJitter(anchor);
				}
				else
				{
					az = MathF.PI * 2f * (float)rnd.NextDouble();
				}

				float lenKm = Math.Min(sideKm * (0.5f + 0.4f * (float)rnd.NextDouble()), 4000f);

				// 近平行排斥：与已放置主 Range 中点距 < minSpacing 且角差 < 25° → 重掷（≤6 次）
				for (int attempt = 0; attempt < 6; attempt++)
				{
					bool clash = false;
					foreach (var (mid, az0) in placedRanges)
					{
						float dist = MathF.Acos(Math.Clamp(anchor.Dot(mid), -1f, 1f)) / radPerKm;
						float dAz = MathF.Abs(AngleDiff(az, az0));
						if (dist < MinRangeSpacingKm && dAz < ParallelAngleDeg * MathF.PI / 180f) { clash = true; break; }
					}
					if (!clash) break;
					az += (MathF.PI / 180f) * (35f + 40f * (float)rnd.NextDouble());   // 强转一个角度
				}

				// v3.1 高度多样性：偏斜分布（多数系统 = 低山/丘陵，少数大成雪山）
				// × 小陆块缩放（小岛上不必有雪山）——治"没有小山脉，全是大的雪山山脉"
				float heightFactor = 0.45f + 0.55f * (float)Math.Pow(rnd.NextDouble(), 1.6);
				float sizeFactor = Math.Clamp(sideKm / 3000f, 0.45f, 1f);
				float heightM = _baseHeightM * heightFactor * sizeFactor;
				float sigmaKm = _baseSigmaKm * (0.8f + 0.4f * (float)rnd.NextDouble());

				var range = WalkRidge(anchor, az, lenKm, sigmaKm, isBranch: false,
					turnScale: 0.035f, maxDriftRad: 55f * MathF.PI / 180f, heightAt: null);
				BuildAxialProfile(range, heightM, rnd);
				ridges.Add(range);
				placedRanges.Add((anchor, az));
				Systems.Add(new MountainSystemFeature
				{
					Anchor = anchor,
					OrientationRad = az,
					Intensity = Tectonic.IntensityAt(anchor),
					Scale = new Scale3(lenKm, sigmaKm * 2f, heightM),
					CapCenter = range.Center,
					CapRadiusRad = range.CapRadiusRad,
				});

				// ── 支脉树（分级：起点按主脊高程加权，曲线生长，长度分布化）──
				SpawnBranches(range, ridges, rnd, lenKm, sigmaKm, heightM);
			}
		}
		Ridges = ridges.ToArray();

		// ── 目标场 = InfluenceAt 的 H3 采样（**无陆格门控**：构造骨架跨海连续，
		//    海洋中以海深衰减表达——浅海海底脊/岛链、深海消失）──
		MountainInfluence = new float[n];
		MountainTargetM = new float[n];
		for (int i = 0; i < n; i++)
			(MountainInfluence[i], MountainTargetM[i]) = InfluenceAt(dirs[i]);
	}

	/// <summary>连续查询（ITerrainField）：（影响度, 目标绝对高度）。影响度 = 各脊包络的
	/// 概率并集 × **海洋表达式**（resolver 唯一口）；目标 = 包络加权的轴向目标高——
	/// 峰高语义单一来源（决策 07 步骤⑤：lerp 替代增量）。</summary>
	public (float influence, float targetM) InfluenceAt(Vector3 dir)
	{
		float union = 0f, wSum = 0f, tAcc = 0f;
		foreach (var ridge in Ridges)
		{
			if (dir.Dot(ridge.Center) < MathF.Cos(ridge.CapRadiusRad)) continue;   // 帽外截断
			float sigmaRad = ridge.SigmaKm / SphericalFbmNoise.EarthRadiusKm;
			float dMin = float.PositiveInfinity;
			int bestP = 0;
			var pts = ridge.Points;
			for (int p = 0; p < pts.Length; p++)
			{
				float d = MathF.Acos(Math.Clamp(dir.Dot(pts[p]), -1f, 1f));
				if (d < dMin) { dMin = d; bestP = p; }
			}
			float envelope = MathF.Exp(-(dMin * dMin) / (sigmaRad * sigmaRad));   // 决策 4.2
			if (envelope <= 0f) continue;
			float detail = 0.92f + 0.16f * MathF.Pow(1f - MathF.Abs(_rugged.Sample(dir)), 2f);   // ±8%
			union = union + (1f - union) * envelope;                     // 概率并集（可叠加、有界 ≤1）
			wSum += envelope;
			tAcc += envelope * ridge.PointHeightM[bestP] * detail;
		}
		if (union <= 0f) return (0f, 0f);
		float influence = union * _surface.OceanFactorAt(dir);           // 海洋表达式（唯一口径）
		return (influence, tAcc / wSum);
	}

	/// <summary>ITerrainField 统一口（合成器只认这个口）。</summary>
	public (float influence, float targetM) SampleAt(Vector3 dir) => InfluenceAt(dir);

	// ── 曲线行走（相关随机游走：heading 动量 + 小随机转角——曲率尺度与脊长匹配）──

	/// <summary>从锚点沿初始方位行走 lenKm：每步转角 = 动量×0.75 + 随机小转角。</summary>
	MountainRidge WalkRidge(Vector3 anchor, float azRad, float lenKm, float sigmaKm,
		bool isBranch, float turnScale, float maxDriftRad, float[] heightAt)
	{
		float radPerKm = 1f / SphericalFbmNoise.EarthRadiusKm;
		int steps = Math.Max(2, (int)(lenKm / PointStepKm));
		float stepRad = lenKm / steps * radPerKm;
		var (t1, t2) = TangentBasis(anchor);
		var heading = t1 * MathF.Cos(azRad) + t2 * MathF.Sin(azRad);
		var heading0 = heading;
		var pts = new Vector3[steps + 1];
		float turn = 0f;
		var pos = anchor;
		for (int s = 0; s <= steps; s++)
		{
			pts[s] = pos;
			turn = turn * 0.75f + ((float)rndForWalk(isBranch, s) - 0.5f) * 2f * turnScale;
			var posNext = (pos + heading * stepRad).Normalized();
			// heading 投影到新切平面并旋转 turn（绕 pos×heading 轴的小角旋转）
			var frameUp = posNext;
			heading = heading - frameUp * heading.Dot(frameUp);
			if (heading.LengthSquared() < 1e-9f) heading = TangentBasis(frameUp).u;
			heading = heading.Normalized();
			var side = frameUp.Cross(heading).Normalized();
			heading = (heading * MathF.Cos(turn) + side * MathF.Sin(turn)).Normalized();
			// v3.1 净转角上限：当前 heading 与初始方位的夹角封顶——弯而不卷（C 形/环圈根因）
			var h0p = heading0 - frameUp * heading0.Dot(frameUp);
			if (h0p.LengthSquared() > 1e-9f)
			{
				h0p = h0p.Normalized();
				float drift = MathF.Acos(Math.Clamp(heading.Dot(h0p), -1f, 1f));
				if (drift > maxDriftRad)
				{
					float back = drift - maxDriftRad;
					heading = (heading * MathF.Cos(back) + h0p * MathF.Sin(back)).Normalized();
				}
			}
			pos = posNext;
		}
		return FinishRidge(pts, sigmaKm, isBranch, heightAt);
	}

	// 行走随机数：主 Range / 支脉各用固定次序的独立流（与其它层种子解耦）
	DeterministicRandom _walkMain, _walkBranch;   // Generate 入口初始化
	float rndForWalk(bool isBranch, int s) => (isBranch ? _walkBranch : _walkMain).Next() / (float)int.MaxValue;

	/// <summary>主脊轴向高度曲线：控制点（宏观，0.45~1.0）+ Catmull-Rom + 轴向中噪声 ±15%。</summary>
	void BuildAxialProfile(MountainRidge range, float baseHeightM, DeterministicRandom rnd)
	{
		const int K = 7;   // 控制点数（峰-垭交替的骨架）
		var ctrl = new float[K];
		float cMin = float.PositiveInfinity, cMax = 0f;
		for (int k = 0; k < K; k++)
		{
			ctrl[k] = (float)rnd.NextDouble();
			cMin = MathF.Min(cMin, ctrl[k]);
			cMax = MathF.Max(cMax, ctrl[k]);
		}
		// 归一化到 [0.45, 1.0]：峰垭极差由构造保证（峰 ≈ 基准高、垭 ≈ 45%——不靠抽样运气）
		float span = MathF.Max(cMax - cMin, 1e-3f);
		for (int k = 0; k < K; k++) ctrl[k] = 0.45f + 0.55f * (ctrl[k] - cMin) / span;
		int steps = range.Points.Length;
		range.PointHeightM = new float[steps];
		for (int s = 0; s < steps; s++)
		{
			float t = s / (float)(steps - 1) * (K - 1);
			int k = Math.Min(K - 2, (int)t);
			float u = t - k;
			// Catmull-Rom（端点重复）
			float p0 = ctrl[Math.Max(k - 1, 0)], p1 = ctrl[k], p2 = ctrl[k + 1], p3 = ctrl[Math.Min(k + 2, K - 1)];
			float macro = 0.5f * ((2f * p1) + (-p0 + p2) * u
				+ (2f * p0 - 5f * p1 + 4f * p2 - p3) * u * u
				+ (-p0 + 3f * p1 - 3f * p2 + p3) * u * u * u);
			float medium = _axialMedium.Sample(range.Points[s]) * 0.15f;
			range.PointHeightM[s] = baseHeightM * Math.Clamp(macro + medium, 0.15f, 1f);
		}
	}

	/// <summary>支脉树：一级 1~2 条（σ×0.55、长 0.3~0.5×主脊、起点取主脊高程加权）+
	/// 二级 2~3 条（σ×0.38、短）；全部曲线生长，高度 = 起点轴向高度 × 沿长衰减。</summary>
	void SpawnBranches(MountainRidge range, List<MountainRidge> ridges, DeterministicRandom rnd,
		float rangeLenKm, float rangeSigmaKm, float rangeBaseHeightM)
	{
		// 高程加权起点池：轴向高度 ≥ 0.55×基准（山从高处 long 出）+ **有效表达区门控**
		//（深海段只保留主构造、不生支脉——决策 04v3 §七）
		var pool = new List<int>();
		for (int p = 0; p < range.Points.Length; p++)
		{
			if (range.PointHeightM[p] < rangeBaseHeightM * 0.55f) continue;
			if (!_surface.IsPlacementLand(range.Points[p]))
			{
				float raw = _surface.RawAt(range.Points[p]);
				float t = Math.Clamp((_surface.SeaThreshold - raw) / _surface.SeaSpread, 0f, 1f);
				if (t > 0.5f) continue;   // 深海段
			}
			pool.Add(p);
		}
		if (pool.Count == 0) return;

		int primary = 1 + (rnd.NextDouble() < 0.5 ? 1 : 0);
		int secondary = 2 + (int)(rnd.NextDouble() * 2);
		for (int b = 0; b < primary + secondary; b++)
		{
			bool isPrimary = b < primary;
			int at = pool[rnd.Next(pool.Count)];
			var start = range.Points[at];
			float startHeight = range.PointHeightM[at];

			// 初始方向 = 主脊行进切向 ± 转角（一级 25°~55°，二级 35°~80°）
			var (t1, t2) = TangentBasis(start);
			var fwd = at + 1 < range.Points.Length ? range.Points[at + 1] - start : start - range.Points[at - 1];
			fwd = (fwd - start * fwd.Dot(start));
			if (fwd.LengthSquared() < 1e-9f) continue;
			fwd = fwd.Normalized();
			float phi = MathF.Atan2(fwd.Dot(t2), fwd.Dot(t1));
			float spread = (isPrimary ? 25f : 35f) + (float)rnd.NextDouble() * (isPrimary ? 30f : 45f);
			float theta = phi + spread * MathF.PI / 180f * (rnd.NextDouble() < 0.5f ? 1f : -1f);
			var heading0 = t1 * MathF.Cos(theta) + t2 * MathF.Sin(theta);

			float lenKm = rangeLenKm * (isPrimary ? 0.3f + 0.2f * (float)rnd.NextDouble() : 0.12f + 0.13f * (float)rnd.NextDouble());
			float sigmaKm = rangeSigmaKm * (isPrimary ? 0.55f : 0.38f);

			var branch = WalkRidge(start, theta, lenKm, sigmaKm, isBranch: true,
				turnScale: 0.11f, maxDriftRad: 75f * MathF.PI / 180f, heightAt: null);
			// 支脉逐点高度 = 起点轴向高度 × 沿长衰减（一级 0.8 起点、末端 45%）
			float h0 = startHeight * (isPrimary ? 0.8f : 0.6f);
			int steps = branch.Points.Length;
			branch.PointHeightM = new float[steps];
			for (int s = 0; s < steps; s++)
				branch.PointHeightM[s] = h0 * (1f - 0.45f * s / (steps - 1));
			ridges.Add(branch);
		}
	}

	MountainRidge FinishRidge(Vector3[] pts, float sigmaKm, bool isBranch, float[] heightAt)
	{
		var center = Vector3.Zero;
		foreach (var p in pts) center += p;
		center = center.Normalized();
		float capMax = 0f;
		foreach (var p in pts) capMax = MathF.Max(capMax, MathF.Acos(Math.Clamp(p.Dot(center), -1f, 1f)));
		return new MountainRidge
		{
			Points = pts,
			PointHeightM = heightAt ?? Array.Empty<float>(),
			IsBranch = isBranch,
			SigmaKm = sigmaKm,
			Center = center,
			CapRadiusRad = capMax + 3f * sigmaKm / SphericalFbmNoise.EarthRadiusKm + 0.01f,
		};
	}

	static float AngleDiff(float a, float b)
	{
		float d = a - b;
		while (d > MathF.PI) d -= 2f * MathF.PI;
		while (d < -MathF.PI) d += 2f * MathF.PI;
		return d;
	}

	static (Vector3 u, Vector3 v) TangentBasis(Vector3 dir)
	{
		var refUp = MathF.Abs(dir.Y) < 0.9f ? Vector3.Up : Vector3.Right;
		var u = refUp.Cross(dir).Normalized();
		var v = dir.Cross(u);
		return (u, v);
	}
}

/// <summary>山脉系统 Feature（决策 06 的第一个完整 Feature）：身份在基类，
/// 形态（中心线/剖面/支脉树）由 MountainSkeleton 的 Morphology 生成并挂在 Ridges。</summary>
public sealed class MountainSystemFeature : TerrainFeature
{
}
