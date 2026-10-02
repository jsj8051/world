using Godot;
using World.Services;        // UserPaths（写盘不落 C 盘纪律：统一经 UserPaths）
using World.NoiseWorld;      // NoisePlanet / NoiseCellPanel（格子 UI 验证）
using World.Utils;           // CoordUtil
using World.Utils.H3;

namespace World.Diagnostics;

// 噪声地形 P0 · 截图验证（对齐 NewBallShotDiag 惯例：窗口模式、指定帧截图、自动退出）：
//   帧型 = 实例化 NoiseWorld → 第 11 帧合成点选（正对相机的球面点 → 拾取/高亮/信息卡）→
//   第 12 帧全景（应含格子高亮 + 信息卡）→ 相机拉近 → 第 24 帧近景 → 相机拉到球背面 →
//   第 36 帧背面（背剔应生效）→ 退出。
//   用法：Godot --path . res://scenes/diag/NoiseShotDiag.tscn --out=<png 基路径>
//   判读：① 全景 = 分档噪声星球 + 选中格白片高亮 + 左上信息卡；② 近景无裂缝；③ 背面剔除生效。
public partial class NoiseShotDiag : Node
{
	int _frame;
	string _out = UserPaths.Resolve("maps/noise_p0.png");
	Node3D _world;          // NoiseWorldRoot
	Camera3D _cam;
	Node _orbital;          // OrbitalCamera 节点（Set 私有字段用）
	bool _camFound;
	ulong? _pickedCell;     // 合成点选命中的格
	Vector3 _pickedDir;     // 该格中心方向（特写镜头对准用）

	const int WideFrame = 12;
	const int NearFrame = 30;
	const int BackFrame = 36;
	const int ExitFrame = 40;
	const int PickFrame = 11;

	public override void _Ready()
	{
		var args = DiagSceneBase.ParseUserArgs();
		if (args.TryGetValue("out", out var o)) _out = o;
		// （灯光注入已退役：2026-09-29 拍板单半径 + Unshaded 平光 ⇒ 场景零灯，无方位可注入）
		var packed = GD.Load<PackedScene>("res://scenes/core/NoiseWorld.tscn");
		_world = (Node3D)packed.Instantiate();
		AddChild(_world);
		// 相机查找放 _Process 头几帧（TryFindCamera）：NoiseWorldManager._Ready 同步跑完后 OrbitalCamera
		// 才存在，其内层 Camera3D 又是 OrbitalCamera._Ready 建的——这里不抢时序，_Process 里兜。
		GD.Print("NoiseShotDiag: NoiseWorld 已实例化");
		PrintTreeLights(_world);
	}

	public override void _Process(double delta)
	{
		_frame++;
		if (!_camFound && _frame <= 5)   // 相机是 NoiseWorldManager._Ready 里 AddChild 的孙节点，
		{                                 // 其 _Ready 在我们 AddChild(_world) 时已同步跑完 ⇒ 直接找
			_camFound = TryFindCamera();
			if (!_camFound) return;       // 头几帧相机未就绪则跳过计时
		}
		if (_frame == PickFrame && _cam != null) SyntheticPick();
		if (_frame == WideFrame && _cam != null) { GD.Print($"[CAM-DIAG] pos={_cam.GlobalPosition} dist={_cam.GlobalPosition.Length():F2}"); Shot(_out, "全景（默认视距）"); }
		if (_frame == WideFrame + 1 && _pickedCell.HasValue)
			MoveCameraTo(_pickedDir, 1.15f);   // 对准被选格特写（贴合判读）
		else if (_frame == WideFrame + 1)
			MoveCamera(1.6f);
		if (_frame == NearFrame) Shot(InsertSuffix(_out, "_near"), "点选特写 1.15R（高亮贴合判读）");
		if (_frame == NearFrame + 1) MoveCamera(-2.8f, behind: true);   // 背面：背剔应生效
		if (_frame == BackFrame) Shot(InsertSuffix(_out, "_back"), "背面（剔除生效则近乎全黑）");
		if (_frame >= ExitFrame) GetTree().Quit();
	}

	// 相机查找：NoiseWorldRoot → OrbitalCamera（Node3D+脚本）→ 其 Camera3D 子节点（OrbitalCamera
	// 自己的 _Ready 里 new + AddChild，无显式名）。

