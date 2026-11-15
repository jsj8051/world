using System;
using World.Data;

namespace World.Application.Pages;

/// <summary>
/// 地理演化页面控制器：把演化面板与页面菜单的意图翻译成演化命令、导出动作和反馈文字。
/// 只依赖 <see cref="IGeographyEvolutionControl"/> 与 <see cref="IGeographyEvolutionPageActions"/>，
/// 不持有 Godot 节点；视图读取本类状态并负责绘制。
/// </summary>
internal sealed class GeographyEvolutionController
{
	readonly IGeographyEvolutionControl _control;
	readonly IGeographyEvolutionPageActions _actions;
	EvolutionControlStatus _observedStatus;
	bool _observedBusy;
	string _feedback;
	bool _feedbackIsError;

	/// <summary>状态或反馈发生变化（视图据此重绘，不含逐帧数据刷新）。</summary>
	internal event Action StateChanged;

	internal GeographyEvolutionController(IGeographyEvolutionControl control,
		IGeographyEvolutionPageActions actions = null)
	{
		_control = control ?? throw new ArgumentNullException(nameof(control));
		_actions = actions;
		_observedStatus = control.Status;
		_observedBusy = control.Busy;
	}

	internal EvolutionControlStatus Status => _control.Status;
	internal bool IsBusy => _control.Busy;
	internal string Feedback => _feedback ?? string.Empty;
	internal bool FeedbackIsError => _feedbackIsError;

	/// <summary>逐帧同步世界侧状态；只在状态切换时通知视图。</summary>
	internal void Refresh()
	{
		if (_control.Status == _observedStatus && _control.Busy == _observedBusy) return;
		RaiseState();
	}

	internal void Play() => Run(_control.Play);
	internal void Pause() => Run(_control.Pause);
	internal void Recover() => Run(_control.ConfirmFault);

	internal void Step()
	{
		if (Status == EvolutionControlStatus.Faulted) Recover();
		else Run(_control.Step);
	}

	internal void Seek(double targetMa)
	{
		try
		{
			SetFeedback(_control.Seek(targetMa), false);
		}
		catch (Exception error)
		{
			SetFeedback(error.Message, true);
		}
	}

	internal void SetSpeed(double yearsPerSecond)
	{
		try
		{
			_control.SetSpeed(yearsPerSecond);
			SetFeedback(null, false);
		}
		catch (Exception error)
		{
			SetFeedback(error.Message, true);
		}
	}

	internal void SaveMap()
	{
		if (_actions == null) { SetFeedback("独立运行无法保存地图。", true); return; }
		try
		{
			_actions.SaveMap();
			SetFeedback(null, false);
		}
		catch (Exception error)
		{
			SetFeedback($"地图保存失败：{error.Message}", true);
		}
	}

	internal void ExportMapImage()
	{
		if (_actions == null) { SetFeedback("独立运行无法导出地图图片。", true); return; }
		try
		{
			_actions.ExportMapImage();
			SetFeedback(null, false);
		}
		catch (Exception error)
		{
			SetFeedback($"地图图片导出失败：{error.Message}", true);
		}
	}

	internal void BackToMainMenu()
	{
		if (_actions == null) { SetFeedback("独立运行无法返回主界面。", true); return; }
		try
		{
			_actions.BackToMainMenu();
			SetFeedback(null, false);
		}
		catch (Exception error)
		{
			SetFeedback(error.Message, true);
		}
	}

	void Run(Action command)
	{
		try
		{
			command();
			SetFeedback(null, false);
		}
		catch (Exception error)
		{
			SetFeedback(error.Message, true);
		}
	}

	void SetFeedback(string message, bool isError)
	{
		string next = string.IsNullOrWhiteSpace(message) ? null : message;
		bool nextIsError = next != null && isError;
		if (next == _feedback && nextIsError == _feedbackIsError)
		{
			RaiseState();
			return;
		}
		_feedback = next;
		_feedbackIsError = nextIsError;
		RaiseState();
	}

	void RaiseState()
	{
		_observedStatus = _control.Status;
		_observedBusy = _control.Busy;
		StateChanged?.Invoke();
	}
}
