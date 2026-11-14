namespace World.Application.Pages;

/// <summary>请求宿主打开一张地图：起异步读包与装配流程，结果页面由宿主决定。</summary>
internal interface IMapOpener
{
	void OpenMap(string path);
}
