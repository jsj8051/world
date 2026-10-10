using World.H3Grid;               // Ball

namespace World.Logic;

/// <summary>
/// **③ 世界事实阶段**（Final）：由最终高度派生 FinalLand / 陆块归属 / 区域归属 / 海岸距离。
///
/// ★此阶段之后，下游（气候 / 水文 / 表现层）**只看 <see cref="Final"/>**，
///   不再回看任何生成依据（架构 §3.2）。
/// </summary>
public sealed class FactsPipeline
{
	/// <summary>最终地理（世界事实层）。</summary>
	public FinalGeography Final { get; private set; }

	public void Run(Ball ball, HeightComposer composer, GeologicalRegions regions)
	{
		// 决策 07 ④⑤：FinalLand/Landmass/Region/Coast 全部由最终高度派生
		Final = new FinalGeography();
		Final.Generate(ball, composer, regions);
	}
}
