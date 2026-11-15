using Godot;

namespace World.Render;

/// <summary>
/// 屏幕空间叠加层基类：缓存相机状态并做球面朝向剔除。
/// 2D 叠加没有深度测试 ⇒ 背面点必须先剔除，否则会透过球体画到正面。
/// 子类只管把各自的点/箭头投到屏幕上，不必重复"相机没变就跳过"的判据。
/// </summary>
internal abstract partial class ScreenSpaceOverlay : Node2D
{
	Transform3D _cameraTransform;
	Projection _cameraProjection;
	Vector2 _viewportSize;
	bool _dirty = true;

	/// <summary>最近一次更新时的视口尺寸（子类投影时复用，避免再次取视口）。</summary>
	protected Vector2 ViewportSize => _viewportSize;

	/// <summary>最近一次更新时的相机变换（需要相机位置做朝向判断）。</summary>
	protected Transform3D CameraTransform => _cameraTransform;

	/// <summary>数据或可见性变了：下次相机更新必须重算。</summary>
	protected void MarkDirty() => _dirty = true;

	/// <summary>
	/// 相机或视口没变则返回 false（子类直接跳过投影）；变了则更新缓存并返回 true。
	/// 相机为空时调用方应先自行清空并 <see cref="MarkDirty"/>。
	/// </summary>
	protected bool UpdateCameraState(Camera3D camera)
	{
		Vector2 size = camera.GetViewport().GetVisibleRect().Size;
		Transform3D transform = camera.GetCameraTransform();
		Projection projection = camera.GetCameraProjection();
		if (!_dirty && size == _viewportSize && transform == _cameraTransform
			&& projection.Equals(_cameraProjection)) return false;
		(_viewportSize, _cameraTransform, _cameraProjection) = (size, transform, projection);
		_dirty = false;
		return true;
	}

	/// <summary>
	/// 球面点是否朝向相机。<paramref name="minFacing"/> 是掠射下限：0 = 只剔除背面，
	/// 更大值会连同边缘一起剔除（斜看时投影会拉长、箭头方向失真）。
	/// </summary>
	protected static bool FacesCamera(Vector3 surface, Vector3 cameraPosition, float minFacing)
	{
		var toCamera = cameraPosition - surface;
		return surface.Normalized().Dot(toCamera) > minFacing * toCamera.Length();
	}
}
