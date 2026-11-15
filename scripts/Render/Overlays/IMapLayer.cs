using Godot;
using World.Logic;

namespace World.Render;

/// <summary>
/// Common map-layer contract. <see cref="SetContext"/> is the only entry that supplies data
/// (a layer reads what it needs from facts plus its own data source); each layer owns its
/// visibility and rendering lifecycle. <see cref="UpdateCamera"/> carries camera changes only.
/// </summary>
public interface IMapLayer
{
	void SetContext(WorldFacts facts, MapMode mode);
	void UpdateCamera(Camera3D camera);
}
