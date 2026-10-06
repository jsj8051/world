using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.NewHexWorld;    // Ball（H3 球壳数据层）
using World.Utils;

namespace World.WorldGen;

// 世界生成空间 · 火山（Feature 架构的第一个实证，决策 08 冻结后首例）：
//   **零架构改动验证**——GeologicalRegion / HeightComposer / FinalGeography 一行未改，
//   火山 = VolcanoFeature（Morphology：径向锥）+ VolcanoField（放置器 + ITerrainField 容器）。
// 形态（决策 06 §二/§四：火山有自己的参数化——径向坐标）：
//   Influence = exp(−(d/σ)²)（径向衰减）；Target = 峰顶目标常数（lerp 语义下
//   中心≈峰、边缘≈基线，**锥体自动涌现**——不需要 target 随距离变化）。
// 尺度：σ 25-45 km（res4 格宽 42 km 的分辨率下限口径；决策 06 §二表 5-30 km 为
//   真实物量，亚格表现留 res5+）。
// 选址（Field 层消费）：每陆块 k = clamp(面积/3000万 × jitter, 0, 2)；锚点 = 强度加权
//   且 IntensityAt ≥ 0.45（火山长在构造活动区）；间距 ≥ 800 km（火山间）且避开
//   山系锚点 600 km（重叠时 Mountain(4) > Volcano(4.5)?——优先级：火山最局部，
//   按 HeightComposer 规则应排 Mountain 之后（后位覆盖）⇒ 火山压过山脊是**正确**语义）。
/// <summary>单个火山：径向锥目标场。</summary>
public sealed class VolcanoFeature : TerrainFeature, ITerrainField
{
	public float SigmaKm;
	public float PeakTargetM;    // 峰顶目标绝对高度（决策 07 lerp 语义）

	public (float influence, float targetM) SampleAt(Vector3 dir)
	{
		float sigmaRad = SigmaKm / SphericalFbmNoise.EarthRadiusKm;
		float d = MathF.Acos(Math.Clamp(dir.Dot(Anchor), -1f, 1f));
		float influence = MathF.Exp(-(d * d) / (sigmaRad * sigmaRad));
		return (influence, PeakTargetM);   // target 常数 ⇒ lerp 后锥体自动涌现
	}
}

/// <summary>
/// 火山场：放置器 + ITerrainField 容器（SampleAt = 各火山最强贡献——火山孤立性强，max 物理于并集）。
/// </summary>
public sealed class VolcanoField : ITerrainField
{
	public List<VolcanoFeature> Volcanoes { get; } = new();

	// ── 旋钮（地球量级）──
	public const float PeakTargetMinM = 3600f, PeakTargetMaxM = 4600f;
	public const float SigmaMinKm = 25f, SigmaMaxKm = 45f;
	public const int MaxPerLandmass = 2;
	public const float MinIntensity = 0.45f;      // 构造强度门槛（Field 层选址语义）
	public const float MinSpacingKm = 800f;       // 火山间最小间距
	public const float MinRangeClearanceKm = 600f; // 避开山系锚点距离

	public (float influence, float targetM) SampleAt(Vector3 dir)
	{
		float bestInf = 0f, bestTgt = 0f;
		foreach (var v in Volcanoes)
		{
			if (!v.InCap(dir)) continue;
			var (inf, tgt) = v.SampleAt(dir);
			if (inf > bestInf) { bestInf = inf; bestTgt = tgt; }
		}
		return (bestInf, bestTgt);
	}

	/// <summary>放置（构造场选址 + 间距排斥；确定性）。</summary>
	public void Place(Ball ball, GeologicalRegions regions, TectonicField tectonic, int seed,
		IReadOnlyList<(Vector3 anchor, float azRad)> placedRangeAnchors)
	{
		var rnd = new DeterministicRandom(SeedDerivation.Derive(seed, SeedDerivation.Volcano_Place));
		float radPerKm = 1f / SphericalFbmNoise.EarthRadiusKm;
		int n = ball.CellDirs.Length;
		// 格面积统一走 SpatialScale（收口 D-10）
		float cellAreaKm2 = (float)SpatialScale.Of(ball).CellAreaKm2;

		// 陆块分组（强度过滤先行：候选格 = 陆 ∧ 构造强度 ≥ 门槛）
		var landmassCells = new SortedDictionary<int, List<int>>();
		var dirs = ball.CellDirs;
		for (int i = 0; i < n; i++)
		{
			int r = regions.RegionOfCell[i];
			if (r < 0) continue;
			if (tectonic.IntensityAt(dirs[i]) < MinIntensity) continue;
			int lm = regions.Regions[r].Landmass;
			if (!landmassCells.TryGetValue(lm, out var cells)) landmassCells[lm] = cells = new List<int>();
			cells.Add(i);
		}

		foreach (var (lm, cells) in landmassCells)
		{
			float areaKm2 = cells.Count * cellAreaKm2;
			int k = Math.Clamp((int)MathF.Round(areaKm2 / 30_000_000f * (0.7f + 0.6f * (float)rnd.NextDouble())), 0, MaxPerLandmass);

			for (int s = 0; s < k; s++)
			{
				// 候选：强度加权 + 间距排斥（火山间 / 避山系锚点）
				int picked = -1;
				for (int attempt = 0; attempt < 24 && picked < 0; attempt++)
				{
					int candidate = cells[rnd.Next(cells.Count)];
					var cd = dirs[candidate];
					bool clash = false;
					foreach (var v in Volcanoes)
					{
						float d = MathF.Acos(Math.Clamp(cd.Dot(v.Anchor), -1f, 1f)) / radPerKm;
						if (d < MinSpacingKm) { clash = true; break; }
					}
					if (!clash)
						foreach (var (ra, _) in placedRangeAnchors)
						{
							float d = MathF.Acos(Math.Clamp(cd.Dot(ra), -1f, 1f)) / radPerKm;
							if (d < MinRangeClearanceKm) { clash = true; break; }
						}
					if (clash) continue;
					picked = candidate;
				}
				if (picked < 0) continue;   // 间距排斥下放弃（宁缺毋滥）

				var anchor = dirs[picked];
				var (t1, t2) = TangentBasis(anchor);
				float az = MathF.PI * 2f * (float)rnd.NextDouble();
				Volcanoes.Add(new VolcanoFeature
				{
					Anchor = anchor,
					OrientationRad = az,   // 点状特征不使用走向（基类身份完整性）
					Intensity = tectonic.IntensityAt(anchor),
					Scale = new Scale3(SigmaMaxKm * 2f, SigmaMaxKm * 2f,
						PeakTargetMinM + (PeakTargetMaxM - PeakTargetMinM) * (float)rnd.NextDouble()),
					CapCenter = anchor,
					CapRadiusRad = 3f * SigmaMaxKm * radPerKm + 0.01f,
					SigmaKm = SigmaMinKm + (SigmaMaxKm - SigmaMinKm) * (float)rnd.NextDouble(),
					PeakTargetM = PeakTargetMinM + (PeakTargetMaxM - PeakTargetMinM) * (float)rnd.NextDouble(),
				});
			}
		}
	}

	static (Vector3 u, Vector3 v) TangentBasis(Vector3 dir)
	{
		var refUp = MathF.Abs(dir.Y) < 0.9f ? Vector3.Up : Vector3.Right;
		var u = refUp.Cross(dir).Normalized();
		var v = dir.Cross(u);
		return (u, v);
	}
}
