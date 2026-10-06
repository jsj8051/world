using System;
using System.Collections.Generic;
using World.NewHexWorld;    // Ball（H3 球壳数据层）

namespace World.WorldGen;

// 世界生成空间 · 流域图（River 2C-A，决策 08 v2 §3.8——**Basin 拓扑，不是 Lake**）：
//   只回答"**这个水最终汇到哪里**"，**不判断"这里有没有湖"**——本轮刻意不出现
//   Lake / SurfaceElevation / Volume / WaterLevel / 蒸发 / 蓄水 / 湖泊几何 / 湖泊表现层（全部 2C-B）。
//   ★原子事实优先（§3.8 永久原则）：先保存**底层拓扑事实**，再由分类派生——
//     原子事实 = `TerminalCell`（水路终止陆格）+ `TerminatesAtOcean`（终止是否入海）；
//     派生分类 = `Kind`（Ocean / Endorheic）。分类规则怎么调，原子事实仍独立可读。
//   ★Basin 成员 = 陆格（FinalLand）；海格是 basin 的出口，不是成员（BasinId = −1）。
//   ★聚合口径：**同一终止陆格 ⇒ 同一 BasinId**（不同河口 = 不同流域）。
//   ★只读不改：本类不回写 RiverNetwork / RiverGraph 的任何数组（退化解测试逐格钉死）。
//   ★契约（决策 08 v2 §3.8）：不依赖 Placement / Feature 生成器 / FinalSpatialIndex /
//     旧气候线（World.Biome）——与 RiverGraph/RiverGeometry 同一组反射钉子；
//     **BasinGraph → Lake 暂不建立依赖**（Lake 属 2C-B，届时按 WaterSystem 并列接入）。
public sealed class BasinGraph
{
	/// <summary>流域分类（**派生**：由原子事实 TerminatesAtOcean 推出）。</summary>
	public enum BasinKind { Ocean, Endorheic }

	/// <summary>每格所属流域（海格 = −1）。</summary>
	public int[] BasinId { get; private set; } = Array.Empty<int>();
	/// <summary>【原子事实】每格水路的终止陆格（最后一个陆格：其下游是海或自身无下游）。</summary>
	public int[] TerminalCell { get; private set; } = Array.Empty<int>();
	/// <summary>【原子事实】该格的水是否最终入海（终止陆格的下游是海格）。</summary>
	public bool[] TerminatesAtOcean { get; private set; } = Array.Empty<bool>();

	public int BasinCount { get; private set; }
	/// <summary>每流域的分类（派生；Ocean = 0 / Endorheic = 1）。</summary>
	public int[] Kind { get; private set; } = Array.Empty<int>();
	/// <summary>每流域的出口（= 终止陆格；Ocean 流域可由此追溯到海）。</summary>
	public int[] Outlet { get; private set; } = Array.Empty<int>();
	/// <summary>每流域的成员格数（**拓扑量**；陆格；Σ = 全部陆格数 ⇒ 不重复计数、不漏格）。</summary>
	public int[] Area { get; private set; } = Array.Empty<int>();
	/// <summary>
	/// 每流域的物理面积（**几何量**，km²）= <see cref="Area"/> × 格面积（收口 D-10）。
	/// ★<see cref="Area"/> 是格数，<b>res 一变它的物理含义就变</b>（res1 一格 ≈ 60 万 km²，
	/// res4 ≈ 1,770 km²）——凡是要比较流域大小的地方（干旱/湿润判读、上游权重大小、
	/// 文明选址）都必须用这个字段，<b>不得</b>直接用 <see cref="Area"/>。
	/// </summary>
	public double[] AreaKm2 { get; private set; } = Array.Empty<double>();
	/// <summary>每流域的径流累积（mm）= 出口格的 RunoffAccum（**不重新求和、不凭空改变**）。</summary>
	public float[] AccumMm { get; private set; } = Array.Empty<float>();

	public int OceanBasinCount { get; private set; }
	public int EndorheicBasinCount { get; private set; }

	/// <summary>由 flow graph 派生流域拓扑（只读 RiverNetwork + FinalLand）。</summary>
	public void Generate(Ball ball, FinalGeography final, RiverNetwork rivers)
	{
		if (rivers == null) throw new ArgumentNullException(nameof(rivers));
		Generate(ball, final, rivers.Downstream, rivers.RunoffAccum);
	}

