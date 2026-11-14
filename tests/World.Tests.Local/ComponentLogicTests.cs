using System;
using System.Collections.Generic;
using World.Application.Components;
using World.Data;
using World.Data.Generation;

namespace World.Tests;

/// <summary>组件控制器的纯逻辑测试：不启引擎、不碰 Godot 控件。</summary>
internal static class ComponentLogicTests
{
	sealed class FakeProfileHost : IProfileLibraryHost
	{
		internal readonly List<GenerationProfile> Items = new();
		internal Guid? Selected;
		internal bool Dirty;
		internal bool RejectSelection;
		internal int Loads, Saves, Renames, Deletes;
		internal string LastName;
		internal bool LastSaveAsNew;
		internal Guid? LastDeleted;

		public GenerationProfile[] Profiles => Items.ToArray();
		public Guid? SelectedProfileId => Selected;
		public bool HasUnappliedChanges => Dirty;
		internal bool AllowDraftSync = true;

		public bool TrySyncDraft() => AllowDraftSync;

		public ProfileSelectOutcome TrySelectProfile(Guid id)
		{
			if (RejectSelection) return ProfileSelectOutcome.Rejected;
			if (Dirty) return ProfileSelectOutcome.ConfirmationRequired;
			Selected = id;
			return ProfileSelectOutcome.Selected;
		}

		public void LoadProfile(Guid id)
		{
			Selected = id;
			Loads++;
		}

		public void SaveProfile(string name, bool saveAsNew)
		{
			Saves++;
			LastName = name;
			LastSaveAsNew = saveAsNew;
			var profile = new GenerationProfile { Id = Guid.NewGuid(), Name = name };
			Items.Add(profile);
			Selected = profile.Id;
		}

		public void RenameSelectedProfile(string name)
		{
			Renames++;
			LastName = name;
		}

		public void DeleteProfile(Guid id)
		{
			Deletes++;
			LastDeleted = id;
			Items.RemoveAll(profile => profile.Id == id);
			if (Selected == id) Selected = null;
		}
	}

	static GenerationProfile Profile(string name) => new() { Id = Guid.NewGuid(), Name = name };

	internal static void EditorFormRoundTrip()
	{
		var controller = new GenerationConfigEditorController();
		var config = new GenerationConfig
		{
			Seed = -7, PlateCount = 12, Layout = ContinentLayout.Archipelago,
			SeaLevel = SeaLevelPreset.High, Relief = TerrainReliefPreset.Rugged,
			Temperature = TemperaturePreset.Cold, Seasonality = SeasonalityPreset.Strong,
			Rainfall = RainfallPreset.Dry,
		};
		GenerationConfig copy = controller.Read(controller.Write(config));
		Program.True(copy.Seed == -7 && copy.PlateCount == 12 && copy.Layout == ContinentLayout.Archipelago
			&& copy.SeaLevel == SeaLevelPreset.High && copy.Relief == TerrainReliefPreset.Rugged
			&& copy.Temperature == TemperaturePreset.Cold && copy.Seasonality == SeasonalityPreset.Strong
			&& copy.Rainfall == RainfallPreset.Dry, "表单往返必须保留全部配置字段");
	}

	internal static void EditorRejectsInvalidSelections()
	{
		var controller = new GenerationConfigEditorController();
		GenerationConfigFormState valid = controller.Write(new GenerationConfig());
		Program.Throws(() => controller.Read(valid with { Layout = 99 }), "非法布局必须被拒绝");
		Program.Throws(() => controller.Read(valid with { PlateCount = 99 }), "越界板块数量必须被拒绝");
	}

	internal static void EditorSuppressesNotificationsWhileLoading()
	{
		var controller = new GenerationConfigEditorController();
		int changes = 0;
		controller.Changed += () => changes++;
		controller.NotifyUserEdit();
		Program.True(changes == 1, "用户改动必须发通知");
		controller.BeginLoad();
		controller.NotifyUserEdit();
		Program.True(changes == 1, "回填控件期间不得发通知");
		controller.EndLoad();
		controller.NotifyUserEdit();
		Program.True(changes == 2, "回填结束后必须恢复通知");
	}

