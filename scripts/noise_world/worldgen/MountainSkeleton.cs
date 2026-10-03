using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.NewHexWorld;    // Ball（H3 球壳数据层）
using World.Utils;

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 山脉骨架层（阶段 4，决策 04——仅次于大陆生成的核心）：
//   **噪声不决定山在哪里，只负责把骨架细化成山**（决策 4.3）。
//   Pipeline：MOUNTAIN 区域（阶段 3 控制图）→ 主脊（球面蜿蜒曲线）+ 分支（斜向分叉）
//   → 逐格角距 d = min(格, 脊点列) → 高斯包络 exp(−d²/σ²) × 脊高 → ridged 噪声乘性细化。
// 脊几何：以区域质心为锚、沿随机方位直线双向延伸 ±L/2，横向蜿蜒 = 低频 fBm 采直线基准点
//   （**位置合成**而非增量积分——无漂移）；分支从主脊 1/3~2/3 段以 50°~105° 斜向分叉，
//   更短更矮更窄（支脉语义，决策 4.1）。
// 包络语义（决策 4.2/4.3）：detail = 0.45 + 0.55×ridged ∈[0.45,1.0]（峰谷比 2.2:1）——
//   骨架定高度包络与位置，噪声只在包络内造峰谷锯齿。
// 性能：逐脊**角帽预筛**（帽 = 脊点包围 + 3σ 余量；exp(−9)≈1e-4 截断）+ 海格跳过——
//   res4 全链增量 <1 s（先对再快；空间索引待实测超预算再立）。
/// <summary>一条山脊：球面点列（~20 km 步长密集采样）+ 高度/宽度性格 + 包围帽（预筛用）。</summary>
public sealed class MountainRidge
{
	public Vector3[] Points = Array.Empty<Vector3>();   // 单位方向（密集采样）
	public bool IsBranch;
	public float HeightM;                               // 脊顶基准高度（米）
	public float SigmaKm;                               // 高斯宽度（exp(−d²/σ²) 的 σ）
	public Vector3 Center = Vector3.Zero;               // 包围帽中心
	public float CapRadiusRad;                          // 包围帽角半径（含 3σ 余量）
}

/// <summary>
/// 山脉骨架：MOUNTAIN 区域 → 主脊 + 分支 → 逐格高斯包络 × ridged 细化的米域加成场。
/// </summary>
public sealed class MountainSkeleton
{
	public MountainRidge[] Ridges { get; private set; } = Array.Empty<MountainRidge>();
	/// <summary>逐格海拔加成（米；仅陆格非零——脊只长在 MOUNTAIN 陆地区域）。</summary>
	public float[] ElevationAddM { get; private set; } = Array.Empty<float>();

	// ── 骨架性格旋钮（地球量级；面板接线走 S5）──
	public const float BaseLengthKm = 1400f;       // （保留接口：主 Range 长度现由陆块尺度定）
	public const float BaseHeightM = 3000f;        // 主 Range 基准脊高（米；区域基座 ~900-1600 之上，峰雪线穿越）
	public const float BaseSigmaKm = 250f;         // 主 Range 高斯宽度（决策 05v2 §二：山脉带 200-800km 量级）
	public const float MeanderAmpKm = 260f;        // 蜿蜒横移幅度（脊是曲线不是直线，决策 4.1）
	public const int PointStepKm = 20;             // 脊点列步长（σ 的 ~1/6，距离量化误差 <8%）
	const float CapSigmaMargin = 3f;               // 帽余量 = 3σ（exp(−9) ≈ 1e-4 截断）

	readonly int _seed;
	readonly SphericalFbmNoise _meander;           // 主脊蜿蜒（低频：走势）
	readonly SphericalFbmNoise _rugged;            // ridged 细化（高频：峰谷锯齿）
	readonly float _baseLengthKm, _baseHeightM, _baseSigmaKm;

	/// <param name="baseSigmaKm">可覆写骨架性格（测试球 res1 格宽 ~800 km，须放大 σ 才采得到山带；
	/// 生产默认 = 常量地球量级）。</param>
	public MountainSkeleton(int seed, float? baseLengthKm = null, float? baseHeightM = null, float? baseSigmaKm = null)
	{
		_seed = seed;
		_baseLengthKm = baseLengthKm ?? BaseLengthKm;
		_baseHeightM = baseHeightM ?? BaseHeightM;
		_baseSigmaKm = baseSigmaKm ?? BaseSigmaKm;
		var rnd = new DeterministicRandom(seed ^ 0x9E57);
		_meander = new SphericalFbmNoise(rnd.Next(), 1800f, 2);   // 蜿蜒波长 ~ 区域尺度
		_rugged = new SphericalFbmNoise(rnd.Next(), 90f, 3);      // 细化波长 ~ 山体内部纹理
	}

