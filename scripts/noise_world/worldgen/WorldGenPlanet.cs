using Godot;
using System;
using System.Diagnostics;
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
	public HeightComposer Composer { get; private set; }    // 高度合成（阶段 6：唯一海拔出处）
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
		Regions = new GeologicalRegions(Seed);
		Regions.Generate(_ball, Projector, TargetRegionAreaKm2);
		Mountains = new MountainSkeleton(Seed);
		Mountains.Generate(_ball, Regions, Field, Projector.ThresholdUsed, Projector.SeaSpreadUsed);
		Landforms = new RegionalLandforms(Seed);
		Landforms.Generate(_ball, Regions);
		Composer = new HeightComposer(Seed);
		Composer.Generate(_ball, Projector, Regions, Mountains, Landforms);
		DisplayElevation = Composer.HeightM;   // 唯一海拔出处（决策 05 §六）

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

	/// <summary>每帧剔除 + LOD 刷新（宿主传当前相机）。</summary>
	public void UpdateVisibility(Camera3D camera) => View?.UpdateVisibility(camera);
}
