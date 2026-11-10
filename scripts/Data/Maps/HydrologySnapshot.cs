using System;
using System.Collections.Generic;

namespace World.Data.Maps;

internal sealed class HydrologyRoutingSnapshot
{
	public bool UsedFill { get; }
	public SnapshotArray<float> RoutingHeightM { get; }
	public SnapshotArray<float> FillDepthM { get; }
	public int DepressionCount { get; }
	public double MaxFillDepthM { get; }
	public int FilledCellCount { get; }

	public HydrologyRoutingSnapshot(bool usedFill, IEnumerable<float> routingHeightM, IEnumerable<float> fillDepthM,
		int depressionCount, double maxFillDepthM, int filledCellCount)
	{
		UsedFill = usedFill;
		RoutingHeightM = new(MapStructure.Present(routingHeightM, "hydrology.rivers.routing.routingHeightM"));
		FillDepthM = new(MapStructure.Present(fillDepthM, "hydrology.rivers.routing.fillDepthM"));
		DepressionCount = depressionCount;
		MaxFillDepthM = maxFillDepthM;
		FilledCellCount = filledCellCount;
	}

	internal void Validate(int cellCount)
	{
		MapStructure.Length(RoutingHeightM.Count, cellCount, "hydrology.rivers.routing.routingHeightM");
		MapStructure.Length(FillDepthM.Count, cellCount, "hydrology.rivers.routing.fillDepthM");
		for (int i = 0; i < cellCount; i++)
		{
			MapStructure.Finite(RoutingHeightM[i], "hydrology.rivers.routing.routingHeightM", i);
			MapStructure.Finite(FillDepthM[i], "hydrology.rivers.routing.fillDepthM", i);
			MapStructure.Require(FillDepthM[i] >= 0, "hydrology.rivers.routing.fillDepthM",
				"A fill depth cannot be negative.", index: i);
		}
		MapStructure.Require(DepressionCount >= 0, "hydrology.rivers.routing.depressionCount",
			"A depression count cannot be negative.");
		MapStructure.Finite(MaxFillDepthM, "hydrology.rivers.routing.maxFillDepthM");
		MapStructure.Require(MaxFillDepthM >= 0, "hydrology.rivers.routing.maxFillDepthM",
			"A maximum fill depth cannot be negative.");
		MapStructure.Require(FilledCellCount >= 0, "hydrology.rivers.routing.filledCellCount",
			"A filled cell count cannot be negative.");
	}
}

internal sealed class RiversSnapshot
{
	public SnapshotArray<int> Downstream { get; }
	public SnapshotArray<int> FlowAccum { get; }
	public SnapshotArray<double> FlowAccumKm2 { get; }
	public SnapshotArray<bool> IsRiver { get; }
	public int SinkCount { get; }
	public SnapshotArray<float> RunoffAccum { get; }
	public SnapshotArray<double> RunoffAccumKm3 { get; }
	public float RunoffThreshold { get; }
	public double RiverThresholdKm2 { get; }
	public HydrologyRoutingSnapshot Routing { get; }

	public RiversSnapshot(IEnumerable<int> downstream, IEnumerable<int> flowAccum, IEnumerable<double> flowAccumKm2,
		IEnumerable<bool> isRiver, int sinkCount, IEnumerable<float> runoffAccum, IEnumerable<double> runoffAccumKm3,
		float runoffThreshold, double riverThresholdKm2, HydrologyRoutingSnapshot routing)
	{
		Downstream = new(MapStructure.Present(downstream, "hydrology.rivers.downstream"));
		FlowAccum = new(MapStructure.Present(flowAccum, "hydrology.rivers.flowAccum"));
		FlowAccumKm2 = new(MapStructure.Present(flowAccumKm2, "hydrology.rivers.flowAccumKm2"));
		IsRiver = new(MapStructure.Present(isRiver, "hydrology.rivers.isRiver"));
		SinkCount = sinkCount;
		RunoffAccum = new(MapStructure.Present(runoffAccum, "hydrology.rivers.runoffAccum"));
		RunoffAccumKm3 = new(MapStructure.Present(runoffAccumKm3, "hydrology.rivers.runoffAccumKm3"));
		RunoffThreshold = runoffThreshold;
		RiverThresholdKm2 = riverThresholdKm2;
		Routing = MapStructure.Present(routing, "hydrology.rivers.routing");
	}

