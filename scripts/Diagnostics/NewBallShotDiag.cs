using Godot;
using System.Linq;
using World.NewHexWorld;
using World.NewHexWorld.Planet;
using World.Services;
using World.Utils.H3;

namespace World.Diagnostics;

/// <summary>new_HexWorld 全域材质渲染截图（2026-09-09 材质覆盖方案视觉验证 + 描边误描回归）：
/// 窗口模式实例化 NewBall，截全景（海拔）→ 定位"≥5 边贴异板"病征格、相机拉近特写 → 截特写 →
/// 模拟点"板块"模式按钮 → 截板块特写 → 退出。
/// 用法（窗口模式，非 headless——headless 无渲染管线截图全黑）：
///   Godot --path E:/godotGames/world res://scenes/diag/NewBallShotDiag.tscn
/// 参数：--out=user://maps/newball_shot.png（基准路径，自动加 _closeup/_plate 后缀）。
/// 判读：特写里病征格唯一那条同板边【不得】有黑线（2026-09-09 用户报告的误描，边粒度记账修复）。</summary>
public partial class NewBallShotDiag : Node
{
	int _frame;
	string _outPath = "user://maps/newball_shot.png";
	const int BaseShotFrame = 10;    // 全景截图帧（首帧提交 + 渲染稳定）
	const int CloseShotFrame = 18;   // 病征格特写截图帧
	const int PlateShotFrame = 42;   // 板块模式特写截图帧

	public override void _Ready()
	{
		var args = DiagSceneBase.ParseUserArgs();
		if (args.TryGetValue("out", out var o)) _outPath = o;
		var packed = GD.Load<PackedScene>("res://scenes/core/NewBall.tscn");
		AddChild(packed.Instantiate());
		GD.Print("NewBallShotDiag: NewBall 已实例化（默认模式 = 海拔）");
	}

	public override void _Process(double delta)
	{
		_frame++;
		if (_frame == BaseShotFrame) Shot(_outPath, "海拔·全景");
		if (_frame == BaseShotFrame + 1) AimBugCell();
		if (_frame == CloseShotFrame) Shot(InsertSuffix(_outPath, "_closeup"), "海拔·病征格特写");
		if (_frame == CloseShotFrame + 1) AimSpecial(true);           // 五边格
		if (_frame == CloseShotFrame + 8) Shot(InsertSuffix(_outPath, "_pent"), "五边格特写");
		if (_frame == CloseShotFrame + 9) AimSpecial(false);          // 极区格
		if (_frame == CloseShotFrame + 16) Shot(InsertSuffix(_outPath, "_pole"), "极区特写");
		if (_frame == CloseShotFrame + 17)
		{
			// 模拟点第二块模式按钮（坞按钮序 = 注册序：0 海拔 / 1 板块）
			var row = FindChild("ModeRow", recursive: true, owned: false) as HBoxContainer;
			if (row != null && row.GetChildCount() > 1 && row.GetChild(1) is Button plate)
			{
				plate.EmitSignal(BaseButton.SignalName.Pressed);
				GD.Print($"NewBallShotDiag: 已模拟点模式按钮 {plate.Name}（{plate.Text}）");
			}
			else
			{
				GD.Print("NewBallShotDiag: ⚠️ 未找到板块模式按钮（ModeRow 结构变更？）");
			}
		}
		if (_frame == PlateShotFrame)
		{
			Shot(InsertSuffix(_outPath, "_plate"), "板块·特写");
			GD.Print("NewBallShotDiag: 截图完成，退出");
			GetTree().Quit(0);
		}
	}

