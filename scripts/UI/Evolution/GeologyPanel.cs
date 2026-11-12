using Godot;
using System;
using System.Globalization;
using System.Threading;
using World.Data;
using World.Data.Work;
using World.Logic;
using World.Scene;
using World.Utils;

namespace World.UI;

/// <summary>
/// 演化控制面板（独立场景）：显示地质时间与账本，提供播放、暂停、单步、跳转、倍率与计算进度。
/// 只发意图，不持仿真；按钮回调由界面管理器转到世界侧。
/// </summary>
public partial class GeologyPanel : PanelContainer
{
	static readonly Color Ink = new("#302217");
	static readonly Color MutedInk = new("#5F5144");
	static readonly Color Border = new("#90704A");
	static readonly Color ErrorInk = new("#842D23");

	[Export] public NodePath StatusPath { get; set; }
	[Export] public NodePath TimePath { get; set; }
	[Export] public NodePath ReadoutPath { get; set; }
	[Export] public NodePath PlayPath { get; set; }
	[Export] public NodePath PausePath { get; set; }
	[Export] public NodePath StepPath { get; set; }
	[Export] public NodePath SeekPath { get; set; }
	[Export] public NodePath JumpPath { get; set; }
	[Export] public NodePath SpeedPath { get; set; }
	[Export] public NodePath ProgressPath { get; set; }
	[Export] public NodePath FeedbackPath { get; set; }

	Label _status;
	Label _time;
	Label _readout;
	Label _feedback;
	LineEdit _seek;
	Button _play;
	Button _pause;
	Button _step;
	Button _jump;
	OptionButton _speed;
	LoadProgressView _progress;

	EvolutionControlStatus _lastStatus;
	bool _wasBusy;
	IWorkSignalSource _source;
	LoadOperation _operation;
	LoadProgressMonitor _monitor;
	LoadProgressMailbox _progressMailbox;
	LoadProgressSubscription _progressSubscription;
	LoadProgressSnapshot? _latestProgress;
	readonly Guid _pageId = Guid.NewGuid();
	LoadOperationBinding _progressBinding;
	double _progressIdle;
	double _sinceProgressSnapshot;

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

	/// <summary>主线程订阅演化进度；这里只观察任务，解绑不取消世界侧计算。</summary>
	internal void SetEvolutionSignals(IWorkSignalSource source, bool busy)
	{
		if (!busy || source == null)
		{
			UnbindProgress();
			return;
		}
		if (source == _source && _monitor != null) return;

		UnbindProgress();
		_operation = new LoadOperation(Guid.NewGuid(), LoadFlow.Evolution, "演化计算");
		_progressBinding = new LoadOperationBinding(_operation, _pageId, CancellationToken.None);
		_progressMailbox = new LoadProgressMailbox(_operation.Id);
		_progressSubscription = _operation.Subscribe(_progressMailbox);
		_monitor = new LoadProgressMonitor(_operation, plan: null, start: 0f, span: 1f,
			completesOperation: true);
		_source = source;
		_monitor.Attach(source);
		_progress.Visible = true;
		RefreshProgress(0);
	}

	void UnbindProgress()
	{
		_progressMailbox?.Dispose();
		_progressMailbox = null;
		_progressSubscription?.Dispose();
		_progressSubscription = null;
		_progressBinding = null;
		_monitor?.Dispose();
		_monitor = null;
		_source = null;
		_operation?.Dispose();
		_operation = null;
		_latestProgress = null;
		_progressIdle = 0;
		_sinceProgressSnapshot = 0;
		if (_progress != null) _progress.Visible = false;
	}

	public override void _Process(double delta) => RefreshProgress(delta);

	void RefreshProgress(double delta)
	{
		if (_progressMailbox == null || _progressBinding == null) return;
		if (!LoadCommitGuard.IsCurrent(_progressBinding.Key, _progressBinding, _pageId))
		{
			UnbindProgress();
			return;
		}

		if (_progressMailbox.TryTakeLatest(out LoadProgressSnapshot snapshot))
		{
			_latestProgress = snapshot;
			_sinceProgressSnapshot = 0;
			_progressIdle = 0;
			_progress.Refresh(snapshot, snapshot.Elapsed);
			_progress.Visible = snapshot.State == LoadState.Running;
			return;
		}

		if (_latestProgress is not LoadProgressSnapshot latest || latest.State != LoadState.Running) return;
		_progressIdle += delta;
		_sinceProgressSnapshot += delta;
		if (_progressIdle < 0.1) return;
		_progressIdle = 0;
		_progress.Refresh(latest, latest.Elapsed + TimeSpan.FromSeconds(_sinceProgressSnapshot));
	}

	public override void _ExitTree() => UnbindProgress();

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
		_status = this.Require<Label>(StatusPath, nameof(StatusPath));
		_time = this.Require<Label>(TimePath, nameof(TimePath));
		_readout = this.Require<Label>(ReadoutPath, nameof(ReadoutPath));
		_feedback = this.Require<Label>(FeedbackPath, nameof(FeedbackPath));
		_seek = this.Require<LineEdit>(SeekPath, nameof(SeekPath));
		_play = this.Require<Button>(PlayPath, nameof(PlayPath));
		_pause = this.Require<Button>(PausePath, nameof(PausePath));
		_step = this.Require<Button>(StepPath, nameof(StepPath));
		_jump = this.Require<Button>(JumpPath, nameof(JumpPath));
		_speed = this.Require<OptionButton>(SpeedPath, nameof(SpeedPath));
		_progress = this.Require<LoadProgressView>(ProgressPath, nameof(ProgressPath));

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

		_play.Pressed += () => PlayPressed?.Invoke();
		_pause.Pressed += () => PausePressed?.Invoke();
		_step.Pressed += () =>
		{
			if (_lastStatus == EvolutionControlStatus.Faulted) RecoverPressed?.Invoke();
			else StepPressed?.Invoke();
		};
		_seek.TextSubmitted += _ => OnSeek();
		_jump.Pressed += OnSeek;

		_speed.AddItem("0.1 Ma/s", 0);
		_speed.AddItem("1 Ma/s", 1);
		_speed.AddItem("10 Ma/s", 2);
		_speed.Selected = 0;
		_speed.ItemSelected += index => SpeedChanged?.Invoke(index switch
		{
			0 => 100_000d,
			1 => 1_000_000d,
			_ => 10_000_000d,
		});
		_speed.GetPopup().Theme = Theme;

		_progress.Visible = false;
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
