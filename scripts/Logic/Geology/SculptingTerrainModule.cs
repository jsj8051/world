using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using World.Data;
using World.H3Grid;
using World.Utils;
using SculptingData = World.Data.WorldSculptingRoundData<World.Data.WorldSculptingState, World.Data.WorldSculptingModuleOutput>;

namespace World.Logic;

/// <summary>将本轮相对位移作用在已搬运地表上，重建海陆和构造事实。</summary>
internal sealed class SculptingTerrainModule(Ball ball, TerrainSpec spec, bool fillInitialInlandSeas)
	: IWorldSculptingModule<WorldSculptingState, WorldSculptingModuleOutput>
{
	internal SculptingTerrainModule(Ball ball, TerrainSpec spec) : this(ball, spec, false) { }

	internal const string ModuleId = "terrain";
	internal const string TerrainFact = "terrain.sculpted";
	public string Id => ModuleId;
	public IReadOnlyCollection<string> Dependencies { get; } = Array.AsReadOnly(new[] { SculptingSurfaceTransportModule.ModuleId });
	public IReadOnlyCollection<string> RequiredFacts { get; } = Array.AsReadOnly(new[]
		{ SculptingSurfaceTransportModule.SurfaceFact, SculptingSurfaceTransportModule.ContactsFact, PlateMovementModule.PosesFact });
	public IReadOnlyCollection<string> ProducedFacts { get; } = Array.AsReadOnly(new[] { TerrainFact });

	internal sealed class Result(WorldSculptingState source, WorldSculptingRound round,
		SculptingSurfaceState surface, PlatePipeline plate, TerrainPipeline terrain)
		: WorldSculptingModuleOutput(source, round)
	{
		internal SculptingSurfaceState Surface { get; } = surface;
		internal PlatePipeline Plate { get; } = plate;
		internal TerrainPipeline Terrain { get; } = terrain;
	}

	public void Execute(SculptingData data, CancellationToken cancellation)
	{
		long timing = WorldBuildProfile.Start();
		var movement = data.Read<PlateMovementResult>(PlateMovementModule.ModuleId);
		var transport = data.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
		var surface = transport.Surface;
		int[] owners = surface.PlateOfCell.ToArray();
		float[] heights = surface.HeightM.ToArray();
		var crust = new CrustField(surface.CrustTypes.ToArray(), surface.CrustProvinceIds.ToArray(),
			surface.CrustProvinces.ToArray());
		var plates = new Plate[transport.Plates.Count];
		for (int id = 0; id < plates.Length; id++)
		{
			var plate = transport.Plates[id];
			var direction = RotationOps.Apply(plate.Orientation, plate.ReferenceSeed);
			var rotation = plate.RotationOverride ?? movement.Rotations[id];
			double angle = plate.IsExtinct ? 0 : Math.IEEERemainder(rotation.AngleRadians, 2 * Math.PI);
			plates[id] = new Plate
			{
				PlateId = id,
				Density = (float)plate.Density,
				SeedTheta = (float)Math.Acos(Math.Clamp(direction.Y, -1, 1)),
				SeedPhi = (float)Math.Atan2(direction.Z, direction.X),
				Omega = new Godot.Vector3((float)(rotation.Axis.X * angle), (float)(rotation.Axis.Y * angle),
					(float)(rotation.Axis.Z * angle)),
			};
		}

		cancellation.ThrowIfCancellationRequested();
		var boundaries = PlateBoundaryBuilder.Build(ball, owners, plates);
		var boundary = PlateBoundaryFields.Build(ball, boundaries, crust, plates,
			edge => Measure(edge, movement.InverseSurfaceMoves, transport.ContactOwners));
		var tectonic = TectonicInfluenceOps.Build(ball, boundaries, plates, crust, owners, boundary.EdgeMotion,
			TectonicInfluenceOps.WorldSearchRadiusKm, boundary);
		SculptingRelief.Apply(ball, spec, heights, transport, cancellation);
		if (fillInitialInlandSeas && data.Round.Index == 1)
			InitialInlandSeaOps.Fill(ball, heights, crust.Type, surface.SeaLevelM);

		var platePipeline = new PlatePipeline();
		platePipeline.SetSurface(new WorldSurfaceInput(owners, plates, crust, heights, boundaries, boundary));
		var terrainPipeline = new TerrainPipeline();
		terrainPipeline.SetSurface(ball, heights, surface.SeaLevelM, tectonic);
		data.Write(new Result(data.Source, data.Round, surface.WithHeights(heights), platePipeline, terrainPipeline));
		WorldBuildProfile.Mark(ref timing, "sculpting terrain");
	}

	float Measure(PlateBoundaryEdge edge, IReadOnlyList<RotationD> inverseMoves, IReadOnlyList<int> contactOwners)
	{
		int plateA = contactOwners[edge.CellA], plateB = contactOwners[edge.CellB];
		if (plateA == plateB) return 0;
		return SculptingBoundaryMotion.NormalMotion(ball, edge.CellA, edge.CellB, plateA, plateB, inverseMoves);
	}

}
