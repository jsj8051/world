using System;
using System.Collections.Generic;
using Godot;
using World.Logic;
using World.Utils;

namespace World.Render;

/// <summary>Coordinates an owned world view, orbital camera and selection without UI or export concerns.</summary>
public partial class WorldPresentation : Node3D, ITickable
{
	[Export] public NodePath WorldViewPath { get; set; }
	[Export] public NodePath OrbitalCameraPath { get; set; }
	[Export] public NodePath SelectionPath { get; set; }

	WorldView _view;
	OrbitalCamera _camera;
	CellSelectionController _selection;
	IWorldFactsSource _source;
	bool _initialized;
	bool _buildComplete;
	bool _interactionEnabled = true;

	public MapModeController Modes { get; private set; }
	public CellSelectionController Selection => _selection ??= this.Require<CellSelectionController>(SelectionPath, nameof(SelectionPath));
	public bool SwapActive { get; private set; }

	public void Initialize(IWorldFactsSource source, List<MapMode> modes, IEnumerable<IMapLayer> layers)
	{
		InitializeIncremental(source, modes, layers);
		while (!StepBuild(1000)) { }
	}

	public void InitializeIncremental(IWorldFactsSource source, List<MapMode> modes, IEnumerable<IMapLayer> layers)
	{
		if (_initialized) return;
		if (source == null) throw new ArgumentNullException(nameof(source));
		if (source.Facts == null) throw new InvalidOperationException("World facts are not available.");
		var controller = new MapModeController(modes);
		_view = this.Require<WorldView>(WorldViewPath, nameof(WorldViewPath));
		_camera = this.Require<OrbitalCamera>(OrbitalCameraPath, nameof(OrbitalCameraPath));
		_source = source;
		Modes = controller;
		if (layers != null)
			foreach (var layer in layers) _view.AddLayer(layer);
		_view.BeginBuild(source, Modes.Current);
		Modes.Changed += ApplyMode;
		Selection.Bind(_view);
		_camera.SetOrbitRadius(source.Facts.Ball.Radius);
		if (GetLandFocusDirection() is Vector3 direction) _camera.LookAtPoint(direction);
		_initialized = true;
		ApplyInteractionState();
	}

	public bool StepBuild(double budgetMs)
	{
		if (_view == null) return true;
		bool done = _view.StepBuild(budgetMs);
		if (done && !_buildComplete)
		{
			_buildComplete = true;
			Selection.RefreshSelection();
			ApplyInteractionState();
		}
		return done;
	}

	public float BuildProgress => _view?.BuildProgress ?? 1f;
	public int BuildTotal => _view?.BuildTotal ?? 0;
	public int BuildStep => _view?.BuildStep ?? 0;
	public string BuildStage => _view?.BuildStage ?? "完成";

	internal bool OwnsLayer(IMapLayer layer) => _view?.OwnsLayer(layer) == true;

	public void AddLayer(IMapLayer layer)
	{
		if (!_initialized) throw new InvalidOperationException("World presentation is not initialized.");
		_view.AddLayer(layer);
	}

	public void BeginSwap()
	{
		SwapActive = true;
		ApplyInteractionState();
	}

	public void EndSwap()
	{
		SwapActive = false;
		ApplyInteractionState();
	}

	/// <summary>Controls camera interaction and picking together; swap/build gates still apply.</summary>
	public void SetInteractionEnabled(bool enabled)
	{
		_interactionEnabled = enabled;
		ApplyInteractionState();
	}

	void ApplyInteractionState()
	{
		bool enabled = _interactionEnabled && _buildComplete && !SwapActive;
		Selection.InputEnabled = enabled;
		_camera ??= this.Require<OrbitalCamera>(OrbitalCameraPath, nameof(OrbitalCameraPath));
		_camera.SetProcess(enabled);
		_camera.SetProcessUnhandledInput(enabled);
	}

	void ApplyMode() => _view.SetMode(Modes.Current);

	public void RebakeView()
	{
		long timing = WorldBuildProfile.Start();
		_view?.Rebake();
		Selection.RefreshSelection();
		WorldBuildProfile.Mark(ref timing, "map-rebake");
	}

	public void Tick(double delta)
	{
		var camera = GetViewport()?.GetCamera3D();
		Selection.UpdateCamera(camera);
		_view?.UpdateVisibility(camera);
	}

	public Vector3? GetLandFocusDirection()
	{
		var facts = _source?.Facts;
		if (facts == null || facts.FinalLand.Length != facts.Ball.CellDirs.Length) return null;
		Vector3 sum = Vector3.Zero;
		for (int i = 0; i < facts.Ball.CellDirs.Length; i++)
			if (facts.FinalLand[i]) sum += facts.Ball.CellDirs[i];
		return sum.IsZeroApprox() ? null : sum.Normalized();
	}

	public override void _ExitTree()
	{
		if (Modes != null) Modes.Changed -= ApplyMode;
	}

	public override string[] _GetConfigurationWarnings()
	{
		var warnings = new List<string>();
		this.Check<WorldView>(WorldViewPath, nameof(WorldViewPath), warnings);
		this.Check<OrbitalCamera>(OrbitalCameraPath, nameof(OrbitalCameraPath), warnings);
		this.Check<CellSelectionController>(SelectionPath, nameof(SelectionPath), warnings);
		return warnings.ToArray();
	}
}
