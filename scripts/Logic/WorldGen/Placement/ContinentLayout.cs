using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.Utils;
using World.Data;           // ContinentAnchor（锚点数据形状）

namespace World.WorldGen;

// 世界生成空间 · 大陆锚点层（阶段 1，决策 docs/newdecision/设计-世界生成空间-02-海陆结构.md）：
//  ★2026-10-09：数据形状 `ContinentAnchor` 已移入数据层（`scripts/Logic/Data/Carrier/ContinentAnchor.cs`，
//    ns `World.Data`）——本文件只留"怎么撒点/怎么派生属性"。
//   「大陆锚点 + 加权 Voronoi + 多尺度 Noise」——先撒大陆中心（**蓝噪声**：Mitchell best-candidate，
//   中心不挤在一起），每锚点带 size / shape / rotation / coastComplexity 属性，形成大陆影响场
//   continentInfluence(dir)；归属仅用于连续场内部塑形（锚点性格参数），地图量 = 陆块连通分量。
// 确定性红线：撒点与属性全部经 DeterministicRandom **固定次序**派生；best-candidate 的候选数固定。
// 尺度口径：地球半径 6371 km；大陆影响半径 1800–4200 km（地球大陆量级：非洲 ~3600、澳洲 ~2000）。
/// <summary>
/// 大陆布局：蓝噪声撒 N 个锚点 + 逐锚点属性（一次构建只读）。
/// </summary>
public sealed class ContinentLayout
{
	public ContinentAnchor[] Anchors { get; }
	public int SeedUsed { get; }

	// ── 属性值域（地球量级；后续接参数表）──
	//   ★四组值域一律成对命名（Min/Max）——此前 `weight` 是唯一写成 `0.8f + 0.45f * rnd` 的裸魔数，
	//     量纲要读者自己算（0.45 是"跨度"不是"上界"），与另三组的写法不一致；2026-10-10 补齐。
	public const float MinRadiusKm = 1800f, MaxRadiusKm = 4200f;
	public const float MaxStretch = 2.2f;
	public const float MinCoastCx = 0.5f, MaxCoastCx = 1.6f;
	public const float MinWeight = 0.8f, MaxWeight = 1.25f;   // 影响权重（进影响场 ⇒ 加权 Voronoi）

	public ContinentLayout(int seed, int continentCount)
	{
		if (continentCount < 1) throw new ArgumentOutOfRangeException(nameof(continentCount));
		SeedUsed = seed;
		var rnd = new DeterministicRandom(seed);
		Anchors = new ContinentAnchor[continentCount];

		// 蓝噪声撒点（Mitchell best-candidate）：每锚点取 K 候选中「到已有锚点最小角距最大」者
		// ⇒ 中心间距有下界保证（对比均匀随机 dart throwing：无最小间距保证、易成簇）。
		const int candidates = 32;
		var dirs = new List<Vector3>(continentCount);
		for (int c = 0; c < continentCount; c++)
		{
			Vector3 best = default;
			float bestGap = -1f;
			for (int k = 0; k < candidates; k++)
			{
				var d = UniformSphere(rnd);
				float gap = MinAngularGap(d, dirs);
				if (gap > bestGap) { bestGap = gap; best = d; }
			}
			dirs.Add(best);
		}

		// 属性：固定次序（半径→拉伸→轴→海岸→权重）逐锚点派生——增删锚点不影响前面锚点的属性
		for (int c = 0; c < continentCount; c++)
		{
			float radiusKm = MinRadiusKm + (MaxRadiusKm - MinRadiusKm) * (float)rnd.NextDouble();
			float stretchU = 1f + (MaxStretch - 1f) * (float)rnd.NextDouble();
			float stretchV = 1f + (stretchU - 1f) * (float)rnd.NextDouble();   // V ≤ U：单轴为主的长条，避免双轴乱拉伸
			float rot = MathF.PI * (float)rnd.NextDouble();
			var center = dirs[c];
			var (u, v) = TangentBasis(center);
			var axisU = (u * MathF.Cos(rot) + v * MathF.Sin(rot)).Normalized();
			var axisV = center.Cross(axisU);                                   // 切平面内与 U 正交（右手系）
			float coastCx = MinCoastCx + (MaxCoastCx - MinCoastCx) * (float)rnd.NextDouble();
			float weight = MinWeight + (MaxWeight - MinWeight) * (float)rnd.NextDouble();
			Anchors[c] = new ContinentAnchor
			{
				Dir = center,
				RadiusKm = radiusKm,
				AxisU = axisU,
				AxisV = axisV,
				StretchU = stretchU,
				StretchV = stretchV,
				CoastComplexity = coastCx,
				Weight = weight,
			};
		}
	}

	/// <summary>球面均匀采样（z = 2u−1 / 柱面半径 / 方位角 2πv——面积元均匀，非经纬采样畸变版）。</summary>
	static Vector3 UniformSphere(DeterministicRandom rnd)
	{
		float z = 2f * (float)rnd.NextDouble() - 1f;
		float a = 2f * MathF.PI * (float)rnd.NextDouble();
		float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
		return new Vector3(r * MathF.Cos(a), z, r * MathF.Sin(a));
	}

	static float MinAngularGap(Vector3 d, List<Vector3> others)
	{
		if (others.Count == 0) return float.PositiveInfinity;
		float min = float.PositiveInfinity;
		foreach (var o in others) min = MathF.Min(min, MathF.Acos(Math.Clamp(d.Dot(o), -1f, 1f)));
		return min;
	}

	/// <summary>中心方向的切平面正交基（与 dir 构成右手系；极轴退化回退 Right）。</summary>
	static (Vector3 u, Vector3 v) TangentBasis(Vector3 dir)
	{
		var refUp = MathF.Abs(dir.Y) < 0.9f ? Vector3.Up : Vector3.Right;
		var u = refUp.Cross(dir).Normalized();
		var v = dir.Cross(u);
		return (u, v);
	}
}