	internal void Validate(int cellCount)
	{
		MapStructure.Length(Downstream.Count, cellCount, "hydrology.rivers.downstream");
		MapStructure.Length(FlowAccum.Count, cellCount, "hydrology.rivers.flowAccum");
		MapStructure.Length(FlowAccumKm2.Count, cellCount, "hydrology.rivers.flowAccumKm2");
		MapStructure.Length(IsRiver.Count, cellCount, "hydrology.rivers.isRiver");
		MapStructure.Length(RunoffAccum.Count, cellCount, "hydrology.rivers.runoffAccum");
		MapStructure.Length(RunoffAccumKm3.Count, cellCount, "hydrology.rivers.runoffAccumKm3");
		MapStructure.Require(SinkCount >= 0, "hydrology.rivers.sinkCount", "A sink count cannot be negative.");
		MapStructure.Finite(RunoffThreshold, "hydrology.rivers.runoffThreshold");
		MapStructure.Require(RunoffThreshold >= 0, "hydrology.rivers.runoffThreshold", "A threshold cannot be negative.");
		MapStructure.Finite(RiverThresholdKm2, "hydrology.rivers.riverThresholdKm2");
		MapStructure.Require(RiverThresholdKm2 >= 0, "hydrology.rivers.riverThresholdKm2", "A threshold cannot be negative.");
		for (int i = 0; i < cellCount; i++)
		{
			MapStructure.Index(Downstream[i], cellCount, "hydrology.rivers.downstream", allowMissing: true, index: i);
			MapStructure.Require(FlowAccum[i] >= 0, "hydrology.rivers.flowAccum", "An upstream count cannot be negative.", index: i);
			MapStructure.Finite(FlowAccumKm2[i], "hydrology.rivers.flowAccumKm2", i);
			MapStructure.Require(FlowAccumKm2[i] >= 0, "hydrology.rivers.flowAccumKm2", "An upstream area cannot be negative.", index: i);
			MapStructure.Finite(RunoffAccum[i], "hydrology.rivers.runoffAccum", i);
			MapStructure.Require(RunoffAccum[i] >= 0, "hydrology.rivers.runoffAccum", "An accumulated depth cannot be negative.", index: i);
			MapStructure.Finite(RunoffAccumKm3[i], "hydrology.rivers.runoffAccumKm3", i);
			MapStructure.Require(RunoffAccumKm3[i] >= 0, "hydrology.rivers.runoffAccumKm3", "An accumulated volume cannot be negative.", index: i);
		}
		MapStructure.Acyclic(Downstream, "hydrology.rivers.downstream");
		Routing.Validate(cellCount);
	}
}

internal sealed class RiverTopologySnapshot
{
	public SnapshotArray<int> DownstreamRiver { get; }
	public SnapshotArray<int> UpstreamCount { get; }
	public SnapshotArray<int> NodeKind { get; }
	public SnapshotArray<float> AccumMm { get; }
	public int RiverCellCount { get; }
	public int SourceCount { get; }
	public int ConfluenceCount { get; }
	public int OutletCount { get; }
	public SnapshotArray<int> Sources { get; }
	public SnapshotArray<int> Outlets { get; }

	public RiverTopologySnapshot(IEnumerable<int> downstreamRiver, IEnumerable<int> upstreamCount,
		IEnumerable<int> nodeKind, IEnumerable<float> accumMm, int riverCellCount, int sourceCount,
		int confluenceCount, int outletCount, IEnumerable<int> sources, IEnumerable<int> outlets)
	{
		DownstreamRiver = new(MapStructure.Present(downstreamRiver, "hydrology.riverTopology.downstreamRiver"));
		UpstreamCount = new(MapStructure.Present(upstreamCount, "hydrology.riverTopology.upstreamCount"));
		NodeKind = new(MapStructure.Present(nodeKind, "hydrology.riverTopology.nodeKind"));
		AccumMm = new(MapStructure.Present(accumMm, "hydrology.riverTopology.accumMm"));
		RiverCellCount = riverCellCount;
		SourceCount = sourceCount;
		ConfluenceCount = confluenceCount;
		OutletCount = outletCount;
		Sources = new(MapStructure.Present(sources, "hydrology.riverTopology.sources"));
		Outlets = new(MapStructure.Present(outlets, "hydrology.riverTopology.outlets"));
	}

