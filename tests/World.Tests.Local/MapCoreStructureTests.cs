using System;
using System.Threading;
using World.Data.Maps;
using World.Scene;
using World.Scene.Navigation;

namespace World.Tests;

internal static class MapCoreStructureTests
{
	public static void BuffersOwnTheirValuesAndKeepFloatBits()
	{
		float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
		float nan = BitConverter.Int32BitsToSingle(unchecked((int)0x7fc01234));
		float[] source = { negativeZero, nan, 2f };
		var frozen = new SnapshotArray<float>(source);
		source[0] = 3;
		float[] copy = frozen.ToArray();
		copy[2] = 99;
		Program.True(BitConverter.SingleToInt32Bits(frozen[0]) == unchecked((int)0x80000000), "Signed zero was changed.");
		Program.True(BitConverter.SingleToInt32Bits(frozen[1]) == unchecked((int)0x7fc01234), "NaN payload was changed.");
		Program.True(frozen[2] == 2, "A returned copy leaked mutable storage.");
	}

	public static void StructuralErrorsKeepTheirLocationAndIndex()
	{
		try { MapStructure.Index(5, 2, "lakes.basinIdOf", index: 3); }
		catch (MapStructureException e)
		{
			Program.True(e.Issue.Code == MapIssueCode.InvalidReference && e.Issue.Location == "lakes.basinIdOf"
				&& e.Issue.ElementIndex == 3, "Structured error context was lost.");
			return;
		}
		throw new Exception("The invalid reference was accepted.");
	}

	public static void GridRejectsDuplicateIdentitiesWithoutEnumeration()
	{
		Program.Throws(() => new GridSnapshot(new ulong[] { 1, 1 }), "Repeated cell IDs were accepted.");
		Program.Throws(() => new GridSnapshot(new ulong[] { 0 }), "Zero cell ID was accepted.");
		Program.True(GridSnapshot.CellCountAtResolution(0) == 122
			&& GridSnapshot.CellCountAtResolution(4) == 288122, "Supported H3 cardinalities changed.");
		Program.Throws(() => GridSnapshot.CellCountAtResolution(5), "An unsupported resolution was accepted.");
	}

	public static void TerrainValidatesMasksAndSeaLevel()
	{
		MixedTerrain().Validate(3, 0);
		Program.Throws(() => MixedTerrain().Validate(3, 3), "A stale land mask was accepted.");
		Program.Throws(() => MixedTerrain().Validate(2, 0), "Wrong terrain length was accepted.");
		Program.Throws(() => new TerrainSnapshot(new[] { float.NaN }, new[] { false }, new[] { 0f },
			new[] { -1 }, 0, new[] { 0 }, new[] { -1 }).Validate(1, 0), "Nonfinite height was accepted.");
	}

	public static void TerrainKeepsAbsentDistanceSources()
	{
		var sea = new TerrainSnapshot(new[] { -1f, -2f }, new[] { false, false }, new[] { 0f, 0f },
			new[] { -1, -1 }, 0, new[] { 0, 0 }, new[] { -1, -1 });
		sea.Validate(2, 0);
		Program.True(sea.FinalDistToLand[0] == -1, "Absent land was normalized to zero distance.");
		Program.Throws(() => new TerrainSnapshot(new[] { -1f }, new[] { false }, new[] { 0f },
			new[] { -1 }, 0, new[] { 0 }, new[] { 0 }).Validate(1, 0), "Missing-source marker mismatch was accepted.");
	}

	public static void QueryLabelsAndDistancesMustAgree()
	{
		var terrain = MixedTerrain();
		terrain.Validate(3, 0);
		var valid = new QueryIndexSnapshot(new[] { 0, 0, 0 }, new[] { 0, 0, 1 },
			new[] { 1, 1, 1 }, new[] { 1, 0, 1 });
		valid.Validate(terrain, new[] { false, true, false });
		Program.Throws(() => new QueryIndexSnapshot(new[] { 0, 0, 0 }, new[] { 0, 0, 1 },
			new[] { 0, 0, 0 }, new[] { 1, 0, 1 }).Validate(terrain, new[] { false, true, false }),
			"A nonriver nearest-object label was accepted.");
		new QueryIndexSnapshot(new[] { 0, 0, 0 }, new[] { 0, 0, 1 },
			new[] { -1, -1, -1 }, new[] { -1, -1, -1 }).Validate(terrain, new[] { false, false, false });
	}

