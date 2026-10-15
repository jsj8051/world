using System;
using World.Data;                 // TerrainSpec（本阶段配置）
using World.H3Grid;               // Ball

namespace World.Logic;

/// <summary>
/// **① 地形阶段**：高度合成。编排层，实现全在 <see cref="TerrainManager"/>。
///
/// · `Composer` = 陆面常量基线 + 三档变化（海面平坦 0 m）；`DisplayElevation` 是它的显示别名。
/// </summary>
public sealed class TerrainPipeline
{
	readonly TerrainManager _impl = new();

	/// <summary>高度合成（常量基线 + 三档变化；最终高度 `HeightM` 单一事实源）。</summary>
	public HeightComposer Composer => _impl.Composer;
	/// <summary>显示海拔（= `Composer.HeightM`；信息卡/判读口）。</summary>
	public float[] DisplayElevation { get; private set; } = Array.Empty<float>();

	/// <param name="seed">世界种子（**全局层**：按值展开成 `int`，本阶段收不到 `WorldSpec`）。</param>
	/// <param name="spec">本阶段配置**实例**（**阶段层**）。</param>
	/// <param name="placementLand">上游板块阶段的陆地掩码。</param>
	public void Run(Ball ball, int seed, TerrainSpec spec, bool[] placementLand)
	{
		_impl.Run(ball, seed, spec, placementLand);
		DisplayElevation = _impl.Composer.HeightM;
	}
}
