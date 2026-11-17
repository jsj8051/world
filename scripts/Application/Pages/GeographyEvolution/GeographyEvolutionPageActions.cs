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
	readonly Action<string> _notify;

	internal GeographyEvolutionPageActions(Func<string> saveMap, Func<string> exportMapImage,
		IMainMenuReturn mainMenu, Action<string> notify)
	{
		_saveMap = saveMap ?? throw new ArgumentNullException(nameof(saveMap));
		_exportMapImage = exportMapImage ?? throw new ArgumentNullException(nameof(exportMapImage));
		_mainMenu = mainMenu;
		_notify = notify;
	}

	public void BackToMainMenu() => _mainMenu?.BackToMainMenu();

	public void SaveMap()
	{
		_notify?.Invoke("正在保存地图…");
		try
		{
			string path = _saveMap();
			_notify?.Invoke($"地图已保存：{path}");
		}
		catch (Exception error)
		{
			_notify?.Invoke($"地图保存失败：{error.Message}");
			throw;
		}
	}

	public void ExportMapImage()
	{
		_notify?.Invoke("正在导出地图图片…");
		try
		{
			string path = _exportMapImage();
			_notify?.Invoke($"地图图片已导出：{path}");
		}
		catch (Exception error)
		{
			_notify?.Invoke($"地图图片导出失败：{error.Message}");
			throw;
		}
	}
}
