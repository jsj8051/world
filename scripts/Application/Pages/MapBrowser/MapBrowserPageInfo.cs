namespace World.Application.Pages;

/// <summary>地图浏览页的只读展示信息：标题、格数与是否有可显示地图。</summary>
internal readonly record struct MapBrowserPageInfo(string Title, int CellCount, bool HasMap);
