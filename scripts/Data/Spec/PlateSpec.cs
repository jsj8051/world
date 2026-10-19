namespace World.Data;

/// <summary>
/// 板块阶段配置 —— **参数实例**（可变类；参数管理器持有唯一一份）。
/// 属性初值 = **出厂档**，必须与正库默认档 `res/params/world_params.json` 同值。
/// 只含板块划分（数量 / 速度 / 边界扰动）与壳型（陆壳占比）——
/// 「哪里是陆、哪里是海」不由本表决定：陆壳铺陆面高度，海岸线由 ① 阶段的高度定。
/// </summary>
public sealed class PlateSpec
{
	// ── 板块划分 ──
	/// <summary>板块数（= 种子点数，也 = 洪泛区域的块数）。出厂值 8——
	/// 每块板块更大 ⇒ 陆壳板块更少更大 ⇒ 大陆少而大（配合 `ContinentalRatio` 得 ≈4 块陆壳）。</summary>
	public int PlateCount { get; set; } = 8;

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

	/// <summary>陆壳板块占板块数的比例 ∈ (0,1]：按「格数 × 随机抖动」打分取前 N 块，
	/// N = clamp(round(板块数 × 它), 1, 板块数)。陆壳的格由 ① 阶段铺陆面高度 ⇒ 陆地长在陆壳上。出厂值 0.5。</summary>
	public float ContinentalRatio { get; set; } = 0.5f;

	/// <summary>逐字段复制到 <paramref name="target"/>（不动实例身份）。</summary>
	public void CopyTo(PlateSpec target)
	{
		target.PlateCount = PlateCount;
		target.MinSpeed = MinSpeed;
		target.MaxSpeed = MaxSpeed;
		target.BoundaryAmplitude = BoundaryAmplitude;
		target.BoundaryWavelengthKm = BoundaryWavelengthKm;
		target.BoundaryOctaves = BoundaryOctaves;
		target.ContinentalRatio = ContinentalRatio;
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
		ContinentalRatio = ContinentalRatio,
	};
}
