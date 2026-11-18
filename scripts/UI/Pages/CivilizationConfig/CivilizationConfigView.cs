using System;
using Godot;
using World.Application.Civilization;
using World.Application.Components;
using World.Application.Pages;
using World.Infrastructure;
using World.Infrastructure.Civilization;
using World.UI.Components;
using World.Utils;

namespace World.UI.Pages;

/// <summary>文明配置页：装配文明种子编辑器和共享命名配置库组件。</summary>
public partial class CivilizationConfigView : Control
{
	CivilizationConfigController _controller;
	ProfileLibraryController _profiles;
	ProfileLibraryPanel _profileLibrary;
	SpinBox _seed;
	Label _state;
	Label _status;
	bool _showing;

	internal static CivilizationConfigView Create()
		=> GD.Load<PackedScene>("res://scenes/pages/CivilizationConfig/CivilizationConfigPage.tscn")
			.Instantiate<CivilizationConfigView>();

	public override void _Ready()
	{
		var editor = GetNode<TabContainer>("%Editor");
		editor.SetTabTitle(0, "基础");
		_seed = editor.GetNode<SpinBox>("%Seed");
		_state = GetNode<Label>("%ConfigState");
		_status = GetNode<Label>("%Status");
		_profileLibrary = GetNode<ProfileLibraryPanel>("%ProfileLibrary");
		_controller = new CivilizationConfigController(new CivilizationConfigSession(
			new FileCivilizationProfileStore(), GamePaths.CivilizationProfilesPath, GamePaths.GameDirectory));
		_controller.StateChanged += Refresh;
		_profiles = new ProfileLibraryController(new ProfileLibraryHost(this, _controller));
		_profileLibrary.Bind(_profiles);
		_seed.ValueChanged += OnSeedChanged;
		editor.GetNode<Button>("%RandomSeed").Pressed += RandomizeSeed;
		GetNode<Button>("%Apply").Pressed += _controller.Apply;
		GetNode<Button>("%Reset").Pressed += ResetDraft;
		GetNode<Button>("%Save").Pressed += _profiles.RequestSave;
		GetNode<Button>("%SaveAs").Pressed += _profiles.RequestSaveAs;
		GetNode<Button>("%Back").Pressed += BackToMainMenu;
		Refresh();
	}

	void OnSeedChanged(double value)
	{
		if (!_showing) _controller.EditSeed((int)value);
	}

	void RandomizeSeed()
	{
		_controller.RandomizeSeed();
	}

	void ResetDraft()
	{
		_controller.ResetDraft();
		_profiles.Refresh();
	}

	bool UpdateDraft()
	{
		_controller.EditSeed((int)_seed.Value);
		return true;
	}

	void ShowDraft()
	{
		_showing = true;
		try { _seed.Value = _controller.Draft.Seed; }
		finally { _showing = false; }
	}

	void Refresh()
	{
		if (_controller == null || _state == null) return;
		ShowDraft();
		_state.Text = _controller.ConfigState;
		_status.Text = _controller.Status;
		_status.Modulate = _controller.StatusIsError ? new Color(0.62f, 0.09f, 0.07f) : Colors.White;
		GetNode<Button>("%Apply").Disabled = !_controller.HasUnappliedChanges;
		_profiles?.Refresh();
	}

	void BackToMainMenu() => SceneReplacement.Replace(this, MainMenuView.Create());

	public override void _ExitTree()
	{
		if (_controller != null) _controller.StateChanged -= Refresh;
	}

	sealed class ProfileLibraryHost : IProfileLibraryHost
	{
		readonly CivilizationConfigView _view;
		readonly CivilizationConfigController _controller;

		internal ProfileLibraryHost(CivilizationConfigView view, CivilizationConfigController controller)
		{
			_view = view ?? throw new ArgumentNullException(nameof(view));
			_controller = controller ?? throw new ArgumentNullException(nameof(controller));
		}

		public ProfileLibraryItem[] Profiles => _controller.Profiles;
		public Guid? SelectedProfileId => _controller.SelectedProfileId;
		public bool HasUnappliedChanges => _controller.HasUnappliedChanges;
		public bool TrySyncDraft() => _view.UpdateDraft();

		public ProfileSelectOutcome TrySelectProfile(Guid id)
		{
			if (!_view.UpdateDraft()) return ProfileSelectOutcome.Rejected;
			ProfileSelectOutcome outcome = _controller.TrySelectProfile(id);
			if (outcome == ProfileSelectOutcome.Selected) _view.ShowDraft();
			return outcome;
		}

		public void LoadProfile(Guid id)
		{
			_controller.LoadProfile(id);
			_view.ShowDraft();
		}

		public void SaveProfile(string name, bool saveAsNew) => _controller.SaveProfile(name, saveAsNew);
		public void RenameSelectedProfile(string name) => _controller.RenameSelectedProfile(name);
		public void DeleteProfile(Guid id) => _controller.DeleteProfile(id);
	}
}
