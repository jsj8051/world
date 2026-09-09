using Godot;
using World.Camera;                          // OrbitalCamera（老树轨道相机，单源复用）
using World.NewHexWorld.Planet;              // H3PlateManager
using World.NewHexWorld.UI;                  // HexDock / HexInfoPanel
using World.NewHexWorld.UI.Modes;            // 地图模式策略 + 注册表
using World.NewHexWorld.UI.ViewModels;       // 球视图 / 坞 / 格信息 VM

namespace World.NewHexWorld
{
	// 场景根 = 组装器 + 输入路由（设计入口 §2.2/§2.4）：构造全部逻辑层对象（Ball → H3PlateManager
	// 静态生成）→ 建 MapModeRegistry（两模式策略注入 Model）→ 建各视图 VM（注入 Model/当前模式）→
	// 下行注入 View（BallView.Init/Bind、HexDock.BindModes、信息面板事件接线）→ 承担点击拾取输入路由。
	// 相机 = OrbitalCamera（老树单源复用：左键拖转/滚轮缩放/WASD）；本类不承载业务数据
	// （只留场景调参旋钮 ResLevel/Radius/NumPlates/Seed）；数据流 = Model → VM 派生 → View。
	public partial class BallManager : Node3D
	{
		[Export] public int ResLevel = 3;        // H3 分辨率档（Ball 单一事实源；相机/视图全由此推导）
		[Export] public float Radius = 2.0f;     // 球半径（须与 OrbitalCamera._planetRadius 场景同值）
		[Export] public int NumPlates = 15;      // 板块数 P（设计参数表：10–20 区间，默认 15）
		[Export] public int Seed = 42;           // 星球种子（定胞心抽取/生长/陆性，同 seed 全球同局）

		OrbitalCamera _orbitalCamera;            // 子节点：轨道相机（拖转/缩放/拾取射线源）
		BallView _ballView;                      // 子节点：球视图（View）
		H3PlateManager _plates;                  // 逻辑层：静态地壳场 + 板统计（唯一权威）
		HexDock _dock;                           // 坞（View；模式按钮 + 坞动画）
		HexInfoPanel _infoPanel;                 // 格信息面板（View）
		DockViewModel _dockVm;                   // 坞 VM：模式列表 + 当前模式（交互状态）
		HexWorldViewModel _worldVm;              // 球视图 VM：颜色投影/边界描边/统计
		CellInfoViewModel _cellInfoVm;           // 格信息 VM：拾取格 → 条目

		// 点击拾取判别（老树同款）：按下记位，释放时位移 <8px 且时长 <300ms 视为点击——
		// 拖动旋转（OrbitalCamera 消费）不会误触发格信息拾取。
		Vector2 _pickPressPos;
		ulong _pickPressTime;
		bool _pickPressArmed;

		public override void _Ready()
		{
			_orbitalCamera = GetNode<OrbitalCamera>("OrbitalCamera");

			// ① 逻辑层（Model 构造权全在组装器，View 只收已建好的引用）：
			//    Ball 网格数据 → 注入球视图建几何 → H3PlateManager 静态生成地壳场
			_ballView = GetNode<BallView>("Ball");
			var ball = new Ball(ResLevel, Radius);
			_ballView.Init(ball);
			_plates = new H3PlateManager();
			_plates.Init(ball, NumPlates, Seed);

			// ② 模式策略注册表（MapMode 注入 Model 只读引用；注册序 = 坞按钮序 = Id 0/1；
			//    海陆模式 2026-09-09 删除——海陆观感由海拔色带 0m 硬台阶天然承载）
			var registry = new MapModeRegistry();
			registry.Register(new ElevationMapMode(_plates.Plate.Crust));
			registry.Register(new PlateMapMode(_plates.Plate.Crust,
				_plates.NumPlates, _plates.PlateCounts));

			// ③ VM（注入 Model；初始模式 = 注册表首模式（海拔）——坞按钮默认高亮同由 HexDock._Ready 置位）
			_worldVm = new HexWorldViewModel(ball, _plates, registry.Modes[0]);
			_dockVm = new DockViewModel(registry);
			_cellInfoVm = new CellInfoViewModel(ball, _plates, registry.Modes[0]);

			// ④ View 接线（全部下行注入）：球视图订阅 VM 变更信号 → 首帧即渲染海拔模式 + 边界线；
			//    坞按钮文案/数量与注册表对账（不同步当场抛，见 HexDock.BindModes）
			_ballView.Bind(_worldVm);

			// UI：坞模式点击（View 事件上抛）→ 坞 VM 选择 → 广播：球视图换显示 uniform + 格信息换策略 + 坞高亮
			var uiLayer = GetNode<CanvasLayer>("UiLayer");
			_dock = uiLayer.GetNode<HexDock>("HexDock");
			_dock.BindModes(_dockVm.Modes);
			_dock.ModeSelected += modeId => _dockVm.Select(modeId);
			_dockVm.ModeChanged += mode =>
			{
				_worldVm.ApplyMode(mode);
				_cellInfoVm.SetMode(mode);
				_dock.SetMode(mode.Id);
			};

			// 格信息面板：VM 条目事件 → 面板渲染（null = 无选中 → 隐藏）
			_infoPanel = uiLayer.GetNode<HexInfoPanel>("HexInfoPanel");
			_cellInfoVm.EntriesChanged += entries =>
			{
				if (entries == null) _infoPanel.HidePanel();
				else _infoPanel.ShowAt(entries);
			};
		}

		// ── 输入路由：点击拾取 → 格信息 VM（不承载业务数据）──
		// 点击判别 = 老树同款"按下记位/释放判点击"（位移 <8px 且时长 <300ms）：
		// 左键拖动已被 OrbitalCamera 消费为旋转，此处只放行真正的点击；空白处点击 → 隐藏面板。

		public override void _UnhandledInput(InputEvent @event)
		{
			if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left) return;
			if (mb.Pressed)
			{
				_pickPressPos = mb.Position;
				_pickPressTime = Time.GetTicksMsec();
				_pickPressArmed = true;
			}
			else if (_pickPressArmed)
			{
				_pickPressArmed = false;   // 无论是否点击都复位（下一次按下重新记位）
				if (mb.Position.DistanceTo(_pickPressPos) >= 8f
					|| Time.GetTicksMsec() - _pickPressTime >= 300) return;   // 拖动 → 不拣选
				ulong? cell = _ballView.PickCell(mb.Position, _orbitalCamera.Cam);
				if (cell is ulong id) _cellInfoVm.ShowCell(id);
				else _cellInfoVm.Hide();   // 点到空白（球外）→ 隐藏面板
			}
		}
	}
}
