using System;
using System.Collections.Generic;
using World.Utils.H3;

namespace World.Data.Maps;

internal sealed class GridSnapshot
{
	public SnapshotArray<ulong> CellIds { get; }

	public GridSnapshot(IEnumerable<ulong> cellIds)
	{
		CellIds = new SnapshotArray<ulong>(cellIds);
		MapStructure.Require(CellIds.Count > 0 && CellIds.Count <= 288122,
			"grid.cellIds", "The grid size is outside the supported range.", MapIssueCode.SizeLimit);
		var unique = new HashSet<ulong>();
		for (int i = 0; i < CellIds.Count; i++)
			MapStructure.Require(CellIds[i] != 0 && unique.Add(CellIds[i]), "grid.cellIds",
				"Cell identities must be nonzero and unique.", MapIssueCode.InvalidReference, i);
	}

	public static int CellCountAtResolution(int resolution)
	{
		MapStructure.Require(resolution >= 0 && resolution <= 4, "grid.resolution",
			"Only resolutions 0 through 4 are supported.", MapIssueCode.UnsupportedScale);
		int count = 120;
		for (int i = 0; i < resolution; i++) count = checked(count * 7);
		return count + 2;
	}

	public void Validate(int resolution, int cellCount)
	{
		MapStructure.Length(cellCount, CellCountAtResolution(resolution), "grid.cellCount");
		MapStructure.Length(CellIds.Count, cellCount, "grid.cellIds");
		// Validate with the existing H3 implementation; never enumerate a replacement grid.
		for (int i = 0; i < CellIds.Count; i++)
			MapStructure.Require(H3.IsValidCell(CellIds[i]) && H3.GetResolution(CellIds[i]) == resolution,
				"grid.cellIds", "A cell identity is invalid or has a different resolution.",
				MapIssueCode.InvalidReference, i);
	}
}

internal sealed class TerrainSnapshot
{
	public SnapshotArray<float> HeightM { get; }
	public SnapshotArray<bool> FinalLand { get; }
	public SnapshotArray<float> FinalLandFraction { get; }
	public SnapshotArray<int> FinalLandmassId { get; }
	public int FinalLandmassCount { get; }
	public SnapshotArray<int> FinalDistToCoast { get; }
	public SnapshotArray<int> FinalDistToLand { get; }

	public TerrainSnapshot(IEnumerable<float> heightM, IEnumerable<bool> finalLand,
		IEnumerable<float> finalLandFraction, IEnumerable<int> finalLandmassId, int finalLandmassCount,
		IEnumerable<int> finalDistToCoast, IEnumerable<int> finalDistToLand)
	{
		HeightM = new(heightM);
		FinalLand = new(finalLand);
		FinalLandFraction = new(finalLandFraction);
		FinalLandmassId = new(finalLandmassId);
		FinalLandmassCount = finalLandmassCount;
		FinalDistToCoast = new(finalDistToCoast);
		FinalDistToLand = new(finalDistToLand);
		MapStructure.Require(finalLandmassCount >= 0 && finalLandmassCount <= HeightM.Count,
			"terrain.finalLandmassCount", "The landmass count is invalid.");
	}

	public void Validate(int cellCount, float seaLevelM)
	{
		MapStructure.Finite(seaLevelM, "terrain.seaLevelM");
		MapStructure.Length(HeightM.Count, cellCount, "terrain.heightM");
		MapStructure.Length(FinalLand.Count, cellCount, "terrain.finalLand");
		MapStructure.Length(FinalLandFraction.Count, cellCount, "terrain.finalLandFraction");
		MapStructure.Length(FinalLandmassId.Count, cellCount, "terrain.finalLandmassId");
		MapStructure.Length(FinalDistToCoast.Count, cellCount, "terrain.finalDistToCoast");
		MapStructure.Length(FinalDistToLand.Count, cellCount, "terrain.finalDistToLand");
		var observed = new bool[FinalLandmassCount];
		bool hasLand = false, hasSea = false;
		for (int i = 0; i < cellCount; i++)
		{
			MapStructure.Finite(HeightM[i], "terrain.heightM", i);
			MapStructure.Require(FinalLand[i] == (HeightM[i] > seaLevelM), "terrain.finalLand",
				"The land mask must match the saved elevation and sea level.", index: i);
			MapStructure.Range(FinalLandFraction[i], 0, 1, "terrain.finalLandFraction", i);
			if (FinalLand[i])
			{
				hasLand = true;
				MapStructure.Index(FinalLandmassId[i], FinalLandmassCount, "terrain.finalLandmassId", index: i);
				observed[FinalLandmassId[i]] = true;
			}
			else
			{
				hasSea = true;
				MapStructure.Require(FinalLandmassId[i] == -1, "terrain.finalLandmassId",
					"Sea cells cannot belong to a landmass.", MapIssueCode.InvalidReference, i);
			}
		}
		for (int i = 0; i < observed.Length; i++)
			MapStructure.Require(observed[i], "terrain.finalLandmassId", "Every declared landmass must have a cell.",
				MapIssueCode.InvalidReference, i);
		for (int i = 0; i < cellCount; i++)
		{
			ValidateDistance(FinalDistToCoast[i], hasSea, !FinalLand[i], cellCount, "terrain.finalDistToCoast", i);
			ValidateDistance(FinalDistToLand[i], hasLand, FinalLand[i], cellCount, "terrain.finalDistToLand", i);
		}
	}

