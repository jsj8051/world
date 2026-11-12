using System;
using System.IO;
using Godot;
using World.Params.Generation;
using World.UI;

namespace World.Scene;

/// <summary>配置页面编排用户操作，通过会话访问配置，通过页面宿主请求生成。</summary>
public partial class MapGenerationPage : Control, IAppPage
{
	IPageNavigator _nav;
	IGenerationStarter _generation;
	GenerationConfigSession _session;
	GenerationConfigEditor _editor;
	ItemList _profiles;
	Label _status;
	Label _state;
	FileDialog _file;
	ConfirmationDialog _nameDialog;
	ConfirmationDialog _confirm;
	LineEdit _name;
	Guid[] _profileIds = Array.Empty<Guid>();
	string _nameOperation;
	string _exportJson;
	Action _confirmed;
	bool _starting;
	bool _reading;

	internal void SetSession(GenerationConfigSession session) => _session = session;
	void IAppPage.Configure(PageContext context)
	{
		_nav = context?.Navigation;
		_generation = context?.Generation;
	}
	void IAppPage.OnHidden() { _starting = true; }

	public override void _Ready()
	{
		_editor = GetNode<GenerationConfigEditor>("%Editor");
		_profiles = GetNode<ItemList>("%Profiles");
		_status = GetNode<Label>("%Status");
		_state = GetNode<Label>("%ConfigState");
		_file = GetNode<FileDialog>("%ConfigFileDialog");
		_nameDialog = GetNode<ConfirmationDialog>("%NameDialog");
		_confirm = GetNode<ConfirmationDialog>("%ConfirmDialog");
		_name = GetNode<LineEdit>("%ProfileName");
		if (_session == null)
		{
			SetStatus("配置页面需要从主界面启动。", true);
			GetNode<Control>("%Workspace").MouseFilter = MouseFilterEnum.Stop;
			foreach (var name in new[] { "Apply", "Save", "SaveAs", "Rename", "Delete", "Import", "Export", "Generate", "Reset" })
				GetNode<Button>("%" + name).Disabled = true;
			return;
		}
		_editor.Changed += ReadEditor;
		_profiles.ItemSelected += SelectProfile;
		Bind("Back", () => _nav?.Navigate(new MainMenuRequest()));
		Bind("Apply", () => Execute(() => { ReadEditor(); _session.Apply(); SetStatus("已应用，仅本次运行有效。"); RefreshState(); }));
		Bind("Reset", () => Execute(() => { _session.ResetDraft(); RefreshEditor(); SetStatus("已恢复默认，尚未应用。"); }));
		Bind("Save", () => OpenName("save"));
		Bind("SaveAs", () => OpenName("saveAs"));
		Bind("Rename", () => OpenName("rename"));
		Bind("Delete", DeleteSelected);
		Bind("Import", OpenImport);
		Bind("Export", OpenExport);
		Bind("Generate", Generate);
		_file.FileSelected += FileSelected;
		_nameDialog.Confirmed += SubmitName;
		_name.TextSubmitted += _ => { _nameDialog.Hide(); SubmitName(); };
		_confirm.Confirmed += () => { var action = _confirmed; _confirmed = null; action?.Invoke(); };
		_nameDialog.GetOkButton().Text = "确认";
		_confirm.GetOkButton().Text = "删除";
		RefreshEditor();
		RefreshProfiles();
		if (!string.IsNullOrEmpty(_session.LoadProblem)) SetStatus(_session.LoadProblem, true);
	}

	void Bind(string name, Action action) => GetNode<Button>("%" + name).Pressed += action;

	void ReadEditor()
	{
		if (_reading) return;
		_reading = true;
		try { _session.Edit(_editor.ReadConfig()); RefreshState(); }
		finally { _reading = false; }
	}

	void RefreshEditor()
	{
		_editor.ShowConfig(_session.Draft);
		RefreshState();
	}

	void RefreshState()
	{
		_state.Text = _session.HasUnappliedChanges ? "有未应用的修改" : "已应用";
		bool selected = _session.SelectedProfileId.HasValue;
		GetNode<Button>("%Rename").Disabled = !selected;
		GetNode<Button>("%Delete").Disabled = !selected;
	}

	void RefreshProfiles()
	{
		_profiles.Clear();
		var profiles = _session.Profiles;
		_profileIds = new Guid[profiles.Count];
		for (int i = 0; i < profiles.Count; i++)
		{
			_profileIds[i] = profiles[i].Id;
			_profiles.AddItem(profiles[i].Name);
			if (_session.SelectedProfileId == profiles[i].Id) _profiles.Select(i);
		}
		RefreshState();
	}

