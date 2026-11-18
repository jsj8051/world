using System;
using Godot;
using World.Utils;

namespace World.Render;

/// <summary>绕组件原点旋转和缩放的相机，取景距离和裁剪范围按传入的半径缩放。</summary>
public partial class OrbitalCamera : Node3D
{
	const float RotationSpeed = 2f;
	const float ZoomLerpSpeed = 8f;
	const float ZoomFactor = 1.08f;
	const float MouseSensitivity = 0.005f;
	const float MinPolarAngle = 0.01f;
	const float MaxPolarAngle = Mathf.Pi - MinPolarAngle;

	[Export(PropertyHint.Range, "0.01,100,0.01,or_greater")]
	float _orbitRadius = 1f;

	readonly PointerGesture _gesture = new();
	float _azimuth = 0.8f;
	float _polarAngle = 0.4f;
	float _distance;
	float _targetDistance;
	Camera3D _camera;

	float MinDistance => _orbitRadius * 1.02f;
	float MaxDistance => _orbitRadius * 5f;

	public override void _Ready()
	{
		_camera = new Camera3D();
		UpdateClipping();
		AddChild(_camera);
		_camera.Current = true;
		_distance = _targetDistance = _orbitRadius * 1.7f;
		UpdatePosition();
	}

	/// <summary>设置取景对象在场景单位中的半径，保留已有的相对取景距离。</summary>
	public void SetOrbitRadius(float radius)
	{
		if (!float.IsFinite(radius) || radius <= 0f)
			throw new ArgumentOutOfRangeException(nameof(radius), "取景半径必须是有限正数。");

		float scale = radius / _orbitRadius;
		_orbitRadius = radius;
		if (_camera == null) return;

		_distance = Mathf.Clamp(_distance * scale, MinDistance, MaxDistance);
		_targetDistance = Mathf.Clamp(_targetDistance * scale, MinDistance, MaxDistance);
		UpdateClipping();
		UpdatePosition();
	}

	/// <summary>让镜头转到组件局部坐标中由该点指定的方向。</summary>
	public void LookAtPoint(Vector3 point)
	{
		if (point.IsZeroApprox()) return;
		Vector3 direction = point.Normalized();
		_polarAngle = Mathf.Clamp(Mathf.Acos(Mathf.Clamp(direction.Y, -1f, 1f)), MinPolarAngle, MaxPolarAngle);
		_azimuth = Mathf.Atan2(direction.Z, direction.X);
		if (_camera != null) UpdatePosition();
	}

	// GUI 优先处理输入，操作滑块和面板时不会同时转动镜头。
	public override void _UnhandledInput(InputEvent @event)
	{
		var intent = _gesture.Feed(@event, out _);
		if (intent == PointerIntent.Drag && @event is InputEventMouseMotion motion)
		{
			_azimuth += motion.Relative.X * MouseSensitivity;
			_polarAngle = Mathf.Clamp(_polarAngle - motion.Relative.Y * MouseSensitivity, MinPolarAngle, MaxPolarAngle);
			UpdatePosition();
			GetViewport().SetInputAsHandled();
		}

		if (@event is InputEventMouseButton button && button.Pressed)
		{
			if (button.ButtonIndex == MouseButton.WheelDown)
			{
				_targetDistance = Mathf.Min(MaxDistance, _targetDistance * ZoomFactor);
				GetViewport().SetInputAsHandled();
			}
			else if (button.ButtonIndex == MouseButton.WheelUp)
			{
				_targetDistance = Mathf.Max(MinDistance, _targetDistance / ZoomFactor);
				GetViewport().SetInputAsHandled();
			}
		}
	}

	public override void _Process(double delta)
	{
		float dt = (float)delta;
		if (Input.IsKeyPressed(Key.W))
			_polarAngle = Mathf.Clamp(_polarAngle - RotationSpeed * dt, MinPolarAngle, MaxPolarAngle);
		if (Input.IsKeyPressed(Key.S))
			_polarAngle = Mathf.Clamp(_polarAngle + RotationSpeed * dt, MinPolarAngle, MaxPolarAngle);
		if (Input.IsKeyPressed(Key.A)) _azimuth += RotationSpeed * dt;
		if (Input.IsKeyPressed(Key.D)) _azimuth -= RotationSpeed * dt;

		_distance = Mathf.Lerp(_distance, _targetDistance, 1f - Mathf.Exp(-ZoomLerpSpeed * dt));
		UpdatePosition();
	}

	void UpdateClipping()
	{
		_camera.Near = _orbitRadius * 0.01f;
		_camera.Far = _orbitRadius * 10f;
	}

	void UpdatePosition()
	{
		float horizontal = _distance * Mathf.Sin(_polarAngle);
		var offset = new Vector3(horizontal * Mathf.Cos(_azimuth), _distance * Mathf.Cos(_polarAngle),
			horizontal * Mathf.Sin(_azimuth));
		_camera.Transform = new Transform3D(Basis.LookingAt(-offset, Vector3.Up), offset);
	}
}
