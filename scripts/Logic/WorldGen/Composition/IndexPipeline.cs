using System.Collections.Generic;
using Godot;                       // 仅 Vector3 值类型
using World.H3Grid;               // Ball

namespace World.WorldGen;

/// <summary>
/// **⑥ 空间索引阶段**（SpatialIndex）：Final 世界事实的 nearest / distance / within 查询基础设施。
///
/// ★排在最后：索引要吃水文产物（河格集合），故必须在 ⑤ 之后建。
/// </summary>
public sealed class IndexPipeline
{
	/// <summary>Final 空间索引（#13 v1）。</summary>
	public FinalSpatialIndex Index { get; private set; }

	public void Run(Ball ball, FinalGeography final, MountainSkeleton mountains,
		VolcanoField volcanoes, RiverNetwork rivers)
	{
		var mountainAnchors = new List<Vector3>();
		foreach (var sys in mountains.Systems) mountainAnchors.Add(sys.Anchor);
		Index = new FinalSpatialIndex(ball, mountainAnchors,
			volcanoes.Volcanoes.ConvertAll(v => v.Anchor));

		var riverCells = new List<int>();
		for (int i = 0; i < rivers.IsRiver.Length; i++)
			if (rivers.IsRiver[i]) riverCells.Add(i);
		Index.Generate(final, riverCells);
	}
}
