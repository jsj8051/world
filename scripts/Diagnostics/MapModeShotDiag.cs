using Godot;
using System;
using System.Linq;
using World.Services;        // UserPaths（写盘统一经此）
using World.WorldGen;        // WorldGenPlanet / WorldGenMapModes

namespace World.Diagnostics;

// 地图模式实拍诊断（2026-10-08）：切到指定地图模式 → 相机摆到赤道正视位 → 截一张干净的地图。
//
// ★窗口模式专用：`--headless` 的 dummy 渲染服务器 `GetViewport().GetTexture()` 取不到帧
//   （见 docs/设计-世界生成空间-07 §"headless 取不到帧"）。本诊断必须**不加 --headless**。
// ★只读：不改任何生产事实，只调**显示模式**（等价于用户点坞按钮）——数据/公式零改动。
//
// 用法：
//   Godot --path . res://scenes/diag/MapModeShotDiag.tscn -- --mode=温度 --out=res://runs/temp_map.png
//   Godot --path . res://scenes/diag/MapModeShotDiag.tscn -- --mode=降水 --phi=1.1 --dist=2.6
public partial class MapModeShotDiag : Node
{
	const float R = 2.0f;        // worldgen 单位球尺度（OrbitalCamera 同款 _planetRadius=2）
	const int Warmup = 24;       // 等世界生成 + CubeChunks + 颜色纹理初烘稳定
	const int Settle = 8;        // 切模式后再等几帧让颜色纹理烘完再截

	int _frame;
	bool _done;
	Node3D _world;
	string _modeName = "温度";
	string _out = "res://runs/map_mode_shot.png";
	float _phi = MathF.PI / 2f;  // 相机极角：π/2 = 赤道正视（可见极→极完整纬度带）
	float _distR = 2.9f;         // 相机距离（单位 = 球半径倍数）
	float _theta = 0.6f;

	public override void _Ready()
	{
		var args = DiagSceneBase.ParseUserArgs();
		if (args.TryGetValue("mode", out var m) && !string.IsNullOrEmpty(m)) _modeName = m;
		if (args.TryGetValue("out", out var o) && !string.IsNullOrEmpty(o)) _out = o;
		if (args.TryGetValue("phi", out var ph) && float.TryParse(ph, out float pv)) _phi = pv;
		if (args.TryGetValue("dist", out var d) && float.TryParse(d, out float dv)) _distR = dv;
		if (args.TryGetValue("theta", out var th) && float.TryParse(th, out float tv)) _theta = tv;

		var packed = GD.Load<PackedScene>("res://scenes/core/WorldGenWorld.tscn");
		_world = (Node3D)packed.Instantiate();
		AddChild(_world);
	}

	public override void _Process(double delta)
	{
		if (_done) return;
		_frame++;
		if (_frame < Warmup) return;
		if (_frame == Warmup) { Apply(); return; }
		if (_frame >= Warmup + Settle) Snap();
	}

	void Apply()
	{
		var planet = _world.GetNode<WorldGenPlanet>("WorldGenPlanet");
		var modes = WorldGenMapModes.CreateAll(planet);
		var mode = modes.FirstOrDefault(mm => mm.Name == _modeName);
		if (mode == null)
		{
			GD.PrintErr($"[MAPSHOT] 无此模式 '{_modeName}'；可用 = [{string.Join(",", modes.Select(x => x.Name))}]");
			mode = modes[0];
		}
		planet.View.SetMode(mode);
		planet.View.RefreshColors();
		GD.Print($"[MAPSHOT] 模式={mode.Name}（Id={mode.Id}） 图例={mode.ScaleCaption}");

		// 清图：藏 GUI（坞/信息卡），只留球面
		if (_world.GetNodeOrNull("PanelLayer") is CanvasLayer ui) ui.Visible = false;

		// 相机摆位（与 RiverShotDiag 同款：直写 OrbitalCamera 私有字段 ⇒ 无插值，两帧对齐）
		if (_world.GetNodeOrNull("OrbitalCamera") is Node orb)
		{
			orb.Set("_targetDistance", R * _distR);
			orb.Set("_distance", R * _distR);
			orb.Set("_theta", _theta);
			orb.Set("_phi", _phi);
		}
	}

	void Snap()
	{
		_done = true;
		var img = GetViewport().GetTexture().GetImage();
		string full = Absolute(_out);
		System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
		img.SavePng(full);
		GD.Print($"[MAPSHOT] → {full} ({img.GetWidth()}×{img.GetHeight()})");
		GetTree().Quit();
	}

	/// <summary>res:// → 实体绝对路径；其余（含 user://）交给 UserPaths；相对路径按项目根解析。</summary>
	static string Absolute(string p)
	{
		if (p.StartsWith("res://", StringComparison.Ordinal)) return ProjectSettings.GlobalizePath(p);
		string r = UserPaths.Resolve(p);
		return System.IO.Path.IsPathRooted(r) ? r : System.IO.Path.GetFullPath(r);
	}
}
