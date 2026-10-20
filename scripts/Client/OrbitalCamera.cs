using Godot;
using World.Data;
using World.Utils;

namespace World.Client
{

    /// <summary>
    /// 轨道相机：球坐标（theta / phi / 距离）绕球心取景，自建 Camera3D、自处理输入与逐帧平滑缩放。
    /// 半径是尺度因子（非 km）：取景距离与近/远裁剪都 ∝ _planetRadius。
    /// </summary>
    public partial class OrbitalCamera : Node3D
    {
        // 字段语义 = 尺度因子（非 km）：球面几何（Ball 顶点/格心）按同值铺。
        // 缺省取 PlanetGeometry.ProductionRadius，与构造 Ball 用的是同一常量 ⇒ 取景/裁剪 ∝ R 的同值由同源保证。
        [Export] private float _planetRadius = PlanetGeometry.ProductionRadius;

        // 球坐标参数
        private float _theta = 0.8f;       // 水平角度
        private float _phi = 0.4f;         // 垂直角度（0=北极，π=南极）
        private float _distance;           // 当前到球心的距离
        private float _targetDistance;     // 目标距离（滚轮设置）

        private const float RotationSpeed = 2f;
        private const float ZoomLerpSpeed = 8f;
        private const float ZoomFactor = 1.08f;
        private const float MouseSensitivity = 0.005f;

        private Camera3D _camera;

        /// <summary>取景半径（只读口：供管理器核对"与球半径同值"这条隐式契约）。</summary>
        public float PlanetRadius => _planetRadius;

        private float MinDistance => _planetRadius * 1.02f; // 贴地视角（球面距 ~2% R）
        private float MaxDistance => _planetRadius * 5f;

        /// <summary>重设取景半径（读档后调用；轨道距离 / 远近裁剪全 ∝ R）。</summary>
        /// <param name="radiusKm">新的半径（尺度因子，非 km）。</param>
        public void SetPlanetRadius(float radiusKm)
        {
            _planetRadius = radiusKm;
            // 近/远裁剪随半径重推（_Ready 同款公式；半径可变后必须同步，否则换档星球裁剪错位）
            _camera.Near = _planetRadius * 0.01f;
            _camera.Far = _planetRadius * 10f;
            // 满屏视图 + 缩放范围随 R 重算（当前距离若超出新范围则钳制）
            _distance = Mathf.Clamp(_distance, MinDistance, MaxDistance);
            _targetDistance = _distance;
            UpdatePosition();
        }

        public override void _Ready()
        {
            _camera = new Camera3D
            {
                // 近/远裁剪按半径推导：单位尺度 R=2 时 Near=0.02（贴地不至于被裁）、
                // Far=20（> 最远 5R+R，覆盖全球）——按比例推导，改 R 自动跟随。
                Near = _planetRadius * 0.01f,
                Far = _planetRadius * 10f
            };
            AddChild(_camera);
            _camera.Current = true; // 必须在入树后设置，否则视口没有活动相机 → 黑屏
            _distance = _planetRadius * 1.7f;  // 初始满屏视图（球视张角 ≈ 72°），滚轮可继续拉近看局部
            _targetDistance = _distance;
            UpdatePosition();
        }

        // 手势判据只有 PointerGesture 一份：本相机一个实例，只关心 Drag。
        // 两个消费者各持实例（各记按下状态），阈值与判据只有一份 ⇒
        // "拖过阈值 ⇒ 点选不触发、旋转照转"由同一份判据保证。
        private readonly PointerGesture _gesture = new();

        // 必须是 _UnhandledInput 而非 _Input：_Input 在 GUI 之前处理，拖动月份滑块（HSlider 也是
        // 左键按下 + 移动）会同时旋转星球；GUI 消费过的事件不到 _UnhandledInput，空处旋转不受影响。
        public override void _UnhandledInput(InputEvent @event)
        {
            // 鼠标左键拖动旋转（向右拖 = 星球表面向右转）
            // 判据交给 PointerGesture：Drag 意图 = 左键按下期间的移动，或超阈值的抬起；
            // 非左键 / 滚轮 / 未按下的移动 ⇒ None。
            var intent = _gesture.Feed(@event, out _);
            if (intent == PointerIntent.Drag && @event is InputEventMouseMotion motion)
            {
                _theta += motion.Relative.X * MouseSensitivity;
                _phi = Mathf.Clamp(
                    _phi - motion.Relative.Y * MouseSensitivity,
                    0.01f, Mathf.Pi - 0.01f
                );
                UpdatePosition();
                GetViewport().SetInputAsHandled();
            }

            // 滚轮缩放
            if (@event is InputEventMouseButton btn && btn.Pressed)
            {
                if (btn.ButtonIndex == MouseButton.WheelDown)
                {
                    _targetDistance = Mathf.Min(MaxDistance, _targetDistance * ZoomFactor);
                    GetViewport().SetInputAsHandled();
                }
                else if (btn.ButtonIndex == MouseButton.WheelUp)
                {
                    _targetDistance = Mathf.Max(MinDistance, _targetDistance / ZoomFactor);
                    GetViewport().SetInputAsHandled();
                }
            }
        }

        public override void _Process(double delta)
        {
            float dt = (float)delta;

            // W/S — 上/下旋转
            if (Input.IsKeyPressed(Key.W))
                _phi = Mathf.Clamp(_phi - RotationSpeed * dt, 0.01f, Mathf.Pi - 0.01f);
            if (Input.IsKeyPressed(Key.S))
                _phi = Mathf.Clamp(_phi + RotationSpeed * dt, 0.01f, Mathf.Pi - 0.01f);

            // A/D — 左/右旋转
            if (Input.IsKeyPressed(Key.A))
                _theta += RotationSpeed * dt;
            if (Input.IsKeyPressed(Key.D))
                _theta -= RotationSpeed * dt;

            // 平滑缩放
            _distance = Mathf.Lerp(_distance, _targetDistance, 1f - Mathf.Exp(-ZoomLerpSpeed * dt));

            UpdatePosition();
        }

        private void UpdatePosition()
        {
            float x = _distance * Mathf.Sin(_phi) * Mathf.Cos(_theta);
            float y = _distance * Mathf.Cos(_phi);
            float z = _distance * Mathf.Sin(_phi) * Mathf.Sin(_theta);

            _camera.Position = new Vector3(x, y, z);
            _camera.LookAt(Vector3.Zero);
        }

        /// <summary>指向球面上一点（取景转向，快照式无动画）：theta/phi 设为该方向球坐标。</summary>
        public void LookAtPoint(Vector3 worldDir)
        {
            var d = worldDir.Normalized();
            _phi = Mathf.Clamp(Mathf.Acos(Mathf.Clamp(d.Y, -1f, 1f)), 0.01f, Mathf.Pi - 0.01f);
            _theta = Mathf.Atan2(d.Z, d.X);
            UpdatePosition();
        }
    }
}
