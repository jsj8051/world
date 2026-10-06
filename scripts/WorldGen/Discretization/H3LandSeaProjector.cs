using System;
using System.Collections.Generic;
using World.NewHexWorld;    // Ball（H3 球壳数据层）

namespace World.WorldGen;

// 世界生成空间 · H3 投影层（阶段 1 输出 + 可见化，决策 02 §2/§4）：
//   连续海陆场 → H3 逐格五件套：Land / LandmassId（连通分量）/ DistToCoast / DistToLand / 可见海拔。
//   海陆比 = **分位校准**：全格采样后按目标占比取阈值——「目标陆地占比」滑块的承接
//   （连续场阈值 0 只是形状语义；精确占比在离散层钉死，继承现役"海陆比滑块可控"性质）。
//   陆块 = 海陆掩码连通分量（2026-10-02 语义修正：大陆=真实陆地板块，非锚点势力）。
//   离岸/离海距离 = H3 邻接图 BFS（多源，跳数）。
//   可见海拔 = raw 相对阈值的超出量映射（陆正海负）——**仅为上色**，阶段 1/2 不生成真实高度
//     （决策 02 §0）；内陆高沿海低的分布由连续场幅值自然给出。
// 确定性红线：分位取值用固定排序（值 + 下标）；BFS 队列序固定 ⇒ 同输入逐位同。
/// <summary>
/// H3 海陆结构投影：把连续海陆场离散化成 Land/LandmassId/距离/可见海拔五件套。
/// </summary>
public sealed class H3LandSeaProjector
{
	/// <summary>**放置期陆海掩码**（生成依据——特征抬升之前的状态；仅供地貌生成/选址/环境判断，
	///   不得进入水文/气候/生态。世界事实 = FinalGeography.FinalLand，由最终高度派生）。</summary>
	public bool[] PlacementLand { get; private set; } = Array.Empty<bool>();
	/// <summary>放置期陆海连续量 ∈[0,1]：多点采样中陆样本占比（决策 07 步骤②——
	///   海岸过渡/滩涂/植被边缘的连续原料；0 完全海 / 1 完全陆）。</summary>
	public float[] PlacementLandFraction { get; private set; } = Array.Empty<float>();
	/// <summary>陆块归属（海陆连通分量号：一片连通陆地 = 一个陆块；海格 = −1）。
	///   语义修正（2026-10-02 用户拍板）：大陆 = 真实陆地板块，不是锚点势力——锚点 Voronoi 归属
	///   只存于连续场内部塑形（LandSeaField 取锚点性格参数），不再产出地图量。</summary>
	public int[] LandmassId { get; private set; } = Array.Empty<int>();
	/// <summary>陆格离海跳数（海格 = 0）——内陆深度。</summary>
	public int[] DistToCoast { get; private set; } = Array.Empty<int>();
	/// <summary>海格离岸跳数（陆格 = 0）——离岸距离（近岸浅水环/海沟口径的原料）。</summary>
	public int[] DistToLand { get; private set; } = Array.Empty<int>();
	/// <summary>可见海拔（米；陆正海负，仅上色——阶段 1/2 不生成真实高度）。</summary>
	public float[] ElevationM { get; private set; } = Array.Empty<float>();
	/// <summary>连续场原始值（逐格；换可见化口径时免重采）。</summary>
	public float[] Raw { get; private set; } = Array.Empty<float>();
	/// <summary>实测海陆比与所用阈值（校准回读）。</summary>
	public float LandFraction { get; private set; }
	/// <summary>校准映射常数回读（连续地形图的海洋公式与离散版同源：决策 05v2 架构——单一事实源）。</summary>
	public float SeaSpreadUsed { get; private set; }
	public float ThresholdUsed { get; private set; }
	/// <summary>陆块数（连通分量数；下游分区用）。</summary>
	public int LandmassCount { get; private set; }

	public void Generate(Ball ball, LandSeaField field, float targetLandFraction = 0.29f,
		H3TerrainSampler.Mode mode = H3TerrainSampler.Mode.CenterAndCorners)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (field == null) throw new ArgumentNullException(nameof(field));
		int n = ball.CellDirs.Length;

		// ① 连续场 → 逐格（多点采样：格心+角点加权，压边界跳变）
		Raw = new H3TerrainSampler(ball).SampleField(field, mode);

		// ② 分位校准海陆比：排序副本（值升序）取目标分位——阈值即"海陆分界"
		var sorted = (float[])Raw.Clone();
		Array.Sort(sorted);
		float thr = SortedQuantile(sorted, 1f - targetLandFraction);
		ThresholdUsed = thr;

