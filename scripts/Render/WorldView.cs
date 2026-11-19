using System;
using System.Collections.Generic;
using Godot;
using World.Logic;

namespace World.Render;

/// <summary>Owns the world mesh and injected layers, synchronizing them with facts and map modes.</summary>
public partial class WorldView : Node3D
{
	IWorldFactsSource _source;
	BallView _ballView;
	readonly List<IMapLayer> _layers = new();
	MapMode _mode;
	bool _buildStarted;
	bool _layersReady;

	[ExportGroup("LOD 与剔除")]
	[Export] public float LodNearRatio { get; set; } = 6f;
	[Export] public float BackfaceCullRatio { get; set; } = 3.0f;

	public WorldPicker CreatePicker(Camera3D camera)
	{
		if (_ballView == null) throw new InvalidOperationException("World view has not begun building.");
		return new WorldPicker(_ballView, camera, _source);
	}

	public void Build(IWorldFactsSource source, MapMode initialMode)
	{
		BeginBuild(source, initialMode);
		while (!StepBuild(1000)) { }
	}

	public void BeginBuild(IWorldFactsSource source, MapMode initialMode)
	{
		if (_buildStarted) return;
		if (source == null) throw new ArgumentNullException(nameof(source));
		if (initialMode == null) throw new ArgumentNullException(nameof(initialMode));
		var facts = source.Facts ?? throw new InvalidOperationException("World facts are not available.");
		_source = source;
		_mode = initialMode;
		_ballView = new BallView(facts.Ball, facts.HeightM, initialMode, LodNearRatio, BackfaceCullRatio);
		AddChild(_ballView);
		_ballView.BeginBuild();
		_buildStarted = true;
	}

	public bool StepBuild(double budgetMs)
	{
		if (_ballView == null) return true;
		if (!_ballView.StepBuild(budgetMs)) return false;
		if (_layersReady) return true;
		// Context can build geometry; never supply it before the sphere is complete.
		_layersReady = true;
		SyncLayers();
		return true;
	}

	public float BuildProgress => _layersReady ? 1f : 0.95f * (_ballView?.BuildProgress ?? 0f);
	public int BuildTotal => _ballView == null ? 0 : _ballView.BuildTotal + 1;
	public int BuildStep => (_ballView?.BuildStep ?? 0) + (_layersReady ? 1 : 0);
	public string BuildStage => _layersReady ? "完成" : _ballView?.BuildStage ?? "画面装配";

	public void SetHighlight(ulong? cellId) => _ballView?.HighlightCell(cellId);

	public void Rebake()
	{
		if (_ballView == null || _source?.Facts == null) return;
		_ballView.SetElevationSource(_source.Facts.HeightM);
		_ballView.RefreshColors();
		SyncLayers();
	}

	public void RefreshColors()
	{
		_ballView?.RefreshColors();
		SyncLayers();
	}

	public void SetMode(MapMode mode)
	{
		if (mode == null) throw new ArgumentNullException(nameof(mode));
		_mode = mode;
		_ballView?.SetMode(mode);
		SyncLayers();
	}

	/// <summary>Registers a layer; node layers become direct children owned by this view.</summary>
	public void AddLayer(IMapLayer layer)
	{
		if (layer == null) throw new ArgumentNullException(nameof(layer));
		if (_layers.Contains(layer)) return;
		if (layer is Node node)
		{
			if (node.GetParent() != null && node.GetParent() != this)
				throw new ArgumentException("A layer node must be unparented or already owned by this view.", nameof(layer));
			if (node.GetParent() == null) AddChild(node);
		}
		_layers.Add(layer);
		if (_layersReady) layer.SetContext(_source.Facts, _mode);
	}

	internal bool OwnsLayer(IMapLayer layer) => _layers.Contains(layer);

	public override void _ExitTree()
	{
		foreach (var layer in _layers)
			if (layer is not Node && layer is IDisposable disposable) disposable.Dispose();
		_layers.Clear();
	}

	void SyncLayers()
	{
		if (!_layersReady) return;
		foreach (var layer in _layers) layer.SetContext(_source?.Facts, _mode);
	}

	public void UpdateVisibility(Camera3D camera)
	{
		_ballView?.UpdateVisibility(camera);
		if (!_layersReady) return;
		foreach (var layer in _layers) layer.UpdateCamera(camera);
	}
}