	/// <summary>生成骨架与加成场。regions 须已 Generate（取 MOUNTAIN 区域质心/规模/逐格归属）。</summary>
	public void Generate(Ball ball, GeologicalRegions regions)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (regions == null) throw new ArgumentNullException(nameof(regions));
	
		var regionOfCell = regions.RegionOfCell;
		int n = ball.CellDirs.Length;
		float cellAreaKm2 = 4f * MathF.PI * SphericalFbmNoise.EarthRadiusKm * SphericalFbmNoise.EarthRadiusKm / n;
		var rnd = new DeterministicRandom(_seed);
		var dirs = ball.CellDirs;
		float radPerKm = 1f / SphericalFbmNoise.EarthRadiusKm;

		// ── ① 山脉系统（决策 05v2 §一/§三：System 数量由**大陆尺度**定、跨 Region 的全局对象）──
		//   陆块 → kSystem = clamp(√面积/2000 × random(0.8,1.2), 0, 4)（大洲 2-5 / 中洲 1-3 / 小洲 0-2）
		//   每 System = 1 条主 Range（长轴跨区域，σ~250km 山脉带）+ 2~3 条支 Ridge（σ~80km 脊线）。
		//   轴起点 70% 从 MOUNTAIN 区域格集合抽（区域控制图定"哪里适合山地"，但结构是跨区域的）。
		var ridges = new List<MountainRidge>();
		var landmassCells = new Dictionary<int, List<int>>();
		var mountainCells = new Dictionary<int, List<int>>();
		for (int i = 0; i < n; i++)
		{
			int r = regionOfCell[i];
			if (r < 0) continue;
			int lm = regions.Regions[r].Landmass;
			if (!landmassCells.TryGetValue(lm, out var cells)) landmassCells[lm] = cells = new List<int>();
			cells.Add(i);
			if (regions.Regions[r].Type == RegionType.Mountain)
			{
				if (!mountainCells.TryGetValue(lm, out var mc)) mountainCells[lm] = mc = new List<int>();
				mc.Add(i);
			}
		}
		foreach (var (lm, cells) in landmassCells)
		{
			float areaKm2 = cells.Count * cellAreaKm2;
			float sideKm = MathF.Sqrt(areaKm2);
			int kSystems = Math.Clamp((int)MathF.Round(sideKm / 2000f * (0.8f + 0.4f * (float)rnd.NextDouble())), 0, 4);
			var preferred = mountainCells.TryGetValue(lm, out var mc) && mc.Count > 0 ? mc : cells;

			for (int s = 0; s < kSystems; s++)
			{
				var anchor = dirs[preferred[rnd.Next(preferred.Count)]];
				var (t1, t2) = TangentBasis(anchor);
				float az = MathF.PI * 2f * (float)rnd.NextDouble();
				var axis = (t1 * MathF.Cos(az) + t2 * MathF.Sin(az)).Normalized();

				// 主 Range：山系级宽度与长度（跨区域；上限 4000 km 防环绕全球）
				float rangeLenKm = Math.Min(sideKm * (0.5f + 0.4f * (float)rnd.NextDouble()), 4000f);
				float rangeHeight = _baseHeightM * (0.8f + 0.4f * (float)rnd.NextDouble());
				float rangeSigma = _baseSigmaKm * (0.8f + 0.4f * (float)rnd.NextDouble());
				var range = BuildRidge(anchor, axis, rangeLenKm, rangeHeight, rangeSigma, isBranch: false);
				ridges.Add(range);

				// 支 Ridge：沿主 Range 2~3 条斜向分叉（脊线级窄山）
				int branches = 2 + (int)(rnd.NextDouble() * 2);
				for (int b = 0; b < branches; b++)
				{
					int at = range.Points.Length / 4 + (int)((float)rnd.NextDouble() * range.Points.Length / 2);
					var dir = TurnDirection(range, at, (0.55f + 0.5f * (float)rnd.NextDouble()) * (rnd.NextDouble() < 0.5f ? 1f : -1f));
					ridges.Add(BuildRidge(range.Points[at], dir, rangeLenKm * (0.3f + 0.2f * (float)rnd.NextDouble()),
						rangeHeight * 0.55f, rangeSigma * 0.32f, isBranch: true));
				}
			}
		}
		Ridges = ridges.ToArray();

