using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using World.Data.Maps;
using World.Utils;

namespace World.Scene;

/// <summary>
/// 地图库：列出 user://maps 的地图包、选择外部文件、识别类型与版本，加载后进入只读浏览。
/// 列表信息只是缓存，每次加载都重新完整校验文件。
/// </summary>
public partial class MapLibrary : Control, IAppPage
{
	[Export] public NodePath ListPath { get; set; }
	[Export] public NodePath DetailPath { get; set; }
	[Export] public NodePath ImportButtonPath { get; set; }
	[Export] public NodePath OpenButtonPath { get; set; }
	[Export] public NodePath BackButtonPath { get; set; }
	[Export] public NodePath FileDialogPath { get; set; }
	[Export] public NodePath StatusPath { get; set; }

	sealed record Entry(string Path, string Title, MapProbeStatus Status, string Detail);

	readonly List<Entry> _entries = new();
	IPageHost _host;
	ItemList _list;
	Label _detail;
	Button _open;
	FileDialog _dialog;
	Label _status;

	void IAppPage.Configure(PageContext context) => _host = context?.Host;

	void IAppPage.OnHidden() { }

	public override void _Ready()
	{
		_list = this.Require<ItemList>(ListPath, nameof(ListPath));
		_detail = this.Require<Label>(DetailPath, nameof(DetailPath));
		var import = this.Require<Button>(ImportButtonPath, nameof(ImportButtonPath));
		_open = this.Require<Button>(OpenButtonPath, nameof(OpenButtonPath));
		var back = this.Require<Button>(BackButtonPath, nameof(BackButtonPath));
		_dialog = this.Require<FileDialog>(FileDialogPath, nameof(FileDialogPath));
		_status = this.Require<Label>(StatusPath, nameof(StatusPath));

		import.Pressed += () => _dialog.PopupCentered(new Vector2I(900, 600));
		back.Pressed += () => _host?.ShowMainMenu();
		_dialog.FileSelected += Import;
		_list.ItemSelected += _ => RefreshSelection();
		_open.Pressed += OpenSelected;

		Refresh();
	}

	string MapsDirectory()
	{
		string directory = ProjectSettings.GlobalizePath("user://maps");
		Directory.CreateDirectory(directory);
		return directory;
	}

	void Refresh()
	{
		_entries.Clear();
		_list.Clear();
		string directory = MapsDirectory();
		foreach (string path in Directory.EnumerateFiles(directory, "*.wmap"))
		{
			MapProbeResult probe = MapPackageProbe.Probe(path);
			string title = probe.Title ?? Path.GetFileNameWithoutExtension(path);
			string detail = probe.Status switch
			{
				MapProbeStatus.HeaderReadable => "可识别的地理地图",
				MapProbeStatus.Unsupported => $"不支持：{probe.Issue?.Message}",
				_ => $"损坏或缺失：{probe.Issue?.Message}",
			};
			_entries.Add(new Entry(path, title, probe.Status, detail));
			_list.AddItem($"{title}  [{Path.GetFileName(path)}]");
		}
		SetStatus(_entries.Count == 0 ? "地图库为空。可用「导入地图数据…」选择 .wmap 文件。" : null);
		RefreshSelection();
	}

	void RefreshSelection()
	{
		int[] selected = _list.GetSelectedItems();
		bool valid = selected.Length > 0 && selected[0] < _entries.Count;
		_open.Disabled = !valid || _entries[selected[0]].Status != MapProbeStatus.HeaderReadable;
		_detail.Text = valid
			? $"{_entries[selected[0]].Detail}\n{_entries[selected[0]].Path}"
			: "从左侧选择一张地图，或导入新的 .wmap 文件。";
	}

	void Import(string path)
	{
		MapProbeResult probe = MapPackageProbe.Probe(path);
		if (probe.Status != MapProbeStatus.HeaderReadable)
		{
			SetStatus($"{probe.Status}：{probe.Issue?.Message}");
			return;
		}
		TryOpen(path);
	}

	void OpenSelected()
	{
		int[] selected = _list.GetSelectedItems();
		if (selected.Length == 0 || selected[0] >= _entries.Count) return;
		TryOpen(_entries[selected[0]].Path);
	}

	void TryOpen(string path)
	{
		if (_host == null)
		{
			SetStatus("独立运行无法打开浏览页，请从主界面进入。");
			return;
		}
		SetStatus("正在加载地图…");
		_host.OpenMap(path);
	}

	void SetStatus(string text)
	{
		_status.Text = text ?? string.Empty;
		_status.Visible = !string.IsNullOrEmpty(text);
	}
}