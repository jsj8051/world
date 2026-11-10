using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using World.Data;
using World.Data.Maps;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// 把已校验的地图结果装配成运行期世界事实：只重建网格几何与查找缓存，
/// 不调用任何生成、演化、汇流或气候计算。
/// </summary>
internal static class ImportedWorldFactsFactory
{
	internal static ImportedWorldFactsSource Build(GeographySnapshot snapshot)
	{
		if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
		int n = snapshot.Manifest.CellCount;
		var manifest = snapshot.Manifest;
		var ball = new Ball(manifest.H3Resolution, manifest.RenderRadius, snapshot.Grid.CellIds.ToArray());

		PlatePipeline plate = BuildPlate(snapshot, ball);
		var terrain = new TerrainPipeline();
		terrain.LoadSaved(snapshot.Terrain.HeightM.ToArray(), snapshot.Terrain.FinalLand.ToArray(),
			snapshot.Terrain.FinalLandFraction.ToArray(), snapshot.Terrain.FinalLandmassId.ToArray(),
			snapshot.Terrain.FinalLandmassCount, snapshot.Terrain.FinalDistToCoast.ToArray(),
			snapshot.Terrain.FinalDistToLand.ToArray(), BuildTectonic(snapshot.Tectonics));
		var climate = BuildClimate(snapshot.Climate, n, manifest.MonthCount);
		var hydrology = BuildHydrology(snapshot.Hydrology);
		var index = new IndexPipeline();
		index.LoadSaved(FinalSpatialIndex.FromSaved(ball,
			snapshot.QueryIndex.NearestLandmassId.ToArray(),
			snapshot.QueryIndex.NearestLandmassDistHops.ToArray(),
			snapshot.QueryIndex.NearestRiverCell.ToArray(),
			snapshot.QueryIndex.NearestRiverDistHops.ToArray()));

		var facts = new WorldFacts(ball, plate, terrain, climate, hydrology, index, null, manifest.SeaLevelM, snapshot);
		return new ImportedWorldFactsSource(snapshot, facts);
	}

	static PlatePipeline BuildPlate(GeographySnapshot snapshot, Ball ball)
	{
		TectonicsSnapshot tectonics = snapshot.Tectonics;
		Plate[] plates = tectonics.Plates.Select(p => new Plate
		{
			PlateId = p.PlateId,
			SeedTheta = p.SeedTheta,
			SeedPhi = p.SeedPhi,
			Omega = new Vector3(p.Omega.X, p.Omega.Y, p.Omega.Z),
			Density = p.Density,
		}).ToArray();
		CrustProvince[] provinces = tectonics.Crust.Provinces.Select(p =>
			new CrustProvince(p.Id, p.Type, p.SeedCell, p.SeedPlateId, p.CellCount)).ToArray();
		var crust = new CrustField(tectonics.Crust.Type.ToArray(), tectonics.Crust.ProvinceId.ToArray(), provinces);

		var boundariesSnapshot = tectonics.PlateBoundaries;
		PlateBoundaryEdge[] edges = boundariesSnapshot.Edges.Select(e =>
			new PlateBoundaryEdge(e.CellA, e.CellB, e.PlateA, e.PlateB)).ToArray();
		PlateBoundaryPair[] pairs = boundariesSnapshot.Pairs.Select(p =>
			new PlateBoundaryPair(p.PlateA, p.PlateB, p.EdgeStart, p.EdgeCount)).ToArray();
		PlateBoundarySet boundaries = PlateBoundarySet.FromSaved(boundariesSnapshot.PlateCount, edges, pairs,
			boundariesSnapshot.CellEdgeStart.ToArray(), boundariesSnapshot.CellEdgeIndex.ToArray());
		var fieldsSnapshot = tectonics.BoundaryFields;
		PlateBoundaryFields fields = PlateBoundaryFields.FromSaved(fieldsSnapshot.EdgeMotion.ToArray(),
			fieldsSnapshot.EdgeKind.ToArray(), fieldsSnapshot.EdgeRoleA.ToArray(), fieldsSnapshot.EdgeRoleB.ToArray());

		var platePipeline = new PlatePipeline();
		platePipeline.LoadSaved(tectonics.PlateOfCell.ToArray(), plates, crust, boundaries, fields,
			ContinentLayout.Auto, 0f);
		return platePipeline;
	}