	// 相机对准指定方向特写：phi/theta 换算按 OrbitalCamera 口径（pos = dist·(sinφcosθ, cosφ, sinφsinθ)
	// ⇒ dir.Y = cosφ、θ = atan2(z,x)，φ 即纬度弧度）。
	void MoveCameraTo(Vector3 dir, float distR)
	{
		if (_cam == null || _orbital == null) return;
		float r = 2.0f;   // 与 NoiseWorld.tscn 星球 Radius / 相机 _planetRadius 同值
		_orbital.Set("_targetDistance", r * distR);
		_orbital.Set("_theta", System.Math.Atan2(dir.Z, dir.X));
		_orbital.Set("_phi", System.Math.Acos(Mathf.Clamp(dir.Y, -1f, 1f)));
	}

	// 合成点选：正对相机的球面点 → 屏幕 → PickCell → 高亮 + 信息卡（走与鼠标点选同一条
	// 视图 API 链；输入手势判定在 Manager，不在本验证范围）。
	void SyntheticPick()
	{
		var planet = _world.GetNode<NoisePlanet>("NoisePlanet");
		Vector3 surf = _cam.GlobalPosition.Normalized() * planet.Radius;
		Vector2 sp = _cam.UnprojectPosition(surf);
		ulong? cell = planet.View.PickCell(sp, _cam);
		GD.Print($"[PICK-DIAG] screen=({sp.X:F0},{sp.Y:F0}) cell={(cell.HasValue ? cell.Value.ToString("X") : "null")}");
		planet.View.HighlightCell(cell);
		if (cell.HasValue)
		{
			_pickedCell = cell;
			var ll = H3.CellToLatLng(cell.Value);
			_pickedDir = CoordUtil.LatLngToSphere(ll, 1f);
			planet.View.TryGetElevation(cell.Value, out float elev);
			_world.GetNode<NoiseCellPanel>("PanelLayer/NoiseCellPanel")
				.ShowCell(cell.Value, (float)(ll.Lat * 180.0 / System.Math.PI), (float)(ll.Lng * 180.0 / System.Math.PI), elev);
		}
	}

	static void PrintTreeLights(Node root)
	{
		DumpLights(root, 0);
	}
	static void DumpLights(Node n, int depth)
	{
		if (n is DirectionalLight3D dl)
			GD.Print($"[LIGHT-DIAG] {n.GetPath()} energy={dl.LightEnergy} rotDeg={dl.RotationDegrees} globalRot={dl.GlobalRotationDegrees}");
		foreach (var c in n.GetChildren()) DumpLights(c, depth + 1);
	}

	bool TryFindCamera()
	{
		var orbital = _world.GetNodeOrNull("OrbitalCamera");
		if (orbital == null) return false;
		foreach (var child in orbital.GetChildren())
			if (child is Camera3D c) { _cam = c; _orbital = orbital; return true; }
		return false;
	}

	// 相机控制：★不能挪 Camera3D 节点（OrbitalCamera._Process 每帧 UpdatePosition 覆写其 Position
	// ——首跑三张图 md5 相同就是因此）。改设它的球坐标私有字段（Node.Set 可达 private C# 成员），
	// 让轨道相机自己"转过去"：theta=水平角、phi=极角、_targetDistance=距离。
	void MoveCamera(float distR, bool behind = false)
	{
		if (_cam == null || _orbital == null) return;
		float r = 2.0f;   // 与 NoiseWorldManager.Radius 同值（P0 常量；面板改半径时此处同步）
		_orbital.Set("_targetDistance", r * distR);
		if (behind) { _orbital.Set("_theta", 0.8 + Mathf.Pi); _orbital.Set("_phi", Mathf.Pi / 2); }   // 转到背面
		else { _orbital.Set("_theta", 0.8); _orbital.Set("_phi", 1.2); }                             // 回正面低角度
	}

	void Shot(string path, string tag)
	{
		var img = GetViewport().GetTexture().GetImage();
		string full = UserPaths.Resolve(path);
		System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
		img.SavePng(full);
		GD.Print($"NoiseShotDiag [{tag}] → {full} ({imgGetWidth(img)}×{imgGetHeight(img)})");
	}
	static int imgGetWidth(Image i) => i.GetWidth();
	static int imgGetHeight(Image i) => i.GetHeight();

	static string InsertSuffix(string path, string suffix)
	{
		int dot = path.LastIndexOf('.');
		return dot < 0 ? path + suffix : path[..dot] + suffix + path[dot..];
	}
}
