using System;
using System.Collections.Generic;
using World.H3Grid;    // Ball（H3 球壳数据层）

namespace World.WorldGen;

// 世界生成空间 · **水文路由表面**（D-11）。
//
// ── ★核心概念：两个高度，不是改一个高度 ──────────────────────────────────────
//   FinalHeight（Composer.HeightM）
//     ├── 地貌 / 渲染 / 判读（DisplayElevation、FinalGeography）
//     └── LakeState 的**原始洼地语义**（哪里是真实内流盆地/湖盆）
//   HydrologyRoutingHeight（本类产出）
//     └── priority-flood 填洼**后**的水文路由表面 → FlowDirection / BasinGraph / FlowAccum
//
// **填洼是水文计算的派生预处理，不是世界地形真的被"填平"。**
// 一旦直接改 FinalHeight，就会把"这里存在一个内流盆地"错误地变成
// "这个世界事实里根本没有这个盆地"，破坏已建立的 LakeState / FinalGeography 语义。
// （D-11 实测依据：res4 有 3,765 个闭合洼地把陆地切碎、攒不到阈值出不了河；
//   补填洼后 res3/res4 达阈值面积比 13.8×→2.43×。详见 §13.4。）
//
// ── ★退化解开关（用户拍板）───────────────────────────────────────────────────
//   `DefaultUseDepressionFill = false` ⇒ **RoutingHeightM 与源高度逐点完全相同**（零漂移）。
//   这不是"生产调参开关"——用途：① 证明没有填洼时的当前行为；② 证明填洼后的变化来自这一机制；
//   ③ 发现副作用时可做 A/B。生产默认值待 `--d11` 完整实测后再定，不凭感觉决定。
//
// ── ★不要让填洼抹掉内流语义 ────────────────────────────────────────────────
// `DepressionCount` / `FillDepthM` **始终基于原始高度**计算，与开关无关。
//   LakeState              = "这里是不是一个真实内流洼地/湖盆？"
//   HydrologyRoutingHeight = "若把微地形闭合洼地视为水文表面，它该往哪里汇流？"
// ⚠️ 不要把"所有 depression 都该填平"写进最终模型语义——真实世界存在长期封闭的内流盆地。
public sealed class HydrologyRoutingSurface
{
	/// <summary>
	/// 生产默认：是否启用 priority-flood 填洼。
	/// ✅ **当前 = true**（2026-10-04 用户拍板翻默认）。
	/// 依据（§13.7 反事实验收，两组事实同时成立）：
	///   · 水文改善：res3/res4 达阈值面积比 **13.8× → 2.43×**
	///   · 世界事实不变：`LakeState` 湖数/面积/体积 **3,671→3,671 / 1,333 万 km² / 91,662 km³**
	///   且 `WaterTopology` 已改为按 `lakes.DepressionIdOf` 接湖 ⇒ 语义链闭合。
	/// ⚠️ 这不是"生产调参开关"——显式传 `useDepressionFill: false` 是**退化/诊断模式**，
	///    用于证明无填洼时的行为、隔离填洼引入的影响、做 A/B。
	///    ⚠️ 为了"调试方便"把它改回 false 会让
	///    `DefaultUseDepressionFill_IsTrue` 直接变红——那是有意为之，请先改测试再改默认。
	/// </summary>
	public static bool DefaultUseDepressionFill { get; set; } = true;

	/// <summary>本次实际是否启用填洼（记录事实，便于诊断与 A/B）。</summary>
	public bool UsedFill { get; private set; }

	/// <summary>
	/// **水文路由高度**（m）：供水文计算（FlowDirection / BasinGraph / FlowAccum）使用。
	/// 开关关闭时与源高度**逐点相同**（退化解）；开启时 = priority-flood 填洼后的表面。
	/// </summary>
	public float[] RoutingHeightM { get; private set; } = Array.Empty<float>();

