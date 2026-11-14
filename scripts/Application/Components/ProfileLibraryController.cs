using System;
using World.Data.Generation;

namespace World.Application.Components;

internal enum ProfileNameOperation { Save, SaveAs, Rename }

/// <summary>命名对话框状态：标题与预填名称。</summary>
internal readonly record struct ProfileNameDialogState(string Title, string InitialName);

internal enum ProfileConfirmationKind { Load, Delete }

/// <summary>确认对话框状态：正文、确认按钮文字与确认后要执行的动作种类。</summary>
internal readonly record struct ProfileConfirmationState(string Text, string OkText, ProfileConfirmationKind Kind);

/// <summary>
/// 配置库组件的逻辑：列表选择、命名对话框与「载入/删除」确认流。
/// 只依赖 <see cref="IProfileLibraryHost"/>，不持有 Godot 节点；视图按状态重绘控件。
/// </summary>
internal sealed class ProfileLibraryController
{
	readonly IProfileLibraryHost _host;
	Guid? _pendingLoadId;
	Guid? _pendingDeleteId;
	ProfileNameOperation? _nameOperation;

	/// <summary>列表、选中项、按钮可用性或对话框状态发生变化。</summary>
	internal event Action StateChanged;

	internal ProfileLibraryController(IProfileLibraryHost host)
		=> _host = host ?? throw new ArgumentNullException(nameof(host));

	internal GenerationProfile[] Profiles => _host.Profiles ?? Array.Empty<GenerationProfile>();

	internal int SelectedIndex
	{
		get
		{
			Guid? selected = _host.SelectedProfileId;
			if (!selected.HasValue) return -1;
			GenerationProfile[] profiles = Profiles;
			for (int i = 0; i < profiles.Length; i++)
				if (profiles[i].Id == selected.Value) return i;
			return -1;
		}
	}

	internal bool HasSelection => _host.SelectedProfileId.HasValue;
	internal ProfileNameDialogState? NameDialog { get; private set; }
	internal ProfileConfirmationState? Confirmation { get; private set; }

	/// <summary>宿主侧列表或选中项可能已变化时，让视图重绘。</summary>
	internal void Refresh() => StateChanged?.Invoke();

	internal void Select(int index)
	{
		GenerationProfile[] profiles = Profiles;
		if (index < 0 || index >= profiles.Length) return;
		Guid id = profiles[index].Id;
		ProfileSelectOutcome outcome = _host.TrySelectProfile(id);
		if (outcome == ProfileSelectOutcome.ConfirmationRequired)
		{
			_pendingLoadId = id;
			Confirmation = new ProfileConfirmationState(
				"载入此配置会替换当前未应用的修改，是否继续？", "载入", ProfileConfirmationKind.Load);
		}
		StateChanged?.Invoke();
	}

	internal void RequestSave() => OpenNameDialog(ProfileNameOperation.Save);

	internal void RequestSaveAs() => OpenNameDialog(ProfileNameOperation.SaveAs);

	internal void RequestRename()
	{
		if (!HasSelection) return;
		OpenNameDialog(ProfileNameOperation.Rename);
	}

	internal void SubmitName(string name)
	{
		ProfileNameOperation? operation = _nameOperation;
		if (!operation.HasValue) return;
		_nameOperation = null;
		NameDialog = null;
		switch (operation.Value)
		{
			case ProfileNameOperation.Rename: _host.RenameSelectedProfile(name); break;
			case ProfileNameOperation.SaveAs: _host.SaveProfile(name, true); break;
			default: _host.SaveProfile(name, false); break;
		}
		StateChanged?.Invoke();
	}

	internal void CancelName()
	{
		if (!_nameOperation.HasValue && NameDialog == null) return;
		_nameOperation = null;
		NameDialog = null;
		StateChanged?.Invoke();
	}

	internal void RequestDelete()
	{
		Guid? id = _host.SelectedProfileId;
		if (!id.HasValue) return;
		_pendingDeleteId = id;
		Confirmation = new ProfileConfirmationState(
			"删除选中的命名配置？当前编辑值和已应用配置不会被删除。", "删除", ProfileConfirmationKind.Delete);
		StateChanged?.Invoke();
	}

	internal void Confirm()
	{
		ProfileConfirmationState? confirmation = Confirmation;
		Guid? loadId = _pendingLoadId;
		Guid? deleteId = _pendingDeleteId;
		_pendingLoadId = null;
		_pendingDeleteId = null;
		Confirmation = null;
		if (confirmation?.Kind == ProfileConfirmationKind.Load && loadId.HasValue) _host.LoadProfile(loadId.Value);
		else if (deleteId.HasValue) _host.DeleteProfile(deleteId.Value);
		StateChanged?.Invoke();
	}

	internal void CancelConfirm()
	{
		_pendingLoadId = null;
		_pendingDeleteId = null;
		Confirmation = null;
		StateChanged?.Invoke();
	}

	void OpenNameDialog(ProfileNameOperation operation)
	{
		if (!_host.TrySyncDraft()) return;
		if (operation == ProfileNameOperation.Rename && !HasSelection) return;
		_nameOperation = operation;
		string title = operation switch
		{
			ProfileNameOperation.Rename => "重命名配置",
			ProfileNameOperation.SaveAs => "另存配置",
			_ => "保存配置",
		};
		NameDialog = new ProfileNameDialogState(title, SelectedProfileName());
		StateChanged?.Invoke();
	}

	string SelectedProfileName()
	{
		Guid? selected = _host.SelectedProfileId;
		if (!selected.HasValue) return string.Empty;
		foreach (GenerationProfile profile in Profiles)
			if (profile.Id == selected.Value) return profile.Name;
		return string.Empty;
	}
}
