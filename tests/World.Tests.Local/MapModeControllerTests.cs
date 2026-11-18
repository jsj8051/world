using System;
using System.Collections.Generic;
using Godot;
using World.Render;

namespace World.Tests;

internal static class MapModeControllerTests
{
	sealed class FakeMapMode : MapMode
	{
		readonly IReadOnlyList<string> _parameters;
		readonly IReadOnlyList<string> _variants;
		int _parameter;
		int _variant;
		internal int ParameterCalls;
		internal int VariantCalls;

		internal FakeMapMode(IReadOnlyList<string> parameters = null,
			IReadOnlyList<string> variants = null, int parameter = 0, int variant = 0)
		{
			_parameters = parameters;
			_variants = variants;
			_parameter = parameter;
			_variant = variant;
		}

		public override string Name => "Test mode";
		public override Color CellColorAt(int i) => throw new NotSupportedException();
		public override IReadOnlyList<string> ParameterOptions => _parameters;
		public override IReadOnlyList<string> VariantOptions => _variants;
		public override int ParameterIndex => _parameter;
		public override int VariantIndex => _variant;

		public override void SetParameter(int index)
		{
			ParameterCalls++;
			_parameter = index;
		}

		public override void SetVariant(int index)
		{
			VariantCalls++;
			_variant = index;
		}
	}

	internal static void RejectsInvalidModeLists()
	{
		Program.Throws(() => new MapModeController(null), "Null mode lists must be rejected");
		Program.Throws(() => new MapModeController(Array.Empty<MapMode>()), "Empty mode lists must be rejected");
		Program.Throws(() => new MapModeController(new MapMode[] { null }), "Null modes must be rejected");
		Program.Throws(() => new MapModeController(new MapMode[] { new FakeMapMode(), null }),
			"Null modes after a valid entry must be rejected");
	}

	internal static void CopiesInputList()
	{
		var first = new FakeMapMode();
		var second = new FakeMapMode();
		var input = new List<MapMode> { first, second };
		var controller = new MapModeController(input);
		input[0] = second;
		input.Clear();
		Program.True(controller.Modes.Count == 2 && ReferenceEquals(controller.Modes[0], first)
			&& ReferenceEquals(controller.Modes[1], second), "Registration must copy the list and preserve mode identity and order");
		if (controller.Modes is IList<MapMode> writable)
			Program.Throws(() => writable[0] = second, "Registered modes must not expose a writable list");
		Program.True(ReferenceEquals(controller.Current, first), "Input mutation must not replace the current mode");
	}

	internal static void StartsWithFirstModeAndExistingParameters()
	{
		var first = new FakeMapMode(new[] { "Annual", "January" }, new[] { "Surface", "Upper" }, 1, 1);
		var controller = new MapModeController(new MapMode[] { first, new FakeMapMode() });
		Program.True(controller.CurrentIndex == 0 && ReferenceEquals(controller.Current, first),
			"The first registered mode must be current initially");
		Program.True(controller.Current.ParameterIndex == 1 && controller.Current.VariantIndex == 1
			&& first.ParameterCalls == 0 && first.VariantCalls == 0, "Construction must preserve existing mode settings without setters");
	}

	internal static void ModeSelectionHonorsBoundsAndNotifiesAfterChange()
	{
		var first = new FakeMapMode();
		var second = new FakeMapMode();
		var controller = new MapModeController(new MapMode[] { first, second });
		int changes = 0;
		controller.Changed += () =>
		{
			changes++;
			Program.True(controller.CurrentIndex == (changes == 1 ? 1 : 0)
				&& ReferenceEquals(controller.Current, changes == 1 ? second : first),
				"Changed subscribers must observe the updated current mode");
		};
		foreach (int index in new[] { int.MinValue, -1, 0, 2, int.MaxValue }) controller.SelectMode(index);
		Program.True(changes == 0 && ReferenceEquals(controller.Current, first), "Invalid or repeated selections must be silent");
		controller.SelectMode(1);
		Program.True(changes == 1 && ReferenceEquals(controller.Current, second), "The last valid mode must notify once");
		foreach (int index in new[] { -1, 1, 2 }) controller.SelectMode(index);
		Program.True(changes == 1 && controller.CurrentIndex == 1, "Invalid or repeated selections must retain the selected mode");
		controller.SelectMode(0);
		Program.True(changes == 2 && ReferenceEquals(controller.Current, first), "The first valid mode must be selectable again");
	}

