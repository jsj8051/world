using System.Collections.Generic;
using Godot;
using World.Logic;

namespace World.Render;

/// <summary>Fixed-pixel village dots projected onto the visible globe surface.</summary>
internal sealed partial class VillageOverlay : ScreenSpaceOverlay
{
	const float DotRadiusPx = 3f;
	static readonly Color Fill = new(0.98f, 0.94f, 0.76f);
	static readonly Color Outline = new(0.12f, 0.15f, 0.12f);
	readonly List<Vector3> _surfacePoints = new();
	readonly List<Vector2> _screenPoints = new();

	public void SetPoints(WorldFacts facts, IReadOnlyList<World.Data.Village> villages)
	{
		if (facts == null) throw new System.ArgumentNullException(nameof(facts));
		if (villages == null) throw new System.ArgumentNullException(nameof(villages));
		_surfacePoints.Clear();
		_screenPoints.Clear();
		MarkDirty();
		var ball = facts.Ball;
		foreach (var village in villages)
		{
			int index = ball.CellIndexOf(village.CellId);
			_surfacePoints.Add(ball.CellDirs[index] * ball.Radius);
		}
		QueueRedraw();
	}

	/// <summary>球面点是否朝向相机（背面点不参与投影）。</summary>
	internal static bool IsSurfaceVisible(Vector3 surface, Vector3 cameraPosition)
		=> FacesCamera(surface, cameraPosition, 0f);

	public void UpdateCamera(Camera3D camera)
	{
		if (!Visible) return;
		if (camera == null)
		{
			_screenPoints.Clear();
			MarkDirty();
			QueueRedraw();
			return;
		}

		if (!UpdateCameraState(camera)) return;
		_screenPoints.Clear();
		var viewport = new Rect2(Vector2.Zero, ViewportSize).Grow(DotRadiusPx + 1f);
		foreach (var surface in _surfacePoints)
		{
			// A 2D overlay has no depth test: reject the far hemisphere before projection.
			if (!IsSurfaceVisible(surface, CameraTransform.Origin) || camera.IsPositionBehind(surface)) continue;
			Vector2 point = camera.UnprojectPosition(surface);
			if (viewport.HasPoint(point)) _screenPoints.Add(point);
		}
		QueueRedraw();
	}

	public override void _Draw()
	{
		foreach (var point in _screenPoints)
		{
			DrawCircle(point, DotRadiusPx + 1f, Outline, true, -1f, true);
			DrawCircle(point, DotRadiusPx, Fill, true, -1f, true);
		}
	}
}
