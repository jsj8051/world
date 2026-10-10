namespace World.Data;

/// <summary>
/// ① 海陆骨架阶段配置 —— **参数实例**（可变类；参数管理器持有唯一一份，读它 / 改它都是同一个对象）。
/// 属性初值 = **出厂档**，必须与正库默认档 `res/params/world_params.json` 同值（逐值守卫 `WorldSpecTests`）；
/// 默认档读不到时世界按它生成，不会静默退化成全 0。
/// 生成本身只读本实例；改它只有一个正门 = `World.Params.WorldParams`（改实例 + 写文件一体）。
/// </summary>
/// <remarks>
/// 字段语义（逐条，与默认档注释同口径）：
/// <list type="bullet">
/// <item><c>ContinentCount</c>：大陆锚点数（蓝噪声撒布；海陆场塑形用）。</item>
/// <item><c>LandFraction</c>：目标陆地**面积**占比（分位校准钉死；不是"陆板数占比"）。</item>
/// <item><c>WarpWavelengthKm</c>：域扭曲波长（km）。</item>
/// <item><c>WarpOctaves</c>：域扭曲八度数。</item>
/// <item><c>WarpAmplitudeKm</c>：域扭曲幅度（km）。**0 = 关断扭曲**（该参数的退化/关断值）。</item>
/// <item><c>LowWavelengthKm</c>：大尺度轮廓波长（km）——大陆整体形状。</item>
/// <item><c>LowOctaves</c>：大尺度八度数。</item>
/// <item><c>LowAmplitude</c>：大尺度幅度（无纲量）。</item>
/// <item><c>MediumWavelengthKm</c>：中尺度波长（km）——半岛 / 海湾。</item>
/// <item><c>MediumAmplitude</c>：中尺度幅度（乘归属大陆的 <c>CoastComplexity</c>，即"海岸性格"）。</item>
/// <item><c>SmallWavelengthKm</c>：小尺度波长（km）——海岸线细节。</item>
/// <item><c>SmallAmplitude</c>：小尺度幅度（乘归属大陆的 <c>CoastComplexity</c>）。</item>
/// </list>
/// <para>
/// ★JSON 字段名 = 属性名（`System.Text.Json` 按属性读写，`float` 字段就读成 `float`——
///   精度纪律见 `World.Params.WorldParams` 的 `JsonOptions` 头注释）。
/// </para>
/// </remarks>
public sealed class LandSeaSpec
{
	/// <summary>大陆锚点数（蓝噪声撒布；海陆场塑形用）。出厂值 7。</summary>
	public int ContinentCount { get; set; } = 7;

	/// <summary>目标陆地**面积**占比（分位校准钉死；不是"陆板数占比"）。出厂值 0.29。</summary>
	public float LandFraction { get; set; } = 0.29f;

	/// <summary>域扭曲波长（km）。出厂值 3000。</summary>
	public float WarpWavelengthKm { get; set; } = 3000f;

	/// <summary>域扭曲八度数。出厂值 3。</summary>
	public int WarpOctaves { get; set; } = 3;

	/// <summary>域扭曲幅度（km）。**0 = 关断扭曲**。出厂值 600。</summary>
	public float WarpAmplitudeKm { get; set; } = 600f;

	/// <summary>大尺度轮廓波长（km）——大陆整体形状。出厂值 5000。</summary>
	public float LowWavelengthKm { get; set; } = 5000f;

	/// <summary>大尺度八度数。出厂值 2。</summary>
	public int LowOctaves { get; set; } = 2;

	/// <summary>大尺度幅度（无纲量）。出厂值 0.35。</summary>
	public float LowAmplitude { get; set; } = 0.35f;

	/// <summary>中尺度波长（km）——半岛 / 海湾。出厂值 1200。</summary>
	public float MediumWavelengthKm { get; set; } = 1200f;

	/// <summary>中尺度幅度（乘归属大陆的 <c>CoastComplexity</c>）。出厂值 0.25。</summary>
	public float MediumAmplitude { get; set; } = 0.25f;

	/// <summary>小尺度波长（km）——海岸线细节。出厂值 300。</summary>
	public float SmallWavelengthKm { get; set; } = 300f;

	/// <summary>小尺度幅度（乘归属大陆的 <c>CoastComplexity</c>）。出厂值 0.12。</summary>
	public float SmallAmplitude { get; set; } = 0.12f;

	/// <summary>逐字段复制到 <paramref name="target"/>（**不动实例身份**：目标对象还是原来那个）。</summary>
	public void CopyTo(LandSeaSpec target)
	{
		target.ContinentCount = ContinentCount;
		target.LandFraction = LandFraction;
		target.WarpWavelengthKm = WarpWavelengthKm;
		target.WarpOctaves = WarpOctaves;
		target.WarpAmplitudeKm = WarpAmplitudeKm;
		target.LowWavelengthKm = LowWavelengthKm;
		target.LowOctaves = LowOctaves;
		target.LowAmplitude = LowAmplitude;
		target.MediumWavelengthKm = MediumWavelengthKm;
		target.MediumAmplitude = MediumAmplitude;
		target.SmallWavelengthKm = SmallWavelengthKm;
		target.SmallAmplitude = SmallAmplitude;
	}

	/// <summary>独立副本（测试要"改一个字段、其余不动"时用它——**绝不**拿管理器那份去改）。</summary>
	public LandSeaSpec Clone() => new()
	{
		ContinentCount = ContinentCount,
		LandFraction = LandFraction,
		WarpWavelengthKm = WarpWavelengthKm,
		WarpOctaves = WarpOctaves,
		WarpAmplitudeKm = WarpAmplitudeKm,
		LowWavelengthKm = LowWavelengthKm,
		LowOctaves = LowOctaves,
		LowAmplitude = LowAmplitude,
		MediumWavelengthKm = MediumWavelengthKm,
		MediumAmplitude = MediumAmplitude,
		SmallWavelengthKm = SmallWavelengthKm,
		SmallAmplitude = SmallAmplitude,
	};
}
