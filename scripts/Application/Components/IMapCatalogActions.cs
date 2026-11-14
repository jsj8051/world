namespace World.Application.Components;

/// <summary>地图目录组件之外的宿主能力：返回主界面与加载一张地图。</summary>
internal interface IMapCatalogActions
{
	void BackToMainMenu();
	void LoadMap(string path);
}
