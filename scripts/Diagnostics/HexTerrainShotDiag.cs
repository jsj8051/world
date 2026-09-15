using System;
using System.Linq;
using Godot;
using World.NewHexWorld;
using World.NewHexWorld.Planet;
using World.NewHexWorld.Plate;
using World.Services;
using World.Utils.H3;

namespace World.Diagnostics;

/// <summary>new_HexWorld 地形截图（可指定分辨率）：实例化 NewBall 场景并在入树前覆盖 ResLevel，
/// 截「海拔·全景」+「海拔·造山带特写」两张，用于判读地形形态。
/// 用法（窗口模式，非 headless）：
///   Godot --path E:/godotGames/world res://scenes/diag/HexTerrainShotDiag.tscn -- --res=4 --out=user://maps/terrain.png
/// 说明：地形带宽度是地球尺度（造山半宽 600 km），res3（格宽 111 km）只能分到 2–6 格、本身就呈阶梯；
/// res4（42 km）才有 14 格/半宽，形态判读应在 res4 起（res5 的网格规模超出渲染承受范围，只做数值判读）。</summary>
public partial class HexTerrainShotDiag : Node
{
	int _res = 4;
	string _outPath = "user://maps/terrain_shot.png";
	int _layer;                      // 图层档：0 = 海拔（默认），1 = 板块（用户 2026-09-11 看的就是这层）
	int _frame;
	Vector3 _aimDir;
	bool _hasAim;

	public override void _Ready()
	{
		var args = DiagSceneBase.ParseUserArgs();
		if (args.TryGetValue("res", out var res)) _res = int.Parse(res);
		if (args.TryGetValue("layer", out var layer)) _layer = int.Parse(layer);
		// 命令行档位与编辑器同一取值域（入口 §4 三档）：越界直接判失败退出——
		// 不静默跑 res6（14.1M 格，几分钟 + 内存爆炸）的档。
		if (!Enum.IsDefined(typeof(HexResLevel), _res))
		{
			GD.PrintErr($"HexTerrainShotDiag: --res={_res} 不在设计档位内（可用 " +
				string.Join(" / ", Enum.GetValues<HexResLevel>().Select(v => (int)v)) + "）");
			GetTree().Quit(1);
			return;
		}
		if (args.TryGetValue("out", out var o)) _outPath = o;

		// 造山带瞄准点：同参数独立再生成（确定性 → 与场景内星球同局），取"离某条陆-陆汇聚边最近"的格心方向。
		var probe = new Ball(_res, 1f);
		var plates = new H3PlateManager();
		plates.Init(probe, 15, 42);
		// 特写瞄准点 = 最高海拔格（03 动态路线下"造山带"不再是参数化带，改用实际地形极值）
		float[] elevation = plates.Plate.Crust.Elevation;
		int best = 0;
		for (int i = 1; i < elevation.Length; i++)
			if (elevation[i] > elevation[best]) best = i;
		_aimDir = probe.CellCenters[best];
		_hasAim = true;
		GD.Print($"HexTerrainShotDiag: 最高海拔瞄准格 {best}（{elevation[best]:F0} m，res={_res}）");

		var packed = GD.Load<PackedScene>("res://scenes/core/NewBall.tscn");
		var manager = packed.Instantiate<BallManager>();
		manager.ResLevel = (HexResLevel)_res;   // 必须在下行 AddChild（_Ready 之前）覆盖导出属性
		AddChild(manager);
		GD.Print($"HexTerrainShotDiag: NewBall 已实例化（ResLevel={_res}）");

		// 图层切换：走坞按钮同一条路（发 ModeSelected 信号 → 坞 VM → 球视图换显示，不绕过 MVVM）。
		// 用途：判读"板块归属场有没有毛刺"必须看**板块图层**（渲染按 PlateId 逐格上色）。
		if (_layer != 0)
		{
			var dock = FindChild("HexDock", recursive: true, owned: false);
			dock?.EmitSignal("ModeSelected", _layer);
			GD.Print($"HexTerrainShotDiag: 已切换图层 → {_layer}（1 = 板块）");
		}
	}

	public override void _Process(double delta)
	{
		_frame++;
		// ⚠️ res4 下 NewBall 网格构建慢于 12 帧（全景帧实测截到空场），全景挪到第 30 帧
		if (_frame == 30) Shot(_outPath, _layer == 0 ? "海拔·全景" : "板块·全景");
		if (_frame == 31 && _hasAim) AimOrogen();
		if (_frame == 40) Shot(InsertSuffix(_outPath, "_orogen"), _layer == 0 ? "海拔·造山带特写" : "板块·极值格特写");
		if (_frame == 41)
		{
			GD.Print("HexTerrainShotDiag: 截图完成，退出");
			GetTree().Quit(0);
		}
	}

	// 相机对准造山带并拉近（OrbitalCamera 无公开距离口 → 反射直设；诊断用途）。
	void AimOrogen()
	{
		var cam = FindChild("OrbitalCamera", recursive: true, owned: false);
		cam?.GetType().GetMethod("LookAtPoint")?.Invoke(cam, new object[] { _aimDir });
		var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
		cam?.GetType().GetField("_targetDistance", flags)?.SetValue(cam, 2.25f);
		cam?.GetType().GetField("_distance", flags)?.SetValue(cam, 2.25f);
	}

	static string InsertSuffix(string path, string suffix)
	{
		int dot = path.LastIndexOf('.');
		return dot < 0 ? path + suffix : path.Insert(dot, suffix);
	}

	void Shot(string path, string label)
	{
		var img = GetViewport().GetTexture().GetImage();
		string resolved = UserPaths.Resolve(path).Replace('\\', '/');
		img.SavePng(resolved);
		GD.Print($"HexTerrainShotDiag: [{label}] 已截图 → {resolved} ({img.GetWidth()}x{img.GetHeight()})");
	}
}
