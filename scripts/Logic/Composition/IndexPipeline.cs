using System.Collections.Generic;
using World.H3Grid;               // Ball

namespace World.Logic;

/// <summary>
/// **④ 空间索引阶段**（SpatialIndex）：世界事实的 nearest / distance / within 查询基础设施。
///
/// 排在最后：索引要吃水文产物（河格集合），故必须在 ③ 之后建。输入 = 世界事实 + 河格。
/// </summary>
public sealed class IndexPipeline
{
	/// <summary>世界事实的空间索引（#13 v1）。</summary>
	public FinalSpatialIndex Index { get; private set; }

	/// <summary>导入地图：直接装配已保存的查询索引（不重跑 BFS）。</summary>
	internal void LoadSaved(FinalSpatialIndex index)
		=> Index = index ?? throw new System.ArgumentNullException(nameof(index));

	public void Run(Ball ball, TerrainPipeline terrain, RiverNetwork rivers)
	{
		Index = new FinalSpatialIndex(ball);

		var riverCells = new List<int>();
		for (int i = 0; i < rivers.IsRiver.Length; i++)
			if (rivers.IsRiver[i]) riverCells.Add(i);
		Index.Generate(terrain, riverCells);
	}
}
