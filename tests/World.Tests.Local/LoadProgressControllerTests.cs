using System;
using World.Application.Components;

namespace World.Tests;

internal static class LoadProgressControllerTests
{
	public static void InitAndUpdateTrackCounts()
	{
		var controller = new LoadProgressController();
		Program.True(!controller.IsActive, "初始状态不应在显示。");

		controller.Init(7, "装配地图");
		Program.True(controller.IsActive && controller.Total == 7 && controller.Done == 0
			&& controller.Title == "装配地图" && controller.Stage.Length > 0, "开始后的读数不正确。");

		controller.Update(3, 7, "构建地形块");
		Program.True(controller.Done == 3 && controller.Stage == "构建地形块", "上报后的读数不正确。");
	}

	public static void UpdateClampsAndMayAdjustTotal()
	{
		var controller = new LoadProgressController();
		controller.Init(7, "装配");

		controller.Update(9, 7, "超额");
		Program.True(controller.Done == 7, "已完成数没有夹到总数。");

		controller.Update(-1, 7, "回退");
		Program.True(controller.Done == 0, "负已完成数没有夹到 0。");

		controller.Update(2, 10, "扩容");
		Program.True(controller.Done == 2 && controller.Total == 10, "总数不能随上报调整。");
	}

	public static void CompleteFillsAndStops()
	{
		var controller = new LoadProgressController();
		controller.Init(7, "装配");
		controller.Update(3, 7, "构建");
		controller.Complete();
		Program.True(!controller.IsActive && controller.Done == 7, "完成后没有置满并结束。");

		controller.Update(5, 7, "迟到");
		Program.True(controller.Done == 7 && !controller.IsActive, "结束后仍接受了上报。");
	}

	public static void RejectsInvalidTotal()
	{
		var controller = new LoadProgressController();
		Program.Throws(() => controller.Init(0, "装配"), "零总数被接受。");
		Program.Throws(() => controller.Init(-1, "装配"), "负总数被接受。");

		controller.Update(1, 7, "未开始");
		Program.True(!controller.IsActive, "未开始时不应显示。");
	}
}
