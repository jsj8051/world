using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using World.Data;
using World.H3Grid;
using World.Utils;
using SculptingData = World.Data.WorldSculptingRoundData<World.Data.WorldSculptingState, World.Data.WorldSculptingModuleOutput>;

namespace World.Logic;

/// <summary>按本轮姿态反向采样旧地表，重建高程并处理张裂空缺。</summary>
internal sealed class SculptingSurfaceTransportModule(Ball ball, float newOceanDepthM, PlateRift rift = null)
	: IWorldSculptingModule<WorldSculptingState, WorldSculptingModuleOutput>
{
	internal const string ModuleId = "surface.transport";
	internal const string SurfaceFact = "surface.transported";
	internal const string ContactsFact = "surface.crust-contacts";
	internal const string PlateTerritoriesFact = "plate.territories";
	public string Id => ModuleId;
	public IReadOnlyCollection<string> Dependencies { get; } = Array.AsReadOnly(new[] { PlateMovementModule.ModuleId });
	public IReadOnlyCollection<string> RequiredFacts { get; } = Array.AsReadOnly(new[] { PlateMovementModule.PosesFact });
	public IReadOnlyCollection<string> ProducedFacts { get; } = Array.AsReadOnly(new[] { SurfaceFact, ContactsFact, PlateTerritoriesFact });

	internal sealed class Result(WorldSculptingState source, WorldSculptingRound round, SculptingSurfaceState surface,
		SculptingCrustContact[] contacts, IReadOnlyList<SculptingPlateState> plates, PlateRift.Split split,
		IReadOnlyList<int> sampledCells, SculptingCoverage coverage, IReadOnlyList<int> contactOwners,
		PlateElevationState elevations, bool[] missingLocalElevations)
		: WorldSculptingModuleOutput(source, round)
	{
		internal SculptingSurfaceState Surface { get; } = surface;
		internal IReadOnlyList<SculptingCrustContact> Contacts { get; } = Array.AsReadOnly(contacts);
		internal IReadOnlyList<SculptingPlateState> Plates { get; } = plates;
		internal PlateRift.Split Split { get; } = split;
		internal IReadOnlyList<int> SampledCells { get; } = sampledCells;
		internal SculptingCoverage Coverage { get; } = coverage;
		internal IReadOnlyList<int> ContactOwners { get; } = contactOwners;
		internal PlateElevationState SourceElevations { get; } = elevations;
		internal IReadOnlyList<bool> MissingLocalElevations { get; } = Array.AsReadOnly(missingLocalElevations);
	}

	public void Execute(SculptingData data, CancellationToken cancellation)
	{
		var source = data.Source;
		var previous = source.Surface;
		if (ball.Res != source.H3Resolution || ball.Radius != source.Radius
			|| !ball.CellIds.SequenceEqual(previous.CellIds))
			throw new ArgumentException("搬运网格与来源地表不一致", nameof(source));
		if (!float.IsFinite(newOceanDepthM) || newOceanDepthM <= 0)
			throw new ArgumentOutOfRangeException(nameof(newOceanDepthM));
		var movement = data.Read<PlateMovementResult>(PlateMovementModule.ModuleId);
		var poses = movement.Plates.Select(plate => plate.Orientation).ToArray();
		int count = previous.CellCount;
		var contacts = new SculptingCrustContact[count];
		cancellation.ThrowIfCancellationRequested();
		bool stationary = poses.SequenceEqual(previous.SampledPlateOrientations);
		var elevations = source.PlateElevations ?? PlateElevationState.FromSurface(previous, cancellation);
		if (rift == null && stationary)
		{
			data.Write(new Result(source, data.Round, previous, contacts, movement.Plates, null,
				Array.AsReadOnly(Enumerable.Range(0, count).ToArray()), null, previous.PlateOfCell, elevations, new bool[count]));
			return;
		}

		long profile = WorldBuildProfile.Start();
		var coverage = stationary ? null : SculptingCoverage.Build(ball, movement, cancellation);
		WorldBuildProfile.Mark(ref profile, "sculpting rotated coverage");
		var sampledCells = coverage == null ? Enumerable.Range(0, count).ToArray()
			: SculptingSurfaceSampling.Resolve(ball, coverage, contacts, cancellation);
		var columns = new SculptingSurfaceGaps.Columns(count);
		var missingLocalElevations = new bool[count];
		var sampler = new SculptingElevationSampler(ball, elevations, previous.SeaLevelM);
		var worldToPlate = new RotationD[poses.Length];
		var referenceWasLastSample = new bool[poses.Length];
		for (int plate = 0; plate < poses.Length; plate++)
		{
			RotationD reference = elevations.ReferencePose(plate);
			worldToPlate[plate] = RotationOps.Compose(reference, RotationOps.Inverse(poses[plate]));
			referenceWasLastSample[plate] = ReferenceEquals(reference, previous.SampledPlateOrientations[plate]);
		}
		Parallel.For(0, count, new ParallelOptions { CancellationToken = cancellation }, cell =>
		{
			int sample = sampledCells[cell];
			if (sample < 0)
			{
				columns.Owners[cell] = -1;
				return;
			}
			int owner = previous.PlateOfCell[sample];
			columns.Owners[cell] = owner;
			var crust = previous.CrustTypes[sample];
			columns.Heights[cell] = previous.HeightM[sample];
			if (!stationary)
			{
				Vector3 point = ball.CellDirs[cell];
				var (x, y, z) = RotationOps.Apply(worldToPlate[owner], point.X, point.Y, point.Z);
				if (sampler.TrySample(owner, crust, new Vector3((float)x, (float)y, (float)z),
					referenceWasLastSample[owner] ? sample : -1, out float height))
					columns.Heights[cell] = height;
				else missingLocalElevations[cell] = true;
			}
			columns.CrustTypes[cell] = crust;
			columns.ProvinceIds[cell] = previous.CrustProvinceIds[sample];
		});

		if (coverage != null)
			SculptingSurfaceGaps.FillNewSurface(ball, columns, coverage, sampledCells, previous, contacts, newOceanDepthM, cancellation);

		var contactOwners = Array.AsReadOnly((int[])columns.Owners.Clone());
		var survivingPlates = SculptingPlateTopology.Update(ball, movement, columns.Owners, coverage, cancellation);
		var split = rift?.TrySplit(survivingPlates, movement.Rotations, columns.Owners, columns.Heights, cancellation);
		IReadOnlyList<SculptingPlateState> plates = split?.Plates ?? Array.AsReadOnly(survivingPlates);
		if (split != null)
			poses = plates.Select(plate => plate.Orientation).ToArray();
		var provinceTable = RebuildProvinces(previous, sampledCells, columns.Owners, columns.ProvinceIds);
		var surface = new SculptingSurfaceState(ball.Res, ball.CellIds, columns.Owners, columns.Heights, columns.CrustTypes,
			columns.ProvinceIds, provinceTable, poses, previous.SeaLevelM);
		WorldBuildProfile.Mark(ref profile, "sculpting surface sampling and topology");
		data.Write(new Result(source, data.Round, surface, contacts, plates, split, Array.AsReadOnly(sampledCells),
			coverage, contactOwners, elevations, missingLocalElevations));
	}

	static CrustProvince[] RebuildProvinces(SculptingSurfaceState previous, IReadOnlyList<int> samples,
		int[] owners, int[] provinceIds)
	{
		var result = previous.CrustProvinces.ToArray();
		var counts = new int[result.Length];
		var seeds = new int[result.Length];
		Array.Fill(seeds, -1);
		for (int cell = 0; cell < provinceIds.Length; cell++)
		{
			int province = provinceIds[cell];
			if (province < 0) continue;
			counts[province]++;
			if (seeds[province] < 0 || samples[cell] == result[province].SeedCell
				&& samples[seeds[province]] != result[province].SeedCell)
				seeds[province] = cell;
		}
		for (int province = 0; province < result.Length; province++)
		{
			CrustProvince old = result[province];
			int seed = seeds[province];
			result[province] = seed < 0
				? new CrustProvince(old.Id, old.Type, old.SeedCell, old.SeedPlateId, 0)
				: new CrustProvince(old.Id, old.Type, seed, owners[seed], counts[province]);
		}
		return result;
	}
}
