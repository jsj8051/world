using System;
using Godot;                 // 仅 Vector3 结构体（纯值类型）；测试宿主可用
using World.H3Grid;    // Ball（H3 球壳数据层）

namespace World.Logic;

/// <summary>
/// 最终地理（世界事实层）：由最终高度派生 FinalLand 掩码，再派生陆块 / 海岸距离。
/// 此阶段之后，下游（气候 / 水文 / 表现层）只看这里的产物。
///
/// · FinalLand = 最终高度 &gt; 0（与渲染逐格一致——掩码说陆则画面必是陆）；
/// · FinalLandFraction = 格 + 6 邻居中陆占比 ∈[0,1]（海岸过渡/滩涂/植被边缘用的连续辅助量，不做硬判）；
/// · FinalLandmassId = FinalLand 连通分量（一片连通陆地 = 一个陆块；海格 = −1）；
/// · FinalDistToCoast/Land = FinalLand 的双向 BFS 跳数。
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

	public void Generate(Ball ball, HeightComposer composer)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		int n = ball.CellDirs.Length;

		// ── ① FinalLand（世界事实 = 最终高度符号；渲染逐格一致）──
		FinalLand = new bool[n];
		for (int i = 0; i < n; i++)
			FinalLand[i] = composer.HeightM[i] > 0f;

		// ── ①b 陆占比：格 + 6 邻居中 FinalLand 占比 ∈[0,1] ──
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
		(FinalLandmassId, FinalLandmassCount) = LandMaskOps.Components(ball, i => FinalLand[i]);

		// ── ③ 双向 BFS 海岸距离 ──
		FinalDistToCoast = LandMaskOps.HopsFrom(ball, i => !FinalLand[i]);
		FinalDistToLand = LandMaskOps.HopsFrom(ball, i => FinalLand[i]);
	}
}
