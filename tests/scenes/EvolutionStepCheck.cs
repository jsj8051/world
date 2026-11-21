using System.Globalization;
using System.Linq;
using Godot;
using World.Data;
using World.Logic;
using World.Render;
using World.Application.Generation;
using World.UI.Pages;

namespace World.Tests;

/// <summary>验证地理页面的 res4 雕刻、单轮发布和跳转。</summary>
public partial class EvolutionStepCheck : Node
{
	public override async void _Ready()
	{
		bool check = OS.GetCmdlineArgs().Contains("--evo-check")
			|| OS.GetCmdlineUserArgs().Contains("--evo-check");
		if (!check) return;
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		var root = (GeographyEvolutionView)GetTree().CurrentScene;
		var world = root.World;
		if (world?.SculptingRoundIndex != WorldGenerationPresets.Production.InitialSculptingRounds
			|| world.Sim.Ball.Res != WorldGenerationPresets.ProductionRes
			|| world.Sim.Facts.BiomeByCell.Count != world.Sim.Ball.CellIds.Length)
		{
			GD.PushError("[EVO-CHECK] 主世界未使用同网格的 res4 演化");
			GetTree().Quit(1);
			return;
		}
		var initial = world.Sim.Facts;
		if (initial.SourceTimeMa.HasValue || !initial.BiomeByCell.Any(type => type.HasValue))
		{
			GD.PushError("[EVO-CHECK] 初始世界未发布完整的雕刻快照");
			GetTree().Quit(1);
			return;
		}
		var viewer = root.GetNode(root.MapViewerPath);
		var dock = viewer.GetNode("Panels/MapDock");
		var modeRow = dock.GetNode("DockBox/ModeRow");
		var crustButton = modeRow.GetChildren().OfType<Button>().FirstOrDefault(button => button.Text == "地壳");
		var motion = viewer.GetNodeOrNull<PlateMotionOverlay>("Presentation/WorldView/PlateMotion");
		var current = root.FindChild("CurrentFlow", recursive: true, owned: false) as CurrentFlowOverlay;
		if (crustButton?.Text != "地壳" || motion == null || motion.Visible
			|| current == null || current.Visible || current.Mesh != null
			|| modeRow.GetChildren().OfType<Button>().Any(button => button.Text == "板块"))
		{
			GD.PushError("[EVO-CHECK] 地壳模式或地图坞按钮不正确");
			GetTree().Quit(1);
			return;
		}
		crustButton.EmitSignal(BaseButton.SignalName.Pressed);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		var crustOptions = dock.FindChildren("*", "Button", recursive: true, owned: false).OfType<Button>().ToArray();
		var crustOnly = crustOptions.FirstOrDefault(button => button.Text == "仅壳型");
		var crustWithMotion = crustOptions.FirstOrDefault(button => button.Text == "壳型+运动");
		if (!motion.IsVisibleInTree() || motion.ArrowCount == 0 || crustOnly == null || crustWithMotion == null)
		{
			GD.PushError("[EVO-CHECK] 地壳模式未生成运动箭头或缺少显示选项");
			GetTree().Quit(1);
			return;
		}
		crustOnly.EmitSignal(BaseButton.SignalName.Pressed);
		if (motion.Visible)
		{
			GD.PushError("[EVO-CHECK] 仅壳型选项未隐藏运动箭头");
			GetTree().Quit(1);
			return;
		}
		crustWithMotion.EmitSignal(BaseButton.SignalName.Pressed);
		var card = viewer.GetNode<Control>("Panels/CellInfoCard");
		var cardSummary = card.FindChild("CellSummary", recursive: true, owned: false) as Label;
		var crustSummary = card.FindChild("SelectionDetails", recursive: true, owned: false) as Label;
		var boundaryList = card.FindChild("ExpandedDetails", recursive: true, owned: false) as Label;
		var boundaryToggle = card.FindChild("ToggleDetails", recursive: true, owned: false) as Button;
		int mixedCell = CheckCrustQueries(initial);
		if (mixedCell < 0 || cardSummary == null || crustSummary == null || boundaryList == null || boundaryToggle == null)
		{
			GD.PushError("[EVO-CHECK] 地壳查询缺少验证样例或信息卡控件");
			GetTree().Quit(1);
			return;
		}
		viewer.GetNode<OrbitalCamera>("Presentation/OrbitalCamera").LookAtPoint(initial.Ball.CellDirs[mixedCell]);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Vector2 mapPoint = GetViewport().GetCamera3D().UnprojectPosition(
			initial.Ball.CellDirs[mixedCell] * initial.Ball.Radius);
		Click(mapPoint);
		ulong selected = initial.Ball.CellIds[mixedCell];
		if (!card.IsVisibleInTree() || !crustSummary.Visible || !boundaryToggle.Visible
			|| !CardMatches(cardSummary, crustSummary, initial, mixedCell, selected))
		{
			GD.PushError("[EVO-CHECK] 点击混合边界没有显示正确的地壳读数");
			GetTree().Quit(1);
			return;
		}
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Click(boundaryToggle);
		if (!boundaryList.IsVisibleInTree() || boundaryList.Text.Split('\n').Length < 2)
		{
			GD.PushError("[EVO-CHECK] 多边界格展开后未列出相邻板块边界");
			GetTree().Quit(1);
			return;
		}

		float before = initial.HeightM[0];
		long expected = initial.SculptingRoundIndex.Value + 1;
		var panel = root.EvolutionReadout;
		var buttons = panel?.FindChildren("*", "Button", recursive: true, owned: false).OfType<Button>().ToArray();
		var play = buttons?.FirstOrDefault(button => button.Text == "播放");
		var pause = buttons?.FirstOrDefault(button => button.Text == "暂停");
		var step = buttons?.FirstOrDefault(button => button.Text == "单轮");
		var jump = buttons?.FirstOrDefault(button => button.Text == "跳转");
		var seek = panel?.FindChild("SeekTime", recursive: true, owned: false) as LineEdit;
		var status = panel?.FindChild("Status", recursive: true, owned: false) as Label;
		var feedback = panel?.FindChild("Feedback", recursive: true, owned: false) as Label;
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		if (play == null || pause == null || step == null || jump == null || seek == null
			|| status == null || feedback == null || !step.IsVisibleInTree())
		{
			GD.PushError("[EVO-CHECK] 找不到演化控件");
			GetTree().Quit(1);
			return;
		}
		if (!Readable(status, panel, "panel") || !Readable(step, step, "normal"))
		{
			GD.PushError("[EVO-CHECK] 面板或按钮的文字与底色对比不足");
			GetTree().Quit(1);
			return;
		}
		Click(play);
		bool playing = world.EvolutionStatus == EvolutionControlStatus.Playing && status.Text == "播放中" && !pause.Disabled;
		Click(pause);
		if (!playing || world.EvolutionStatus != EvolutionControlStatus.Paused || status.Text != "暂停")
		{
			GD.PushError("[EVO-CHECK] 播放或暂停未立即更新面板");
			GetTree().Quit(1);
			return;
		}
		seek.Text = world.SculptingRoundIndex.Value.ToString(CultureInfo.InvariantCulture);
		Click(jump);
		if (!feedback.Visible || !feedback.Text.Contains("当前轮") || world.EvolutionBusy)
		{
			GD.PushError("[EVO-CHECK] 跳转到当前步没有可见提示");
			GetTree().Quit(1);
			return;
		}
		var stepTime = System.Diagnostics.Stopwatch.StartNew();
		Click(step);
		if (!world.EvolutionBusy || !step.Disabled || !status.Text.StartsWith("正在计算"))
		{
			GD.PushError($"[EVO-CHECK] 点击单步后没有开始计算，按钮区域={step.GetGlobalRect()}，" +
				$"鼠标所在控件={GetViewport().GuiGetHoveredControl()?.GetPath()}");
			GetTree().Quit(1);
			return;
		}
		var timer = System.Diagnostics.Stopwatch.StartNew();
		while (timer.Elapsed.TotalSeconds < 90 && world.EvolutionBusy)
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		float after = world.Sim.Facts.HeightM[0];
		if (world.EvolutionBusy || world.SculptingRoundIndex != expected || panel?.Visible != true
			|| step.Disabled || status.Text != "暂停"
			|| ReferenceEquals(world.Sim.Facts, initial)
			|| !motion.IsVisibleInTree() || motion.ArrowCount == 0
			|| !CardMatches(cardSummary, crustSummary, world.Sim.Facts, mixedCell, selected)
			|| world.Sim.Facts.HeightM.SequenceEqual(initial.HeightM))
		{
			GD.PushError("[EVO-CHECK] 未能提交演化步、重建世界快照或显示面板");
			GetTree().Quit(1);
			return;
		}
		stepTime.Stop();
		GD.Print($"[EVO-CHECK] res={world.Sim.Ball.Res} cells={world.Sim.Ball.CellIds.Length} " +
			$"round={world.SculptingRoundIndex} arrows={motion.ArrowCount} " +
			$"height[0]={before:F2}->{after:F2} round-to-view-ms={stepTime.ElapsedMilliseconds}");

		long target = expected + 2;
		seek.Text = target.ToString(CultureInfo.InvariantCulture);
		string oldCard = crustSummary.Text;
		var jumpTime = System.Diagnostics.Stopwatch.StartNew();
		Click(jump);
		if (!world.EvolutionBusy || crustSummary.Text != oldCard)
		{
			GD.PushError("[EVO-CHECK] 跳转开始时信息卡应保留已提交年代");
			GetTree().Quit(1);
			return;
		}
		while (jumpTime.Elapsed.TotalSeconds < 190 && world.EvolutionBusy)
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		if (world.EvolutionBusy || world.SculptingRoundIndex != target
			|| !CardMatches(cardSummary, crustSummary, world.Sim.Facts, mixedCell, selected))
		{
			GD.PushError("[EVO-CHECK] 跳转后信息卡未刷新同一位置");
			GetTree().Quit(1);
			return;
		}
		GD.Print($"[EVO-CHECK] selected={selected:X} round={target} jump-to-view-ms={jumpTime.ElapsedMilliseconds}");
		if (System.Environment.GetEnvironmentVariable("WORLD_BUILD_PROFILE") == "1")
		{
			using var process = System.Diagnostics.Process.GetCurrentProcess();
			GD.Print($"[WORLD-PROFILE] peak-working-set={process.PeakWorkingSet64 / 1048576d:F1} MiB");
		}
		if (current.Mesh != null || current.Visible)
		{
			GD.PushError("[EVO-CHECK] 地壳模式推进时不应构建洋流粒子网格");
			GetTree().Quit(1);
			return;
		}
		var oceanCurrent = modeRow.GetChildren().OfType<Button>().FirstOrDefault(button => button.Text == "洋流");
		oceanCurrent.EmitSignal(BaseButton.SignalName.Pressed);
		Mesh flowMesh = current.Mesh;
		crustButton.EmitSignal(BaseButton.SignalName.Pressed);
		oceanCurrent.EmitSignal(BaseButton.SignalName.Pressed);
		if (!current.Visible || flowMesh == null || current.Mesh != flowMesh)
		{
			GD.PushError("[EVO-CHECK] 首次切换洋流应按需生成网格，同帧再次切换应复用");
			GetTree().Quit(1);
			return;
		}
		GetTree().Quit(0);
	}

