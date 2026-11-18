using System.Collections.Generic;

namespace World.Render;

/// <summary>Creates natural-world overlays; callers choose whether to register this set.</summary>
public static class WorldMapLayers
{
	public static IReadOnlyList<IMapLayer> CreateNatural() => new IMapLayer[]
	{
		new RiverLineOverlay { Name = "RiverLines", Visible = false },
		new WindFlowOverlay { Name = "WindFlow", Visible = false },
		new CurrentFlowOverlay { Name = "CurrentFlow", Visible = false },
		new PlateMotionOverlay { Name = "PlateMotion", Visible = false },
	};
}