	internal void Validate(int cellCount)
	{
		MapStructure.Length(DownstreamRiver.Count, cellCount, "hydrology.riverTopology.downstreamRiver");
		MapStructure.Length(UpstreamCount.Count, cellCount, "hydrology.riverTopology.upstreamCount");
		MapStructure.Length(NodeKind.Count, cellCount, "hydrology.riverTopology.nodeKind");
		MapStructure.Length(AccumMm.Count, cellCount, "hydrology.riverTopology.accumMm");
		MapStructure.Require(RiverCellCount >= 0 && SourceCount >= 0 && ConfluenceCount >= 0 && OutletCount >= 0,
			"hydrology.riverTopology.counts", "Graph counts cannot be negative.");
		MapStructure.Length(Sources.Count, SourceCount, "hydrology.riverTopology.sources");
		MapStructure.Length(Outlets.Count, OutletCount, "hydrology.riverTopology.outlets");
		for (int i = 0; i < cellCount; i++)
		{
			MapStructure.Index(DownstreamRiver[i], cellCount, "hydrology.riverTopology.downstreamRiver",
				allowMissing: true, index: i);
			MapStructure.Require(UpstreamCount[i] >= 0, "hydrology.riverTopology.upstreamCount",
				"An upstream count cannot be negative.", index: i);
			MapStructure.Require(NodeKind[i] == -1 || NodeKind[i] is >= 0 and <= 4, "hydrology.riverTopology.nodeKind",
				"The node kind is outside the schema.", index: i);
			MapStructure.Finite(AccumMm[i], "hydrology.riverTopology.accumMm", i);
			MapStructure.Require(AccumMm[i] >= 0, "hydrology.riverTopology.accumMm",
				"An accumulated depth cannot be negative.", index: i);
		}
		MapStructure.Acyclic(DownstreamRiver, "hydrology.riverTopology.downstreamRiver");
		for (int i = 0; i < Sources.Count; i++)
			MapStructure.Index(Sources[i], cellCount, "hydrology.riverTopology.sources", index: i);
		for (int i = 0; i < Outlets.Count; i++)
			MapStructure.Index(Outlets[i], cellCount, "hydrology.riverTopology.outlets", index: i);
	}
}

internal sealed class RiverLinesSnapshot
{
	public int SourceRes { get; }
	public SnapshotArray<int> PointOffsets { get; }
	public SnapshotArray<int> Cells { get; }
	public SnapshotArray<float> Points { get; }
	public SnapshotArray<int> LineOutlet { get; }

	public RiverLinesSnapshot(int sourceRes, IEnumerable<int> pointOffsets, IEnumerable<int> cells,
		IEnumerable<float> points, IEnumerable<int> lineOutlet)
	{
		SourceRes = sourceRes;
		PointOffsets = new(MapStructure.Present(pointOffsets, "hydrology.riverLines.pointOffsets"));
		Cells = new(MapStructure.Present(cells, "hydrology.riverLines.cells"));
		Points = new(MapStructure.Present(points, "hydrology.riverLines.points"));
		LineOutlet = new(MapStructure.Present(lineOutlet, "hydrology.riverLines.lineOutlet"));
	}

	internal void Validate(int cellCount, int resolution)
	{
		MapStructure.Require(SourceRes == resolution, "hydrology.riverLines.sourceRes",
			"Line geometry must belong to the map resolution.");
		MapStructure.Length(PointOffsets.Count, LineOutlet.Count + 1, "hydrology.riverLines.pointOffsets");
		MapStructure.Length(Points.Count, checked(Cells.Count * 3), "hydrology.riverLines.points");
		MapStructure.Require(PointOffsets.Count > 0 && PointOffsets[0] == 0, "hydrology.riverLines.pointOffsets",
			"Offsets must start at zero.");
		for (int i = 1; i < PointOffsets.Count; i++)
			MapStructure.Require(PointOffsets[i] >= PointOffsets[i - 1], "hydrology.riverLines.pointOffsets",
				"Offsets must be monotonic.", index: i);
		MapStructure.Require(PointOffsets[PointOffsets.Count - 1] == Cells.Count, "hydrology.riverLines.pointOffsets",
			"Offsets must end at the cell count.");
		for (int i = 0; i < Cells.Count; i++)
			MapStructure.Index(Cells[i], cellCount, "hydrology.riverLines.cells", index: i);
		for (int i = 0; i < LineOutlet.Count; i++)
			MapStructure.Index(LineOutlet[i], cellCount, "hydrology.riverLines.lineOutlet", index: i);
		for (int i = 0; i < Cells.Count; i++)
		{
			double x = Points[i * 3], y = Points[i * 3 + 1], z = Points[i * 3 + 2];
			MapStructure.Finite(x, "hydrology.riverLines.points", i);
			MapStructure.Finite(y, "hydrology.riverLines.points", i);
			MapStructure.Finite(z, "hydrology.riverLines.points", i);
			double norm = Math.Sqrt(x * x + y * y + z * z);
			MapStructure.Require(Math.Abs(norm - 1) <= 1e-4, "hydrology.riverLines.points",
				"Line points must be unit sphere directions.", index: i);
		}
	}
}

