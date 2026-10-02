using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using World.NewHexWorld;
using World.NoiseWorld;

namespace World.Tests;

// 临时诊断探针（验证后删除）：检查最终板图的粗格沙漏是否被护栏漏过。
public class PlateDumpProbe
{
	[Test]
	public void Diagnose()
	{
		var ball = new Ball(3, 1f);
		for (int seed = 42; seed <= 46; seed++)
		{
			var plates = new NoisePlates();
			plates.Generate(ball, 8, seed, 0.29f);

			// 从细格板图重建粗商图（与 Generate 内部同构：板号提升 + 去重）
			int n = ball.CellIds.Length;
			var plateOf = plates.PlateOfCell;
			int P = plates.NumPlates;
			var coarseIdx = new Dictionary<int, List<int>>();
			for (int i = 0; i < n; i++)
			{
				int p = plateOf[i];
				if (!coarseIdx.TryGetValue(p, out var list)) coarseIdx[p] = list = new List<int>();
				list.Add(i);
			}
			// 板子图 = 每板细格 + 板内细邻接；直接在细图上做"拔格测部件"（细粒度真值）
			var neighbors = ball.CellNeighbors;
			int bottleneckPlates = 0;
			var stamp = new int[n];
			int cur = 0;
			var bfs = new Queue<int>();
			for (int p = 0; p < P; p++)
			{
				if (!coarseIdx.TryGetValue(p, out var cells)) continue;
				float minLobe = Math.Max(2f, cells.Count * NoisePlates.BottleneckMinLobeFraction);
				bool flagged = false;
				foreach (int v in cells)
				{
					cur++;
					var sizes = new List<int>();
					foreach (int nb in neighbors[v])
					{
						if (plateOf[nb] != p || stamp[nb] == cur) continue;
						int size = 0;
						bfs.Enqueue(nb);
						stamp[nb] = cur;
						while (bfs.Count > 0)
						{
							int u = bfs.Dequeue();
							size++;
							foreach (int u2 in neighbors[u])
							{
								if (plateOf[u2] != p || u2 == v || stamp[u2] == cur) continue;
								stamp[u2] = cur;
								bfs.Enqueue(u2);
							}
						}
						sizes.Add(size);
					}
					int big = sizes.Count(sz => sz >= minLobe);
					if (big >= 2) { flagged = true; break; }
				}
				if (flagged) bottleneckPlates++;
			}
			TestContext.Out.WriteLine($"seed {seed}: 有窄颈的板块数 = {bottleneckPlates}/{P}");
		}
	}
}
