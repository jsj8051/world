using System;
using System.Collections.Generic;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.H3Grid;    // Ball（H3 球壳数据层）
using World.Utils;
using World.Utils.H3;

namespace World.WorldGen;

// 世界生成空间 · 最终地理（决策 07 数据语义链的 ④ FinalLandMask + ⑤ 空间派生）：
//   **只有这里输出的才是"世界事实"**——由最终高度（composer.HeightM / SampleSurface）派生：
//     FinalLand        = 最终高度 > 0（与渲染逐格一致——掩码说陆则画面必是陆）
//     FinalLandFraction= 7 采样点（格心+角点）中陆占比 ∈[0,1]（连续辅助数据——海岸过渡/
//                        滩涂/植被边缘用，不做硬判）
//     FinalLandmassId  = FinalLand 连通分量（一片连通陆地 = 一个陆块；山脉抬出的岛屿
//                        自然成为正常陆地——决策 07 §1 的核心兑现）
//     FinalDistToCoast/Land = FinalLand 的双向 BFS
//     FinalRegionOfCell = 放置区域优先；**山脉新增岛屿 → RegionIndexAt 归入最近构造域**
//   放置期版本（PlacementLand/LandmassId…）留在投影器/区域层——只服务生成阶段，互不混用。
/// <summary>
/// 最终地理：FinalLand 掩码 + 陆块/区域/海岸距离的空间派生（世界事实层）。
/// </summary>
public sealed class FinalGeography
{
	/// <summary>最终陆海掩码（世界事实；与渲染高度逐格一致）。</summary>
	public bool[] FinalLand { get; private set; } = Array.Empty<bool>();
	/// <summary>陆占比 ∈[0,1]（格+6 邻居 FinalLand 占比；连续辅助数据）。</summary>
	public float[] FinalLandFraction { get; private set; } = Array.Empty<float>();
	/// <summary>陆块归属（FinalLand 连通分量；海格 = −1）。</summary>
	public int[] FinalLandmassId { get; private set; } = Array.Empty<int>();
	public int FinalLandmassCount { get; private set; }
	/// <summary>陆格离海跳数（海格 = 0）。</summary>
	public int[] FinalDistToCoast { get; private set; } = Array.Empty<int>();
	/// <summary>海格离岸跳数（陆格 = 0）。</summary>
	public int[] FinalDistToLand { get; private set; } = Array.Empty<int>();
	/// <summary>最终区域归属（放置区域优先；山脉新增岛屿归入最近构造域；海格 = −1）。</summary>
	public int[] FinalRegionOfCell { get; private set; } = Array.Empty<int>();

	public void Generate(Ball ball, HeightComposer composer, GeologicalRegions regions)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		int n = ball.CellDirs.Length;
		var dirs = ball.CellDirs;

		// ── ① FinalLand（世界事实 = 最终高度符号；渲染逐格一致）──
		FinalLand = new bool[n];
		for (int i = 0; i < n; i++)
			FinalLand[i] = composer.HeightM[i] > 0f;

		// ── ①b 陆占比（**邻域拓扑口径**，T8 性能重构）：格 + 6 邻居中 FinalLand 占比 ∈[0,1]。
		//    原 7 点全链连续采样（12s 增量大头）退役——量纲不变（局部陆占比），判定基础
		//    同源（都源自 FinalLand/最终高度），成本 O(n)；供海岸过渡/滩涂/植被边缘的
		//    连续用途。FinalLand 与逐格高度符号的强不变量不受影响。
		FinalLandFraction = new float[n];
		for (int i = 0; i < n; i++)
		{
			float sum = FinalLand[i] ? 1f : 0f;
			int cnt = 1;
			foreach (int j in ball.CellNeighbors[i])
			{
				sum += FinalLand[j] ? 1f : 0f;
				cnt++;
			}
			FinalLandFraction[i] = sum / cnt;
		}

		// ── ② 陆块连通分量（FinalLand flood fill）──
		FinalLandmassId = new int[n];
		Array.Fill(FinalLandmassId, -1);
		var neighbors = ball.CellNeighbors;
		int landmassCount = 0;
		var bfs = new Queue<int>(n);
		for (int seed = 0; seed < n; seed++)
		{
			if (!FinalLand[seed] || FinalLandmassId[seed] >= 0) continue;
			FinalLandmassId[seed] = landmassCount;
			bfs.Enqueue(seed);
			while (bfs.Count > 0)
			{
				int i = bfs.Dequeue();
				foreach (int j in neighbors[i])
				{
					if (!FinalLand[j] || FinalLandmassId[j] >= 0) continue;
					FinalLandmassId[j] = landmassCount;
					bfs.Enqueue(j);
				}
			}
			landmassCount++;
		}
		FinalLandmassCount = landmassCount;

		// ── ③ 双向 BFS 海岸距离 ──
		FinalDistToCoast = BfsFrom(ball, i => !FinalLand[i]);
		FinalDistToLand = BfsFrom(ball, i => FinalLand[i]);

		// ── ④ 最终区域归属（**显式拓扑归属策略**，决策 07 v2：非隐式 fallback）——
		//    放置区域优先（区域=构造背景，背景不因地貌抬升改变）；山脉新增岛屿（放置期
		//    海格被抬成陆）归入 RegionIndexAt = 扭曲坐标上最近的区域种子。已知边界情形：
		//    跨海脊连接的两陆之间新岛可能归入对面大陆的区域——区域 ≠ 陆块（陆块看
		//    FinalLandmassId），下游按区域统计时以此口径为准。
		FinalRegionOfCell = new int[n];
		for (int i = 0; i < n; i++)
		{
			int placed = regions.RegionOfCell[i];
			if (placed >= 0) { FinalRegionOfCell[i] = placed; continue; }
			FinalRegionOfCell[i] = FinalLand[i] ? regions.RegionIndexAt(dirs[i]) : -1;
		}
	}

	/// <summary>格 i 的最终表面采样点：格心 + H3 角点（与投影器多点采样同构）。</summary>
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