	internal static void EditorRandomSeedIsSeeded()
	{
		int first = GenerationConfigEditorController.RandomSeed(new Random(7));
		int second = GenerationConfigEditorController.RandomSeed(new Random(7));
		Program.True(first == second, "相同种子的随机种子必须一致");
	}

	internal static void ProfileSelectionConfirmationFlow()
	{
		var first = Profile("First");
		var second = Profile("Second");
		var host = new FakeProfileHost();
		host.Items.Add(first);
		host.Items.Add(second);
		host.Selected = first.Id;
		var controller = new ProfileLibraryController(host);
		Program.True(controller.SelectedIndex == 0 && controller.HasSelection, "选中项必须映射到列表下标");

		controller.Select(1);
		Program.True(host.Selected == second.Id && controller.Confirmation == null, "草稿干净时必须直接载入");

		host.Selected = first.Id;
		host.Dirty = true;
		controller.Select(1);
		Program.True(controller.Confirmation?.Kind == ProfileConfirmationKind.Load && host.Selected == first.Id,
			"草稿有修改时必须先确认且不改变选中项");
		controller.CancelConfirm();
		Program.True(controller.Confirmation == null && host.Selected == first.Id, "取消确认必须保留原选中");

		controller.Select(1);
		controller.Confirm();
		Program.True(host.Loads == 1 && host.Selected == second.Id && controller.Confirmation == null,
			"确认后必须载入最初请求的配置");
	}

	internal static void ProfileNameDialogRoutesOperations()
	{
		var first = Profile("First");
		var host = new FakeProfileHost();
		host.Items.Add(first);
		host.Selected = first.Id;
		var controller = new ProfileLibraryController(host);

		controller.RequestSave();
		Program.True(controller.NameDialog?.Title == "保存配置" && controller.NameDialog?.InitialName == "First",
			"保存对话框必须预填选中配置名");
		controller.SubmitName("Copy");
		Program.True(host.Saves == 1 && host.LastName == "Copy" && !host.LastSaveAsNew, "保存必须按覆盖提交");

		controller.RequestSaveAs();
		Program.True(controller.NameDialog?.Title == "另存配置", "另存对话框标题必须正确");
		controller.SubmitName("Copy2");
		Program.True(host.Saves == 2 && host.LastSaveAsNew, "另存必须以新档案提交");

		controller.RequestRename();
		Program.True(controller.NameDialog?.Title == "重命名配置", "重命名对话框标题必须正确");
		controller.SubmitName("Renamed");
		Program.True(host.Renames == 1 && host.LastName == "Renamed", "重命名必须交给宿主");

		controller.RequestSave();
		controller.CancelName();
		Program.True(controller.NameDialog == null && host.Saves == 2, "取消命名对话框不得提交");

		host.AllowDraftSync = false;
		controller.RequestSave();
		Program.True(controller.NameDialog == null && host.Saves == 2, "草稿非法时不得打开命名对话框");
	}

	internal static void ProfileDeleteConfirmationFlow()
	{
		var profile = Profile("Doomed");
		var host = new FakeProfileHost();
		host.Items.Add(profile);
		host.Selected = profile.Id;
		var controller = new ProfileLibraryController(host);

		controller.RequestDelete();
		Program.True(controller.Confirmation?.Kind == ProfileConfirmationKind.Delete, "删除必须先确认");
		controller.Confirm();
		Program.True(host.Deletes == 1 && host.LastDeleted == profile.Id && controller.Confirmation == null,
			"确认后必须删除选中的配置");

		controller.RequestDelete();
		Program.True(controller.Confirmation == null, "无选中项不得删除");
	}

