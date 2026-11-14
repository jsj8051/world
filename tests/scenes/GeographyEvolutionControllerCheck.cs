using System;
using World.Application.Pages;
using World.Data;

namespace World.Tests;

/// <summary>地理演化页面控制器的纯逻辑检查：命令转发、状态通知、反馈与失败回退。</summary>
internal static class GeographyEvolutionControllerCheck
{
	sealed class Control : IGeographyEvolutionControl
	{
		public EvolutionControlStatus Status { get; set; } = EvolutionControlStatus.Paused;
		public bool Busy { get; set; }
		public int Plays, Pauses, Steps, Recoveries;
		public double SeekTarget, Speed;
		public string SeekMessage;
		public bool ThrowOnSeek, ThrowOnSpeed;

		public void Play() => Plays++;
		public void Pause() => Pauses++;
		public void Step() => Steps++;
		public void ConfirmFault() => Recoveries++;

		public string Seek(double targetMa)
		{
			SeekTarget = targetMa;
			if (ThrowOnSeek) throw new InvalidOperationException("seek failed");
			return SeekMessage;
		}

		public void SetSpeed(double yearsPerSecond)
		{
			if (ThrowOnSpeed) throw new ArgumentOutOfRangeException(nameof(yearsPerSecond));
			Speed = yearsPerSecond;
		}
	}

	sealed class Actions : IGeographyEvolutionPageActions
	{
		public int Backs, MapSaves, ImageExports;
		public bool FailBack, FailSave, FailImage;

		public void BackToMainMenu()
		{
			if (FailBack) throw new InvalidOperationException("back failed");
			Backs++;
		}

		public void SaveMap()
		{
			if (FailSave) throw new InvalidOperationException("map failed");
			MapSaves++;
		}

		public void ExportMapImage()
		{
			if (FailImage) throw new InvalidOperationException("image failed");
			ImageExports++;
		}
	}

	internal static int Run()
	{
		int assertions = 0;
		void Require(bool condition, string message)
		{
			if (!condition) throw new InvalidOperationException("[GEOGRAPHY-CONTROLLER] " + message);
			assertions++;
		}

		var control = new Control();
		var actions = new Actions();
		var controller = new GeographyEvolutionController(control, actions);
		int changes = 0;
		controller.StateChanged += () => changes++;

		Require(controller.Status == EvolutionControlStatus.Paused && !controller.IsBusy
			&& controller.Feedback.Length == 0, "Initial state must mirror the control port.");
		controller.Refresh();
		Require(changes == 0, "Unchanged status must not notify the view.");
		control.Status = EvolutionControlStatus.Playing;
		controller.Refresh();
		Require(changes == 1 && controller.Status == EvolutionControlStatus.Playing,
			"Status change must notify once and be readable.");
		control.Busy = true;
		controller.Refresh();
		Require(changes == 2 && controller.IsBusy, "Busy change must notify once.");
		controller.Refresh();
		Require(changes == 2, "Repeated refresh without a change must stay silent.");

		controller.Play();
		controller.Pause();
		controller.Step();
		Require(control.Plays == 1 && control.Pauses == 1 && control.Steps == 1,
			"Play, pause and step must forward to the control port.");
		control.Status = EvolutionControlStatus.Faulted;
		controller.Recover();
		Require(control.Recoveries == 1, "Recover must forward the confirmation.");
		controller.Step();
		Require(control.Steps == 1 && control.Recoveries == 2,
			"Step in the faulted state must recover instead of starting another step.");

		control.Status = EvolutionControlStatus.Paused;
		control.SeekMessage = "只能向前跳转。";
		controller.Seek(10);
		Require(control.SeekTarget == 10 && controller.Feedback == "只能向前跳转。"
			&& !controller.FeedbackIsError, "Seek must forward the target and surface the port message.");
		control.SeekMessage = null;
		controller.Seek(12);
		Require(controller.Feedback.Length == 0, "A successful seek must clear the feedback.");
		control.ThrowOnSeek = true;
		controller.Seek(13);
		Require(controller.FeedbackIsError && controller.Feedback.Contains("seek failed"),
			"Seek failures must become error feedback.");

		controller.SetSpeed(123);
		Require(control.Speed == 123 && !controller.FeedbackIsError, "Speed must forward to the control port.");
		control.ThrowOnSpeed = true;
		controller.SetSpeed(-1);
		Require(controller.FeedbackIsError, "Rejected speed must become error feedback.");
		control.ThrowOnSpeed = false;

		controller.SaveMap();
		Require(actions.MapSaves == 1 && controller.Feedback.Length == 0,
			"Save must reach the page actions and clear old feedback.");
		actions.FailSave = true;
		controller.SaveMap();
		Require(controller.FeedbackIsError && controller.Feedback.Contains("地图保存失败"),
			"Save failures must become error feedback.");
		actions.FailSave = false;
		controller.ExportMapImage();
		Require(actions.ImageExports == 1, "Image export must reach the page actions.");
		actions.FailImage = true;
		controller.ExportMapImage();
		Require(controller.FeedbackIsError && controller.Feedback.Contains("地图图片导出失败"),
			"Image export failures must become error feedback.");
		actions.FailImage = false;

		controller.BackToMainMenu();
		Require(actions.Backs == 1, "Back must reach the page actions.");
		actions.FailBack = true;
		controller.BackToMainMenu();
		Require(controller.FeedbackIsError && controller.Feedback.Contains("back failed"),
			"Back failures must become error feedback.");
		controller.Play();
		Require(controller.Feedback.Length == 0 && !controller.FeedbackIsError,
			"A successful command must clear previous command feedback.");

		var standalone = new GeographyEvolutionController(control);
		standalone.SaveMap();
		Require(standalone.FeedbackIsError, "Standalone save must explain the missing host.");
		standalone.ExportMapImage();
		Require(standalone.FeedbackIsError, "Standalone image export must explain the missing host.");
		standalone.BackToMainMenu();
		Require(standalone.FeedbackIsError && actions.MapSaves == 1 && actions.Backs == 1,
			"Standalone commands must not reach the page actions.");

		try { _ = new GeographyEvolutionController(null); throw new InvalidOperationException("Expected null control rejection."); }
		catch (ArgumentNullException) { assertions++; }

		return assertions;
	}
}
