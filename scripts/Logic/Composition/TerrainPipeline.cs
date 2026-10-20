using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// ① 地形阶段：高度合成，编排层，实现全在 <see cref="TerrainManager"/>。
/// <c>HeightM</c> = 逐格最终海拔（米）：陆壳铺陆面（基线 + 三档变化 + 高地），洋壳铺海底
/// （洋中脊 / 大陆架 / 海沟 / 岛弧）；全链单一事实源，海陆由这个高度定、不由壳型直接定。
/// </summary>
public sealed class TerrainPipeline
{
	readonly TerrainManager _impl = new();

	/// <summary>逐格最终海拔（米；渲染 / 信息卡 / 下游阶段的唯一来源）。</summary>
	public float[] HeightM => _impl.HeightM;

	/// <param name="seed">世界种子（全局层：按值展开成 <c>int</c>，本阶段收不到 <c>WorldSpec</c>）。</param>
	/// <param name="spec">本阶段配置实例（阶段层）。</param>
	/// <param name="plateOfCell">上游板块阶段的逐格板块号。</param>
	/// <param name="plates">上游板块阶段的板块表（壳型 / 欧拉矢量 / 密度）。</param>
	/// <param name="landBaseDropM">上游 ⓪ 给出的陆面基线偏移（米；群岛档压基线，其余 0）。</param>
	public void Run(Ball ball, int seed, TerrainSpec spec, int[] plateOfCell, Plate[] plates,
		float landBaseDropM)
		=> _impl.Run(ball, seed, spec, plateOfCell, plates, landBaseDropM);
}
