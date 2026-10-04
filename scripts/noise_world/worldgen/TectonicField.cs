using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.NewHexWorld;    // Ball（H3 球壳数据层）
using World.Utils;

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 构造/环境场层（决策 06「Field」）：回答"哪里容易发生什么"——
//   构造主轴（陆块走向）、构造强度（连续场，区域类型驱动）、放置锚点池、方向扰动噪声。
//   **不负责任何形状**——形状是各 Feature 的 Morphology 的事（决策 06 §六：
//   Region/Domain 降级为轻量环境背景，山脉可以跨过多个 Domain）。
// 强度口径（区域类型 → 构造强度基值，Shepard 帽插值成连续场）：
//   Mountain 1.0（造山带）/ Rift 0.7 / Highland 0.75 / Plateau 0.6 / Plain 0.35 /
//   Basin 0.3 / Coastal 0.2——山脉等 Feature 按 IntensityAt 选址（强度加权），
//   未来盆地/火山/断层同样读本场 + 各自阈值即可，无需再动总架构。
/// <summary>
/// 构造场：陆块主轴 + 构造强度连续场 + 放置锚点池 + 方向扰动（世界生成的"原因"层）。
/// </summary>
public sealed class TectonicField
{
	public int SeedUsed { get; }

	readonly Ball _ball;
	readonly GeologicalRegions _regions;
	readonly Dictionary<int, Vector3> _axes = new();            // 陆块号 → 主轴（切向）
	readonly (Vector3 anchor, float sigmaRad, float intensity, float cutR2)[] _intensityCaps;
	readonly SphericalFbmNoise _orientNoise;                    // 方向扰动（低频）
	readonly Random _rndSrc;                                    // 强度帽等构建期随机（确定性）

	public TectonicField(int seed, Ball ball, GeologicalRegions regions)
	{
		SeedUsed = seed;
		_ball = ball;
		_regions = regions;
		var rnd = new DeterministicRandom(SeedDerivation.Derive(seed, SeedDerivation.Tectonic_Orient));
		_orientNoise = new SphericalFbmNoise(rnd.Next(), 6000f, 2);
		_rndSrc = Random.Shared;   // 构建期无随机需求（帽几何是确定函数）——占位不用
		_intensityCaps = BuildIntensityCaps();
		BuildAxes();
	}

	// ── 陆块主轴（方向协方差 PCA 主成分；从 MountainSkeleton v3 抽出）──
	public Vector3 PrincipalAxis(int landmass) =>
		_axes.TryGetValue(landmass, out var a) ? a : Vector3.Zero;

	void BuildAxes()
	{
		var byLm = new SortedDictionary<int, List<Vector3>>();
		var regionOfCell = _regions.RegionOfCell;
		var dirs = _ball.CellDirs;
		for (int i = 0; i < dirs.Length; i++)
		{
			int r = regionOfCell[i];
			if (r < 0) continue;
			int lm = _regions.Regions[r].Landmass;
			if (!byLm.TryGetValue(lm, out var list)) byLm[lm] = list = new List<Vector3>();
			list.Add(dirs[i]);
		}
		foreach (var (lm, list) in byLm)
		{
			Vector3 mean = Vector3.Zero;
			foreach (var d in list) mean += d;
			mean = (mean / list.Count).Normalized();
			float[,] cov = new float[3, 3];
			foreach (var d in list)
			{
				var e = d - mean;
				cov[0, 0] += e.X * e.X; cov[0, 1] += e.X * e.Y; cov[0, 2] += e.X * e.Z;
				cov[1, 1] += e.Y * e.Y; cov[1, 2] += e.Y * e.Z; cov[2, 2] += e.Z * e.Z;
			}
			cov[1, 0] = cov[0, 1]; cov[2, 0] = cov[0, 2]; cov[2, 1] = cov[1, 2];
			var v = Vector3.One.Normalized();
			for (int it = 0; it < 12; it++)
			{
				var nv = new Vector3(
					cov[0, 0] * v.X + cov[0, 1] * v.Y + cov[0, 2] * v.Z,
					cov[1, 0] * v.X + cov[1, 1] * v.Y + cov[1, 2] * v.Z,
					cov[2, 0] * v.X + cov[2, 1] * v.Y + cov[2, 2] * v.Z);
				if (nv.LengthSquared() < 1e-12f) { v = Vector3.Zero; break; }
				v = nv.Normalized();
			}
			_axes[lm] = v;
		}
	}

