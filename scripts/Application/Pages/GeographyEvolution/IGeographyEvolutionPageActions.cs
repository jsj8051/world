namespace World.Application.Pages;

/// <summary>
/// 地理演化页面之外的宿主能力：返回主界面与两种导出。
/// 实现方负责导航、落盘与用户提示；控制器只决定何时调用并处理失败。
/// </summary>
internal interface IGeographyEvolutionPageActions
{
	void BackToMainMenu();
	void SaveMap();
	void ExportMapImage();
}