	/// <summary>
	/// 由**任意**下游链派生流域拓扑。
	/// ★D-16（2026-10-04）：这是"原始地形内流洼地分区"的入口——
	///   传 `H3Hydrology` 在**原始高度**上算出的 `Downstream`，即可得到
	///   "世界里哪里存在内流洼地"的分区，与 `HydrologyRoutingSurface`（填洼路由）无关。
	///   于是 `LakeState` 能基于**原始地形事实**判湖，而不是从填洼后的 BasinGraph 派生。
	/// </summary>
	public void Generate(Ball ball, FinalGeography final, int[] downstream, float[] accumMm = null)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (final == null) throw new ArgumentNullException(nameof(final));
		if (downstream == null) throw new ArgumentNullException(nameof(downstream));

		int n = ball.CellDirs.Length;
		var land = final.FinalLand;
		var down = downstream;

		// pass1：终止陆格（路径压缩一次成型——沿链走到"下游是海 / 无下游"为止）
		var term = new int[n];
		Array.Fill(term, -1);
		var path = new List<int>();
		for (int i = 0; i < n; i++)
		{
			if (!land[i] || term[i] >= 0) continue;
			path.Clear();
			int cur = i;
			while (term[cur] < 0)
			{
				path.Add(cur);
				int d = down[cur];
				if (d < 0 || !land[d]) break;   // 无下游（内流洼地）或下游是海 ⇒ cur 即终止陆格
				cur = d;
			}
			int t = term[cur] >= 0 ? term[cur] : cur;
			foreach (int p in path) term[p] = t;
		}

		// pass2：原子事实——终止是否入海（终止陆格的下游格是海格）
		var toOcean = new bool[n];
		for (int i = 0; i < n; i++)
		{
			if (!land[i]) continue;
			int d = down[term[i]];
			toOcean[i] = d >= 0 && !land[d];
		}

		// pass3：按终止陆格聚合流域（格索引升序扫描 ⇒ 编号确定性）
		var basinId = new int[n];
		Array.Fill(basinId, -1);
		var idOfTerminal = new Dictionary<int, int>();
		var terminalOfId = new List<int>();
		for (int i = 0; i < n; i++)
		{
			if (!land[i]) continue;
			int t = term[i];
			if (!idOfTerminal.TryGetValue(t, out int id))
			{
				id = terminalOfId.Count;
				idOfTerminal[t] = id;
				terminalOfId.Add(t);
			}
			basinId[i] = id;
		}

		// pass4：派生分类 + 统计（Area = 成员格数；AccumMm = 出口格既有累积，不重算）
		int bc = terminalOfId.Count;
		var kind = new int[bc];
		var outlet = new int[bc];
		var area = new int[bc];
		var accum = new float[bc];
		for (int i = 0; i < n; i++)
			if (basinId[i] >= 0) area[basinId[i]]++;
		int ocean = 0, endo = 0;
		for (int b = 0; b < bc; b++)
		{
			int t = terminalOfId[b];
			outlet[b] = t;
			// 派生：分类只读原子事实，不反过来定义它
			if (toOcean[t]) { kind[b] = (int)BasinKind.Ocean; ocean++; }
			else { kind[b] = (int)BasinKind.Endorheic; endo++; }
			accum[b] = accumMm != null && t < accumMm.Length ? accumMm[t] : 0f;
		}

		BasinId = basinId;
		TerminalCell = term;
		TerminatesAtOcean = toOcean;
		BasinCount = bc;
		Kind = kind;
		Outlet = outlet;
		Area = area;
		// 物理面积 = 格数 × 格面积（收口 D-10：格数是拓扑量，面积才是几何量）
		double cellKm2 = SpatialScale.Of(ball).CellAreaKm2;
		var areaKm2 = new double[bc];
		for (int b = 0; b < bc; b++) areaKm2[b] = area[b] * cellKm2;
		AreaKm2 = areaKm2;
		AccumMm = accum;
		OceanBasinCount = ocean;
		EndorheicBasinCount = endo;
	}
}
