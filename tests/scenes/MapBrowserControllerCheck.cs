using System;
using World.Application.Pages;

namespace World.Tests;

/// <summary>地图浏览控制器的纯逻辑检查：页面信息只读暴露与返回地图库意图。</summary>
internal static class MapBrowserControllerCheck
{
	sealed class Actions : IMapBrowserPageActions
	{
		public int Backs;
		public void BackToMapLibrary() => Backs++;
	}

	internal static int Run()
	{
		int assertions = 0;
		void Require(bool condition, string message)
		{
			if (!condition) throw new InvalidOperationException("[MAP-BROWSER-CONTROLLER] " + message);
			assertions++;
		}

		var actions = new Actions();
		var controller = new MapBrowserController(new MapBrowserPageInfo("样例地图", 1234, true), actions);
		Require(controller.Title == "样例地图" && controller.CellCount == 1234 && controller.HasMap,
			"Page info must be exposed unchanged.");
		controller.BackToMapLibrary();
		Require(actions.Backs == 1, "Back must reach the map-library host action.");

		var empty = new MapBrowserController(new MapBrowserPageInfo("没有地图数据", 0, false));
		Require(!empty.HasMap && empty.CellCount == 0, "A missing map must remain an explicit empty state.");
		empty.BackToMapLibrary();
		Require(empty.Title == "没有地图数据", "Back without a host must be a no-op without altering the page info.");

		return assertions;
	}
}
