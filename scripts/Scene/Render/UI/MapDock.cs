using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace World.Render.UI
{
	// 地图模式坞（决策 08 §4.4 表现层保留资产 · 从 `World.NoiseWorld.NoiseDock` 迁入改名）：
	//   地图模式按钮行 + 坞滑出/滑入动画。**生成语义为零**——不认识任何生成类型，
	// 只负责"点按钮 → 发ModeSelected(id) 信号"+ 按鼠标位置做滑出/滑入。
	// 与旧坞的两点差异：① BindModes 收**模式名列表**（噪声世界无 MapMode 策略类型，文案由
	// MapMode.Name 下行）；② 其余状态机逐行保留（显式 current/collapsed/expanded/_target、
	// 折返不跳变、布局漂移 instant 补正——原实现已验证，不重写）。
	// 模式按钮 = %ModeRow 场景预置按钮（代码自动收集）；按钮数量与模式数不符当场抛（启动首日暴露错位）。
	//
	// ★2026-10-07 地图坞契约：参数化模式（温度=时间 / 季节气候=指标）+ 色标图例。
	//   MapMode = 温度，Parameter = 年均/1月…12月——**不是**每月一个按钮；坞只管把
	//   ParameterOptions 画成第二行按钮（代码构建，随模式切换重建/隐藏），点选 → 发
	//   ParameterSelected(idx)，由控制器调 mode.SetParameter + 重烘。图例行显示
	//   ScaleCaption（固定物理域声明）；两类文案**均由模式类下行**，坞仍是哑组件。
	public partial class MapDock : PanelContainer
	{
		// 模式选择信号（上行：点模式按钮 → 控制器订阅后切换显示）。
		[Signal] public delegate void ModeSelectedEventHandler(int modeId);

		// 参数选择信号（上行：参数化模式的参数按钮 → 控制器调 mode.SetParameter + 重烘）。
		[Signal] public delegate void ParameterSelectedEventHandler(int paramIndex);

		Button[] _modeButtons;      // 模式按钮（%ModeRow 预置；下标 = 注册序 = 模式身份，0 起）
		int _currentMode;           // 当前模式镜像（SetMode 下行同步）

		// ── 参数行 + 图例行（代码构建；随模式切换由 SetParameterOptions/SetLegend 下行）──
		HBoxContainer _paramRow;    // 参数按钮行（无参数模式隐藏）
		Button[] _paramButtons;
		Label _legendLabel;         // 色标说明（模式无 ScaleCaption 时隐藏）

		// ── 坞滑出/滑入状态机（显式：current/collapsed/expanded/_target 方向）──
		Vector2? _target;           // 动画目标坐标；null=已静止
		Tween _tween;               // 动画（新动画前 Kill 旧的防串台）
		const float HeadH = 30f;            // 标题条高（收起时唯一露出部分；场景 HeadRow 同值）
		const float SlideDur = 0.25f;       // 滑出/滑入时长（匀速 Linear，固定时间固定距离）

		public override void _Ready()
		{
			// 模式按钮收集：%ModeRow 下全部 Button，顺序即模式 id（场景预置，扩展 = 加按钮）
			var row = GetNode<HBoxContainer>("%ModeRow");
			_modeButtons = row.GetChildren().OfType<Button>().ToArray();
			for (int i = 0; i < _modeButtons.Length; i++)
			{
				int mode = i;   // 闭包捕获
				_modeButtons[i].Pressed += () =>
				{
					_currentMode = mode;
					EmitSignal(SignalName.ModeSelected, mode);
				};
			}
			SetMode(_currentMode);   // 初始高亮第一个（默认模式）
			BuildAuxRows();          // 参数行 + 图例行（默认无参数/无图例 → 隐藏）
			// 初始收起（只露标题条）：布局未稳先按最小高算，首次 _Process 布局稳定后补正
			MoveDockTo(CollapsedPos(), instant: true);
		}

		// 参数行 + 图例行构建（DockBox 内 ModeRow 之后；样式取模式按钮的场景 StyleBox 实例复用）。
		void BuildAuxRows()
		{
			_paramRow = new HBoxContainer { UniqueNameInOwner = true };
			_paramRow.AddThemeConstantOverride("separation", 4);
			_paramRow.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
			_paramRow.Visible = false;

			var paramLabel = new Label { Text = "参数" };
			paramLabel.AddThemeColorOverride("font_color", new Color(0.227f, 0.173f, 0.102f));
			paramLabel.AddThemeFontSizeOverride("font_size", 13);
			_paramRow.AddChild(paramLabel);
			_paramButtons = Array.Empty<Button>();

			var legend = new Label { UniqueNameInOwner = true };
			legend.AddThemeColorOverride("font_color", new Color(0.35f, 0.27f, 0.13f));
			legend.AddThemeFontSizeOverride("font_size", 12);
			legend.HorizontalAlignment = HorizontalAlignment.Center;
			legend.Visible = false;
			_legendLabel = legend;

			var box = GetNode<VBoxContainer>("DockBox");
			box.AddChild(_paramRow);
			box.AddChild(_legendLabel);
			box.MoveChild(_paramRow, 2);   // HeadRow(0) → ModeRow(1) → 参数行(2) → 图例(3)
		}

		// 参数按钮样式：直接复用模式按钮的场景 StyleBox/色（实例共享，不复制定义）。
		void ApplyParamStyle(Button b)
		{
			var mode0 = _modeButtons[0];
			b.ToggleMode = true;
			b.ButtonGroup = _paramGroup ??= new ButtonGroup();
			b.AddThemeStyleboxOverride("normal", mode0.GetThemeStylebox("normal"));
			b.AddThemeStyleboxOverride("pressed", mode0.GetThemeStylebox("pressed"));
			b.AddThemeStyleboxOverride("hover", mode0.GetThemeStylebox("hover"));
			b.AddThemeStyleboxOverride("focus", mode0.GetThemeStylebox("focus"));
			b.AddThemeColorOverride("font_color", mode0.GetThemeColor("font_color"));
			b.AddThemeColorOverride("font_hover_color", mode0.GetThemeColor("font_hover_color"));
			b.AddThemeFontSizeOverride("font_size", 13);
		}
		ButtonGroup _paramGroup;   // 参数按钮互斥组（整坞一份）

		// 下行：参数化模式的参数按钮行（options = null → 隐藏行）。每次重建（选项随模式变）。
		public void SetParameterOptions(IReadOnlyList<string> options, int currentIndex)
		{
			if (_paramRow == null) throw new InvalidOperationException("参数行未构建（_Ready 未跑）");

			foreach (var b in _paramButtons) b.QueueFree();
			_paramButtons = Array.Empty<Button>();

			if (options == null || options.Count == 0) { _paramRow.Visible = false; return; }
			_paramRow.Visible = true;

			_paramButtons = new Button[options.Count];
			for (int i = 0; i < options.Count; i++)
			{
				int idx = i;   // 闭包捕获
				var b = new Button { Text = options[i] };
				ApplyParamStyle(b);
				b.Pressed += () => EmitSignal(SignalName.ParameterSelected, idx);
				_paramRow.AddChild(b);
				_paramButtons[i] = b;
			}
			SetParameter(currentIndex);
		}

		// 下行：参数按钮高亮同步（控制器 SetParameter 后调；不发信号防回环）。
		public void SetParameter(int index)
		{
			if (_paramButtons == null) return;
			for (int i = 0; i < _paramButtons.Length; i++)
				_paramButtons[i].ButtonPressed = i == index;
		}

		// 下行：色标说明（caption = null → 隐藏）。
		public void SetLegend(string caption)
		{
			_legendLabel.Text = caption ?? "";
			_legendLabel.Visible = !string.IsNullOrEmpty(caption);
		}

		// 每帧坞状态：指针在面板矩形内 → 展开；移出 → 收起（无防抖）。
		// 动画中目标没变 → 不打扰；变了 → 从 current 折返；静止漂移且意图没变 → instant 补正。
		public override void _Process(double delta) => UpdateDockByMouse();

		private void UpdateDockByMouse()
		{
			if (ContentHeight() <= 1f) return;   // 布局未就绪（拿不到高）跳过，稳定后自然补正

			var mouse = GetViewport().GetMousePosition();
			bool mouseInside = GetGlobalRect().HasPoint(mouse);   // 跟随真实绘制区（含坞内按钮）
			Vector2 target = mouseInside ? ExpandedPos() : CollapsedPos();

			if (_tween != null)
			{
				if (_target != target) MoveDockTo(target, instant: false);   // 动画中意图变了 → 折返
				return;
			}
			if (IsAt(target)) { _target = null; return; }   // 静止已对齐
			// 静止未对齐：意图没变的布局漂移 → instant 补正；意图变了 → 正常动画
			if (IsNearerExpanded() == mouseInside) MoveDockTo(target, instant: true);
			else MoveDockTo(target, instant: false);
		}

		// 下行：外部切模式时同步按钮高亮（WorldGenManager 调）。
		public void SetMode(int modeId)
		{
			if (_modeButtons == null || modeId < 0 || modeId >= _modeButtons.Length) return;
			_currentMode = modeId;
			for (int i = 0; i < _modeButtons.Length; i++)
				_modeButtons[i].ButtonPressed = i == modeId;
		}

		// 下行：模式名与坞按钮对账（组装器建好模式表后调一次）。按钮文案取模式 Name（模式类
		// 单一事实源，场景文案只是编辑器预览）；数量不符当场抛——场景 ModeRow 与模式表不同步
		// 在启动首日暴露，而不是运行期静默错位高亮。
		public void BindModes(IReadOnlyList<string> modeNames)
		{
			if (_modeButtons == null) throw new InvalidOperationException("坞按钮未收集（_Ready 未跑）");
			if (modeNames.Count != _modeButtons.Length)
				throw new InvalidOperationException(
					$"坞按钮数 {_modeButtons.Length} 与模式数 {modeNames.Count} 不同步：场景 ModeRow 与 MapMode 注册表须一一对应（按钮顺序 = 注册序 = 模式身份·下标）");
			for (int i = 0; i < _modeButtons.Length; i++)
				_modeButtons[i].Text = modeNames[i];
		}

		// ──────────────────────────────────────────────
		// 坞滑出/滑入（显式状态模型，唯一路径 from=CurrentPos → to=target）
		// ──────────────────────────────────────────────

		// 面板内容声明高（GetCombinedMinimumSize.Y；声明值不经布局写回 → 恒定，
		// 写出的 offset 差恒等于它 → rect 高恰 = min → 布局永不 clamp 漂移）。
		float ContentHeight() => GetCombinedMinimumSize().Y;

		// 当前 offset 坐标——实时读属性永不缓存（显式状态模型的"当前坐标"）。
		Vector2 CurrentPos() => new Vector2(OffsetTop, OffsetBottom);

		// 展开坐标（全露）：顶边 -h、底边 0（CenterBottom 锚，面板整体在屏内）。
		Vector2 ExpandedPos() => new Vector2(-ContentHeight(), 0f);

		// 收起坐标（只露标题条）：整体下移埋入屏下，只留 HeadH 高标题条可见。
		Vector2 CollapsedPos() => new Vector2(-HeadH, ContentHeight() - HeadH);

		// 当前 offset 是否已对齐目标（差 < eps）。布局未就绪按已对齐处理。
		bool IsAt(Vector2 pos, float eps = 1f)
		{
			if (ContentHeight() <= 1f) return true;
			return Mathf.Abs(OffsetTop - pos.X) < eps && Mathf.Abs(OffsetBottom - pos.Y) < eps;
		}

		// 静止位置离展开态近还是收起态近（漂移补正判定用）。
		bool IsNearerExpanded()
		{
			var current = CurrentPos();
			return current.DistanceSquaredTo(ExpandedPos()) <= current.DistanceSquaredTo(CollapsedPos());
		}

		// 移动坞到目标坐标——唯一路径：from=CurrentPos（实时读，永真）→ to=target。
		// instant=true 直接跳（初始收起/漂移补正）；false 走 SlideDur 匀速滑。已在目标位无操作；
		// 动画中换目标 kill 旧 tween 从属性当前值续滑（折返不跳变）。
		void MoveDockTo(Vector2 target, bool instant)
		{
			if (IsAt(target))
			{
				_tween?.Kill();
				_tween = null;
				_target = null;
				return;
			}
			if (instant)
			{
				_tween?.Kill();
				_tween = null;
				OffsetTop = target.X;       // CenterBottom 锚下 Position setter 不可靠，必须用 Offset
				OffsetBottom = target.Y;
				_target = null;
				return;
			}
			_tween?.Kill();
			var tw = CreateTween();
			tw.SetProcessMode(Tween.TweenProcessMode.Physics);
			tw.SetTrans(Tween.TransitionType.Linear);   // 匀速（固定时间固定距离）
			tw.Parallel();
			tw.TweenProperty(this, "offset_top", target.X, SlideDur);
			tw.TweenProperty(this, "offset_bottom", target.Y, SlideDur);
			tw.Finished += () => { _tween = null; _target = null; };
			_tween = tw;
			_target = target;
		}
	}
}
