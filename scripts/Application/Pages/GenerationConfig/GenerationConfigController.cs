using System;
using System.Linq;
using World.Application.Components;
using World.Application.Generation;
using World.Data.Generation;

namespace World.Application.Pages;

internal sealed class GenerationConfigController
{
	readonly GenerationConfigSession _session;
	readonly IGenerationConfigPageActions _pageActions;
	string _status;
	bool _statusIsError;
	GenerationConfig _pendingGeneration;
	bool _starting;
	bool _isVisible = true;

	internal event Action StateChanged;

	internal GenerationConfigController(GenerationConfigSession session,
		IGenerationConfigPageActions pageActions)
	{
		_session = session ?? throw new ArgumentNullException(nameof(session));
		_pageActions = pageActions;
		_status = session.LoadProblem;
		_statusIsError = !string.IsNullOrEmpty(_status);
	}

	internal GenerationConfig Draft => _session.Draft;
	internal GenerationProfile[] Profiles => _session.Profiles.ToArray();
	internal Guid? SelectedProfileId => _session.SelectedProfileId;
	internal bool HasUnappliedChanges => _session.HasUnappliedChanges;
	internal bool IsStarting => _starting;
	internal string Status => _status ?? string.Empty;
	internal bool StatusIsError => _statusIsError;
	internal string ConfigState => HasUnappliedChanges ? "有未应用的修改" : "已应用";

	internal bool UpdateDraft(GenerationConfig config)
	{
		try
		{
			_session.Edit(config);
			StateChanged?.Invoke();
			return true;
		}
		catch (Exception error)
		{
			SetError(error);
			return false;
		}
	}

	internal void ReportError(Exception error)
	{
		SetError(error ?? throw new ArgumentNullException(nameof(error)));
	}

	internal void Apply()
	{
		Run(_session.Apply, "已应用，仅本次运行有效。");
	}

	internal void ResetDraft()
	{
		Run(_session.ResetDraft, "已恢复默认，尚未应用。");
	}

	/// <summary>未应用的修改会阻止直接载入，由配置库组件弹确认；草稿非法时拒绝。</summary>
	internal ProfileSelectOutcome TrySelectProfile(Guid id)
	{
		if (HasUnappliedChanges) return ProfileSelectOutcome.ConfirmationRequired;
		return Run(() => _session.Select(id), "已载入配置，请应用后生成。")
			? ProfileSelectOutcome.Selected
			: ProfileSelectOutcome.Rejected;
	}

	/// <summary>已确认替换草稿后直接载入。</summary>
	internal void LoadProfile(Guid id)
		=> Run(() => _session.Select(id), "已载入配置，请应用后生成。");

	internal void SaveProfile(string name, bool saveAsNew)
	{
		Run(() => _session.Save(name, saveAsNew), "配置已长期保存，应用状态未改变。");
	}

	internal void RenameSelectedProfile(string name)
	{
		if (!_session.SelectedProfileId.HasValue) return;
		Run(() => _session.Rename(_session.SelectedProfileId.Value, name), "配置已重命名。");
	}

	internal void DeleteProfile(Guid id)
	{
		Run(() => _session.Delete(id), "命名配置已删除。");
	}

	internal bool PrepareGeneration()
	{
		if (!_isVisible || _starting) return false;
		try
		{
			if (_pageActions == null) throw new InvalidOperationException("请从主界面进入配置页面。");
			_pendingGeneration = _session.GetAppliedForGeneration();
			_starting = true;
			_status = "正在生成地图…";
			_statusIsError = false;
			StateChanged?.Invoke();
			return true;
		}
		catch (Exception error)
		{
			SetError(error);
			return false;
		}
	}

	internal void CommitGeneration()
	{
		if (!_isVisible || !_starting || _pendingGeneration == null) return;
		GenerationConfig config = _pendingGeneration;
		_pendingGeneration = null;
		try { _pageActions.StartGeneration(config); }
		catch (Exception error)
		{
			_starting = false;
			SetError(error);
		}
	}

	internal void BackToMainMenu()
	{
		_pageActions?.BackToMainMenu();
	}

	internal void OnHidden()
	{
		if (!_isVisible) return;
		_isVisible = false;
		_starting = true;
		_pendingGeneration = null;
		StateChanged?.Invoke();
	}

	bool Run(Action action, string successStatus = null)
	{
		try
		{
			action();
			_status = successStatus;
			_statusIsError = false;
			StateChanged?.Invoke();
			return true;
		}
		catch (Exception error)
		{
			SetError(error);
			return false;
		}
	}

	void SetError(Exception error)
	{
		_status = error.Message;
		_statusIsError = true;
		StateChanged?.Invoke();
	}
}