internal sealed class BasinsSnapshot
{
	public SnapshotArray<int> BasinId { get; }
	public SnapshotArray<int> TerminalCell { get; }
	public SnapshotArray<bool> TerminatesAtOcean { get; }
	public int BasinCount { get; }
	public SnapshotArray<int> Kind { get; }
	public SnapshotArray<int> Outlet { get; }
	public SnapshotArray<int> Area { get; }
	public SnapshotArray<double> AreaKm2 { get; }
	public SnapshotArray<float> AccumMm { get; }
	public int OceanBasinCount { get; }
	public int EndorheicBasinCount { get; }

	public BasinsSnapshot(IEnumerable<int> basinId, IEnumerable<int> terminalCell, IEnumerable<bool> terminatesAtOcean,
		int basinCount, IEnumerable<int> kind, IEnumerable<int> outlet, IEnumerable<int> area,
		IEnumerable<double> areaKm2, IEnumerable<float> accumMm, int oceanBasinCount, int endorheicBasinCount)
	{
		BasinId = new(MapStructure.Present(basinId, "hydrology.basins.basinId"));
		TerminalCell = new(MapStructure.Present(terminalCell, "hydrology.basins.terminalCell"));
		TerminatesAtOcean = new(MapStructure.Present(terminatesAtOcean, "hydrology.basins.terminatesAtOcean"));
		BasinCount = basinCount;
		Kind = new(MapStructure.Present(kind, "hydrology.basins.kind"));
		Outlet = new(MapStructure.Present(outlet, "hydrology.basins.outlet"));
		Area = new(MapStructure.Present(area, "hydrology.basins.area"));
		AreaKm2 = new(MapStructure.Present(areaKm2, "hydrology.basins.areaKm2"));
		AccumMm = new(MapStructure.Present(accumMm, "hydrology.basins.accumMm"));
		OceanBasinCount = oceanBasinCount;
		EndorheicBasinCount = endorheicBasinCount;
	}

	internal void Validate(int cellCount)
	{
		MapStructure.Length(BasinId.Count, cellCount, "hydrology.basins.basinId");
		MapStructure.Length(TerminalCell.Count, cellCount, "hydrology.basins.terminalCell");
		MapStructure.Length(TerminatesAtOcean.Count, cellCount, "hydrology.basins.terminatesAtOcean");
		MapStructure.Length(Kind.Count, BasinCount, "hydrology.basins.kind");
		MapStructure.Length(Outlet.Count, BasinCount, "hydrology.basins.outlet");
		MapStructure.Length(Area.Count, BasinCount, "hydrology.basins.area");
		MapStructure.Length(AreaKm2.Count, BasinCount, "hydrology.basins.areaKm2");
		MapStructure.Length(AccumMm.Count, BasinCount, "hydrology.basins.accumMm");
		MapStructure.Require(OceanBasinCount >= 0 && EndorheicBasinCount >= 0, "hydrology.basins.counts",
			"Basin counts cannot be negative.");
		int ocean = 0, endorheic = 0;
		for (int i = 0; i < cellCount; i++)
		{
			MapStructure.Index(BasinId[i], BasinCount, "hydrology.basins.basinId", allowMissing: true, index: i);
			MapStructure.Index(TerminalCell[i], cellCount, "hydrology.basins.terminalCell", allowMissing: true, index: i);
		}
		for (int i = 0; i < BasinCount; i++)
		{
			MapStructure.Require(Kind[i] is 0 or 1, "hydrology.basins.kind", "The basin kind is outside the schema.", index: i);
			if (Kind[i] == 0) ocean++; else endorheic++;
			MapStructure.Index(Outlet[i], cellCount, "hydrology.basins.outlet", index: i);
			MapStructure.Require(Area[i] >= 0, "hydrology.basins.area", "A basin area cannot be negative.", index: i);
			MapStructure.Finite(AreaKm2[i], "hydrology.basins.areaKm2", i);
			MapStructure.Require(AreaKm2[i] >= 0, "hydrology.basins.areaKm2", "A basin area cannot be negative.", index: i);
			MapStructure.Finite(AccumMm[i], "hydrology.basins.accumMm", i);
			MapStructure.Require(AccumMm[i] >= 0, "hydrology.basins.accumMm", "An accumulated depth cannot be negative.", index: i);
		}
		MapStructure.Require(ocean == OceanBasinCount && endorheic == EndorheicBasinCount, "hydrology.basins.counts",
			"Basin kind counts must match their table.");
	}
}

