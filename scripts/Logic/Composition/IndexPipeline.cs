using System.Collections.Generic;
using World.H3Grid;               // Ball

namespace World.Logic;

/// <summary>
/// **⑥ 空间索引阶段**（SpatialIndex）：Final 世界事实的 nearest / distance / within 查询基础设施。
///
/// 排在最后：索引要吃水文产物（河格集合），故必须在 ⑤ 之后建。输入 = Final 事实 + 河格。
/// </summary>
public sealed class IndexPipeline
{
	/// <summary>Final 空间索引（#13 v1）。</summary>
	public FinalSpatialIndex Index { get; private set; }

	public void Run(Ball ball, FinalGeography final, RiverNetwork rivers)
	{
		Index = new FinalSpatialIndex(ball);

		var riverCells = new List<int>();
		for (int i = 0; i < rivers.IsRiver.Length; i++)
			if (rivers.IsRiver[i]) riverCells.Add(i);
		Index.Generate(final, riverCells);
	}
}
