using System;
using World.NewHexWorld;    // Ball（H3 球壳数据层）

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 水文层（World Simulation 的**第一个下游消费者**，决策 08 v2）：
//   架构边界验证口——本类**只依赖 Final 世界事实**（FinalLand 掩码 + 最终高度数组），
//   不读 PlacementLand、不知道任何 LandformFeature/投影器/噪声的存在。
//   生成阶段的实现细节对它不可见——这是"Final World + Consumers"架构的成立性检验。
// 第一版（刻意克制，决策 08 v2 §3）：Final 地形 → 单流向 → 汇流累积 → 河流掩码。
//   只回答四件事：① 河流只在 FinalLand 上运行；② 海洋是最终汇出口；③ 高程梯度稳定
//   产生下坡流；④ 多条支流自然汇合。降水/湖泊/流域/侵蚀全部留待后续批次。
// 复用：H3Hydrology（W1 基建激活）——单流向（严格降高、平局取格索引最小）+ 拓扑序汇流。
//   海口判定 elev ≤ 0 与 FinalLand（HeightM > 0 = 陆）严格互补 ⇒ 无第二套海陆判断。
/// <summary>
/// 河网（水文第一版）：Final 地形上的单流向汇流网络 + 河流掩码。
/// </summary>
public sealed class RiverNetwork
{
	/// <summary>下游格索引（−1 = 出口：入海或内流洼地）。</summary>
	public int[] Downstream { get; private set; } = Array.Empty<int>();
	/// <summary>汇流累积（上游格数 + 1，含自身；海格恒 1）。</summary>
	public int[] FlowAccum { get; private set; } = Array.Empty<int>();
	/// <summary>河流掩码（FinalLand ∧ FlowAccum ≥ RiverThresholdCells）。</summary>
	public bool[] IsRiver { get; private set; } = Array.Empty<bool>();
	/// <summary>内流洼地格数（无更低邻居且不临海的陆格——湖泊语义候选，本版只计数）。</summary>
	public int SinkCount { get; private set; }

	/// <summary>河流判定阈值（汇流累积格数；40 格 ≈ 7 万 km² 流域，res4 量级）。</summary>
	public const int RiverThresholdCells = 40;

	public void Generate(Ball ball, FinalGeography final, HeightComposer composer,
		int riverThresholdCells = RiverThresholdCells)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		if (final == null) throw new ArgumentNullException(nameof(final));
		if (composer == null) throw new ArgumentNullException(nameof(composer));

		// H3Hydrology 的海判定（elev ≤ seaLevel=0）与 FinalLand（HeightM > 0 = 陆）严格互补
		var hydro = new H3Hydrology();
		hydro.Generate(ball, composer.HeightM, 0f);
		Downstream = hydro.Downstream;
		FlowAccum = hydro.FlowAccum;
		SinkCount = hydro.SinkCount;

		IsRiver = new bool[Downstream.Length];
		for (int i = 0; i < Downstream.Length; i++)
			IsRiver[i] = final.FinalLand[i] && FlowAccum[i] >= riverThresholdCells;
	}
}
