using Godot;

namespace World.Camera
{

    public partial class OrbitalCamera : Node3D
    {
        [Export] private float _planetRadius = 6330f;

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

        private float MinDistance => _planetRadius * 1.02f; // 贴地视角（球面距 ~127km）
        private float MaxDistance => _planetRadius * 5f;

        public override void _Ready()
        {
            _camera = new Camera3D
            {
                // 真实比例星球（半径 6330 km）：far 必须覆盖整颗球。
                // near=10 允许贴地视角（最小 1.02× 半径 → 球面距相机 ~127km）
                Near = 10f,
                Far = 50000f
            };
            AddChild(_camera);
            _camera.Current = true; // 必须在入树后设置，否则视口没有活动相机 → 黑屏
            _distance = _planetRadius * 1.7f;  // 初始满屏视图（球视张角 ≈ 72°），滚轮可继续拉近看局部
            _targetDistance = _distance;
            UpdatePosition();
        }

        public override void _Input(InputEvent @event)
        {
            // 鼠标左键拖动旋转（向右拖 = 星球表面向右转）
            if (@event is InputEventMouseMotion motion &&
                Input.IsMouseButtonPressed(MouseButton.Left))
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
    }
}