		// ── ② 高度场：陆格 min 角距 → 高斯包络 × ridged 细化（决策 4.2/4.3；帽预筛 + 海格跳过）──
		ElevationAddM = new float[n];
		foreach (var ridge in Ridges)
		{
			float sigmaRad = ridge.SigmaKm * radPerKm;
			float capCos = MathF.Cos(ridge.CapRadiusRad);
			var pts = ridge.Points;
			for (int i = 0; i < n; i++)
			{
				if (regionOfCell[i] < 0) continue;                            // 海格不受骨架影响
				if (dirs[i].Dot(ridge.Center) < capCos) continue;    // 帽外截断（exp(−9) 以下）
				float dMin = float.PositiveInfinity;
				for (int p = 0; p < pts.Length; p++)
				{
					float d = MathF.Acos(Math.Clamp(dirs[i].Dot(pts[p]), -1f, 1f));
					if (d < dMin) dMin = d;
				}
				float envelope = MathF.Exp(-(dMin * dMin) / (sigmaRad * sigmaRad));   // 决策 4.2
				float ridged01 = MathF.Pow(1f - MathF.Abs(_rugged.Sample(dirs[i])), 2f);
				float detail = 0.45f + 0.55f * ridged01;                              // 决策 4.3：∈[0.45,1.0] 峰谷比 2.2:1（v1 ±30% 拉不出谷——"白盘"根因之一）
				ElevationAddM[i] += envelope * ridge.HeightM * detail;
			}
		}
	}

	/// <summary>构建一条脊：锚点 ± 沿轴 lenKm/2 直线基准 + 横向蜿蜒（位置合成，无积分漂移）。</summary>
	MountainRidge BuildRidge(Vector3 anchor, Vector3 axis, float lenKm, float heightM, float sigmaKm, bool isBranch)
	{
		float radPerKm = 1f / SphericalFbmNoise.EarthRadiusKm;
		int steps = Math.Max(2, (int)(lenKm / PointStepKm));
		var (_, perp) = TangentBasis(axis);   // 与轴正交的固定横移方向
		var pts = new Vector3[steps + 1];
		for (int s = 0; s <= steps; s++)
		{
			float alongKm = (s / (float)steps - 0.5f) * lenKm;                 // −L/2 … +L/2
			var basePos = (anchor + axis * (alongKm * radPerKm)).Normalized(); // 直线基准点
			float w = _meander.Sample(basePos) * MeanderAmpKm * radPerKm;      // 蜿蜒偏移（km → rad）
			pts[s] = (basePos + perp * w).Normalized();
		}
		var center = Vector3.Zero;
		foreach (var p in pts) center += p;
		center = center.Normalized();
		float capMax = 0f;
		foreach (var p in pts) capMax = MathF.Max(capMax, MathF.Acos(Math.Clamp(p.Dot(center), -1f, 1f)));
		return new MountainRidge
		{
			Points = pts,
			IsBranch = isBranch,
			HeightM = heightM,
			SigmaKm = sigmaKm,
			Center = center,
			CapRadiusRad = capMax + CapSigmaMargin * sigmaKm * radPerKm + 0.01f,
		};
	}

	/// <summary>分支方向：主脊在 k 点的行进切向，在其切平面内斜转 turn ±(50°~105°)（决策 4.1 分叉）。</summary>
	Vector3 TurnDirection(MountainRidge main, int k, float turnSign)
	{
		var at = main.Points[k];
		var fwd = k + 1 < main.Points.Length ? main.Points[k + 1] - at : at - main.Points[k - 1];
		fwd = (fwd - at * fwd.Dot(at)).Normalized();       // 切平面投影
		var (u, v) = TangentBasis(at);
		float phi = MathF.Atan2(fwd.Dot(v), fwd.Dot(u));
		float theta = turnSign * MathF.PI * (0.28f + 0.30f * MathF.Abs(turnSign));   // 50°~105°
		return (u * MathF.Cos(phi + theta) + v * MathF.Sin(phi + theta)).Normalized();
	}

	static (Vector3 u, Vector3 v) TangentBasis(Vector3 dir)
	{
		var refUp = MathF.Abs(dir.Y) < 0.9f ? Vector3.Up : Vector3.Right;
		var u = refUp.Cross(dir).Normalized();
		var v = dir.Cross(u);
		return (u, v);
	}
}
