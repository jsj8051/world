namespace World.Data;

/// <summary>
/// ① 地形阶段配置 —— **参数实例**（可变类；参数管理器持有唯一一份）。
/// 属性初值 = **出厂档**，必须与正库默认档 `res/params/world_params.json` 同值。
///
/// 高度 = 陆面（基线 + 三档幅值 × 各自变化场）或海面（`SeaLevelM` 一档平地）。
/// 与板块阶段的 `PlateSpec.SeaLevel` 不是一回事：那个是**判陆阈值**（∈[0,1]，决定哪些格算陆），
/// 本表的 `SeaLevelM` 是**海拔标高**（米，海面高度本身）。
/// </summary>
public sealed class TerrainSpec
{
	// ── 海陆基准 ──

	/// <summary>海面标高（米）——海格恒平这一档，也是「高于它才是陆」的判据
	/// （世界事实 `FinalLand = 高度 &gt; SeaLevelM`、水文的「elev ≤ SeaLevelM 即海」）。
	/// 出厂值 0。</summary>
	public float SeaLevelM { get; set; } = 0f;

	/// <summary>陆格海拔保底（米）——放置期陆地不因变化场淹死。**须 &gt; `SeaLevelM`**
	/// （否则陆地一律被判成海）。出厂值 5。</summary>
	public float MinLandElevationM { get; set; } = 5f;

	// ── 陆面幅度 ──

	/// <summary>陆面常量基线（米）——地形塑造的低平台底子。出厂值 30。</summary>
	public float LandBaseM { get; set; } = 30f;

	/// <summary>大尺度起伏幅值（米）。出厂值 45。</summary>
	public float LargeVariationM { get; set; } = 45f;

	/// <summary>中尺度起伏幅值（米；丘陵 / 山谷）。出厂值 27。</summary>
	public float MediumVariationM { get; set; } = 27f;

	/// <summary>细节起伏幅值（米）。出厂值 15。</summary>
	public float RegionalNoiseM { get; set; } = 15f;

	// ── 三档变化场：基波长（km；相隔这么久一个特征）+ 倍频层数（越细）──

	/// <summary>大尺度基波长（km）——大地形起伏。出厂值 5000。</summary>
	public float LargeWavelengthKm { get; set; } = 5000f;

	/// <summary>大尺度层数。出厂值 2。</summary>
	public int LargeOctaves { get; set; } = 2;

	/// <summary>中尺度基波长（km）——丘陵 / 山谷。出厂值 600。</summary>
	public float MediumWavelengthKm { get; set; } = 600f;

	/// <summary>中尺度层数。出厂值 3。</summary>
	public int MediumOctaves { get; set; } = 3;

	/// <summary>细节基波长（km）。出厂值 90。</summary>
	public float RegionalWavelengthKm { get; set; } = 90f;

	/// <summary>细节层数。出厂值 2。</summary>
	public int RegionalOctaves { get; set; } = 2;

	/// <summary>逐字段复制到 <paramref name="target"/>（不动实例身份）。</summary>
	public void CopyTo(TerrainSpec target)
	{
		target.SeaLevelM = SeaLevelM;
		target.MinLandElevationM = MinLandElevationM;
		target.LandBaseM = LandBaseM;
		target.LargeVariationM = LargeVariationM;
		target.MediumVariationM = MediumVariationM;
		target.RegionalNoiseM = RegionalNoiseM;
		target.LargeWavelengthKm = LargeWavelengthKm;
		target.LargeOctaves = LargeOctaves;
		target.MediumWavelengthKm = MediumWavelengthKm;
		target.MediumOctaves = MediumOctaves;
		target.RegionalWavelengthKm = RegionalWavelengthKm;
		target.RegionalOctaves = RegionalOctaves;
	}

	/// <summary>独立副本（测试用；**绝不**拿管理器那份去改）。</summary>
	public TerrainSpec Clone() => new()
	{
		SeaLevelM = SeaLevelM,
		MinLandElevationM = MinLandElevationM,
		LandBaseM = LandBaseM,
		LargeVariationM = LargeVariationM,
		MediumVariationM = MediumVariationM,
		RegionalNoiseM = RegionalNoiseM,
		LargeWavelengthKm = LargeWavelengthKm,
		LargeOctaves = LargeOctaves,
		MediumWavelengthKm = MediumWavelengthKm,
		MediumOctaves = MediumOctaves,
		RegionalWavelengthKm = RegionalWavelengthKm,
		RegionalOctaves = RegionalOctaves,
	};
}
