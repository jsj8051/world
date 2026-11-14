using World.Data.Maps;

namespace World.Application.Components;

/// <summary>地图库列表项：路径、显示文字、探测状态与详情行。</summary>
internal readonly record struct MapCatalogEntry(
	string FilePath, string Title, string DisplayName, MapProbeStatus Status, string Detail);
