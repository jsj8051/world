using System;

namespace World.Render;

/// <summary>
/// Read-only access to owner-managed data, without copying it into layer snapshots.
/// Current must remain stable during a read. Changed is raised on the scene thread,
/// after data is updated, including mutations to an existing collection.
/// </summary>
public interface IMapLayerDataSource<out T>
{
	T Current { get; }
	event Action Changed;
}
