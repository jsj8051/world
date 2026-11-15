using System;
using Godot;
using World.Application.Components;
using World.Application.Pages;
using World.Data.Generation;
using World.UI.Components;

namespace World.UI.Pages;

/// <summary>
/// 生成配置页宿主：装配配置编辑器、已保存配置组件与页面动作按钮；
/// 草稿、应用门禁与生成启动归 <see cref="GenerationConfigController"/>，
/// 配置库的选择/命名/确认流转给 <see cref="ProfileLibraryController"/>。
/// </summary>
public partial class GenerationConfigView : Control, IAppPage
{
	GenerationConfigController _controller;
	GenerationConfigEditor _editor;
	ProfileLibraryController _profiles;
	ProfileLibraryPanel _profileLibrary;
	Label _status;
	Label _configState;

	void IAppPage.Configure(PageContext context) { }

	void IAppPage.OnHidden() => DetachController();

	internal void SetController(GenerationConfigController controller)
	{
		_controller = controller ?? throw new ArgumentNullException(nameof(controller));
		_controller.StateChanged += RefreshFromController;
	}

	public override void _Ready()
	{
		_editor = GetNode<GenerationConfigEditor>("%Editor");
		_profileLibrary = GetNode<ProfileLibraryPanel>("%ProfileLibrary");
		_status = GetNode<Label>("%Status");
		_configState = GetNode<Label>("%ConfigState");

		if (_controller == null)
		{
			SetStandaloneState();
			return;
		}

		_profileLibrary.Bind(_profiles = new ProfileLibraryController(new ProfileLibraryHost(this, _controller)));
		_editor.Changed += () => UpdateDraft();
		Bind("Back", _controller.BackToMainMenu);
		Bind("Apply", Apply);
		Bind("Reset", ResetDraft);
		Bind("Save", () => _profiles.RequestSave());
		Bind("SaveAs", () => _profiles.RequestSaveAs());
		Bind("Generate", StartGeneration);
		RefreshEditorAndProfiles();
	}

	void SetStandaloneState()
	{
		SetStatus("配置页面需要从主界面启动。", true);
		GetNode<Control>("%Workspace").MouseFilter = MouseFilterEnum.Stop;
		foreach (string name in new[] { "Apply", "Save", "SaveAs", "Generate", "Reset" })
			GetNode<Button>("%" + name).Disabled = true;
		_profileLibrary.SetInteractive(false);
	}

	void Bind(string name, Action action) => GetNode<Button>("%" + name).Pressed += action;

	bool UpdateDraft()
	{
		try
		{
			return _controller.UpdateDraft(_editor.ReadConfig());
		}
		catch (Exception error)
		{
			_controller.ReportError(error);
			return false;
		}
	}

	void Apply()
	{
		if (UpdateDraft()) _controller.Apply();
	}

	void ResetDraft()
	{
		_controller.ResetDraft();
		_editor.ShowConfig(_controller.Draft);
		_profiles.Refresh();
		RefreshFromController();
	}

	async void StartGeneration()
	{
		if (!UpdateDraft() || !_controller.PrepareGeneration()) return;
		await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
		if (IsInsideTree()) _controller.CommitGeneration();
	}

	void RefreshFromController()
	{
		if (_controller == null || _status == null) return;
		_configState.Text = _controller.ConfigState;
		GetNode<Button>("%Generate").Disabled = _controller.IsStarting;
		SetStatus(_controller.Status, _controller.StatusIsError);
	}

	void RefreshEditorAndProfiles()
	{
		_editor.ShowConfig(_controller.Draft);
		_profiles.Refresh();
		RefreshFromController();
	}

	/// <summary>载入配置后把草稿回填到编辑器（由配置库宿主动作调用）。</summary>
	void SyncEditor() => _editor.ShowConfig(_controller.Draft);

	public override void _ExitTree() => DetachController();

	void DetachController()
	{
		if (_controller == null) return;
		_controller.StateChanged -= RefreshFromController;
		_controller.OnHidden();
	}

	void SetStatus(string message, bool error = false)
	{
		_status.Text = message;
		_status.Modulate = error ? new Color(0.62f, 0.09f, 0.07f) : Colors.White;
	}

	/// <summary>把配置库组件的能力接到页面控制器与编辑器回填上。</summary>
	sealed class ProfileLibraryHost : IProfileLibraryHost
	{
		readonly GenerationConfigView _view;
		readonly GenerationConfigController _controller;

		internal ProfileLibraryHost(GenerationConfigView view, GenerationConfigController controller)
		{
			_view = view ?? throw new ArgumentNullException(nameof(view));
			_controller = controller ?? throw new ArgumentNullException(nameof(controller));
		}

		public GenerationProfile[] Profiles => _controller.Profiles;
		public Guid? SelectedProfileId => _controller.SelectedProfileId;
		public bool HasUnappliedChanges => _controller.HasUnappliedChanges;

		public bool TrySyncDraft() => _view.UpdateDraft();

		public ProfileSelectOutcome TrySelectProfile(Guid id)
		{
			if (!_view.UpdateDraft()) return ProfileSelectOutcome.Rejected;
			ProfileSelectOutcome outcome = _controller.TrySelectProfile(id);
			if (outcome == ProfileSelectOutcome.Selected) _view.SyncEditor();
			return outcome;
		}

		public void LoadProfile(Guid id)
		{
			_controller.LoadProfile(id);
			_view.SyncEditor();
		}

		public void SaveProfile(string name, bool saveAsNew) => _controller.SaveProfile(name, saveAsNew);

		public void RenameSelectedProfile(string name) => _controller.RenameSelectedProfile(name);

		public void DeleteProfile(Guid id) => _controller.DeleteProfile(id);
	}
}