	void SelectProfile(long index) => Execute(() =>
	{
		ReadEditor();
		if (_session.HasUnappliedChanges)
		{
			_confirm.GetOkButton().Text = "载入";
			_confirm.DialogText = "载入此配置会替换当前未应用的修改，是否继续？";
			_confirmed = () => LoadProfile((int)index);
			_confirm.PopupCentered();
			RefreshProfiles();
		}
		else LoadProfile((int)index);
	});

	void LoadProfile(int index) => Execute(() =>
	{
		_session.Select(_profileIds[index]);
		RefreshEditor();
		RefreshProfiles();
		SetStatus("已载入配置，请应用后生成。");
	});

	void OpenName(string operation) => Execute(() =>
	{
		ReadEditor();
		if (operation == "rename" && !_session.SelectedProfileId.HasValue) return;
		_nameOperation = operation;
		_nameDialog.Title = operation == "rename" ? "重命名配置" : operation == "saveAs" ? "配置另存为" : "保存配置";
		_name.Text = "";
		foreach (var profile in _session.Profiles)
			if (profile.Id == _session.SelectedProfileId) _name.Text = profile.Name;
		_nameDialog.PopupCentered(new Vector2I(480, 180));
		_name.GrabFocus();
		_name.SelectAll();
	});

	void SubmitName() => Execute(() =>
	{
		if (_nameOperation == "rename") _session.Rename(_session.SelectedProfileId.Value, _name.Text);
		else _session.Save(_name.Text, _nameOperation == "saveAs");
		RefreshProfiles();
		SetStatus(_nameOperation == "rename" ? "配置已重命名。" : "配置已长期保存，应用状态未改变。");
	});

	void DeleteSelected()
	{
		if (!_session.SelectedProfileId.HasValue) return;
		Guid id = _session.SelectedProfileId.Value;
		_confirm.GetOkButton().Text = "删除";
		_confirm.DialogText = "删除选中的命名配置？当前编辑值和已应用配置不会被删除。";
		_confirmed = () => Execute(() => { _session.Delete(id); RefreshProfiles(); SetStatus("命名配置已删除。"); });
		_confirm.PopupCentered();
	}

	void OpenImport()
	{
		_exportJson = null;
		_file.Title = "导入配置";
		_file.FileMode = FileDialog.FileModeEnum.OpenFile;
		_file.PopupCentered(new Vector2I(900, 600));
	}

	void OpenExport() => Execute(() =>
	{
		ReadEditor();
		string name = "导出配置";
		foreach (var profile in _session.Profiles)
			if (profile.Id == _session.SelectedProfileId) name = profile.Name;
		_exportJson = _session.Export(name);
		_file.Title = "导出配置";
		_file.FileMode = FileDialog.FileModeEnum.SaveFile;
		_file.CurrentFile = "world-config.json";
		_file.PopupCentered(new Vector2I(900, 600));
	});

	void FileSelected(string path) => Execute(() =>
	{
		if (_exportJson != null)
		{
			AtomicTextFile.Write(path, _exportJson);
			SetStatus("配置文件已导出。");
		}
		else
		{
			if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("配置文件不能超过 1 MiB。");
			_session.Import(File.ReadAllText(path));
			RefreshEditor(); RefreshProfiles();
			SetStatus("配置已导入，尚未应用或长期保存。");
		}
	});

	async void Generate()
	{
		if (_starting) return;
		try
		{
			ReadEditor();
			var config = _session.GetAppliedForGeneration();
			if (_generation == null) throw new InvalidOperationException("请从主界面进入配置页面。");
			_starting = true;
			GetNode<Button>("%Generate").Disabled = true;
			SetStatus("正在生成地图…");
			await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			if (IsInsideTree()) _generation.StartGeography(config);
		}
		catch (Exception e)
		{
			_starting = false;
			if (IsInsideTree()) { GetNode<Button>("%Generate").Disabled = false; SetStatus(e.Message, true); }
		}
	}

	void Execute(Action action)
	{
		try { action(); }
		catch (Exception e) { SetStatus(e.Message, true); }
	}

	void SetStatus(string message, bool error = false)
	{
		_status.Text = message;
		_status.Modulate = error ? new Color(0.62f, 0.09f, 0.07f) : Colors.White;
	}
}
