using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using World.NewHexWorld.UI.Modes;

namespace World.NewHexWorld.UI
{
	// 新世界归航坞（自旧 CukHud 迁移，独立演进）：模式按钮行状态 + 坞滑出/滑入动画（鼠标驱动）。
	// 模式按钮 = %ModeRow 场景预置按钮（代码自动收集）；按钮文案/数量由 BindModes 从模式注册表
	// 同源下行（场景只保留按钮顺序职责：下标 = 模式 Id = 注册序）——加模式 = 注册表注册一行
	// + 场景加一个无文案按钮，错位/漏加由 BindModes 当场暴露。
	// 坞动画用显式状态模型（用户拍板）：current 实时读 offset，唯一路径 from=CurrentPos() → to=target。
	public partial class HexDock : PanelContainer
	{
		// 模式选择信号（上行：点模式按钮 → 控制器订阅后切换显示）。
		[Signal] public delegate void ModeSelectedEventHandler(int modeId);

		Button[] _modeButtons;      // 模式按钮（%ModeRow 预置；下标 = 模式 id，0 起）
		int _currentMode;           // 当前模式镜像（SetMode 下行同步）

		// ── 坞滑出/滑入状态机（显式：current/collapsed/expanded/_target 方向）──
		Vector2? _target;           // 动画目标坐标；null=已静止
		Tween _tween;               // 动画（新动画前 Kill 旧的防串台）
		const float HeadH = 30f;            // 标题条高（收起时唯一露出部分；场景 HeadRow 同值）
		const float SlideDur = 0.25f;       // 滑出/滑入时长（匀速 Linear，用户拍板固定时间固定距离）

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
			// 初始收起（只露标题条）：布局未稳先按最小高算，首次 _Process 布局稳定后补正
			MoveDockTo(CollapsedPos(), instant: true);
		}

		// 每帧坞状态：指针在面板矩形内 → 展开；移出 → 收起（无防抖，用户拍板）。
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

		// 下行：外部切模式时同步按钮高亮（BallManager 调）。
		public void SetMode(int modeId)
		{
			if (_modeButtons == null || modeId < 0 || modeId >= _modeButtons.Length) return;
			_currentMode = modeId;
			for (int i = 0; i < _modeButtons.Length; i++)
				_modeButtons[i].ButtonPressed = i == modeId;
		}

		// 下行：模式清单与坞按钮对账（组装器建好注册表后调一次）。按钮文案取模式 Name（注册表
		// 单一事实源，场景文案只是编辑器预览）；数量不符当场抛——场景 ModeRow 与注册表不同步
		// （顺序调了/漏加按钮）在启动首日暴露，而不是运行期静默错位高亮。
		public void BindModes(IReadOnlyList<MapMode> modes)
		{
			if (_modeButtons == null) throw new InvalidOperationException("坞按钮未收集（_Ready 未跑）");
			if (modes.Count != _modeButtons.Length)
				throw new InvalidOperationException(
					$"坞按钮数 {_modeButtons.Length} 与注册模式数 {modes.Count} 不同步：场景 ModeRow 与 MapModeRegistry 须一一对应（按钮顺序 = 注册序 = 模式 Id）");
			for (int i = 0; i < _modeButtons.Length; i++)
				_modeButtons[i].Text = modes[i].Name;
		}

		// ──────────────────────────────────────────────
		// 坞滑出/滑入（移植自 CukHud 显式状态模型，唯一路径 from=CurrentPos → to=target）
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

		// 移动坞到目标坐标——唯一路径：from=CurrentPos()（实时读，永真）→ to=target。
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
			tw.SetTrans(Tween.TransitionType.Linear);   // 匀速（用户拍板：固定时间固定距离，拒缓动）
			tw.Parallel();
			tw.TweenProperty(this, "offset_top", target.X, SlideDur);
			tw.TweenProperty(this, "offset_bottom", target.Y, SlideDur);
			tw.Finished += () => { _tween = null; _target = null; };
			_tween = tw;
			_target = target;
		}
	}
}
