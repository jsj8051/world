using System;
using System.Collections.Generic;
using System.IO;
using World.Application.Storage;
using World.Data.Maps;

namespace World.Application.Components;

/// <summary>
/// 地图目录组件的逻辑：加载存档目录、维护选择与状态文字，把加载/返回意图交给宿主。
/// 只依赖存档库端口与组件动作端口；不碰 Godot 节点、文件对话框或浏览流程。
/// </summary>
internal sealed class MapCatalogController
{
	readonly IGeographyMapRepository _repository;
	readonly IMapCatalogActions _actions;
	IReadOnlyList<MapCatalogEntry> _entries = Array.Empty<MapCatalogEntry>();
	int _selectedIndex = -1;
	string _status;
	bool _statusIsError;

	/// <summary>存档列表被重新读取（视图重建列表行）。</summary>
	internal event Action CatalogChanged;

	/// <summary>选择或状态文字变化（视图只刷新选中态、详情与按钮）。</summary>
	internal event Action StateChanged;

	internal MapCatalogController(IGeographyMapRepository repository, IMapCatalogActions actions = null)
	{
		_repository = repository ?? throw new ArgumentNullException(nameof(repository));
		_actions = actions;
	}

	internal IReadOnlyList<MapCatalogEntry> Entries => _entries;
	internal int SelectedIndex => _selectedIndex;
	internal bool CanLoad => TryGetSelected(out MapCatalogEntry entry)
		&& entry.Status == MapProbeStatus.HeaderReadable;
	internal string DetailText => TryGetSelected(out MapCatalogEntry entry)
		? $"{entry.Detail}\n{entry.FilePath}"
		: "从左侧选择一张已保存的地图。";
	internal string StatusText => _status ?? string.Empty;
	internal bool StatusVisible => !string.IsNullOrEmpty(_status);
	internal bool StatusIsError => _statusIsError;

	/// <summary>重新读取存档目录并探测每个候选包；读取失败转成页面状态文字。</summary>
	internal void Refresh()
	{
		try
		{
			var entries = new List<MapCatalogEntry>();
			foreach (MapPackageCandidate candidate in _repository.List() ?? Array.Empty<MapPackageCandidate>())
				entries.Add(CreateEntry(candidate));
			_entries = entries;
			if (_selectedIndex >= _entries.Count) _selectedIndex = -1;
			SetStatus(_entries.Count == 0 ? "地图库为空。请先在地理演化页保存地图。" : null);
		}
		catch (Exception error)
		{
			_entries = Array.Empty<MapCatalogEntry>();
			_selectedIndex = -1;
			SetStatus($"读取地图库失败：{error.Message}", true);
		}
		CatalogChanged?.Invoke();
		StateChanged?.Invoke();
	}

	internal void Select(int index)
	{
		int next = index >= 0 && index < _entries.Count ? index : -1;
		if (next == _selectedIndex) return;
		_selectedIndex = next;
		StateChanged?.Invoke();
	}

	internal void LoadSelected()
	{
		if (!TryGetSelected(out MapCatalogEntry entry)) return;
		if (entry.Status != MapProbeStatus.HeaderReadable) return;
		TryLoad(entry.FilePath);
	}

	internal void BackToMainMenu()
	{
		if (_actions == null) { SetStatus("独立运行无法返回主界面。", true); StateChanged?.Invoke(); return; }
		try { _actions.BackToMainMenu(); }
		catch (Exception error) { SetStatus(error.Message, true); StateChanged?.Invoke(); }
	}

	void TryLoad(string path)
	{
		if (_actions == null)
		{
			SetStatus("独立运行无法加载地图，请从主界面进入。", true);
			StateChanged?.Invoke();
			return;
		}
		SetStatus("正在加载地图…");
		StateChanged?.Invoke();
		try { _actions.LoadMap(path); }
		catch (Exception error)
		{
			SetStatus($"无法加载地图：{error.Message}", true);
			StateChanged?.Invoke();
		}
	}

	static MapCatalogEntry CreateEntry(MapPackageCandidate candidate)
	{
		MapProbeResult probe = candidate.Probe;
		string fileName = Path.GetFileName(candidate.FilePath);
		string title = string.IsNullOrWhiteSpace(probe?.Title)
			? Path.GetFileNameWithoutExtension(candidate.FilePath)
			: probe.Title;
		string detail = probe?.Status switch
		{
			MapProbeStatus.HeaderReadable => "可识别的地理地图",
			MapProbeStatus.Unsupported => $"不支持：{probe.Issue?.Message}",
			_ => $"损坏或缺失：{probe?.Issue?.Message}",
		};
		return new MapCatalogEntry(candidate.FilePath, title, $"{title}  [{fileName}]",
			probe?.Status ?? MapProbeStatus.Invalid, detail);
	}

	bool TryGetSelected(out MapCatalogEntry entry)
	{
		if (_selectedIndex >= 0 && _selectedIndex < _entries.Count)
		{
			entry = _entries[_selectedIndex];
			return true;
		}
		entry = default;
		return false;
	}

	void SetStatus(string message, bool isError = false)
	{
		_status = string.IsNullOrWhiteSpace(message) ? null : message;
		_statusIsError = _status != null && isError;
	}
}
