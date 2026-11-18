using System;
using System.Linq;
using World.Application.Civilization;
using World.Application.Components;
using World.Data.Civilization;

namespace World.Application.Pages;

/// <summary>文明配置页用例控制器：管理草稿、应用门禁和命名档案操作。</summary>
internal sealed class CivilizationConfigController
{
	readonly CivilizationConfigSession _session;
	string _status;
	bool _statusIsError;

	internal CivilizationConfigController(CivilizationConfigSession session)
	{
		_session = session ?? throw new ArgumentNullException(nameof(session));
		_status = session.LoadProblem;
		_statusIsError = !string.IsNullOrEmpty(_status);
	}

	internal event Action StateChanged;
	internal CivilizationConfig Draft => _session.Draft;
	internal CivilizationConfig Applied => _session.Applied;
	internal ProfileLibraryItem[] Profiles => _session.Profiles
		.Select(profile => new ProfileLibraryItem(profile.Id, profile.Name)).ToArray();
	internal Guid? SelectedProfileId => _session.SelectedProfileId;
	internal bool HasUnappliedChanges => _session.HasUnappliedChanges;
	internal string Status => _status ?? string.Empty;
	internal bool StatusIsError => _statusIsError;
	internal string ConfigState => HasUnappliedChanges ? "有未应用的修改" : "已应用";

	internal void EditSeed(int seed) => Run(() => _session.Edit(new CivilizationConfig { Seed = seed }));
	internal void RandomizeSeed() => EditSeed(CivilizationConfigStrategy.RandomSeed());
	internal void Apply() => Run(_session.Apply, "文明配置已应用，仅本次运行有效。");
	internal void ResetDraft() => Run(_session.ResetDraft, "已恢复默认，尚未应用。");

	internal ProfileSelectOutcome TrySelectProfile(Guid id)
	{
		if (HasUnappliedChanges) return ProfileSelectOutcome.ConfirmationRequired;
		return Run(() => _session.Select(id), "已载入配置，请应用后使用。")
			? ProfileSelectOutcome.Selected
			: ProfileSelectOutcome.Rejected;
	}

	internal void LoadProfile(Guid id) => Run(() => _session.Select(id), "已载入配置，请应用后使用。");
	internal void SaveProfile(string name, bool saveAsNew)
		=> Run(() => _session.Save(name, saveAsNew), "配置已长期保存，应用状态未改变。");

	internal void RenameSelectedProfile(string name)
	{
		if (_session.SelectedProfileId.HasValue)
			Run(() => _session.Rename(_session.SelectedProfileId.Value, name), "配置已重命名。");
	}

	internal void DeleteProfile(Guid id) => Run(() => _session.Delete(id), "命名配置已删除。");

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
			_status = error.Message;
			_statusIsError = true;
			StateChanged?.Invoke();
			return false;
		}
	}
}