	public static void GraphStructureRejectsCyclesAndInvalidLinks()
	{
		MapStructure.Acyclic(new[] { 1, 2, -1 }, "test.flow");
		MapStructure.Acyclic(new[] { -1, 2, -1 }, "test.flow");
		Program.Throws(() => MapStructure.Acyclic(new[] { 1, 0 }, "test.flow"), "Cycle was accepted.");
		Program.Throws(() => MapStructure.Acyclic(new[] { 0 }, "test.flow"), "Self cycle was accepted.");
		Program.Throws(() => MapStructure.Acyclic(new[] { 5 }, "test.flow"), "Out-of-range edge was accepted.");
	}

	public static void SceneContractsKeepUnsupportedScreensUnreachable()
	{
		Program.True(Enum.GetValues<ScreenKind>().Length == 4, "An unimplemented play or civilization route was added.");
		Program.True(SceneContractRules.CanNavigate(ScreenKind.MainMenu, ScreenKind.Geography), "Geography entry is missing.");
		Program.True(SceneContractRules.CanNavigate(ScreenKind.MapLibrary, ScreenKind.MapBrowser), "Map browse route is missing.");
		Program.True(!SceneContractRules.CanNavigate(ScreenKind.Geography, ScreenKind.MapBrowser), "An unsupported direct route was accepted.");
	}

	/// <summary>The load lifecycle has one state source; a screen's commit right is page identity plus lifetime.</summary>
	public static void CommitGuardRejectsStaleOwnerAndCancelledWork()
	{
		using var pageLifetime = new CancellationTokenSource();
		var operation = new LoadOperation(Guid.NewGuid(), LoadFlow.MapBrowse, "Load map", new CancellationTokenSource());
		Guid pageId = Guid.NewGuid();
		var binding = new LoadOperationBinding(operation, pageId, pageLifetime.Token);

		Program.True(LoadCommitGuard.CanCommit(binding.Key, binding, pageId), "A current operation cannot commit.");
		Program.True(!LoadCommitGuard.CanCommit(binding.Key, binding, Guid.NewGuid()), "A stale page can commit.");
		Program.True(!LoadCommitGuard.CanCommit(new LoadCommitKey(Guid.NewGuid(), pageId), binding, pageId),
			"A stale operation can commit.");
		Program.True(!LoadCommitGuard.CanCommit(new LoadCommitKey(operation.Id, Guid.NewGuid()), binding, pageId),
			"An operation owned by another page can commit.");
		Program.True(!LoadCommitGuard.CanCommit(binding.Key, null, pageId), "A missing binding can commit.");

		pageLifetime.Cancel();
		Program.True(!LoadCommitGuard.CanCommit(binding.Key, binding, pageId), "A cancelled page can commit.");

		using var freshLifetime = new CancellationTokenSource();
		var second = new LoadOperationBinding(operation, pageId, freshLifetime.Token);
		operation.Complete();
		Program.True(!LoadCommitGuard.CanCommit(second.Key, second, pageId), "A completed operation can commit twice.");
		Program.True(LoadCommitGuard.IsCurrent(second.Key, second, pageId), "A completed operation lost its identity.");

		using var other = new LoadOperation(Guid.NewGuid(), LoadFlow.MapBrowse, "Load map", new CancellationTokenSource());
		var third = new LoadOperationBinding(other, pageId, freshLifetime.Token);
		other.Cancel();
		Program.True(!LoadCommitGuard.IsCurrent(third.Key, third, pageId), "A cancelled operation is still current.");
		Program.True(!LoadCommitGuard.CanCommit(third.Key, third, pageId), "A cancelled operation can commit.");
	}

	public static void BrowseRequestNeverFallsBackToGeneration()
	{
		Program.Throws(() => new ScreenRequest.MapBrowseRequest(null), "A missing imported source was accepted.");
		Program.Throws(() => new ScreenRequest.GeographyRequest(""), "An implicit geography parameter source was accepted.");
		Program.True(new ScreenRequest.GeographyRequest(ScreenRequest.GeographyRequest.EditorParameterResource).Kind == ScreenKind.Geography,
			"An explicit editor geography request was rejected.");
	}

	public static void NativeGridKeepsFileOrderAndRejectsInvalidCells()
	{
		ulong[] identities = World.Utils.H3.H3.GetRes0Cells();
		Array.Reverse(identities);
		var grid = new GridSnapshot(identities);
		grid.Validate(0, 122);
		Program.True(grid.CellIds[0] == identities[0] && grid.CellIds[121] == identities[121],
			"Validation reordered the saved grid.");
		identities[0] = 1;
		Program.Throws(() => new GridSnapshot(identities).Validate(0, 122), "Invalid native H3 identity accepted.");
	}

	static TerrainSnapshot MixedTerrain() => new(new[] { 1f, 2f, -1f }, new[] { true, true, false },
		new[] { 0.5f, 0.5f, 0.5f }, new[] { 0, 0, -1 }, 1, new[] { 1, 1, 0 }, new[] { 0, 0, 1 });
}