internal sealed class LakesSnapshot
{
	public int LakeCount { get; }
	public SnapshotArray<int> DepressionIdOf { get; }
	public SnapshotArray<int> BasinIdOf { get; }
	public SnapshotArray<int> LakeOfBasin { get; }
	public SnapshotArray<float> SurfaceElevationM { get; }
	public SnapshotArray<double> AreaKm2 { get; }
	public SnapshotArray<double> VolumeKm3 { get; }
	public SnapshotArray<float> SpillElevationM { get; }
	public SnapshotArray<int> OutletCell { get; }

	public LakesSnapshot(int lakeCount, IEnumerable<int> depressionIdOf, IEnumerable<int> basinIdOf,
		IEnumerable<int> lakeOfBasin, IEnumerable<float> surfaceElevationM, IEnumerable<double> areaKm2,
		IEnumerable<double> volumeKm3, IEnumerable<float> spillElevationM, IEnumerable<int> outletCell)
	{
		LakeCount = lakeCount;
		DepressionIdOf = new(MapStructure.Present(depressionIdOf, "hydrology.lakes.depressionIdOf"));
		BasinIdOf = new(MapStructure.Present(basinIdOf, "hydrology.lakes.basinIdOf"));
		LakeOfBasin = new(MapStructure.Present(lakeOfBasin, "hydrology.lakes.lakeOfBasin"));
		SurfaceElevationM = new(MapStructure.Present(surfaceElevationM, "hydrology.lakes.surfaceElevationM"));
		AreaKm2 = new(MapStructure.Present(areaKm2, "hydrology.lakes.areaKm2"));
		VolumeKm3 = new(MapStructure.Present(volumeKm3, "hydrology.lakes.volumeKm3"));
		SpillElevationM = new(MapStructure.Present(spillElevationM, "hydrology.lakes.spillElevationM"));
		OutletCell = new(MapStructure.Present(outletCell, "hydrology.lakes.outletCell"));
	}

