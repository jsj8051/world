using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// ① 地形阶段：高度合成 + 世界事实派生，编排层，实现全在 <see cref="TerrainManager"/>。
/// <c>HeightM</c> = 逐格最终海拔（米）：陆壳铺陆面（基线 + 三档变化 + 高地），洋壳铺海底
/// （洋中脊 / 大陆架 / 海沟 / 岛弧）；全链单一事实源，海陆由这个高度定、不由壳型直接定。
/// <c>FinalLand</c> / 陆块 / 海岸距离是同一次产出的世界事实，下游只读这些，不看生成依据。
/// </summary>
public sealed class TerrainPipeline
{
	readonly TerrainManager _impl = new();

	/// <summary>逐格最终海拔（米；渲染 / 信息卡 / 下游阶段的唯一来源）。</summary>
	public float[] HeightM => _impl.HeightM;

	/// <summary>最终陆海掩码（<c>HeightM &gt; SeaLevelM</c>；与渲染逐格一致）。</summary>
	public bool[] FinalLand => _impl.FinalLand;
	/// <summary>陆占比 ∈[0,1]（格 + 6 邻居陆占比；海岸过渡的连续辅助量）。</summary>
	public float[] FinalLandFraction => _impl.FinalLandFraction;
	/// <summary>陆块归属（陆海掩码连通分量；海格 = −1）。</summary>
	public int[] FinalLandmassId => _impl.FinalLandmassId;
	public int FinalLandmassCount => _impl.FinalLandmassCount;
	/// <summary>陆格离海跳数（海格 = 0）。</summary>
	public int[] FinalDistToCoast => _impl.FinalDistToCoast;
	/// <summary>海格离岸跳数（陆格 = 0）。</summary>
	public int[] FinalDistToLand => _impl.FinalDistToLand;
	/// <summary>本阶段的构造影响场（地形与地图共用）。</summary>
	public TectonicInfluenceSet Tectonic => _impl.Tectonic;

	internal void SetSurface(Ball ball, float[] height, float seaLevelM, TectonicInfluenceSet tectonic)
		=> _impl.SetSurface(ball, height, seaLevelM, tectonic);

	/// <summary>导入地图：直接装配已保存的最终地形事实（不跑地形生成）。</summary>
	internal void LoadSaved(float[] heightM, bool[] finalLand, float[] finalLandFraction,
		int[] finalLandmassId, int finalLandmassCount, int[] finalDistToCoast, int[] finalDistToLand,
		TectonicInfluenceSet tectonic)
		=> _impl.LoadSaved(heightM, finalLand, finalLandFraction, finalLandmassId, finalLandmassCount,
			finalDistToCoast, finalDistToLand, tectonic);

	/// <param name="seed">世界种子（全局层：按值展开成 <c>int</c>，本阶段收不到 <c>WorldSpec</c>）。</param>
	/// <param name="spec">本阶段配置实例（阶段层）。</param>
	/// <param name="plateOfCell">上游板块阶段的逐格板块号。</param>
	/// <param name="plates">上游板块阶段的板块表（欧拉矢量 / 密度）。</param>
	/// <param name="crust">上游逐格壳型（陆壳 / 洋壳）。</param>
	/// <param name="boundaries">上游边界集（构造影响场的源）。</param>
	/// <param name="boundary">上游 ⓪ 的边界场（逐格运动 / 角色 / 跳数）。</param>
	/// <param name="landBaseDropM">上游 ⓪ 给出的陆面基线偏移（米；群岛档压基线，其余 0）。</param>
	public void Run(Ball ball, int seed, TerrainSpec spec, int[] plateOfCell, Plate[] plates,
		CrustField crust, PlateBoundarySet boundaries, PlateBoundaryFields boundary, float landBaseDropM)
		=> _impl.Run(ball, seed, spec, plateOfCell, plates, crust, boundaries, boundary, landBaseDropM);
}
