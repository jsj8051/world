using Godot;
using System;
using System.Globalization;
using World.Data;
using World.Logic;

namespace World.UI;

/// <summary>
/// 演化控制面板：显示地质时间与账本，提供播放、暂停、单步、跳转和倍率。
/// 只发意图，不持仿真；按钮回调由界面管理器转到世界侧。
/// </summary>
[Tool]
public partial class GeologyPanel : PanelContainer
{
	static readonly Color Ink = new("#302217");
	static readonly Color MutedInk = new("#5F5144");
	static readonly Color Border = new("#90704A");
	static readonly Color ErrorInk = new("#842D23");

	Label _status;
	Label _time;
	Label _readout;
	Label _feedback;
	LineEdit _seek;
	Button _play;
	Button _pause;
	Button _step;
	Button _jump;
	EvolutionControlStatus _lastStatus;
	bool _wasBusy;

	public void SetStatus(EvolutionControlStatus status, bool busy)
	{
		if (_status == null || (_lastStatus == status && _wasBusy == busy)) return;
		_lastStatus = status;
		_wasBusy = busy;
		string text = busy ? "正在计算…完成后刷新地图" : status switch
		{
			EvolutionControlStatus.Paused => "暂停",
			EvolutionControlStatus.Playing => "播放中",
			EvolutionControlStatus.Seeking => "跳转中",
			EvolutionControlStatus.Faulted => "演化失败，详情见输出",
			_ => "未初始化",
		};
		if (_status.Text != text)
		{
			_status.Text = text;
			_status.AddThemeColorOverride("font_color",
				status == EvolutionControlStatus.Faulted ? ErrorInk : Ink);
		}
		bool canStep = !busy && status == EvolutionControlStatus.Paused;
		bool canSeek = !busy && (status == EvolutionControlStatus.Paused || status == EvolutionControlStatus.Playing);
		bool canRecover = !busy && status == EvolutionControlStatus.Faulted;
		_play.Disabled = !canStep;
		_pause.Disabled = status != EvolutionControlStatus.Playing;
		_step.Disabled = !canStep && !canRecover;
		_step.Text = canRecover ? "恢复" : "单步";
		_jump.Disabled = !canSeek;
		_seek.Editable = canSeek;
	}

	public void SetFeedback(string message)
	{
		_feedback.Text = message ?? "";
		_feedback.Visible = !string.IsNullOrEmpty(message);
	}

	/// <summary>播放请求。</summary>
	public event Action PlayPressed;
	/// <summary>暂停请求。</summary>
	public event Action PausePressed;
	/// <summary>单步请求。</summary>
	public event Action StepPressed;
	/// <summary>确认故障并回到上一个已发布的年代。</summary>
	public event Action RecoverPressed;
	/// <summary>跳转请求（目标 Ma）。</summary>
	public event Action<double> SeekRequested;
	/// <summary>倍率请求（地质年/秒）。</summary>
	public event Action<double> SpeedChanged;

	public override void _Ready()
	{
		CustomMinimumSize = new Vector2(360, 0);
		Theme = CreatePanelTheme();
		MouseFilter = MouseFilterEnum.Stop;
		MouseForcePassScrollEvents = false;
		AnchorLeft = 1;
		AnchorRight = 1;
		AnchorTop = 0;
		AnchorBottom = 0;
		OffsetLeft = -376;
		OffsetRight = -16;
		OffsetTop = 16;
		OffsetBottom = 16;
		GrowHorizontal = GrowDirection.Begin;

		var box = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
		box.AddThemeConstantOverride("separation", 8);
		AddChild(box);

		var title = new Label { Text = "地质演化" };
		title.AddThemeFontSizeOverride("font_size", 20);
		box.AddChild(title);

		_status = new Label { Name = "Status", Text = "暂停" };
		_time = new Label { Text = "0.0 Ma · 第 0 步" };
		_readout = new Label { Text = "" };
		_readout.AddThemeColorOverride("font_color", MutedInk);
		box.AddChild(_status);
		box.AddChild(_time);
		box.AddChild(_readout);

		var buttons = AddRow(box);
		_play = AddButton(buttons, "播放", () => PlayPressed?.Invoke());
		_pause = AddButton(buttons, "暂停", () => PausePressed?.Invoke());
		_step = AddButton(buttons, "单步", () =>
		{
			if (_lastStatus == EvolutionControlStatus.Faulted) RecoverPressed?.Invoke();
			else StepPressed?.Invoke();
		});

		var seekRow = AddRow(box);
		_seek = new LineEdit
		{
			Name = "SeekTime",
			PlaceholderText = "目标时间（Ma）",
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0, 34),
			MouseFilter = MouseFilterEnum.Stop,
		};
		_seek.TextSubmitted += _ => OnSeek();
		seekRow.AddChild(_seek);
		_jump = AddButton(seekRow, "跳转", OnSeek);
		_jump.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;

		var speedRow = AddRow(box);
		speedRow.AddChild(new Label { Text = "倍率" });
		var speed = new OptionButton
		{
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(0, 34),
			MouseFilter = MouseFilterEnum.Stop,
		};
		speed.AddItem("0.1 Ma/s", 0);
		speed.AddItem("1 Ma/s", 1);
		speed.AddItem("10 Ma/s", 2);
		speed.Selected = 0;
		speed.ItemSelected += index => SpeedChanged?.Invoke(index switch
		{
			0 => 100_000d,
			1 => 1_000_000d,
			_ => 10_000_000d,
		});
		speedRow.AddChild(speed);
		speed.GetPopup().Theme = Theme;