	internal void Validate(int cellCount)
	{
		int lakes = LakeCount, depressions = LakeOfBasin.Count;
		MapStructure.Require(LakeCount >= 0, "hydrology.lakes.lakeCount", "A lake count cannot be negative.");
		MapStructure.Length(DepressionIdOf.Count, cellCount, "hydrology.lakes.depressionIdOf");
		MapStructure.Length(BasinIdOf.Count, lakes, "hydrology.lakes.basinIdOf");
		MapStructure.Length(SurfaceElevationM.Count, lakes, "hydrology.lakes.surfaceElevationM");
		MapStructure.Length(AreaKm2.Count, lakes, "hydrology.lakes.areaKm2");
		MapStructure.Length(VolumeKm3.Count, lakes, "hydrology.lakes.volumeKm3");
		MapStructure.Length(SpillElevationM.Count, lakes, "hydrology.lakes.spillElevationM");
		MapStructure.Length(OutletCell.Count, lakes, "hydrology.lakes.outletCell");
		for (int i = 0; i < cellCount; i++)
			MapStructure.Require(DepressionIdOf[i] == -1 || (DepressionIdOf[i] >= 0 && DepressionIdOf[i] < depressions),
				"hydrology.lakes.depressionIdOf", "A raw depression reference is out of range.",
				MapIssueCode.InvalidReference, i);
		for (int lake = 0; lake < lakes; lake++)
		{
			MapStructure.Require(BasinIdOf[lake] >= 0 && BasinIdOf[lake] < depressions, "hydrology.lakes.basinIdOf",
				"A lake must reference an existing raw depression.", MapIssueCode.InvalidReference, lake);
			// Raw depression IDs and routed basin IDs are separate namespaces; the inverse map must
			// use the raw length even when it differs from the routed basin table.
			MapStructure.Require(LakeOfBasin[BasinIdOf[lake]] == lake, "hydrology.lakes.lakeOfBasin",
				"The lake/depression inverse mapping is inconsistent.", MapIssueCode.InvalidReference, lake);
			MapStructure.Finite(SurfaceElevationM[lake], "hydrology.lakes.surfaceElevationM", lake);
			MapStructure.Finite(AreaKm2[lake], "hydrology.lakes.areaKm2", lake);
			MapStructure.Require(AreaKm2[lake] >= 0, "hydrology.lakes.areaKm2", "A lake area cannot be negative.", index: lake);
			MapStructure.Finite(VolumeKm3[lake], "hydrology.lakes.volumeKm3", lake);
			MapStructure.Require(VolumeKm3[lake] >= 0, "hydrology.lakes.volumeKm3", "A lake volume cannot be negative.", index: lake);
			// A closed basin has no rim; the producer stores NaN there and the snapshot keeps it.
			MapStructure.Require(!float.IsInfinity(SpillElevationM[lake]), "hydrology.lakes.spillElevationM",
				"An infinite spill elevation is not part of the schema.", index: lake);
			MapStructure.Index(OutletCell[lake], cellCount, "hydrology.lakes.outletCell", allowMissing: true, index: lake);
		}
		for (int depression = 0; depression < depressions; depression++)
		{
			int lake = LakeOfBasin[depression];
			MapStructure.Require(lake == -1 || (lake >= 0 && lake < lakes), "hydrology.lakes.lakeOfBasin",
				"A depression lake reference is out of range.", MapIssueCode.InvalidReference, depression);
			if (lake >= 0)
				MapStructure.Require(BasinIdOf[lake] == depression, "hydrology.lakes.lakeOfBasin",
					"The depression/lake inverse mapping is inconsistent.", MapIssueCode.InvalidReference, depression);
		}
	}
}

internal sealed class WaterSystemSnapshot
{
	public int NodeCount { get; }
	public SnapshotArray<int> KindOf { get; }
	public SnapshotArray<int> RefOf { get; }
	public SnapshotArray<int> Next { get; }
	public SnapshotArray<int> ViaCell { get; }
	public SnapshotArray<int> NodeOfRiverCell { get; }
	public SnapshotArray<int> NodeOfLake { get; }
	public int OceanNode { get; }
	public int RiverCount { get; }
	public int LakeNodeCount { get; }
	public int SinkCount { get; }

	public WaterSystemSnapshot(int nodeCount, IEnumerable<int> kindOf, IEnumerable<int> refOf, IEnumerable<int> next,
		IEnumerable<int> viaCell, IEnumerable<int> nodeOfRiverCell, IEnumerable<int> nodeOfLake, int oceanNode,
		int riverCount, int lakeNodeCount, int sinkCount)
	{
		NodeCount = nodeCount;
		KindOf = new(MapStructure.Present(kindOf, "hydrology.waterSystem.kindOf"));
		RefOf = new(MapStructure.Present(refOf, "hydrology.waterSystem.refOf"));
		Next = new(MapStructure.Present(next, "hydrology.waterSystem.next"));
		ViaCell = new(MapStructure.Present(viaCell, "hydrology.waterSystem.viaCell"));
		NodeOfRiverCell = new(MapStructure.Present(nodeOfRiverCell, "hydrology.waterSystem.nodeOfRiverCell"));
		NodeOfLake = new(MapStructure.Present(nodeOfLake, "hydrology.waterSystem.nodeOfLake"));
		OceanNode = oceanNode;
		RiverCount = riverCount;
		LakeNodeCount = lakeNodeCount;
		SinkCount = sinkCount;
	}

