using World.H3Grid;               // Ball
using World.Data;                 // LandSeaSpec

namespace World.Logic;

/// <summary>
/// **① 海陆骨架阶段**（Placement·上半）：大陆锚点 → 连续海陆场 → H3 投影 → 唯一海陆口径。
///
/// ★产物是**生成依据**，不是世界事实（世界事实见 <see cref="FactsPipeline"/>）。
/// ★本类只**聚合引用**——逐格数组仍归各子系统类自己持有（SoA），此处不复制数据。
/// </summary>
public sealed class LandSeaPipeline
{
	public ContinentLayout Layout { get; private set; }     // 大陆锚点（蓝噪声 + 属性）
	public LandSeaField Field { get; private set; }         // 连续海陆场（域扭曲 + 三尺度）
	public H3LandSeaProjector Projector { get; private set; }   // H3 投影五件套
	public SurfaceResolver Surface { get; private set; }    // 唯一海陆口径

	/// <param name="seed">世界种子（**全局层**：按值展开成 `int`，本阶段收不到 `WorldSpec`）。</param>
	/// <param name="spec">本阶段配置（**阶段层**：只含本阶段参数，见 <see cref="LandSeaSpec"/>）。</param>
	public void Run(Ball ball, int seed, LandSeaSpec spec)
	{
		Layout = new ContinentLayout(seed, spec.ContinentCount);
		Field = new LandSeaField(Layout, seed, spec);   // seed 已由装配层按值展开；本阶段收不到 WorldSpec

		Projector = new H3LandSeaProjector();
		Projector.Generate(ball, Field, spec.LandFraction);

		// 唯一海陆口径（决策 07 步骤③）：SurfaceResolver 收拢本阶段的海陆判断
		Surface = new SurfaceResolver(Field, Projector.ThresholdUsed, Projector.SeaSpreadUsed);
	}
}
