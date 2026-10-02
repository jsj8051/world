using System;
using System.Collections.Generic;
using World.NewHexWorld;    // Ball（H3 球壳数据层）

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · H3 投影层（阶段 1 输出 + 可见化，决策 02 §2/§4）：
//   连续海陆场 → H3 逐格五件套：Land / ContinentId / DistToCoast / DistToLand / 可见海拔。
//   海陆比 = **分位校准**：全格采样后按目标占比取阈值——「目标陆地占比」滑块的承接
//   （连续场阈值 0 只是形状语义；精确占比在离散层钉死，继承现役"海陆比滑块可控"性质）。
//   ContinentId = 归属锚点 argmax（**未扭曲格心方向**——大陆归属是板块感分区，不被海岸噪声撕碎）。
//   离岸/离海距离 = H3 邻接图 BFS（多源，跳数；现役 NoiseTerrain._distToLand 同构）。
//   可见海拔 = raw 相对阈值的超出量映射（陆正海负）——**仅为上色**，阶段 1/2 不生成真实高度
//     （决策 02 §0）；内陆高沿海低的分布由连续场幅值自然给出。
// 确定性红线：分位取值用固定排序（值 + 下标）；BFS 队列序固定 ⇒ 同输入逐位同。
/// <summary>
/// H3 海陆结构投影：把连续海陆场离散化成 Land/ContinentId/距离/可见海拔五件套。
/// </summary>
public sealed class H3LandSeaProjector
{
	/// <summary>海陆掩码（陆 = true）。</summary>
	public bool[] Land { get; private set; } = Array.Empty<bool>();
	/// <summary>大陆归属（加权 Voronoi 锚点下标；海格 = −1）。</summary>
	public int[] ContinentId { get; private set; } = Array.Empty<int>();
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
	public float ThresholdUsed { get; private set; }

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

		Land = new bool[n];
		int landCount = 0;
		for (int i = 0; i < n; i++)
		{
			Land[i] = Raw[i] > thr;
			if (Land[i]) landCount++;
		}

		// ③ 大陆归属（未扭曲格心方向的锚点 argmax；海格 −1）
		ContinentId = new int[n];
		for (int i = 0; i < n; i++)
			ContinentId[i] = Land[i] ? field.Influence.AnchorAt(ball.CellDirs[i]) : -1;

		// ④ 双向 BFS：离海（陆深度）与离岸（海远度）
		DistToCoast = BfsFrom(ball, i => !Land[i]);
		DistToLand = BfsFrom(ball, i => Land[i]);

		// ⑤ 可见海拔：raw 相对阈值的超出量 → 陆正海负（幅域取 1%/99% 分位防极值）
		float landSpread = MathF.Max(thr - SortedQuantile(sorted, 0.99f), 1e-4f);
		float seaSpread = MathF.Max(SortedQuantile(sorted, 0.01f) - thr, 1e-4f);
		ElevationM = new float[n];
		for (int i = 0; i < n; i++)
		{
			if (Land[i])
			{
				float t = Math.Clamp((Raw[i] - thr) / landSpread, 0f, 1f);
				ElevationM[i] = 40f + 2200f * MathF.Pow(t, 1.3f);
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