	internal void Validate(int cellCount, int lakeCount)
	{
		MapStructure.Length(KindOf.Count, NodeCount, "hydrology.waterSystem.kindOf");
		MapStructure.Length(RefOf.Count, NodeCount, "hydrology.waterSystem.refOf");
		MapStructure.Length(Next.Count, NodeCount, "hydrology.waterSystem.next");
		MapStructure.Length(ViaCell.Count, NodeCount, "hydrology.waterSystem.viaCell");
		MapStructure.Length(NodeOfRiverCell.Count, cellCount, "hydrology.waterSystem.nodeOfRiverCell");
		MapStructure.Length(NodeOfLake.Count, lakeCount, "hydrology.waterSystem.nodeOfLake");
		MapStructure.Require(RiverCount >= 0 && LakeNodeCount >= 0 && SinkCount >= 0, "hydrology.waterSystem.counts",
			"Water-system counts cannot be negative.");
		MapStructure.Require(OceanNode == -1 || (OceanNode >= 0 && OceanNode < NodeCount), "hydrology.waterSystem.oceanNode",
			"The ocean node is out of range.", MapIssueCode.InvalidReference);
		int rivers = 0, lakes = 0, sinks = 0, oceans = 0;
		for (int node = 0; node < NodeCount; node++)
		{
			int kind = KindOf[node];
			MapStructure.Require(kind is >= 0 and <= 3, "hydrology.waterSystem.kindOf", "The node kind is outside the schema.", index: node);
			switch (kind)
			{
				case 0: rivers++; MapStructure.Index(RefOf[node], cellCount, "hydrology.waterSystem.refOf", index: node); break;
				case 1: lakes++; MapStructure.Index(RefOf[node], lakeCount, "hydrology.waterSystem.refOf", index: node); break;
				case 2:
					oceans++;
					MapStructure.Require(RefOf[node] == -1, "hydrology.waterSystem.refOf", "An ocean node reference must be -1.", index: node);
					break;
				default:
					sinks++;
					// Sink references are opaque legacy keys: routed basin IDs or raw depression IDs.
					MapStructure.Require(RefOf[node] >= 0, "hydrology.waterSystem.refOf", "A sink key cannot be negative.", index: node);
					break;
			}
			MapStructure.Index(Next[node], NodeCount, "hydrology.waterSystem.next", allowMissing: true, index: node);
			MapStructure.Index(ViaCell[node], cellCount, "hydrology.waterSystem.viaCell", allowMissing: true, index: node);
		}
		MapStructure.Acyclic(Next, "hydrology.waterSystem.next");
		MapStructure.Require(rivers == RiverCount && lakes == LakeNodeCount && sinks == SinkCount,
			"hydrology.waterSystem.counts", "Water-system kind counts must match their node table.");
		MapStructure.Require(oceans == (OceanNode < 0 ? 0 : 1) && rivers + lakes + sinks + oceans == NodeCount,
			"hydrology.waterSystem.counts", "Ocean nodes and total node count must agree.");
		if (OceanNode >= 0) MapStructure.Require(KindOf[OceanNode] == 2, "hydrology.waterSystem.oceanNode",
			"The ocean node must have the ocean kind.", MapIssueCode.InvalidReference, OceanNode);
		for (int cell = 0; cell < cellCount; cell++)
			MapStructure.Index(NodeOfRiverCell[cell], NodeCount, "hydrology.waterSystem.nodeOfRiverCell", allowMissing: true, index: cell);
		for (int lake = 0; lake < lakeCount; lake++)
			MapStructure.Index(NodeOfLake[lake], NodeCount, "hydrology.waterSystem.nodeOfLake", allowMissing: true, index: lake);
	}
}

internal sealed class HydrologySnapshot
{
	public RiversSnapshot Rivers { get; }
	public RiverTopologySnapshot RiverTopology { get; }
	public RiverLinesSnapshot RiverLines { get; }
	public BasinsSnapshot Basins { get; }
	public LakesSnapshot Lakes { get; }
	public WaterSystemSnapshot WaterSystem { get; }

	public HydrologySnapshot(RiversSnapshot rivers, RiverTopologySnapshot riverTopology, RiverLinesSnapshot riverLines,
		BasinsSnapshot basins, LakesSnapshot lakes, WaterSystemSnapshot waterSystem)
	{
		Rivers = MapStructure.Present(rivers, "hydrology.rivers");
		RiverTopology = MapStructure.Present(riverTopology, "hydrology.riverTopology");
		RiverLines = MapStructure.Present(riverLines, "hydrology.riverLines");
		Basins = MapStructure.Present(basins, "hydrology.basins");
		Lakes = MapStructure.Present(lakes, "hydrology.lakes");
		WaterSystem = MapStructure.Present(waterSystem, "hydrology.waterSystem");
	}

	public void Validate(int cellCount, int resolution)
	{
		Rivers.Validate(cellCount);
		RiverTopology.Validate(cellCount);
		RiverLines.Validate(cellCount, resolution);
		Basins.Validate(cellCount);
		Lakes.Validate(cellCount);
		WaterSystem.Validate(cellCount, Lakes.LakeCount);
	}
}