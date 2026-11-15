using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;
using World.H3Grid;
using World.Logic;

namespace World.Render;

/// <summary>在可见球面上采样板块切向速度，用固定屏幕尺寸的箭头显示方向。</summary>
internal sealed partial class PlateMotionOverlay : ScreenSpaceOverlay, IMapLayer
{
	public void SetContext(WorldFacts facts, MapMode mode)
		=> ApplyFacts(mode is CrustMode crust && crust.ShowMotion ? facts : null);

	const float SpacingPx = 72f;
	static readonly Color Ink = new(1f, 0.98f, 0.88f);
	static readonly Color Outline = new(0.07f, 0.10f, 0.15f);
	readonly List<Vector2> _strokes = new();
	WorldFacts _facts;

	internal int ArrowCount => _strokes.Count / 6;

	void ApplyFacts(WorldFacts facts)
	{
		if (ReferenceEquals(_facts, facts)) return;
		_facts = facts;
		Visible = facts != null;
		MarkDirty();
		_strokes.Clear();
		QueueRedraw();
	}

	public void UpdateCamera(Camera3D camera)
	{
		if (_facts == null) return;
		if (camera == null)
		{
			_strokes.Clear();
			MarkDirty();
			QueueRedraw();
			return;
		}

		if (!UpdateCameraState(camera)) return;

		_strokes.Clear();
		float radius = _facts.Ball.Radius;
		Vector3 cameraPosition = CameraTransform.Origin;
		for (float y = SpacingPx / 2; y < ViewportSize.Y; y += SpacingPx)
		{
			for (float x = SpacingPx / 2; x < ViewportSize.X; x += SpacingPx)
			{
				var center = new Vector2(x, y);
				if (!CellQuery.TrySurfaceDirection(radius, center, camera, out Vector3 normal)) continue;
				Vector3 surface = normal * radius;
				// 掠射角下限 0.22：斜看时投影拉长，箭头方向会失真 ⇒ 连同边缘一起剔除。
				if (!FacesCamera(surface, cameraPosition, 0.22f)) continue;
				int cell = BallGeoIndex.CellAt(_facts.Ball, normal);
				Vector3 tangent = CrustCellQuery.PlateTangent(_facts, _facts.PlateOfCell[cell], normal);
				if (tangent.LengthSquared() < 1e-16f) continue;
				Vector3 ahead = (normal + tangent.Normalized() * 0.001f).Normalized() * radius;
				Vector2 heading = camera.UnprojectPosition(ahead) - camera.UnprojectPosition(surface);
				if (heading.LengthSquared() > 1e-6f) AddArrow(center, heading.Normalized());
			}
		}
		QueueRedraw();
	}

	void AddArrow(Vector2 center, Vector2 forward)
	{
		var side = new Vector2(-forward.Y, forward.X);
		Vector2 tip = center + 11f * forward;
		Vector2 shoulders = tip - 7f * forward;
		_strokes.Add(center - 11f * forward);
		_strokes.Add(tip);
		_strokes.Add(shoulders + 4f * side);
		_strokes.Add(tip);
		_strokes.Add(shoulders - 4f * side);
		_strokes.Add(tip);
	}

	public override void _Draw()
	{
		if (_strokes.Count == 0) return;
		var points = CollectionsMarshal.AsSpan(_strokes);
		DrawMultiline(points, Outline, 4.5f, true);
		DrawMultiline(points, Ink, 2f, true);
	}
}