		PlacementLand = new bool[n];
		PlacementLandFraction = new float[n];
		int landCount = 0;
		for (int i = 0; i < n; i++)
		{
			PlacementLand[i] = Raw[i] > thr;   // 口径沿用：加权均值过阈值（行为不变）
			PlacementLandFraction[i] = PlacementLand[i] ? 1f : 0f;   // 连续量：粗粒度占位（多点分数化走采样器重构，登记 T7）
			if (PlacementLand[i]) landCount++;
		}

		// ③ 陆块归属 = 海陆掩码的**连通分量**（一片连通陆地 = 一个陆块；海格 −1）——
		//    "这块陆地属于哪个大陆"的自然语义；内陆归属岛从构造上消失。
		//    （v2 踩坑记录：锚点势力 argmax 会把连片陆地内部判成圆形归属岛——势力与海陆不同源。）
		LandmassId = new int[n];
		Array.Fill(LandmassId, -1);
		var neighbors = ball.CellNeighbors;
		int landmassCount = 0;
		var bfs = new Queue<int>(n);
		for (int seed = 0; seed < n; seed++)
		{
			if (!PlacementLand[seed] || LandmassId[seed] >= 0) continue;
			LandmassId[seed] = landmassCount;
			bfs.Enqueue(seed);
			while (bfs.Count > 0)
			{
				int i = bfs.Dequeue();
				foreach (int j in neighbors[i])
				{
					if (!PlacementLand[j] || LandmassId[j] >= 0) continue;
					LandmassId[j] = landmassCount;
					bfs.Enqueue(j);
				}
			}
			landmassCount++;
		}
		LandmassCount = landmassCount;

		// ④ 双向 BFS：离海（陆深度）与离岸（海远度）
		DistToCoast = BfsFrom(ball, i => !PlacementLand[i]);
		DistToLand = BfsFrom(ball, i => PlacementLand[i]);

		// ⑤ 海拔基线：raw 相对阈值的超出量 → 陆正海负（幅域取 1%/99% 分位防极值）。
		//   陆侧 = **ContinentalElevation 大陆基线**（阶段 6 合成公式的第一项）：峰值压到 ~940 m
		//   （丘陵以下）——山/高原/盆地由骨架与区域场叠加拔起/下挖（决策 05），基线自己不进高地档
		//   （v1 基线 2240 + 区域偏移 1500 未算骨架就已过雪线 2800 ⇒ "雪山圆盘"的根因之一）。
		float landSpread = MathF.Max(thr - SortedQuantile(sorted, 0.99f), 1e-4f);
		float seaSpread = MathF.Max(SortedQuantile(sorted, 0.01f) - thr, 1e-4f);
		SeaSpreadUsed = seaSpread;
		ElevationM = new float[n];
		for (int i = 0; i < n; i++)
		{
			if (PlacementLand[i])
			{
				float t = Math.Clamp((Raw[i] - thr) / landSpread, 0f, 1f);
				ElevationM[i] = 40f + 900f * MathF.Pow(t, 1.3f);
			}
			else
			{
				float t = Math.Clamp((thr - Raw[i]) / seaSpread, 0f, 1f);
				ElevationM[i] = -30f - 3200f * MathF.Pow(t, 1.2f);
			}
		}

		LandFraction = (float)landCount / n;
	}

	// 升序数组的分位（线性位置插值；q∈[0,1]）。
	static float SortedQuantile(float[] sorted, float q)
	{
		float pos = Math.Clamp(q, 0f, 1f) * (sorted.Length - 1);
		int lo = (int)pos;
		int hi = Math.Min(lo + 1, sorted.Length - 1);
		return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
	}

	// 多源 BFS：source(i) = true 的格距离 0，逐跳 +1 扩散全图（队列序固定 ⇒ 确定性）。
	static int[] BfsFrom(Ball ball, Func<int, bool> source)
	{
		int n = ball.CellDirs.Length;
		var dist = new int[n];
		var queue = new Queue<int>(n);
		for (int i = 0; i < n; i++)
		{
			if (!source(i)) { dist[i] = -1; continue; }
			dist[i] = 0;
			queue.Enqueue(i);
		}
		var neighbors = ball.CellNeighbors;
		while (queue.Count > 0)
		{
			int i = queue.Dequeue();
			foreach (int j in neighbors[i])
				if (dist[j] < 0) { dist[j] = dist[i] + 1; queue.Enqueue(j); }
		}
		return dist;
	}
}
