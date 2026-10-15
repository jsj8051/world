using World.H3Grid;               // Ball

namespace World.Logic;

/// <summary>
/// **③ 世界事实阶段**（Final）：由最终高度派生 FinalLand / 陆块归属 / 海岸距离。
/// 此阶段之后，下游（气候 / 水文 / 表现层）**只看 <see cref="Final"/>**，不再回看生成依据。
/// </summary>
public sealed class FactsPipeline
{
	/// <summary>最终地理（世界事实层）。</summary>
	public FinalGeography Final { get; private set; }

	public void Run(Ball ball, float[] heightM, float seaLevelM)
	{
		Final = new FinalGeography();
		Final.Generate(ball, heightM, seaLevelM);
	}
}
