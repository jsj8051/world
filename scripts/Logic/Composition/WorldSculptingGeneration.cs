using System;
using System.Collections.Generic;
using System.Threading;
using World.Data;
using World.H3Grid;
using World.Utils;
using SculptingData = World.Data.WorldSculptingRoundData<World.Data.WorldSculptingState, World.Data.WorldSculptingModuleOutput>;
using SculptingResult = World.Data.WorldSculptingRoundResult<World.Data.WorldSculptingState, World.Data.WorldSculptingModuleOutput>;

namespace World.Logic;

/// <summary>注册生产雕刻模块，将每轮持久地表和展示快照一起交给应用层提交。</summary>
internal sealed class WorldSculptingGeneration
{
	readonly Ball _ball;
	readonly WorldSculptingSession<WorldSculptingState, WorldSculptingModuleOutput> _session;

	internal long CompletedRounds => _session.CompletedRounds;
	internal sealed record Candidate(SculptingResult Round, WorldGenSimulation.State Presentation);

	internal WorldSculptingGeneration(WorldFacts initial, WorldSpec spec, IReadOnlyList<PlateRotation> rotations = null)
		: this(initial.Ball, WorldSculptingStateFactory.FromFacts(initial, Guid.NewGuid()), initial.Plates, spec, rotations)
	{
	}

	internal WorldSculptingGeneration(Ball ball, PlatePipeline plate, TerrainPipeline terrain, WorldSpec spec)
		: this(ball, WorldSculptingStateFactory.FromSurface(ball, plate.Plates, plate.PlateOfCell,
			plate.Crust, terrain.HeightM, spec.Terrain.SeaLevelM, Guid.NewGuid()), plate.Plates, spec, null,
			fillInitialInlandSeas: true)
	{
	}

	WorldSculptingGeneration(Ball ball, WorldSculptingState seed, Plate[] initialPlates, WorldSpec spec,
		IReadOnlyList<PlateRotation> rotations, bool fillInitialInlandSeas = false)
	{
		_ball = ball;
		var frozen = new WorldSpec();
		spec.CopyTo(frozen);
		// Legacy seed motion supplies direction and relative magnitude only.
		var plan = rotations == null ? BuildRotationPlan(initialPlates) : new List<PlateRotation>(rotations).ToArray();
		float ridgeElevationM = frozen.Terrain.RidgeCrestDepthM * TerrainManager.HeightScale(frozen.Terrain);
		float oceanDepth = Math.Max(1f, seed.Surface.SeaLevelM - ridgeElevationM);
		var pipeline = new WorldSculptingPipeline<WorldSculptingState, WorldSculptingModuleOutput>(
			new IWorldSculptingModule<WorldSculptingState, WorldSculptingModuleOutput>[]
			{
				new PlateMovementModule(plan),
				new SculptingSurfaceTransportModule(_ball, oceanDepth, new PlateRift(_ball)),
				new SculptingTerrainModule(_ball, frozen.Terrain, fillInitialInlandSeas),
				new Stage<ClimatePipeline>("climate", "climate.fields", SculptingTerrainModule.ModuleId,
					new[] { SculptingTerrainModule.TerrainFact }, (data, cancellation) =>
					{
						var terrain = Terrain(data);
						var climate = new ClimatePipeline();
						climate.Run(_ball, terrain.Terrain, terrain.Surface.SeaLevelM, frozen.Climate, frozen.Seed, cancellation);
						return climate;
					}),
				new Stage<HydrologyPipeline>("hydrology", "hydrology.fields", "climate",
					new[] { SculptingTerrainModule.TerrainFact, "climate.fields" }, (data, _) =>
					{
						var terrain = Terrain(data);
						var hydrology = new HydrologyPipeline();
						hydrology.Run(_ball, terrain.Terrain, terrain.Surface.SeaLevelM,
							Read<ClimatePipeline>(data, "climate").Precipitation);
						return hydrology;
					}),
				new Stage<BiomeType?[]>("biome.classification", "biome.land", "hydrology",
					new[] { SculptingTerrainModule.TerrainFact, "climate.fields", "hydrology.fields" },
					(data, cancellation) => BiomeClassifier.Classify(_ball, Terrain(data).Terrain,
						Read<ClimatePipeline>(data, "climate"), Read<HydrologyPipeline>(data, "hydrology").Lakes, cancellation)),
				new Stage<IndexPipeline>("index", "query.index", "hydrology",
					new[] { SculptingTerrainModule.TerrainFact, "hydrology.fields" }, (data, _) =>
					{
						var index = new IndexPipeline();
						index.Run(_ball, Terrain(data).Terrain, Read<HydrologyPipeline>(data, "hydrology").Rivers);
						return index;
					}),
			}, new Composer(_ball));
		_session = new WorldSculptingSession<WorldSculptingState, WorldSculptingModuleOutput>(seed, pipeline);
	}