	static TectonicInfluenceSet BuildTectonic(TectonicsSnapshot tectonics)
	{
		int count = Enum.GetValues<TectonicChannel>().Length;
		var fields = new TectonicInfluenceField[count];
		foreach (TectonicInfluenceSnapshot channel in tectonics.Channels)
		{
			int index = (int)channel.Channel;
			TectonicSource[] sources = channel.Sources.Select(s =>
				new TectonicSource(s.EdgeIndex, s.SideCell, s.Channel)).ToArray();
			fields[index] = new TectonicInfluenceField(channel.Channel, sources,
				channel.DistanceKm.ToArray(), channel.SourceIndex.ToArray());
		}
		for (int i = 0; i < count; i++)
			fields[i] ??= TectonicInfluenceField.Empty((TectonicChannel)i);
		return new TectonicInfluenceSet(fields);
	}

	static ClimatePipeline BuildClimate(ClimateSnapshot climate, int cellCount, int months)
	{
		var precipitation = PrecipitationModel.FromSaved(climate.PrecipitationMm.ToArray(),
			Reshape(climate.PrecipitationMonthlyMm.ToArray(), months, cellCount));
		var temperature = Temperature.FromSaved(climate.TemperatureC.ToArray(),
			Reshape(climate.TemperatureMonthlyC.ToArray(), months, cellCount),
			climate.HotMonthC.ToArray(), climate.ColdMonthC.ToArray());
		var pressure = PressureField.FromSaved(climate.PressureAnnualHpa.ToArray(),
			Reshape(climate.PressureMonthlyHpa.ToArray(), months, cellCount));
		var currents = OceanCurrentModel.FromSaved(ToVectors(climate.OceanCurrentDirection.ToArray()),
			climate.OceanCurrentWarmth.ToArray(), climate.OceanCurrentStrength.ToArray());
		var wind = WindField.FromSaved(
			ReshapeVectors(climate.SurfaceWind.Monthly.ToArray(), months, cellCount),
			ToVectors(climate.SurfaceWind.Annual.ToArray()), climate.SurfaceWind.AnnualSpeed.ToArray(),
			ReshapeVectors(climate.LowWind.Monthly.ToArray(), months, cellCount),
			ToVectors(climate.LowWind.Annual.ToArray()), climate.LowWind.AnnualSpeed.ToArray(),
			ReshapeVectors(climate.FreeWind.Monthly.ToArray(), months, cellCount),
			ToVectors(climate.FreeWind.Annual.ToArray()), climate.FreeWind.AnnualSpeed.ToArray());
		var pipeline = new ClimatePipeline();
		pipeline.LoadSaved(precipitation, temperature, pressure, currents, wind,
			climate.MonsoonStrength.ToArray(), climate.DryMonthMm.ToArray(), climate.DryMonthIndex.ToArray());
		return pipeline;
	}

	static HydrologySnapshot BuildHydrologySource(HydrologySnapshot snapshot) => snapshot;

