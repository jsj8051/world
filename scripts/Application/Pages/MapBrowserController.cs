namespace World.Application.Pages;

/// <summary>
/// 地图浏览页控制器：暴露只读页面信息并处理返回地图库的意图。
/// 渲染装配与分帧构建属于页面表现，留在视图。
/// </summary>
internal sealed class MapBrowserController
{
	readonly MapBrowserPageInfo _info;
	readonly IMapBrowserPageActions _actions;

	internal MapBrowserController(MapBrowserPageInfo info, IMapBrowserPageActions actions = null)
	{
		_info = info;
		_actions = actions;
	}

	internal string Title => _info.Title;
	internal int CellCount => _info.CellCount;
	internal bool HasMap => _info.HasMap;

	internal void BackToMapLibrary() => _actions?.BackToMapLibrary();
}
