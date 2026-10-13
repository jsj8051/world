namespace World.Data;

/// <summary>
/// ① 区域与地形阶段配置 —— **参数实例**（可变类；参数管理器持有唯一一份）。
/// 当前无字段：区域图与高度合成的尺度旋钮都在各自实现类里（`GeologicalRegionManager` / `HeightComposer`）。
/// 保留本段 = 世界定义参数的稳定形状，地形内容上线时在这里加字段。
/// </summary>
public sealed class TerrainSpec
{
	/// <summary>逐字段复制（当前无字段；保留以维持 `WorldSpec.CopyTo` 的形状）。</summary>
	public void CopyTo(TerrainSpec target) { }

	/// <summary>独立副本（测试用；**绝不**拿管理器那份去改）。</summary>
	public TerrainSpec Clone() => new();
}
