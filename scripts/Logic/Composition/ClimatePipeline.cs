using World.H3Grid;               // Ball

namespace World.Logic;

/// <summary>
/// **④ 气候阶段**（World Simulation·气候）：降水 → 温度。
///
/// ★只消费世界事实（`Final`）与最终高度，不看生成依据。
/// </summary>
public sealed class ClimatePipeline
{
	public PrecipitationModel Precipitation { get; private set; }  // 降水（水文阶段 ⑤ 的输入）
	public Temperature Temperature { get; private set; }          // 温度场（v1：逐格能量收支）

	public void Run(Ball ball, FinalGeography final, HeightComposer composer)
	{
		Precipitation = new PrecipitationModel();
		Precipitation.Generate(ball, final);

		Temperature = new Temperature();
		Temperature.Generate(ball, composer, final);
	}
}
