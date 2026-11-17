using System;
using World.Application.Components;

namespace World.Tests;

internal static class LoadProgressControllerTests
{
	public static void InitAndUpdateTrackCurrentStep()
	{
		var controller = new LoadProgressController();
		Program.True(!controller.IsActive, "初始状态不应在显示。");

		controller.Init(7, "装配地图");
		Program.True(controller.IsActive && controller.Total == 7 && controller.Step == 0
			&& controller.Title == "装配地图" && controller.Stage.Length > 0, "开始后的读数不正确。");

		controller.Update(3, "构建地形块");
		Program.True(controller.Step == 3 && controller.Stage == "构建地形块", "上报后的读数不正确。");
	}

	public static void UpdateClampsStepToTotal()
	{
		var controller = new LoadProgressController();
		controller.Init(7, "装配");

		controller.Update(9, "超额");
		Program.True(controller.Step == 7, "当前项没有夹到总数。");

		controller.Update(-1, "回退");
		Program.True(controller.Step == 0, "负序号没有夹到 0。");
	}

	public static void CompleteFillsAndStops()
	{
		var controller = new LoadProgressController();
		controller.Init(7, "装配");
		controller.Update(3, "构建");
		controller.Complete();
		Program.True(!controller.IsActive && controller.Step == 7, "完成后没有置满并结束。");

		controller.Update(5, "迟到");
		Program.True(controller.Step == 7 && !controller.IsActive, "结束后仍接受了上报。");
	}

	public static void RejectsInvalidTotal()
	{
		var controller = new LoadProgressController();
		Program.Throws(() => controller.Init(0, "装配"), "零总数被接受。");
		Program.Throws(() => controller.Init(-1, "装配"), "负总数被接受。");

		controller.Update(1, "未开始");
		Program.True(!controller.IsActive, "未开始时不应显示。");
	}
}