		_feedback = new Label { Name = "Feedback", Visible = false, AutowrapMode = TextServer.AutowrapMode.WordSmart };
		_feedback.AddThemeColorOverride("font_color", ErrorInk);
		box.AddChild(_feedback);
		SetStatus(EvolutionControlStatus.Paused, false);
	}

	void OnSeek()
	{
		if (_jump.Disabled) return;
		if (double.TryParse(_seek.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double ma)
			&& double.IsFinite(ma) && ma >= 0)
			SeekRequested?.Invoke(ma);
		else SetFeedback("请输入非负的目标时间，例如 10。");
	}

	static HBoxContainer AddRow(Container parent)
	{
		var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
		row.AddThemeConstantOverride("separation", 6);
		parent.AddChild(row);
		return row;
	}

	static Button AddButton(Container parent, string text, Action pressed)
	{
		var button = new Button
		{
			Text = text,
			SizeFlagsHorizontal = SizeFlags.ExpandFill,
			CustomMinimumSize = new Vector2(64, 34),
			MouseFilter = MouseFilterEnum.Stop,
		};
		button.Pressed += pressed;
		parent.AddChild(button);
		return button;
	}

	static Theme CreatePanelTheme()
	{
		var theme = new Theme { DefaultFontSize = 16 };
		var paper = Box("#F6EBD4", 14);
		var normal = Box("#FFF7E5", 9);
		var hover = Box("#F2D9A6", 9);
		var pressed = Box("#DDBB80", 9);
		var disabled = Box("#E8DDCA", 9);
		var focus = Box("#FFF7E5", 9);
		focus.DrawCenter = false;
		focus.BorderColor = new Color("#94561D");
		focus.SetBorderWidthAll(2);
		theme.SetStylebox("panel", "PanelContainer", paper);
		theme.SetColor("font_color", "Label", Ink);
		foreach (string type in new[] { "Button", "OptionButton" })
		{
			theme.SetStylebox("normal", type, normal);
			theme.SetStylebox("hover", type, hover);
			theme.SetStylebox("pressed", type, pressed);
			theme.SetStylebox("hover_pressed", type, pressed);
			theme.SetStylebox("disabled", type, disabled);
			theme.SetStylebox("focus", type, focus);
			foreach (string state in new[] { "font_color", "font_hover_color", "font_pressed_color",
				"font_hover_pressed_color", "font_focus_color" })
				theme.SetColor(state, type, Ink);
			theme.SetColor("font_disabled_color", type, MutedInk);
		}
		theme.SetStylebox("normal", "LineEdit", normal);
		theme.SetStylebox("read_only", "LineEdit", disabled);
		theme.SetStylebox("focus", "LineEdit", focus);
		theme.SetColor("font_color", "LineEdit", Ink);
		theme.SetColor("font_placeholder_color", "LineEdit", MutedInk);
		theme.SetColor("font_uneditable_color", "LineEdit", MutedInk);
		theme.SetColor("font_selected_color", "LineEdit", Ink);
		theme.SetColor("selection_color", "LineEdit", new Color("#E8C892"));
		theme.SetColor("caret_color", "LineEdit", Ink);
		theme.SetConstant("modulate_arrow", "OptionButton", 1);
		theme.SetStylebox("panel", "PopupMenu", paper);
		theme.SetStylebox("hover", "PopupMenu", hover);
		theme.SetColor("font_color", "PopupMenu", Ink);
		theme.SetColor("font_hover_color", "PopupMenu", Ink);
		return theme;
	}

	static StyleBoxFlat Box(string color, float margin)
	{
		var style = new StyleBoxFlat
		{
			BgColor = new Color(color), BorderColor = Border,
			ContentMarginLeft = margin, ContentMarginRight = margin,
			ContentMarginTop = margin / 2, ContentMarginBottom = margin / 2,
		};
		style.SetCornerRadiusAll(6);
		style.SetBorderWidthAll(1);
		return style;
	}

	/// <summary>刷新读数；由界面管理器在每帧提交后调用。</summary>
	public void Refresh(WorldFacts world)
	{
		EvolutionFrame frame = world.Evolution;
		_time.Text = $"{frame.Clock.TimeMa:F2} Ma · 第 {frame.Clock.StepIndex} 步";

		double minAge = double.MaxValue, maxAge = 0;
		foreach (double? oceanAge in frame.Projection.OceanAgeMa)
		{
			if (oceanAge is double age)
			{
				minAge = Math.Min(minAge, age);
				maxAge = Math.Max(maxAge, age);
			}
		}
		string ages = minAge == double.MaxValue ? "—" : $"{minAge:F1}…{maxAge:F1} Ma";
		_readout.Text = $"露出陆地 {frame.Projection.EmergentLandFraction:P1} · 海底年龄 {ages}\n" +
			$"俯冲 {frame.Budget.TotalSubducted:G4} · 新生 {frame.Budget.TotalNewOceanic:G4}\n" +
			$"火山 {frame.Budget.TotalVolcanic:G4} km³\n" +
			$"地幔 {frame.Mantle.StoredVolumeKm3:G4} km³ · 残差 {frame.Budget.CumulativeResidual:G3}\n" +
			$"材料槽 {frame.Material.OccupiedSlotCount} · 覆盖修正 {frame.CoverageCorrectionKm2:G3} km²";
	}
}