	/// <summary>
	/// 每格被抬升的高度（m；0 = 未被填）。**始终基于原始高度**计算，与开关无关。
	/// 用途：让 LakeState / 判读层仍能识别"这里原本是洼地"，不被填洼抹掉。
	/// </summary>
	public float[] FillDepthM { get; private set; } = Array.Empty<float>();

	/// <summary>
	/// **原始高度**上的闭合洼地数（无严格更低邻居的陆格）。与开关无关——
	/// 即使填洼后汇流侧 `SinkCount = 0`，这里仍报告真实洼地数。
	/// </summary>
	public int DepressionCount { get; private set; }

	/// <summary>最大填洼深度（m；诊断用：量级异常说明预处理可能过激）。</summary>
	public double MaxFillDepthM { get; private set; }

	/// <summary>被抬升的陆格数（>0 的 `FillDepthM` 格数）。</summary>
	public int FilledCellCount { get; private set; }

	/// <summary>填洼时为保证**严格下降**注入的微小梯度（m）。</summary>
	public const float DescentEpsilonM = 1e-3f;

	public void Generate(Ball ball, float[] rawHeightM, float seaLevelM = 0f, bool? useFill = null)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (rawHeightM == null) throw new ArgumentNullException(nameof(rawHeightM));
		int n = ball.CellDirs.Length;
		if (rawHeightM.Length != n)
			throw new ArgumentException($"高度数组长度 {rawHeightM.Length} ≠ 格数 {n}", nameof(rawHeightM));

		bool fill = useFill ?? DefaultUseDepressionFill;
		UsedFill = fill;

		// ── 洼地检测：**始终读原始高度**，与开关无关（填洼不得抹掉这个事实）──
		DepressionCount = 0;
		for (int i = 0; i < n; i++)
		{
			if (rawHeightM[i] <= seaLevelM) continue;              // 海格不算洼地
			bool hasLower = false;
			foreach (int j in ball.CellNeighbors[i])
				if (rawHeightM[j] < rawHeightM[i]) { hasLower = true; break; }
			if (!hasLower) DepressionCount++;
		}

		if (!fill)
		{
			// 退化解：**逐点复制**，保证与旧行为零漂移
			RoutingHeightM = (float[])rawHeightM.Clone();
			FillDepthM = new float[n];
			MaxFillDepthM = 0;
			FilledCellCount = 0;
			return;
		}

		var h = (float[])rawHeightM.Clone();
		PriorityFloodFill(ball, h, seaLevelM);
		RoutingHeightM = h;

		FillDepthM = new float[n];
		FilledCellCount = 0;
		double maxFill = 0;
		for (int i = 0; i < n; i++)
		{
			float d = h[i] - rawHeightM[i];
			FillDepthM[i] = d;
			if (d > DescentEpsilonM) { FilledCellCount++; if (d > maxFill) maxFill = d; }
		}
		MaxFillDepthM = maxFill;
	}

	/// <summary>
	/// priority-flood 填洼（Barnes 改进版；标准 D8 水文预处理）。
	/// 从所有出口（海格 elev ≤ seaLevel）按高度升序 flood：遇到洼地格就抬到
	/// 刚好能溢出的水位（+ε 保证严格下降）。
	/// ⚠️ **只改 `h`（副本），绝不回写源高度**——Final 地形事实保持不动。
	/// </summary>
	static void PriorityFloodFill(Ball ball, float[] h, float seaLevelM)
	{
		int n = h.Length;
		var visited = new bool[n];
		var pq = new PriorityQueue<int, float>();
		for (int i = 0; i < n; i++)
			if (h[i] <= seaLevelM) { visited[i] = true; pq.Enqueue(i, h[i]); }

		while (pq.Count > 0)
		{
			int c = pq.Dequeue();
			foreach (int nb in ball.CellNeighbors[c])
			{
				if (nb < 0 || nb >= n || visited[nb]) continue;
				visited[nb] = true;
				if (h[nb] <= h[c]) h[nb] = h[c] + DescentEpsilonM;
				pq.Enqueue(nb, h[nb]);
			}
		}
	}
}