	internal static void ParameterAndVariantSelectionsHonorBounds()
	{
		var mode = new FakeMapMode(new[] { "Annual", "January", "February" }, new[] { "Surface", "Upper", "Free" });
		var controller = new MapModeController(new MapMode[] { mode });
		var observed = new List<(int Parameter, int Variant)>();
		controller.Changed += () => observed.Add((mode.ParameterIndex, mode.VariantIndex));
		foreach (int index in new[] { int.MinValue, -1, 0, 3, int.MaxValue })
		{
			controller.SelectParameter(index);
			controller.SelectVariant(index);
		}
		Program.True(observed.Count == 0 && mode.ParameterCalls == 0 && mode.VariantCalls == 0,
			"Invalid or repeated axis selections must neither invoke setters nor notify");
		controller.SelectParameter(2);
		controller.SelectParameter(2);
		controller.SelectVariant(2);
		controller.SelectVariant(2);
		controller.SelectParameter(0);
		controller.SelectVariant(0);
		Program.True(mode.ParameterCalls == 2 && mode.VariantCalls == 2 && observed.Count == 4,
			"Valid first and last options must invoke each setter and notify exactly once");
		Program.True(observed[0] == (2, 0) && observed[1] == (2, 2)
			&& observed[2] == (0, 2) && observed[3] == (0, 0),
			"Notifications must observe updated settings while preserving the other axis");
	}

	internal static void MissingAndEmptyOptionsIgnoreSelections()
	{
		var modes = new[]
		{
			new FakeMapMode(),
			new FakeMapMode(Array.Empty<string>(), Array.Empty<string>()),
			new FakeMapMode(new[] { "Annual" }, null),
			new FakeMapMode(null, new[] { "Surface" }),
		};
		foreach (FakeMapMode mode in modes)
		{
			var controller = new MapModeController(new MapMode[] { mode });
			int changes = 0;
			controller.Changed += () => changes++;
			foreach (int index in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
			{
				controller.SelectParameter(index);
				controller.SelectVariant(index);
			}
			Program.True(changes == 0 && mode.ParameterCalls == 0 && mode.VariantCalls == 0
				&& mode.ParameterIndex == 0 && mode.VariantIndex == 0,
				"Absent, empty, or already-selected singleton options must be ignored");
		}
	}

	internal static void SwitchingModesRetainsIndependentSettings()
	{
		var first = new FakeMapMode(new[] { "Annual", "January", "February" }, new[] { "Surface", "Upper" });
		var second = new FakeMapMode(new[] { "Annual", "January" }, new[] { "Surface", "Upper", "Free" });
		var controller = new MapModeController(new MapMode[] { first, second });
		int changes = 0;
		controller.Changed += () => changes++;
		controller.SelectParameter(2);
		controller.SelectVariant(1);
		controller.SelectMode(1);
		Program.True(second.ParameterIndex == 0 && second.VariantIndex == 0,
			"Selecting another mode must not copy the previous mode's settings");
		controller.SelectParameter(1);
		controller.SelectVariant(2);
		controller.SelectMode(0);
		Program.True(ReferenceEquals(controller.Current, first) && first.ParameterIndex == 2 && first.VariantIndex == 1,
			"Returning to a mode must restore its own parameter and variant");
		controller.SelectMode(1);
		Program.True(ReferenceEquals(controller.Current, second) && second.ParameterIndex == 1 && second.VariantIndex == 2,
			"Each mode must retain independent settings across repeated switches");
		Program.True(changes == 7 && first.ParameterCalls == 1 && first.VariantCalls == 1
			&& second.ParameterCalls == 1 && second.VariantCalls == 1,
			"Mode switches must notify once without invoking axis setters");
	}
}
