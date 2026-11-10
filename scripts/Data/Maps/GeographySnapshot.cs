using System;
using System.Collections.Generic;
using World.Utils;
using World.Utils.H3;

namespace World.Data.Maps;

internal sealed class GeographySnapshot
{
	public GeographyManifest Manifest { get; }
	public GridSnapshot Grid { get; }
	public TerrainSnapshot Terrain { get; }
	public SurfaceSnapshot Surface { get; }
	public TectonicsSnapshot Tectonics { get; }
	public ClimateSnapshot Climate { get; }
	public HydrologySnapshot Hydrology { get; }
	public QueryIndexSnapshot QueryIndex { get; }

	public GeographySnapshot(GeographyManifest manifest, GridSnapshot grid, TerrainSnapshot terrain,
		SurfaceSnapshot surface, TectonicsSnapshot tectonics, ClimateSnapshot climate,
		HydrologySnapshot hydrology, QueryIndexSnapshot queryIndex)
	{
		Manifest = MapStructure.Present(manifest, "manifest");
		Grid = MapStructure.Present(grid, "grid");
		Terrain = MapStructure.Present(terrain, "terrain");
		Surface = MapStructure.Present(surface, "surface");
		Tectonics = MapStructure.Present(tectonics, "tectonics");
		Climate = MapStructure.Present(climate, "climate");
		Hydrology = MapStructure.Present(hydrology, "hydrology");
		QueryIndex = MapStructure.Present(queryIndex, "queryIndex");
		Validate();
	}

	void Validate()
	{
		int n = Manifest.CellCount;
		MapStructure.Require(Manifest.HasProjectedSurface == (Surface.Projected != null),
			"surface.projected", "The projected surface group must match the manifest declaration.", MapIssueCode.InvalidShape);
		Terrain.Validate(n, Manifest.SeaLevelM);
		Tectonics.Validate(n);
		Surface.Validate(n, Tectonics.Plates.Count);
		Climate.Validate(n, Manifest.MonthCount);
		Hydrology.Validate(n, Manifest.H3Resolution);
		QueryIndex.Validate(Terrain, Hydrology.Rivers.IsRiver);
		if (Surface.Projected == null)
			for (int i = 0; i < Tectonics.Plates.Count; i++)
				MapStructure.Require(Surface.MotionOmega[i] == Tectonics.Plates[i].Omega, "surface.motionOmega",
					"Static display motion must match its saved plate value.", index: i);

		// 捕获阶段清单只有头信息；完整块目录由打包器生成后再走布局校验。
		if (Manifest.Blocks.Count > 0) Manifest.ValidateLayout(GetDimensions());
		Grid.Validate(Manifest.H3Resolution, n);
		ValidateGeographicReferences();
	}

	Dictionary<string, int> GetDimensions()
	{
		var dimensions = new Dictionary<string, int>(StringComparer.Ordinal)
		{
			["N"] = Manifest.CellCount,
			["P"] = Tectonics.Plates.Count,
			["Q"] = Tectonics.Crust.Provinces.Count,
			["E"] = Tectonics.PlateBoundaries.Edges.Count,
			["B"] = Hydrology.Basins.Kind.Count,
			["D"] = Hydrology.Lakes.LakeOfBasin.Count,
			["L"] = Hydrology.Lakes.SurfaceElevationM.Count,
			["W"] = Hydrology.WaterSystem.KindOf.Count,
			["Sources"] = Hydrology.RiverTopology.Sources.Count,
			["Outlets"] = Hydrology.RiverTopology.Outlets.Count,
			["R"] = Hydrology.RiverLines.LineOutlet.Count,
			["K"] = Hydrology.RiverLines.Cells.Count,
		};
		string[] names = { "continental_collision", "continental_arc", "oceanic_arc", "subduction", "continental_rift", "oceanic_ridge" };
		foreach (TectonicInfluenceSnapshot channel in Tectonics.Channels)
		{
			string name = names[MapGeologyWire.ToTectonicChannel(channel.Channel)];
			dimensions.Add("S_" + name, channel.Sources.Count);
			dimensions.Add("C_" + name, channel.DistanceKm.Count);
		}
		return dimensions;
	}

	void ValidateGeographicReferences()
	{
		int n = Manifest.CellCount;
		var directions = new float[checked(n * 3)];
		for (int cell = 0; cell < n; cell++)
		{
			var direction = CoordUtil.LatLngToSphere(H3.CellToLatLng(Grid.CellIds[cell]), Manifest.RenderRadius).Normalized();
			directions[cell * 3] = direction.X;
			directions[cell * 3 + 1] = direction.Y;
			directions[cell * 3 + 2] = direction.Z;
			MapStructure.Require(!Hydrology.Rivers.IsRiver[cell] || Terrain.FinalLand[cell],
				"hydrology.rivers.isRiver", "Only land cells may be river cells.", index: cell);
			if (!Terrain.FinalLand[cell])
				MapStructure.Require(Hydrology.Basins.BasinId[cell] == -1 && Hydrology.Lakes.DepressionIdOf[cell] == -1,
					"hydrology.basins", "Sea cells cannot belong to a land basin or raw depression.",
					MapIssueCode.InvalidReference, cell);
			int downstream = Hydrology.Rivers.Downstream[cell];
			if (downstream != -1)
				MapStructure.Require(H3.AreNeighborCells(Grid.CellIds[cell], Grid.CellIds[downstream]),
					"hydrology.rivers.downstream", "Flow must follow adjacent cells.", MapIssueCode.InvalidReference, cell);
		}
		Climate.ValidateDirections(directions, n, Manifest.MonthCount);
		for (int i = 0; i < Tectonics.PlateBoundaries.Edges.Count; i++)
		{
			BoundaryEdgeSnapshot edge = Tectonics.PlateBoundaries.Edges[i];
			MapStructure.Require(H3.AreNeighborCells(Grid.CellIds[edge.CellA], Grid.CellIds[edge.CellB]),
				"tectonics.plateBoundaries.edges", "A plate boundary must join adjacent cells.", MapIssueCode.InvalidReference, i);
		}
	}
}