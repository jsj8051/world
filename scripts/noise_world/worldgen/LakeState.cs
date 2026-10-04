using System;
using System.Collections.Generic;
using System.Linq;
using World.NewHexWorld;    // Ball（H3 球壳数据层）

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 湖泊状态层（River 2C-B，决策 08 v2 §2C-B——**WaterSystem 的 Water State**）：
//   只解决一件事：**内流流域是否形成湖，以及湖的水量状态**。
//   输入只来自既有事实：BasinGraph（拓扑）+ 降水（PrecipitationModel）+ FinalHeight（地形）→ 水量平衡。
//
// ★**不要把 BasinGraph.AccumMm 直接等同于湖泊水量**：
//   AccumMm 是出口格的累计径流量（**流域汇流事实**，单位 mm）；Lake 的 Volume 是**水量平衡后的状态**
//   （由水面高程 × 淹没格体积算出，单位 km³）。链路是：
//       降水/汇流输入 → 水量平衡 → SurfaceElevation → Area / Volume / Overflow
//   **不是** AccumMm → Volume。否则将来加入蒸发/渗漏/蓄水容量/季节变化时，
//   Lake 会被迫重新解释 River/Basin 的统计量。
//   （v1 无蒸发损失 ⇒ **退化解**：不溢出的湖把来水全部蓄住，Volume 数值上等于输入水量——
//    这是"零损失"的物理推论，不是定义。）
//
// ★**Lake 是水系统的另一个状态层，不回头修改已冻结的河网**：
//   FlowDirection / FlowAccum / RunoffAccum / IsRiver / DownstreamRiver / RiverGraph / BasinGraph
//   一律只读（退化解测试逐格钉死）。Lake 与 BasinGraph **并列**于 WaterSystem，
//   不是 BasinGraph → Lake → RiverGraph；也不允许 Lake 反向成为 RiverGraph 的 Outlet。
// v1 容量模型：格为蓄水单元（H3 平均格面积 × 水深），水位由"来水量 = 蓄水量"求解；
//   水位超过最低溢出高程（rim）⇒ 溢出，水位收敛到 rim，多余水量外泄（本版不追踪外泄后的去向）。
public sealed class LakeState
{
	/// <summary>湖数（= 形成稳定水体的内流洼地数）。</summary>
	public int LakeCount { get; private set; }
	/// <summary>
	/// **逐格**所属的内流洼地单元（−1 = 不属于任何内流洼地；= 原始洼地分区的副本）。
	/// ★D-16（2026-10-04）：`WaterTopology` 靠它把河流/水系节点接到湖上——
	///   必须走**原始地形分区**，不能用填洼后的 `BasinGraph.BasinId`
	///   （那是路由分区，填洼开启后内流流域归零 ⇒ 会接不到湖）。
	/// </summary>
	public int[] DepressionIdOf { get; private set; } = Array.Empty<int>();
	/// <summary>每湖绑定的内流洼地单元号（**Lake 必须绑定洼地**，不允许脱离洼地的水体）。</summary>
	public int[] BasinIdOf { get; private set; } = Array.Empty<int>();
	/// <summary>流域 → 湖下标（−1 = 该流域无湖：外流流域或水量不足以成湖）。</summary>
	public int[] LakeOfBasin { get; private set; } = Array.Empty<int>();
	/// <summary>水面高程（m）。</summary>
	public float[] SurfaceElevationM { get; private set; } = Array.Empty<float>();
	/// <summary>湖面面积（km²；= 淹没格数 × 格面积）。</summary>
	public double[] AreaKm2 { get; private set; } = Array.Empty<double>();
	/// <summary>蓄水量（km³；= Σ(水面 − 格底) × 格面积 / 1000——**几何算出，不是取 AccumMm**）。</summary>
	public double[] VolumeKm3 { get; private set; } = Array.Empty<double>();
	/// <summary>最低有效溢出高程（m；= 流域外相邻格的最低高程；无外邻 = +∞）。</summary>
	public float[] SpillElevationM { get; private set; } = Array.Empty<float>();
	/// <summary>溢出点格（**溢出事实独立**：未溢出 = −1；只有水位达到 rim 才存在出口）。</summary>
	public int[] OutletCell { get; private set; } = Array.Empty<int>();

	/// <summary>
	/// 水量平衡（只读洼地分区/降水/地形；不回写任何冻结层）。
	/// <paramref name="depressionBasins"/> **必须**是在**原始高度**上算出的内流洼地分区
	/// （D-16）——即 `H3Hydrology(rawHeight)` → `BasinGraph`，**不是**水文路由表面
	/// （`HydrologyRoutingSurface`）上那个 BasinGraph。
	/// ★语义：**填洼只改变"水怎么走"，不改变"湖是否存在"**。因此本方法的输出
	/// 对 `useDepressionFill = true/false` **必须保持不变**（反事实测试逐对象钉死）。
	/// </summary>
	public void Generate(Ball ball, FinalGeography final, float[] rawHeightM,
		BasinGraph depressionBasins, float[] annualPrecipMm)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (final == null) throw new ArgumentNullException(nameof(final));
		if (rawHeightM == null) throw new ArgumentNullException(nameof(rawHeightM));
		if (depressionBasins == null) throw new ArgumentNullException(nameof(depressionBasins));
		if (annualPrecipMm == null)
			throw new ArgumentNullException(nameof(annualPrecipMm),
				"2C-B 的水量输入必须是降水事实（mm/年）——无降水时水量平衡不成立（单位不是 mm）");