	internal static void EvolutionPanelGating()
	{
		EvolutionPanelControls paused = EvolutionPanelController.Describe(EvolutionControlStatus.Paused, false);
		Program.True(paused.PlayEnabled && !paused.PauseEnabled && paused.StepEnabled && paused.StepText == "单步"
			&& paused.JumpEnabled && paused.SeekEditable && paused.StatusText == "暂停" && !paused.StatusIsError,
			"暂停态必须可播放、可单步、可跳转");

		EvolutionPanelControls playing = EvolutionPanelController.Describe(EvolutionControlStatus.Playing, false);
		Program.True(!playing.PlayEnabled && playing.PauseEnabled && !playing.StepEnabled && playing.JumpEnabled,
			"播放态必须只能暂停或跳转");

		EvolutionPanelControls faulted = EvolutionPanelController.Describe(EvolutionControlStatus.Faulted, false);
		Program.True(!faulted.PlayEnabled && !faulted.PauseEnabled && faulted.StepEnabled && faulted.StepText == "恢复"
			&& !faulted.JumpEnabled && faulted.StatusIsError, "故障态必须显示错误并只允许恢复");

		EvolutionPanelControls busy = EvolutionPanelController.Describe(EvolutionControlStatus.Playing, true);
		Program.True(!busy.PlayEnabled && busy.PauseEnabled && !busy.StepEnabled && !busy.JumpEnabled
			&& busy.StatusText.Contains("正在计算"), "计算中必须禁用会并发提交的操作");
	}

	internal static void EvolutionPanelParsingAndSpeeds()
	{
		Program.True(EvolutionPanelController.TryParseSeekTarget("10", out double whole) && whole == 10,
			"整数目标时间必须可解析");
		Program.True(EvolutionPanelController.TryParseSeekTarget("1.5", out double fraction) && fraction == 1.5,
			"小数目标时间必须可解析");
		Program.True(!EvolutionPanelController.TryParseSeekTarget("-1", out _), "负目标时间必须拒绝");
		Program.True(!EvolutionPanelController.TryParseSeekTarget("abc", out _), "非数字必须拒绝");
		Program.True(!EvolutionPanelController.TryParseSeekTarget("NaN", out _), "非有限值必须拒绝");

		Program.True(EvolutionPanelController.YearsPerSecond(0) == 100_000d
			&& EvolutionPanelController.YearsPerSecond(1) == 1_000_000d
			&& EvolutionPanelController.YearsPerSecond(2) == 10_000_000d
			&& EvolutionPanelController.YearsPerSecond(-1) == 100_000d
			&& EvolutionPanelController.YearsPerSecond(99) == 10_000_000d, "倍率下标必须映射到地质年/秒");
	}

	internal static void CellInfoCardFormatting()
	{
		var cell = new PickedCell
		{
			CellId = 0x2A, LatDeg = -12.34f, LngDeg = 56.78f, ElevM = 123f,
			IsLand = true, LandFraction = 0.5f, DistToCoast = 3, DistToLand = 4,
		};
		CellInfoCardContent plain = CellInfoCardFormatter.Describe(cell, "丘陵", null);
		Program.True(plain.Summary.Contains("12.3°S") && plain.Summary.Contains("56.8°E")
			&& plain.Summary.Contains("+123 m") && plain.Summary.Contains("丘陵") && plain.Summary.Contains("陆地"),
			"摘要必须包含坐标、海拔档位与海陆");
		Program.True(!plain.ShowCrust && !plain.ShowBoundaryToggle && plain.Crust.Length == 0,
			"无地壳详情必须隐藏地壳段");

		CrustCellBoundary[] edges =
		{
			new(2, BoundaryKind.Convergent, ConvergenceKind.Subduction, BoundarySideRole.Overriding, -10, 5),
			new(3, BoundaryKind.Divergent, ConvergenceKind.None, BoundarySideRole.None, 4, 1),
		};
		var crust = new CrustCellDetails(1, CrustType.Continental, 12.5, 0.25, 30, 90, 2.5, 800, 120, edges);
		CellInfoCardContent detailed = CellInfoCardFormatter.Describe(cell, "丘陵", crust);
		Program.True(detailed.ShowCrust && detailed.ShowBoundaryToggle, "有边界时必须显示地壳段与边界开关");
		Program.True(detailed.BoundaryToggleText.Contains("2") && detailed.Boundaries.Contains("邻接板块 2")
			&& detailed.Boundaries.Contains("邻接板块 3"), "边界列表必须列出全部相邻边界");
		Program.True(detailed.Crust.Contains("表层板块 1") && detailed.Crust.Contains("陆壳")
			&& detailed.Crust.Contains("俯冲") && detailed.Crust.Contains("东（90°）"),
			"地壳段必须包含板块、壳型、运动方向与首条边界摘要");
	}
}
