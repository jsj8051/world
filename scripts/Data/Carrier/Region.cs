using World.Constants;    // GeologyClass（地质区域类别词表；常量族不是生成域类型）

namespace World.Data;

/// <summary>
/// 地质区域元数据：一张区域图上的"块"——同类格连通成一片。
/// </summary>
public sealed class Region
{
	/// <summary>区域号（全局，按类别序 + 片序编号）。</summary>
	public int Id;
	/// <summary>类别（八类之一）。</summary>
	public GeologyClass Class;
	/// <summary>格数。</summary>
	public int CellCount;
	/// <summary>面积（km²；等积口径，见 <see cref="World.Logic.SpatialScale"/>）。</summary>
	public float AreaKm2;
}
