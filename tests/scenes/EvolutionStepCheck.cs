using System.Globalization;
using System.Linq;
using Godot;
using World.Data;
using World.Logic;
using World.Render;
using World.Scene;
using World.UI.Pages;

namespace World.Tests;

/// <summary>验证地理演化页的真实 res4 演化；带 --evo-check 时单步一次并退出。</summary>
public partial class EvolutionStepCheck : Node
{
	public override async void _Ready()
	{
		bool check = OS.GetCmdlineArgs().Contains("--evo-check")
			|| OS.GetCmdlineUserArgs().Contains("--evo-check");
		if (!check) return;
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		var world = GetTree().CurrentScene?.GetNodeOrNull<WorldManager>("WorldManager");
		if (world?.EvolutionFrame == null
			|| world.Sim.Ball.Res != WorldManager.ProductionRes
			|| world.EvolutionFrame.Definition.H3Resolution != world.Sim.Ball.Res
			|| world.EvolutionFrame.Material.CellCount != world.Sim.Facts.HeightM.Length)
		{
			GD.PushError("[EVO-CHECK] 主世界未使用同网格的 res4 演化");
			GetTree().Quit(1);
			return;
		}
		var initial = world.Sim.Facts;
		var initialFrame = world.EvolutionFrame;
		if (!ReferenceEquals(initial.Evolution, initialFrame)
			|| !ReferenceEquals(initial.PlateBoundaries, initialFrame.Projection.PlateBoundaries)
			|| !ReferenceEquals(initial.PlateMotions, initialFrame.Motions)
			|| !initial.HeightM.SequenceEqual(initialFrame.Projection.HeightM))
		{
			GD.PushError("[EVO-CHECK] 初始世界未发布演化帧的地形与边界");
			GetTree().Quit(1);
			return;
		}
		var root = (GeographyEvolutionView)GetTree().CurrentScene;
		var dock = root.GetNode("Presentation/UIManager/PanelLayer/MapDock");
		var modeRow = dock.GetNode("DockBox/ModeRow");
		var crustButton = modeRow.GetChildren().OfType<Button>().FirstOrDefault(button => button.Text == "地壳");
		var motion = root.GetNodeOrNull<PlateMotionOverlay>("Presentation/RenderManager/WorldView/PlateMotion");
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
		var card = root.UI.CellCard;
		var cardSummary = card.FindChild("CellSummary", recursive: true, owned: false) as Label;
		var crustSummary = card.FindChild("CrustSummary", recursive: true, owned: false) as Label;
		var boundaryList = card.FindChild("CrustBoundaries", recursive: true, owned: false) as Label;
		var boundaryToggle = card.FindChild("ToggleBoundaries", recursive: true, owned: false) as Button;
		int mixedCell = CheckCrustQueries(initial, initialFrame);
		if (mixedCell < 0 || cardSummary == null || crustSummary == null || boundaryList == null || boundaryToggle == null)
		{
			GD.PushError("[EVO-CHECK] 地壳查询缺少验证样例或信息卡控件");
			GetTree().Quit(1);
			return;
		}
		root.Camera.LookAtPoint(initial.Ball.CellDirs[mixedCell]);
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Vector2 mapPoint = GetViewport().GetCamera3D().UnprojectPosition(
			initial.Ball.CellDirs[mixedCell] * initial.Ball.Radius);
		Click(mapPoint);
		ulong selected = initial.Ball.CellIds[mixedCell];
		if (!card.IsVisibleInTree() || !crustSummary.Visible || !boundaryToggle.Visible
			|| !CardMatches(cardSummary, crustSummary, initial, initialFrame, mixedCell, selected))
		{
			GD.PushError("[EVO-CHECK] 点击混合边界没有显示正确的地壳读数");
			GetTree().Quit(1);
			return;
		}
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		Click(boundaryToggle);
		if (!boundaryList.IsVisibleInTree() || !boundaryList.Text.Contains("张裂")
			|| !(boundaryList.Text.Contains("俯冲") || boundaryList.Text.Contains("陆陆碰撞")))
		{
			GD.PushError("[EVO-CHECK] 混合格展开后没有同时列出汇聚与张裂边");
			GetTree().Quit(1);
			return;
		}

		float before = initial.HeightM[0];
		long expected = initialFrame.Clock.StepIndex + 1;
		var panel = root.EvolutionReadout;
		var buttons = panel?.FindChildren("*", "Button", recursive: true, owned: false).OfType<Button>().ToArray();
		var play = buttons?.FirstOrDefault(button => button.Text == "播放");
		var pause = buttons?.FirstOrDefault(button => button.Text == "暂停");
		var step = buttons?.FirstOrDefault(button => button.Text == "单步");
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
		seek.Text = world.EvolutionFrame.Clock.TimeMa.ToString(CultureInfo.InvariantCulture);
		Click(jump);
		if (!feedback.Visible || !feedback.Text.Contains("当前步") || world.EvolutionBusy)
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
		if (world.EvolutionBusy || world.EvolutionFrame.Clock.StepIndex != expected || panel?.Visible != true
			|| step.Disabled || status.Text != "暂停"
			|| ReferenceEquals(world.Sim.Facts, initial)
			|| !ReferenceEquals(world.Sim.Facts.Evolution, world.EvolutionFrame)
			|| !ReferenceEquals(world.Sim.Facts.PlateMotions, world.EvolutionFrame.Motions)
			|| !motion.IsVisibleInTree() || motion.ArrowCount == 0
			|| !CardMatches(cardSummary, crustSummary, world.Sim.Facts, world.EvolutionFrame, mixedCell, selected)
			|| world.Sim.Facts.HeightM.SequenceEqual(initial.HeightM))
		{
			GD.PushError("[EVO-CHECK] 未能提交演化步、重建世界快照或显示面板");
			GetTree().Quit(1);
			return;
		}
		stepTime.Stop();
		GD.Print($"[EVO-CHECK] res={world.Sim.Ball.Res} cells={world.EvolutionFrame.Material.CellCount} " +
			$"step={world.EvolutionFrame.Clock.StepIndex} panel={panel != null} arrows={motion.ArrowCount} " +
			$"height[0]={before:F2}->{after:F2} mantle={world.EvolutionFrame.Mantle.StoredVolumeKm3:G6} " +
			$"residual={world.EvolutionFrame.Budget.CumulativeResidual:G6} step-to-view-ms={stepTime.ElapsedMilliseconds}");

		var clock = world.EvolutionFrame.Clock;
		long target = OS.GetCmdlineUserArgs().Contains("--evo-check-10ma")
			? System.Math.Max(expected + 2, clock.StepsFor(10_000_000)) : expected + 2;
		seek.Text = (target * (double)clock.StepYears / 1_000_000d).ToString(CultureInfo.InvariantCulture);
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
		if (world.EvolutionBusy || world.EvolutionFrame.Clock.StepIndex != target
			|| !CardMatches(cardSummary, crustSummary, world.Sim.Facts, world.EvolutionFrame, mixedCell, selected))
		{
			GD.PushError("[EVO-CHECK] 跳转后信息卡未刷新同一位置");
			GetTree().Quit(1);
			return;
		}
		GD.Print($"[EVO-CHECK] selected={selected:X} step={target} Ma={world.EvolutionFrame.Clock.TimeMa:F2} jump-to-view-ms={jumpTime.ElapsedMilliseconds}");
		if (System.Environment.GetEnvironmentVariable("WORLD_BUILD_PROFILE") == "1")
		{
			using var process = System.Diagnostics.Process.GetCurrentProcess();
			var material = world.EvolutionFrame.Material;
			long slotBytes = material.Rows.AllocatedEntries * System.Runtime.InteropServices.Marshal.SizeOf<CrustEntry>();
			GD.Print($"[WORLD-PROFILE] peak-working-set={process.PeakWorkingSet64 / 1048576d:F1} MiB " +
				$"slots={material.OccupiedSlotCount}/{material.Rows.AllocatedEntries} slot-storage={slotBytes / 1048576d:F1} MiB");
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

	static int CheckCrustQueries(WorldFacts facts, EvolutionFrame frame)
	{
		var boundaries = facts.PlateBoundaries;
		var field = frame.Projection.Boundary;
		bool ridge = false, trench = false, collision = false, interior = false;
		int mixed = -1;
		for (int cell = 0; cell < facts.HeightM.Length; cell++)
		{
			var adjacent = boundaries.EdgesOfCell(cell);
			if (adjacent.IsEmpty)
			{
				if (!interior) interior = CrustCellQuery.Read(facts, cell).Boundaries.Length == 0;
				continue;
			}
			int dominant = facts.BoundaryFields.DominantNormalEdge(boundaries, cell);
			if (dominant < 0) continue;
			var query = CrustCellQuery.Read(facts, cell);
			var primary = query.Boundaries[0];
			if (query.Boundaries.Length != adjacent.Length || query.TimeMa != frame.Clock.TimeMa
				|| query.MaterialHeightM != frame.Projection.MaterialHeightM[cell]
				|| query.ReliefM + query.MaterialHeightM != facts.HeightM[cell]
				|| primary.NormalKmPerMa != field.NormalSpeedKmPerMa[dominant]
				|| primary.ShearKmPerMa != field.TangentSpeedKmPerMa[dominant]) return -1;
			ridge |= query.Type == CrustType.Oceanic && primary.Motion == BoundaryKind.Divergent;
			trench |= primary.Convergence == ConvergenceKind.Subduction && primary.Role == BoundarySideRole.Underthrusting;
			collision |= primary.Convergence == ConvergenceKind.Collision && primary.Role == BoundarySideRole.None;
			if (mixed < 0 && query.Boundaries.Any(edge => edge.Motion == BoundaryKind.Divergent)
				&& query.Boundaries.Any(edge => edge.Motion == BoundaryKind.Convergent)) mixed = cell;
			if (ridge && trench && collision && interior && mixed >= 0) return mixed;
		}
		return -1;
	}

	static bool CardMatches(Label card, Label crust, WorldFacts facts, EvolutionFrame frame, int cell, ulong id)
	{
		float material = frame.Projection.MaterialHeightM[cell];
		double relief = facts.HeightM[cell] - (double)material;
		return card.Text.Contains(id.ToString("X")) && card.Text.Contains($"{facts.HeightM[cell]:+0;-0;0} m")
			&& crust.Text.Contains($"{frame.Clock.TimeMa:0.00####} Ma")
			&& crust.Text.Contains($"材料海拔    {material:+0;-0;0} m")
			&& crust.Text.Contains($"剖面净修正  {relief:+0;-0;0} m");
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