	static int CheckCrustQueries(WorldFacts facts)
	{
		var boundaries = facts.PlateBoundaries;
		for (int cell = 0; cell < facts.HeightM.Length; cell++)
		{
			var adjacent = boundaries.EdgesOfCell(cell);
			if (adjacent.Length < 2) continue;
			var query = CrustCellQuery.Read(facts, cell);
			return query.Boundaries.Length == adjacent.Length && !query.TimeMa.HasValue ? cell : -1;
		}
		return -1;
	}

	static bool CardMatches(Label card, Label crust, WorldFacts facts, int cell, ulong id)
	{
		return card.Text.Contains(id.ToString("X")) && card.Text.Contains($"{facts.HeightM[cell]:+0;-0;0} m")
			&& crust.Text.Contains($"表层板块 {facts.PlateOfCell[cell]}");
	}

	static bool Readable(Control text, Control backdrop, string style)
	{
		if (backdrop.GetThemeStylebox(style) is not StyleBoxFlat box || box.BgColor.A < 1) return false;
		float ink = text.GetThemeColor("font_color").SrgbToLinear().Luminance;
		float background = box.BgColor.SrgbToLinear().Luminance;
		return (Mathf.Max(ink, background) + 0.05f) / (Mathf.Min(ink, background) + 0.05f) >= 4.5f;
	}

	void Click(Control control)
	{
		Vector2 position = control.GetGlobalTransformWithCanvas() * (control.Size / 2);
		Click(position);
	}

	void Click(Vector2 position)
	{
		Viewport viewport = GetViewport();
		viewport.PushInput(new InputEventMouseMotion { Position = position, GlobalPosition = position }, true);
		viewport.PushInput(new InputEventMouseButton
		{
			ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left,
			Position = position, GlobalPosition = position,
		}, true);
		viewport.PushInput(new InputEventMouseButton
		{
			ButtonIndex = MouseButton.Left, Pressed = false, Position = position, GlobalPosition = position,
		}, true);
	}
}