	// 定位特殊格（五边格/极区格）→ 相机 LookAtPoint 指向 + 拉近（检查解析边距在这些格上的表现）。
	void AimSpecial(bool pentagon)
	{
		var ball = new Ball(3, 2f);
		var plates = new H3PlateManager();
		plates.Init(ball, 15, 42);
		var plateId = plates.Plate.Crust.PlateId;
		int best = -1;
		for (int i = 0; i < ball.CellIds.Length; i++)
		{
			if (pentagon)
			{
				// 找压在板块边界上的五边格（≥2 条邻边为异板边），其描边最考验解析边距
				if (H3.CellToVertexes(ball.CellIds[i]).Length != 5) continue;
				int boundaryEdges = ball.CellNeighbors[i].Count(j => plateId[j] != plateId[i]);
				if (boundaryEdges >= 2) { best = i; break; }
			}
			else if (best < 0 || ball.CellCenters[i].Y > ball.CellCenters[best].Y)
			{
				best = i;                                                                   // 极区格（|纬度|最大）
			}
		}
		if (best < 0) { GD.Print("NewBallShotDiag: ⚠️ 未找到目标格"); return; }
		GD.Print($"NewBallShotDiag: {(pentagon ? "边界五边格" : "极区格")} {H3.H3ToString(ball.CellIds[best])}");

		var cam = FindChild("OrbitalCamera", recursive: true, owned: false);
		cam?.GetType().GetMethod("LookAtPoint")?.Invoke(cam, new object[] { ball.CellCenters[best] });
		var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
		cam?.GetType().GetField("_targetDistance", flags)?.SetValue(cam, 2.3f);
		cam?.GetType().GetField("_distance", flags)?.SetValue(cam, 2.3f);
	}

	// 定位病征格（异板邻居 ≥5，退化取全星最大值）→ 相机 LookAtPoint 指向它 + 反射拉近（特写验证描边）。
	// 板块数据用同参数独立再生成（BallManager 导出默认：res3 / R=2 / P=15 / seed=42；静态生成确定性
	// → 与场景内星球同局）。
	void AimBugCell()
	{
		var ball = new Ball(3, 2f);
		var plates = new H3PlateManager();
		plates.Init(ball, 15, 42);
		var plateId = plates.Plate.Crust.PlateId;

		int best = -1, bestCount = 0;
		for (int i = 0; i < plateId.Length; i++)
		{
			int diff = ball.CellNeighbors[i].Count(j => plateId[j] != plateId[i]);
			if (diff > bestCount) { bestCount = diff; best = i; }
		}
		GD.Print($"NewBallShotDiag: 病征格 {H3.H3ToString(ball.CellIds[best])}——{bestCount}/6 边贴异板"
			+ (bestCount >= 5 ? "（≥5，即用户报告场景）" : "（全星最大值 <5）"));
		if (bestCount < 4)
		{
			GD.Print("NewBallShotDiag: ⚠️ 本局无 ≥4 边贴异板的格，特写无回归价值（换 seed 复跑）");
			return;
		}

		var cam = FindChild("OrbitalCamera", recursive: true, owned: false);
		cam?.GetType().GetMethod("LookAtPoint")?.Invoke(cam, new object[] { ball.CellCenters[best] });
		// 拉近：反射直设 _distance/_targetDistance（OrbitalCamera 无公开距离口；诊断用途）
		var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
		cam?.GetType().GetField("_targetDistance", flags)?.SetValue(cam, 2.3f);
		cam?.GetType().GetField("_distance", flags)?.SetValue(cam, 2.3f);
		GD.Print("NewBallShotDiag: 相机已指向病征格并拉近（距离 = 2.3，贴表面）");
	}

	// ⚠️ 勿用 System.IO.Path 拼接——"user://" 前缀会被当 Windows 路径解析（user:/ 单斜杠）。
	static string InsertSuffix(string path, string suffix)
	{
		int dot = path.LastIndexOf('.');
		return dot < 0 ? path + suffix : path.Insert(dot, suffix);
	}

	// 截图 → UserPaths 落盘（Godot SavePng 走 user:// 语义，统一转游戏目录旁真实路径）。
	// 特写另存屏幕中心 1200×900 裁片（病征格在正中，逐像素判读描边缺口用）。
	void Shot(string path, string label)
	{
		var img = GetViewport().GetTexture().GetImage();
		string resolved = UserPaths.Resolve(path).Replace('\\', '/');
		img.SavePng(resolved);
		GD.Print($"NewBallShotDiag: [{label}] 已截图 → {resolved} ({img.GetWidth()}x{img.GetHeight()})");
		if (label.Contains("特写"))
		{
			var center = new Rect2I((img.GetWidth() - 1200) / 2, (img.GetHeight() - 900) / 2, 1200, 900);
			string cropPath = InsertSuffix(path, "_crop");
			img.GetRegion(center).SavePng(UserPaths.Resolve(cropPath).Replace('\\', '/'));
			GD.Print($"NewBallShotDiag: [{label}] 中心裁片 → {UserPaths.Resolve(cropPath)}");
		}
	}
}