		int n = ball.CellDirs.Length;
		var h = rawHeightM;
		if (h.Length != n)
			throw new ArgumentException($"高度数组长度 {h.Length} ≠ 格数 {n}", nameof(rawHeightM));
		// ★格面积统一走 SpatialScale（收口 D-10）：此前本类内置一张 H3 平均面积表，
		// 而 GeologicalRegions / MountainSkeleton / VolcanoField 用球面均分 4πR²/n
		// —— 同一世界里"一格多大"有两个口径（res1 差 0.24%）。现统一为**等积口径**
		// （Σ 格面积 = 地球表面积严格守恒，是收敛测试与守恒断言的前提）。
		double A = SpatialScale.Of(ball).CellAreaKm2;   // km²/格（等积口径）
		var basinId = depressionBasins.BasinId;
		var nbrs = ball.CellNeighbors;

		// ★D-16：逐格分区对外暴露，供水文拓扑层（WaterTopology）按**原始洼地**接湖
		DepressionIdOf = (int[])basinId.Clone();

		// pass1：按流域收集成员格
		var members = new List<int>[depressionBasins.BasinCount];
		for (int b = 0; b < members.Length; b++) members[b] = new List<int>();
		for (int i = 0; i < n; i++)
			if (basinId[i] >= 0) members[basinId[i]].Add(i);

		var lakeOfBasin = new int[depressionBasins.BasinCount];
		Array.Fill(lakeOfBasin, -1);
		var basinOf = new List<int>();
		var levels = new List<float>();
		var areas = new List<double>();
		var volumes = new List<double>();
		var spills = new List<float>();
		var outlets = new List<int>();

		for (int b = 0; b < depressionBasins.BasinCount; b++)
		{
			// ★只有 Endorheic 流域才可能成湖：Ocean 流域永远不生成 Lake
			if (depressionBasins.Kind[b] != (int)BasinGraph.BasinKind.Endorheic) continue;
			var mem = members[b];
			if (mem.Count == 0) continue;

			// ① 来水量（km³/年）：流域内降水的体积和——**自己求和，不取 AccumMm**
			double inflow = 0;
			foreach (int i in mem) inflow += annualPrecipMm[i] * A / 1e6;   // mm × km² → km³
			if (inflow <= 0) continue;

			// ② 最低溢出高程 rim = 流域外相邻格的最低高程（+ 记下是哪个格）
			float rim = float.PositiveInfinity;
			int rimCell = -1;
			foreach (int i in mem)
				foreach (int j in nbrs[i])
				{
					if (basinId[j] == b) continue;
					if (h[j] < rim) { rim = h[j]; rimCell = j; }
				}

			// ③ 水位求解：容量曲线 Σ(水位 − 格底) × A/1000 = 来水量（零损失 ⇒ 全部蓄存）
			var hs = mem.Select(i => h[i]).OrderBy(v => v).ToArray();
			double W = inflow * 1000.0 / A;      // 目标"总水深·格"（m）
			double level = double.NaN;
			bool overflow = false;
			double prefix = 0;
			for (int k = 1; k <= hs.Length; k++)
			{
				prefix += hs[k - 1];
				double L = (W + prefix) / k;
				if (L > rim) { level = rim; overflow = true; break; }   // 溢出 ⇒ 水位收敛到 rim
				if (k == hs.Length || L <= hs[k]) { level = L; break; }
			}
			if (double.IsNaN(level)) continue;

			// ④ 几何量复核（Volume 由水面 × 淹没体积算出，不是取 AccumMm）
			double wUsed = 0;
			int submerged = 0;
			foreach (float hv in hs)
				if (hv < level) { wUsed += level - hv; submerged++; }
			double volume = wUsed * A / 1000.0;                 // m × km² → km³
			// ★Volume > 0 才是实际 Lake（不允许"Volume = 0 但仍有湖"的假对象）
			if (!(volume > 0) || submerged == 0) continue;

			int lake = basinOf.Count;
			lakeOfBasin[b] = lake;
			basinOf.Add(b);
			levels.Add((float)level);
			areas.Add(submerged * A);
			volumes.Add(volume);
			spills.Add(float.IsPositiveInfinity(rim) ? float.NaN : rim);
			outlets.Add(overflow ? rimCell : -1);   // 溢出事实独立：未溢出 = −1
		}

		LakeCount = basinOf.Count;
		BasinIdOf = basinOf.ToArray();
		LakeOfBasin = lakeOfBasin;
		SurfaceElevationM = levels.ToArray();
		AreaKm2 = areas.ToArray();
		VolumeKm3 = volumes.ToArray();
		SpillElevationM = spills.ToArray();
		OutletCell = outlets.ToArray();
	}
}
