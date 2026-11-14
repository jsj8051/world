using System;

namespace World.Application.Pages;

/// <summary>
/// 地理演化页的宿主动作：把控制器的返回 / 保存 / 导出图片意图落到回主界面端口与两个副作用委托上。
/// 提示经宿主提示通道；失败继续抛给控制器，由页面反馈区显示原因。
/// </summary>
internal sealed class GeographyEvolutionPageActions : IGeographyEvolutionPageActions
{
	readonly Func<string> _saveMap;
	readonly Func<string> _exportMapImage;
	readonly IMainMenuReturn _mainMenu;
	readonly INoticeSink _notice;

	internal GeographyEvolutionPageActions(Func<string> saveMap, Func<string> exportMapImage,
		IMainMenuReturn mainMenu, INoticeSink notice)
	{
		_saveMap = saveMap ?? throw new ArgumentNullException(nameof(saveMap));
		_exportMapImage = exportMapImage ?? throw new ArgumentNullException(nameof(exportMapImage));
		_mainMenu = mainMenu;
		_notice = notice;
	}

	public void BackToMainMenu() => _mainMenu?.BackToMainMenu();

	public void SaveMap()
	{
		_notice?.Notify("正在保存地图…");
		try
		{
			string path = _saveMap();
			_notice?.Notify($"地图已保存：{path}", NoticeKind.Success);
		}
		catch (Exception error)
		{
			_notice?.Notify($"地图保存失败：{error.Message}", NoticeKind.Error);
			throw;
		}
	}

	public void ExportMapImage()
	{
		_notice?.Notify("正在导出地图图片…");
		try
		{
			string path = _exportMapImage();
			_notice?.Notify($"地图图片已导出：{path}", NoticeKind.Success);
		}
		catch (Exception error)
		{
			_notice?.Notify($"地图图片导出失败：{error.Message}", NoticeKind.Error);
			throw;
		}
	}
}