	// ── 构造强度连续场（区域类型 → 基值，Shepard 帽插值；跨海连续——构造背景不认海陆）──

	static float TypeIntensity(RegionType t) => t switch
	{
		RegionType.Mountain => 1f,
		RegionType.Highland => 0.75f,
		RegionType.Rift => 0.7f,
		RegionType.Plateau => 0.6f,
		RegionType.Plain => 0.35f,
		RegionType.Basin => 0.3f,
		RegionType.Coastal => 0.2f,
		_ => 0.3f,
	};

	(Vector3 anchor, float sigmaRad, float intensity, float cutR2)[] BuildIntensityCaps()
	{
		var caps = new (Vector3, float, float, float)[_regions.Regions.Length];
		for (int i = 0; i < _regions.Regions.Length; i++)
		{
			var r = _regions.Regions[i];
			float equivKm = MathF.Sqrt(r.AreaKm2 / MathF.PI);
			float sigmaRad = equivKm * 1.4f / SphericalFbmNoise.EarthRadiusKm;
			float cutRad = 2.5f * sigmaRad;
			caps[i] = (r.Seed, sigmaRad, TypeIntensity(r.Type), cutRad * cutRad);
		}
		return caps;
	}

	/// <summary>构造强度 ∈[0,1]（连续；锚点选址/未来特征阈值共用这一个口）。</summary>
	public float IntensityAt(Vector3 dir)
	{
		float wSum = 0f, acc = 0f;
		for (int c = 0; c < _intensityCaps.Length; c++)
		{
			float d2 = (dir - _intensityCaps[c].anchor).LengthSquared();
			if (d2 > _intensityCaps[c].cutR2) continue;
			float dRad = MathF.Acos(Math.Clamp(dir.Dot(_intensityCaps[c].anchor), -1f, 1f));
			float w = MathF.Exp(-(dRad * dRad) / (2f * _intensityCaps[c].sigmaRad * _intensityCaps[c].sigmaRad));
			if (w < 1e-4f) continue;
			wSum += w;
			acc += w * _intensityCaps[c].intensity;
		}
		return wSum > 1e-3f ? acc / wSum : 0.3f;   // 无帽覆盖（远洋）= 弱构造背景
	}

	/// <summary>方向扰动（低频，±25° 弧度口径）——主轴之外的走向性格。</summary>
	public float OrientationJitter(Vector3 dir) => _orientNoise.Sample(dir) * (MathF.PI / 180f) * 25f;

	/// <summary>放置锚点池（陆块内格索引；mountainOnly = 只取 MOUNTAIN 区域格）。</summary>
	public List<int> AnchorPool(int landmass, bool mountainOnly)
	{
		var pool = new List<int>();
		var dirs = _ball.CellDirs;
		for (int i = 0; i < dirs.Length; i++)
		{
			int r = _regions.RegionOfCell[i];
			if (r < 0 || _regions.Regions[r].Landmass != landmass) continue;
			if (mountainOnly && _regions.Regions[r].Type != RegionType.Mountain) continue;
			pool.Add(i);
		}
		return pool;
	}

	/// <summary>强度加权锚点抽取（Feature 选址 = "为什么长在这里"的落点；确定性）。</summary>
	public int PickAnchorWeighted(List<int> pool, DeterministicRandom rnd)
	{
		float total = 0f;
		var weights = new float[pool.Count];
		for (int k = 0; k < pool.Count; k++)
		{
			weights[k] = IntensityAt(_ball.CellDirs[pool[k]]) + 0.05f;   // +0.05 防零权
			total += weights[k];
		}
		float pick = (float)rnd.NextDouble() * total;
		float acc = 0f;
		for (int k = 0; k < pool.Count; k++)
		{
			acc += weights[k];
			if (pick <= acc) return pool[k];
		}
		return pool[^1];
	}
}
