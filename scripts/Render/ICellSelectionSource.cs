using System;
using World.Data;

namespace World.Render;

/// <summary>地图在主线程发布的选中格数据。</summary>
public interface ICellSelectionSource
{
	/// <summary>点选或刷新同一格时发布当前数据，取消选中时发布 null。</summary>
	event Action<PickedCell?> CellPicked;
}
