using Godot;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using World.NewHexWorld;        // Ball（H3 球壳数据层）

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 星球组件（阶段 1-4 落地场景件）：数据层（Ball + 大陆布局 + 海陆场 + H3 投影
// + 地质区域 + 山脉骨架）+ 视图层（复用 NoiseBallView——海拔源直供构造）。自包含、不含相机。
// 显示海拔 = 投影基线 + 区域类型偏移（阶段 3 占位调制）+ 山脉骨架加成（阶段 4，决策 04）。
public partial class WorldGenPlanet : Node3D
{
	[ExportGroup("星球")]
	[Export(PropertyHint.Enum, "res3 (41k 格)/res4 (288k 格)")]
	public int ResLevel = 4;                    // H3 分辨率档（res4 = 渲染预算实算舒适档）
	[Export] public float Radius = 2.0f;        // 球半径（与轨道相机 _planetRadius 同值时取景正确）
	[Export] public int ContinentCount = 7;     // 大陆锚点数（蓝噪声撒布；海陆场塑形用，地图量=陆块连通分量）
	[Export(PropertyHint.Range, "0.02,0.9,0.01")]
	public float LandFraction = 0.29f;          // 目标陆地占比（分位校准钉死）
	[Export] public int Seed = 42;
	[Export] public float TargetRegionAreaKm2 = GeologicalRegions.TargetRegionAreaKm2;   // 地质区域粒度（km²/区域）

	[ExportGroup("LOD 与剔除")]
	[Export] public float LodNearRatio = 6f;         // 相机距 < 球半径×此值 ⇒ 高分辨率面
	[Export] public float BackfaceCullRatio = 3.0f;

	public ContinentLayout Layout { get; private set; }     // 大陆锚点（蓝噪声 + 属性）
	public LandSeaField Field { get; private set; }         // 连续海陆场（域扭曲 + 三尺度）
	public H3LandSeaProjector Projector { get; private set; }   // H3 投影五件套
	public GeologicalRegions Regions { get; private set; }  // 地质区域（阶段 3：两级 Voronoi + 约束式类型）
	public MountainSkeleton Mountains { get; private set; } // 山脉骨架（阶段 4：主脊+分支 → 高斯包络 × ridged 细化）
	public RegionalLandforms Landforms { get; private set; }    // 区域地貌（阶段 5：高原帽 / 盆地下挖）
	public HeightComposer Composer { get; private set; }    // 高度合成（FinalHeight 单一事实源）
	public SurfaceResolver Surface { get; private set; }    // 唯一海陆口径（Placement 阶段）
	public FinalGeography Final { get; private set; }      // 最终地理（FinalLand/Landmass/Region/Coast——世界事实层）
	public VolcanoField Volcanoes { get; private set; }    // 火山（Feature 实证第一例；决策 08 冻结后首例）
	public RiverNetwork Rivers { get; private set; }      // 河网（World Simulation 第一下游消费者；只读 Final 层）
	public NoiseBallView View { get; private set; }         // 视图（复用现役渲染：LOD/剔除/拾取）
	/// <summary>显示海拔（= Composer.HeightM；信息卡/判读口）。</summary>
	public float[] DisplayElevation { get; private set; } = Array.Empty<float>();

	Ball _ball;
	int _timingDiag;   // 生成耗时打印限次

	public override void _Ready()
	{
		_ball = new Ball(ResLevel, Radius);
		Regenerate();
		View = new NoiseBallView(_ball, DisplayElevation,
			lodNearRatio: LodNearRatio, backfaceCullRatio: BackfaceCullRatio);
		AddChild(View);
		View.BuildChunks();
	}

	/// <summary>全量重算（锚点 → 场 → 投影 → 区域 → 骨架 → 地貌 → 合成）+ 重烘颜色纹理。</summary>
	public void Regenerate()
	{
		var sw = Stopwatch.StartNew();
		Layout = new ContinentLayout(Seed, ContinentCount);
		Field = new LandSeaField(Layout, new LandSeaParams { Seed = Seed });
		Projector = new H3LandSeaProjector();
		Projector.Generate(_ball, Field, LandFraction);
		// 唯一海陆口径（决策 07 步骤③）：SurfaceResolver 收拢 Placement 阶段判断
		Surface = new SurfaceResolver(Field, Projector.ThresholdUsed, Projector.SeaSpreadUsed);
		Regions = new GeologicalRegions(Seed);
		Regions.Generate(_ball, Projector, TargetRegionAreaKm2);
		Mountains = new MountainSkeleton(Seed);
		Mountains.Generate(_ball, Regions, Surface);
		Landforms = new RegionalLandforms(Seed);
		Landforms.Generate(_ball, Regions);
		Volcanoes = new VolcanoField();
		Volcanoes.Place(_ball, Regions, TectonicOf(Mountains), Seed, Mountains.RangeAnchors());

		// 特征场列表（架构 v1 冻结：新特征 = 实现 ITerrainField + 插入本列表——
		// 合成器/FinalGeography 零改动；列表序 = 优先级序，后位覆盖先位）
		var features = new List<FeatureField>
		{
			new(Landforms, TerrainDomain.LandOnly),      // Plateau(2)/Basin(3) 组合口
			new(Mountains, TerrainDomain.LandAndSea),    // Mountain(4)：跨海（海底脊/海山）
			new(Volcanoes, TerrainDomain.LandAndSea),    // Volcano(4.5)：最局部，后位覆盖
		};
		Composer = new HeightComposer(Seed);
		Composer.Generate(_ball, Surface, Regions, features);
		DisplayElevation = Composer.HeightM;   // 最终高度（决策 07 ③）
		// 最终地理（决策 07 ④⑤）：FinalLand/Landmass/Region/Coast 全部由最终高度派生
		Final = new FinalGeography();
		Final.Generate(_ball, Composer, Regions);
		Rivers = new RiverNetwork();
		Rivers.Generate(_ball, Final, Composer);

		if (_timingDiag++ < 3)
			GD.Print($"[WORLDGEN-TIMING] n={_ball.CellIds.Length} res={_ball.Res} " +
					 $"land={Projector.LandFraction:P1} regions={Regions.Regions.Length} " +
					 $"ridges={Mountains.Ridges.Length} " +
					 $"thr={Projector.ThresholdUsed:F3} {sw.Elapsed.TotalMilliseconds:F0} ms");
		if (View != null)
		{
			// 重算换了海拔数组实例 ⇒ 视图海拔源重绑后重烘（几何/UV 不动；海拔 = 合成版）
			View.SetElevationSource(DisplayElevation);
			View.RefreshColors();
		}
	}

	static TectonicField TectonicOf(MountainSkeleton m) => m.Tectonic;

	/// <summary>每帧剔除 + LOD 刷新（宿主传当前相机）。</summary>
	public void UpdateVisibility(Camera3D camera) => View?.UpdateVisibility(camera);
}
