using System;
using System.Collections.Generic;
using World.H3Grid;               // Ball
using World.Data;                 // TerrainSpec（本阶段配置：纯数据形状，数据层）

namespace World.WorldGen;

/// <summary>
/// **② 区域与地形阶段**（Placement·下半）：地质区域 → 山脉骨架 → 区域地貌 → 火山 → 高度合成。
///
/// ★**不是"逐个区域跑一遍"**：区域只是塑形依据，山系/盆地可跨区域 ⇒ 必须全局一趟算完。
///   改成逐区域循环会破坏跨区约束（山脊跨界）与确定性。
/// ★本类只聚合引用——逐格数组仍归各子系统类自己持有（SoA）。
/// </summary>
public sealed class TerrainPipeline
{
	public GeologicalRegions Regions { get; private set; }  // 地质区域（两级 Voronoi + 约束式类型）
	public MountainSkeleton Mountains { get; private set; } // 山脉骨架（主脊+分支 → 高斯包络 × ridged 细化）
	public RegionalLandforms Landforms { get; private set; }    // 区域地貌（高原帽 / 盆地下挖）
	public VolcanoField Volcanoes { get; private set; }    // 火山（Feature 实证第一例）
	public HeightComposer Composer { get; private set; }    // 高度合成（FinalHeight 单一事实源）
	/// <summary>显示海拔（= `Composer.HeightM`；信息卡/判读口）。</summary>
	public float[] DisplayElevation { get; private set; } = Array.Empty<float>();

	/// <param name="seed">世界种子（**全局层**：按值展开成 `int`，本阶段收不到 `WorldSpec`）。</param>
	/// <param name="spec">本阶段配置（**阶段层**：只含本阶段参数，见 <see cref="TerrainSpec"/>）。</param>
	/// <param name="projector">上游海陆投影（区域划分吃它）。</param>
	/// <param name="surface">上游唯一海陆口径（山脉生成吃它）。</param>
	public void Run(Ball ball, int seed, TerrainSpec spec,
		H3LandSeaProjector projector, SurfaceResolver surface)
	{
		Regions = new GeologicalRegions(seed);
		Regions.Generate(ball, projector, spec.TargetRegionAreaKm2);

		Mountains = new MountainSkeleton(seed);
		Mountains.Generate(ball, Regions, surface);

		Landforms = new RegionalLandforms(seed);
		Landforms.Generate(ball, Regions);

		Volcanoes = new VolcanoField();
		Volcanoes.Place(ball, Regions, Mountains.Tectonic, seed, Mountains.RangeAnchors());

		// 特征场列表（架构 v1 冻结：新特征 = 实现 ITerrainField + 插入本列表——
		// 合成器/FinalGeography 零改动；列表序 = 优先级序，后位覆盖先位）
		var features = new List<FeatureField>
		{
			new(Landforms, TerrainDomain.LandOnly),      // Plateau(2)/Basin(3) 组合口
			new(Mountains, TerrainDomain.LandAndSea),    // Mountain(4)：跨海（海底脊/海山）
			new(Volcanoes, TerrainDomain.LandAndSea),    // Volcano(4.5)：最局部，后位覆盖
		};
		Composer = new HeightComposer(seed);
		Composer.Generate(ball, surface, Regions, features);
		DisplayElevation = Composer.HeightM;   // 最终高度（决策 07 ③）
	}
}
