using World.H3Grid;               // Ball

namespace World.Logic;

/// <summary>
/// **② 气候阶段**（World Simulation·气候）：降水 → 温度。
///
/// ★只消费 ① 的世界事实（陆海 / 海岸距离）与最终高度，不看生成依据。
/// </summary>
public sealed class ClimatePipeline
{
	public PrecipitationModel Precipitation { get; private set; }  // 降水（水文阶段 ③ 的输入）
	public Temperature Temperature { get; private set; }          // 温度场（v1：逐格能量收支）

	public void Run(Ball ball, TerrainPipeline terrain)
	{
		Precipitation = new PrecipitationModel();
		Precipitation.Generate(ball, terrain);

		Temperature = new Temperature();
		Temperature.Generate(ball, terrain.HeightM);
	}
}