	static void ValidateDistance(int value, bool hasSource, bool isSource, int count, string location, int cell)
	{
		MapStructure.Require(hasSource ? (value >= 0 && value < count && ((value == 0) == isSource)) : value == -1,
			location, "The distance or missing-source marker is invalid.", index: cell);
	}
}

internal sealed class QueryIndexSnapshot
{
	public SnapshotArray<int> NearestLandmassId { get; }
	public SnapshotArray<int> NearestLandmassDistHops { get; }
	public SnapshotArray<int> NearestRiverCell { get; }
	public SnapshotArray<int> NearestRiverDistHops { get; }

	public QueryIndexSnapshot(IEnumerable<int> nearestLandmassId, IEnumerable<int> nearestLandmassDistHops,
		IEnumerable<int> nearestRiverCell, IEnumerable<int> nearestRiverDistHops)
	{
		NearestLandmassId = new(nearestLandmassId);
		NearestLandmassDistHops = new(nearestLandmassDistHops);
		NearestRiverCell = new(nearestRiverCell);
		NearestRiverDistHops = new(nearestRiverDistHops);
	}

	public void Validate(TerrainSnapshot terrain, IReadOnlyList<bool> riverMask)
	{
		MapStructure.Present(terrain, "queryIndex.terrain");
		MapStructure.Present(riverMask, "queryIndex.riverMask");
		int n = terrain.HeightM.Count;
		MapStructure.Length(riverMask.Count, n, "queryIndex.riverMask");
		MapStructure.Length(NearestLandmassId.Count, n, "queryIndex.nearestLandmassId");
		MapStructure.Length(NearestLandmassDistHops.Count, n, "queryIndex.nearestLandmassDistHops");
		MapStructure.Length(NearestRiverCell.Count, n, "queryIndex.nearestRiverCell");
		MapStructure.Length(NearestRiverDistHops.Count, n, "queryIndex.nearestRiverDistHops");
		bool hasRiver = false;
		for (int i = 0; i < n; i++) hasRiver |= riverMask[i];
		for (int i = 0; i < n; i++)
		{
			ValidatePair(NearestLandmassId[i], NearestLandmassDistHops[i], terrain.FinalLandmassCount,
				terrain.FinalLandmassCount != 0, n, "queryIndex.nearestLandmass", i);
			ValidatePair(NearestRiverCell[i], NearestRiverDistHops[i], n, hasRiver, n, "queryIndex.nearestRiver", i);
			if (terrain.FinalLand[i])
				MapStructure.Require(NearestLandmassId[i] == terrain.FinalLandmassId[i] && NearestLandmassDistHops[i] == 0,
					"queryIndex.nearestLandmass", "Land cells must reference their own landmass at distance zero.", index: i);
			if (hasRiver)
			{
				MapStructure.Require(riverMask[NearestRiverCell[i]], "queryIndex.nearestRiverCell",
					"The nearest river label must reference a river cell.", MapIssueCode.InvalidReference, i);
				MapStructure.Require((NearestRiverDistHops[i] == 0) == riverMask[i], "queryIndex.nearestRiverDistHops",
					"Only river source cells can have distance zero.", index: i);
				if (riverMask[i]) MapStructure.Require(NearestRiverCell[i] == i, "queryIndex.nearestRiverCell",
					"River cells must identify themselves at distance zero.", MapIssueCode.InvalidReference, i);
			}
		}
	}

	static void ValidatePair(int label, int distance, int labelCount, bool hasSource, int cellCount, string location, int index)
	{
		MapStructure.Require(hasSource
			? (uint)label < (uint)labelCount && distance >= 0 && distance < cellCount
			: label == -1 && distance == -1,
			location, "The nearest-object identity and distance must agree.", MapIssueCode.InvalidReference, index);
	}
}