	internal Candidate PrepareNext(CancellationToken cancellation, Action<string> onModuleStarted = null)
	{
		var round = _session.PrepareNext(cancellation, onModuleStarted);
		var data = round.Data;
		var terrain = Terrain(data);
		var climate = Read<ClimatePipeline>(data, "climate");
		var hydrology = Read<HydrologyPipeline>(data, "hydrology");
		var index = Read<IndexPipeline>(data, "index");
		var facts = new WorldFacts(_ball, terrain.Plate, terrain.Terrain, climate, hydrology, index, null,
			terrain.Surface.SeaLevelM, biomeByCell: Read<BiomeType?[]>(data, "biome.classification"),
			sculptingRoundIndex: round.Round.Index);
		cancellation.ThrowIfCancellationRequested();
		return new Candidate(round, new WorldGenSimulation.State(terrain.Plate, terrain.Terrain, climate, hydrology, index, facts));
	}

	internal void Commit(Candidate candidate) => _session.Commit(candidate.Round);

	internal static PlateRotation[] BuildRotationPlan(Plate[] plates)
	{
		const double radiansPerRound = .10;
		var plan = new PlateRotation[plates.Length];
		foreach (Plate plate in plates)
		{
			double x = plate.Omega.X, y = plate.Omega.Y, z = plate.Omega.Z;
			double magnitude = Math.Sqrt(x * x + y * y + z * z);
			var axis = magnitude == 0 ? new SpherePointD(0, 1, 0)
				: new SpherePointD(x / magnitude, y / magnitude, z / magnitude);
			plan[plate.PlateId] = new PlateRotation(plate.PlateId, axis, magnitude * radiansPerRound);
		}
		return plan;
	}

	static SculptingTerrainModule.Result Terrain(SculptingData data)
		=> data.Read<SculptingTerrainModule.Result>(SculptingTerrainModule.ModuleId);

	static T Read<T>(SculptingData data, string id) where T : class
		=> data.Read<Stage<T>.Result>(id).Value;

	internal sealed class Composer(Ball ball) : IWorldSculptingRoundComposer<WorldSculptingState, WorldSculptingModuleOutput>
	{
		public WorldSculptingState Compose(SculptingData data, CancellationToken cancellation)
		{
			var transport = data.Read<SculptingSurfaceTransportModule.Result>(SculptingSurfaceTransportModule.ModuleId);
			var source = data.Source;
			var surface = Terrain(data).Surface;
			long profile = WorldBuildProfile.Start();
			var elevations = SculptingElevationWriteback.Apply(ball, transport.SourceElevations, transport.Surface, surface,
				transport.Plates, transport.ContactOwners, transport.SampledCells, transport.MissingLocalElevations, cancellation);
			WorldBuildProfile.Mark(ref profile, "sculpting plate elevation writeback");
			return new WorldSculptingState(source.WorldId, source.H3Resolution, source.Radius, transport.Plates, surface, elevations);
		}
	}

	// Adapter for existing domain stages with local inputs and a single typed result.
	sealed class Stage<T>(string id, string fact, string dependency, string[] required,
		Func<SculptingData, CancellationToken, T> run)
		: IWorldSculptingModule<WorldSculptingState, WorldSculptingModuleOutput> where T : class
	{
		internal sealed class Result(WorldSculptingState source, WorldSculptingRound round, T value)
			: WorldSculptingModuleOutput(source, round)
		{
			internal T Value { get; } = value;
		}

		public string Id => id;
		public IReadOnlyCollection<string> Dependencies { get; } = Array.AsReadOnly(new[] { dependency });
		public IReadOnlyCollection<string> RequiredFacts { get; } = Array.AsReadOnly(required);
		public IReadOnlyCollection<string> ProducedFacts { get; } = Array.AsReadOnly(new[] { fact });

		public void Execute(SculptingData data, CancellationToken cancellation)
			=> data.Write(new Result(data.Source, data.Round, run(data, cancellation)));
	}
}
