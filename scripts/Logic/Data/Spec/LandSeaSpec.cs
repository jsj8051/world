namespace World.Data;

/// <param name="ContinentCount">大陆锚点数（蓝噪声撒布；海陆场塑形用）。</param>
/// <param name="LandFraction">目标陆地**面积**占比（分位校准钉死；不是"陆板数占比"）。</param>
/// <param name="WarpWavelengthKm">域扭曲波长（km）。</param>
/// <param name="WarpOctaves">域扭曲八度数。</param>
/// <param name="WarpAmplitudeKm">域扭曲幅度（km）。**0 = 关断扭曲**（该参数的退化/关断值）。</param>
/// <param name="LowWavelengthKm">大尺度轮廓波长（km）——大陆整体形状。</param>
/// <param name="LowOctaves">大尺度八度数。</param>
/// <param name="LowAmplitude">大尺度幅度（无纲量）。</param>
/// <param name="MediumWavelengthKm">中尺度波长（km）——半岛 / 海湾。</param>
/// <param name="MediumAmplitude">中尺度幅度（乘归属大陆的 `CoastComplexity`，即"海岸性格"）。</param>
/// <param name="SmallWavelengthKm">小尺度波长（km）——海岸线细节。</param>
/// <param name="SmallAmplitude">小尺度幅度（乘归属大陆的 `CoastComplexity`）。</param>
public readonly record struct LandSeaSpec(
	int ContinentCount,
	float LandFraction,
	float WarpWavelengthKm,
	int WarpOctaves,
	float WarpAmplitudeKm,
	float LowWavelengthKm,
	int LowOctaves,
	float LowAmplitude,
	float MediumWavelengthKm,
	float MediumAmplitude,
	float SmallWavelengthKm,
	float SmallAmplitude);