	static HydrologyPipeline BuildHydrology(HydrologySnapshot snapshot)
	{
		RiversSnapshot riversDto = snapshot.Rivers;
		HydrologyRoutingSurface routing = HydrologyRoutingSurface.FromSaved(riversDto.Routing.UsedFill,
			riversDto.Routing.RoutingHeightM.ToArray(), riversDto.Routing.FillDepthM.ToArray(),
			riversDto.Routing.DepressionCount, riversDto.Routing.MaxFillDepthM, riversDto.Routing.FilledCellCount);
		RiverNetwork rivers = RiverNetwork.FromSaved(riversDto.Downstream.ToArray(), riversDto.FlowAccum.ToArray(),
			riversDto.FlowAccumKm2.ToArray(), riversDto.IsRiver.ToArray(), riversDto.SinkCount,
			riversDto.RunoffAccum.ToArray(), riversDto.RunoffAccumKm3.ToArray(), riversDto.RunoffThreshold,
			riversDto.RiverThresholdKm2, routing);

		RiverTopologySnapshot topologyDto = snapshot.RiverTopology;
		RiverGraph topology = RiverGraph.FromSaved(topologyDto.DownstreamRiver.ToArray(),
			topologyDto.UpstreamCount.ToArray(), topologyDto.NodeKind.ToArray(), topologyDto.AccumMm.ToArray(),
			topologyDto.RiverCellCount, topologyDto.SourceCount, topologyDto.ConfluenceCount,
			topologyDto.OutletCount, topologyDto.Sources.ToArray(), topologyDto.Outlets.ToArray());

		RiverLinesSnapshot linesDto = snapshot.RiverLines;
		var lineCells = new List<int[]>();
		var points = new List<Vector3[]>();
		for (int line = 0; line < linesDto.LineOutlet.Count; line++)
		{
			int start = linesDto.PointOffsets[line];
			int end = linesDto.PointOffsets[line + 1];
			var cells = new int[end - start];
			var vertices = new Vector3[end - start];
			for (int i = start; i < end; i++)
			{
				cells[i - start] = linesDto.Cells[i];
				vertices[i - start] = new Vector3(linesDto.Points[i * 3], linesDto.Points[i * 3 + 1],
					linesDto.Points[i * 3 + 2]);
			}
			lineCells.Add(cells);
			points.Add(vertices);
		}
		RiverGeometry geometry = RiverGeometry.FromSaved(linesDto.SourceRes, lineCells, points,
			linesDto.LineOutlet.ToArray());

		BasinsSnapshot basinsDto = snapshot.Basins;
		BasinGraph basins = BasinGraph.FromSaved(basinsDto.BasinId.ToArray(), basinsDto.TerminalCell.ToArray(),
			basinsDto.TerminatesAtOcean.ToArray(), basinsDto.BasinCount, basinsDto.Kind.ToArray(),
			basinsDto.Outlet.ToArray(), basinsDto.Area.ToArray(), basinsDto.AreaKm2.ToArray(),
			basinsDto.AccumMm.ToArray(), basinsDto.OceanBasinCount, basinsDto.EndorheicBasinCount);

		LakesSnapshot lakesDto = snapshot.Lakes;
		LakeState lakes = LakeState.FromSaved(lakesDto.LakeCount, lakesDto.DepressionIdOf.ToArray(),
			lakesDto.BasinIdOf.ToArray(), lakesDto.LakeOfBasin.ToArray(), lakesDto.SurfaceElevationM.ToArray(),
			lakesDto.AreaKm2.ToArray(), lakesDto.VolumeKm3.ToArray(), lakesDto.SpillElevationM.ToArray(),
			lakesDto.OutletCell.ToArray());

		WaterSystemSnapshot waterDto = snapshot.WaterSystem;
		WaterTopology water = WaterTopology.FromSaved(waterDto.NodeCount, waterDto.KindOf.ToArray(),
			waterDto.RefOf.ToArray(), waterDto.Next.ToArray(), waterDto.ViaCell.ToArray(),
			waterDto.NodeOfRiverCell.ToArray(), waterDto.NodeOfLake.ToArray(), waterDto.OceanNode,
			waterDto.RiverCount, waterDto.LakeNodeCount, waterDto.SinkCount);

		return HydrologyPipeline.FromSaved(rivers, topology, geometry, basins, lakes, water);
	}

	static float[][] Reshape(float[] flat, int months, int cells)
	{
		var rows = new float[months][];
		for (int month = 0; month < months; month++)
		{
			rows[month] = new float[cells];
			Array.Copy(flat, month * cells, rows[month], 0, cells);
		}
		return rows;
	}

	static Vector3[] ToVectors(float[] flat)
	{
		var values = new Vector3[flat.Length / 3];
		for (int i = 0; i < values.Length; i++)
			values[i] = new Vector3(flat[i * 3], flat[i * 3 + 1], flat[i * 3 + 2]);
		return values;
	}

	static Vector3[][] ReshapeVectors(float[] flat, int rows, int cells)
	{
		var monthly = new Vector3[rows][];
		for (int row = 0; row < rows; row++)
		{
			monthly[row] = new Vector3[cells];
			for (int cell = 0; cell < cells; cell++)
			{
				int offset = (row * cells + cell) * 3;
				monthly[row][cell] = new Vector3(flat[offset], flat[offset + 1], flat[offset + 2]);
			}
		}
		return monthly;
	}
}