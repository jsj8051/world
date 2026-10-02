using Godot;
using System.Diagnostics;
using World.NewHexWorld;        // Ball（H3 球壳数据层）

namespace World.NoiseWorld;

// 噪声星球 · 组件场景（噪声地形 P2）：数据层（Ball + NoiseTerrain）+ 视图层（NoiseBallView）
// 的自包含封装——**不含相机与调参面板**（宿主自配）。可拖进任意场景当前景球/背景球：
//   主场景 NoiseWorld = 本组件 + OrbitalCamera + NoiseParamPanel（组装层 NoiseWorldManager）。
// 组件旋钮走 [Export]（编辑器 inspector 即调参 UI）；运行期改噪声参数经 Terrain.Params
// 就地修改 + NotifyParamsChanged()，宿主防抖后调 Regenerate()。
public partial class NoisePlanet : Node3D
{
	[ExportGroup("星球")]
	[Export(PropertyHint.Enum, "res3 (41k 格)/res4 (288k 格)")]
	public int ResLevel = 4;                    // H3 分辨率档（默认 res4：288k 格，渲染预算 61MB 实测舒适）
	[Export] public float Radius = 2.0f;        // 球半径（与轨道相机 _planetRadius 同值时取景正确）
	[Export] public int NumPlates = 12;         // 板块数（纯分割显示用；板号 → HSV 色相，板块地图模式）

	[ExportGroup("LOD 与剔除（拍板：远低近高、不可见不画）")]
	[Export] public float LodNearRatio = 6f;         // 相机距 < 球半径×此值 ⇒ 高分辨率面
	[Export] public float BackfaceCullRatio = 3.0f;  // 保留接口；现版可见角限 = 90°+块角半径+0.6rad

	public NoiseTerrain Terrain { get; private set; }   // 逻辑层（权威海拔场；参数单一事实源在其 Params）
	public NoisePlates Plates { get; private set; }     // 板块划分（纯分割；种子 = Terrain.Params.Seed）
	public NoiseClimate Climate { get; private set; }   // 气候层（温度/降水/风/生物群系；依赖海拔+身份）
	public NoiseBallView View { get; private set; }     // 视图层（网格/LOD/剔除）

	Ball _ball;
	int _timingDiag;   // 重算耗时打印限次（防刷屏；参数变更本身是用户节奏）

	public override void _Ready()
	{
		// 数据层：H3 球壳（单一事实源：分辨率/半径/方向全由 Ball 出）
		_ball = new Ball(ResLevel, Radius);

		// 逻辑层：噪声栈（参数 = TerrainNoiseParams 默认值；重算统一走 Regenerate）
		Terrain = new NoiseTerrain();
		Plates = new NoisePlates();
		Climate = new NoiseClimate();
		Regenerate();

		// 视图层：单半径网格 + 分档色 + LOD + 背面剔除（材质 Unshaded ⇒ 组件零灯）
		View = new NoiseBallView(_ball, Terrain,
			lodNearRatio: LodNearRatio, backfaceCullRatio: BackfaceCullRatio);
		AddChild(View);
		View.BuildChunks();
	}

	/// <summary>全量重算 + 重烘颜色纹理（宿主防抖后调；段缓存自动跳过未变段）。</summary>
	public void Regenerate()
	{
		var sw = Stopwatch.StartNew();
		// 板块先行（地形依赖板块）：划分 + 陆洋身份 + 运动向（同 格数/板数/seed/陆板占比 幂等跳过）
		// → 地形按板块身份定海陆重算 → 气候（温度/降水/风/生物群系）随海拔重算
		Plates.Generate(_ball, NumPlates, Terrain.Params.Seed, Terrain.Params.LandFractionTarget);
		Terrain.Generate(_ball.CellDirs, Plates, _ball.CellNeighbors);
		Climate.Generate(_ball, Terrain, Terrain.Params.Seed);
		// 测量项①（噪声地形-01 §7）：全量重算耗时。限次打印防刷屏。
		if (_timingDiag++ < 3)
			GD.Print($"[NOISE-TIMING] n={_ball.CellIds.Length} res={_ball.Res} {sw.Elapsed.TotalMilliseconds:F1} ms");
		View?.RefreshColors();               // 首建时视图未建，跳过（BuildChunks 自带首烘）
	}

	/// <summary>每帧剔除 + LOD 刷新（宿主传当前相机；相机动了才重判，静态省帧）。</summary>
	public void UpdateVisibility(Camera3D camera) => View?.UpdateVisibility(camera);
}
