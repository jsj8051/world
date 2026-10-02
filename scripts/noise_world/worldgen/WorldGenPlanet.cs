using Godot;
using System.Diagnostics;
using World.NewHexWorld;        // Ball（H3 球壳数据层）

namespace World.NoiseWorld.WorldGen;

// 世界生成空间 · 星球组件（阶段 1+2 落地场景件）：数据层（Ball + 大陆布局 + 海陆场 + H3 投影）
// + 视图层（复用 NoiseBallView——海拔源直供构造）。自包含、不含相机（宿主自配）。
// 海陆结构五件套（Land/ContinentId/DistToCoast/DistToLand/ElevationM）由投影层一次产出；
// 可见海拔 = raw 相对阈值映射（仅上色——阶段 1/2 不生成真实高度，决策 02 §0）。
public partial class WorldGenPlanet : Node3D
{
	[ExportGroup("星球")]
	[Export(PropertyHint.Enum, "res3 (41k 格)/res4 (288k 格)")]
	public int ResLevel = 4;                    // H3 分辨率档（res4 = 渲染预算实算舒适档）
	[Export] public float Radius = 2.0f;        // 球半径（与轨道相机 _planetRadius 同值时取景正确）
	[Export] public int ContinentCount = 7;     // 大陆锚点数（蓝噪声撒布）
	[Export(PropertyHint.Range, "0.02,0.9,0.01")]
	public float LandFraction = 0.29f;          // 目标陆地占比（分位校准钉死）
	[Export] public int Seed = 42;

	[ExportGroup("LOD 与剔除")]
	[Export] public float LodNearRatio = 6f;         // 相机距 < 球半径×此值 ⇒ 高分辨率面
	[Export] public float BackfaceCullRatio = 3.0f;

	public ContinentLayout Layout { get; private set; }     // 大陆锚点（蓝噪声 + 属性）
	public LandSeaField Field { get; private set; }         // 连续海陆场（域扭曲 + 三尺度）
	public H3LandSeaProjector Projector { get; private set; }   // H3 投影五件套
	public NoiseBallView View { get; private set; }         // 视图（复用现役渲染：LOD/剔除/拾取）

	Ball _ball;
	int _timingDiag;   // 生成耗时打印限次

	public override void _Ready()
	{
		_ball = new Ball(ResLevel, Radius);
		Regenerate();
		View = new NoiseBallView(_ball, Projector.ElevationM,
			lodNearRatio: LodNearRatio, backfaceCullRatio: BackfaceCullRatio);
		AddChild(View);
		View.BuildChunks();
	}

	/// <summary>全量重算（锚点 → 场 → 投影）+ 重烘颜色纹理（宿主防抖后调）。</summary>
	public void Regenerate()
	{
		var sw = Stopwatch.StartNew();
		Layout = new ContinentLayout(Seed, ContinentCount);
		Field = new LandSeaField(Layout, new LandSeaParams { Seed = Seed });
		Projector = new H3LandSeaProjector();
		Projector.Generate(_ball, Field, LandFraction);
		if (_timingDiag++ < 3)
			GD.Print($"[WORLDGEN-TIMING] n={_ball.CellIds.Length} res={_ball.Res} " +
					 $"land={Projector.LandFraction:P1} thr={Projector.ThresholdUsed:F3} {sw.Elapsed.TotalMilliseconds:F0} ms");
		if (View != null)
		{
			// 重算换了海拔数组实例 ⇒ 视图海拔源重绑后重烘（几何/UV 不动）
			View.SetElevationSource(Projector.ElevationM);
			View.RefreshColors();
		}
	}

	/// <summary>每帧剔除 + LOD 刷新（宿主传当前相机）。</summary>
	public void UpdateVisibility(Camera3D camera) => View?.UpdateVisibility(camera);
}
