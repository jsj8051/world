namespace World.Data;

/// <summary>
/// 板块阶段配置 —— **参数实例**（可变类；参数管理器持有唯一一份）。
/// 属性初值 = **出厂档**，必须与正库默认档 `res/params/world_params.json` 同值。
/// 含两块：板块划分（数量/速度/边界扰动）与陆地场（判陆阈值/多尺度轮廓）。
/// </summary>
public sealed class PlateSpec
{
	// ── 板块划分 ──
	/// <summary>板块数（= 种子点数，也 = 大陆锚点数）。出厂值 12。</summary>
	public int PlateCount { get; set; } = 12;

	/// <summary>速率下界（含）。出厂值 0。</summary>
	public float MinSpeed { get; set; } = 0f;

	/// <summary>速率上界（不含）。出厂值 1。</summary>
	public float MaxSpeed { get; set; } = 1f;

	/// <summary>边界扰动幅度（无量纲）。0 = 均匀阻力 ⇒ 纯测地 Voronoi。出厂值 5。</summary>
	public float BoundaryAmplitude { get; set; } = 5f;

	/// <summary>扰动基波长（km）。出厂值 1200。</summary>
	public float BoundaryWavelengthKm { get; set; } = 1200f;

	/// <summary>扰动倍频层数。出厂值 4。</summary>
	public int BoundaryOctaves { get; set; } = 4;

	// ── 陆地场 ──
	/// <summary>判陆阈值 ∈ [0,1]：逐格陆地标量（值域 [0,1]）&gt; 它即陆。出厂值 0.29。</summary>
	public float SeaLevel { get; set; } = 0.29f;

	/// <summary>板块陆地身份判定阈值（板块内陆格占比 &gt; 它 = 陆壳板块）。出厂值 0.5。</summary>
	public float IsLandThreshold { get; set; } = 0.5f;

	/// <summary>域扭曲波长（km）。出厂值 3000。</summary>
	public float WarpWavelengthKm { get; set; } = 3000f;

	/// <summary>域扭曲八度数。出厂值 3。</summary>
	public int WarpOctaves { get; set; } = 3;

	/// <summary>域扭曲幅度（km）。0 = 关断扭曲。出厂值 600。</summary>
	public float WarpAmplitudeKm { get; set; } = 600f;

	/// <summary>大尺度轮廓波长（km）——大陆整体形状。出厂值 5000。</summary>
	public float LowWavelengthKm { get; set; } = 5000f;

	/// <summary>大尺度八度数。出厂值 2。</summary>
	public int LowOctaves { get; set; } = 2;

	/// <summary>大尺度幅度（无纲量）。出厂值 0.35。</summary>
	public float LowAmplitude { get; set; } = 0.35f;

	/// <summary>中尺度波长（km）——半岛/海湾。出厂值 1200。</summary>
	public float MediumWavelengthKm { get; set; } = 1200f;

	/// <summary>中尺度幅度（乘归属锚点的 CoastComplexity）。出厂值 0.25。</summary>
	public float MediumAmplitude { get; set; } = 0.25f;

	/// <summary>小尺度波长（km）——海岸线细节。出厂值 300。</summary>
	public float SmallWavelengthKm { get; set; } = 300f;

	/// <summary>小尺度幅度（乘归属锚点的 CoastComplexity）。出厂值 0.12。</summary>
	public float SmallAmplitude { get; set; } = 0.12f;

	/// <summary>逐字段复制到 <paramref name="target"/>（不动实例身份）。</summary>
	public void CopyTo(PlateSpec target)
	{
		target.PlateCount = PlateCount;
		target.MinSpeed = MinSpeed;
		target.MaxSpeed = MaxSpeed;
		target.BoundaryAmplitude = BoundaryAmplitude;
		target.BoundaryWavelengthKm = BoundaryWavelengthKm;
		target.BoundaryOctaves = BoundaryOctaves;
		target.SeaLevel = SeaLevel;
		target.IsLandThreshold = IsLandThreshold;
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

	/// <summary>独立副本（改一个字段、其余不动时用它）。</summary>
	public PlateSpec Clone() => new()
	{
		PlateCount = PlateCount,
		MinSpeed = MinSpeed,
		MaxSpeed = MaxSpeed,
		BoundaryAmplitude = BoundaryAmplitude,
		BoundaryWavelengthKm = BoundaryWavelengthKm,
		BoundaryOctaves = BoundaryOctaves,
		SeaLevel = SeaLevel,
		IsLandThreshold = IsLandThreshold,
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
