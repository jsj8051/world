using System;
using System.Collections.Generic;
using Godot;
using World.Data;
using World.Render;

namespace World.Tests;

static class VillageLayerTests
{
	sealed class VillageSource : IMapLayerDataSource<IReadOnlyList<Village>>
	{
		readonly List<Village> _villages = new();
		public IReadOnlyList<Village> Current => _villages;
		public event Action Changed;
		public void Add(Village village)
		{
			_villages.Add(village);
			Changed?.Invoke();
		}
	}

	public static void StageAndModeGateVisibility()
	{
		foreach (WorldStage stage in Enum.GetValues<WorldStage>())
		foreach (var mode in WorldGenMapModes.CreateAll(null))
		{
			bool expected = stage != WorldStage.GeographyEvolution && mode is TerrainMode;
			if (VillageLayerController.IsApplicable(stage, mode, 1) != expected)
				throw new Exception($"Unexpected village visibility: {stage}, {mode.GetType().Name}");
			if (VillageLayerController.IsApplicable(stage, mode, 0))
				throw new Exception("Empty village layers must be hidden.");
		}
		if (VillageLayerController.IsApplicable(WorldStage.Playing, null, 1)
			|| VillageLayerController.IsApplicable((WorldStage)99, new TerrainMode(null), 1))
			throw new Exception("Unknown stage or mode must not enable villages.");
	}

	public static void SourcesExposeOwnerData()
	{
		ExpectArgument(() => new Village(0, 42));
		ExpectArgument(() => new Village(1, 0));
		var owner = new VillageSource();
		IMapLayerDataSource<IReadOnlyList<Village>> source = owner;
		var data = source.Current;
		int notifications = 0;
		Action onChanged = () => notifications++;
		source.Changed += onChanged;
		owner.Add(new Village(1, 42));
		if (!ReferenceEquals(data, source.Current) || data.Count != 1 || notifications != 1)
			throw new Exception("Reads must expose owner data, including in-place updates.");
		source.Changed -= onChanged;
		owner.Add(new Village(2, 43));
		if (notifications != 1) throw new Exception("Released subscriptions must remain silent.");
		foreach (var type in new[] { typeof(CurrentFlowOverlay), typeof(WindFlowOverlay),
			typeof(RiverLineOverlay), typeof(PlateMotionOverlay), typeof(VillageLayerController) })
			if (!typeof(IMapLayer).IsAssignableFrom(type))
				throw new Exception($"{type.Name} must implement the common layer contract.");
	}

	public static void BackHemisphereIsOccluded()
	{
		var camera = new Vector3(0, 0, 3);
		if (!VillageOverlay.IsSurfaceVisible(Vector3.Back, camera))
			throw new Exception("Near surface must be visible.");
		if (VillageOverlay.IsSurfaceVisible(Vector3.Forward, camera))
			throw new Exception("Far surface must be hidden.");
		if (VillageOverlay.IsSurfaceVisible(Vector3.Right, camera))
			throw new Exception("Occluded limb must be hidden.");
		if (VillageOverlay.IsSurfaceVisible(Vector3.Back, Vector3.Back))
			throw new Exception("Surface tangent must not be visible.");
	}

	public static void LayersExposeSingleUpdateEntry()
	{
		foreach (var type in new[] { typeof(CurrentFlowOverlay), typeof(WindFlowOverlay),
			typeof(RiverLineOverlay), typeof(PlateMotionOverlay), typeof(VillageLayerController) })
		{
			foreach (var method in type.GetMethods(System.Reflection.BindingFlags.Public
				| System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
			{
				if (method.Name is "Build" or "SetFacts")
					throw new Exception($"{type.Name} keeps a second public data entry: {method.Name}");
			}
		}
	}

	static void ExpectArgument(Action action)
	{
		try { action(); }
		catch (ArgumentException) { return; }
		throw new Exception("Expected invalid village input to be rejected.");
	}
}
