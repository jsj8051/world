using System;
using System.Threading.Tasks;
using Godot;
using World.Data;
using World.H3Grid;
using World.Logic;
using World.Utils;

namespace World.Logic;

/// <summary>诊断用跨分辨率投影。由粗网格演化恢复细地形残差。</summary>
internal sealed class CrossResolutionSurfaceRemapper
{
	readonly WorldFacts _initial;
	readonly EvolutionGrid _grid;
	readonly EvolutionSpec _spec;
	readonly TerrainSpec _terrain;
	readonly int _seed;
	readonly int[] _fineToCoarse;
	readonly float[] _initialCoarseHeight;

	public EvolutionSimulation Simulation { get; }
	public EvolutionFrame InitialFrame { get; }

	public CrossResolutionSurfaceRemapper(WorldSpec spec, WorldFacts initial, int resolution, float displayRadius)
	{
		if (resolution >= initial.Ball.Res)
			throw new ArgumentOutOfRangeException(nameof(resolution), "诊断网格必须比世界网格粗");
		var coarse = new Ball(resolution, displayRadius);
		_grid = EvolutionGrid.FromBall(coarse, SphericalFbmNoise.WorldRadiusKm, spec.Evolution.GeometryAngularToleranceRad);

		_initial = initial;
		_fineToCoarse = new int[initial.Ball.CellDirs.Length];
		var plates = new int[_grid.CellCount];
		var types = new CrustType[_grid.CellCount];
		var provinces = new int[_grid.CellCount];
		_initialCoarseHeight = new float[_grid.CellCount];
		for (int cell = 0; cell < _grid.CellCount; cell++)
		{
			int fine = BallGeoIndex.CellAt(initial.Ball, coarse.CellDirs[cell]);
			plates[cell] = initial.PlateOfCell[fine];
			types[cell] = initial.Crust.Type[fine];
			provinces[cell] = initial.Crust.ProvinceId[fine];
			_initialCoarseHeight[cell] = initial.HeightM[fine];
		}
		Parallel.For(0, _fineToCoarse.Length, cell =>
			_fineToCoarse[cell] = BallGeoIndex.CellAt(coarse, initial.Ball.CellDirs[cell]));
		CrustField crust = CrustProjectionBuilder.BuildCrust(types, provinces, initial.Crust.Provinces);
		Simulation = EvolutionSimulation.Create(spec, _grid, plates, crust, _initialCoarseHeight, initial.Plates,
			out var initialFrame);
		InitialFrame = initialFrame;
		WorldSpec frozen = Simulation.Definition.CreateSpecCopy();
		_spec = frozen.Evolution;
		_terrain = frozen.Terrain;
		_seed = frozen.Seed;
	}

	public WorldSurfaceInput Project(EvolutionFrame frame)
	{
		CrustProjection projection = frame.Projection;

		Ball ball = _initial.Ball;
		int n = ball.CellDirs.Length;
		var plates = new int[n];
		var heights = new float[n];
		var types = new CrustType[n];
		var provinces = new int[n];
		var ages = new double?[n];
		float scale = _terrain.VerticalScale > 0 ? _terrain.VerticalScale : 1;
		var continental = new bool[n];
		var uplift = new float[n];
		var ridge = new SphericalFbmNoise(SeedDerivation.Derive(_seed, SeedDerivation.Terrain_Ridge),
			_terrain.MountainRidgeWavelengthKm, NoiseSampling.ClampRidgedOctaves(_terrain.MountainRidgeOctaves,
				_terrain.MountainRidgeWavelengthKm, SpatialScale.Of(ball).NeighborCenterKm));
		var reverse = new Quaternion[frame.Motions.Count];
		for (int i = 0; i < reverse.Length; i++)
		{
			RotationD q = frame.Motions[i].Orientation;
			reverse[i] = new Quaternion((float)-q.X, (float)-q.Y, (float)-q.Z, (float)q.W).Normalized();
		}
		Parallel.For(0, n, fine =>
		{
			int coarse = _fineToCoarse[fine];
			int plate = projection.PlateOfCell[coarse];
			CrustType type = projection.Crust.Type[coarse];
			int province = projection.Crust.ProvinceId[coarse];
			Vector3 direction = ball.CellDirs[fine];
			float relief = projection.Relief.At(coarse, Unit(direction), plate, fine: true,
				ridgeDetail: 0.5 + ridge.SampleRidged(direction));
			float height = projection.MaterialHeightM[coarse] - projection.Relief.RedistributedHeightM(coarse);
			continental[fine] = type == CrustType.Continental;
			if (continental[fine] && relief > 0) uplift[fine] = relief / scale;
			else height += relief;
			int source = BallGeoIndex.CellAt(ball, (reverse[plate] * direction).Normalized());
			if (_initial.PlateOfCell[source] == plate && _initial.Crust.Type[source] == type)
				height += _initial.HeightM[source] - _initialCoarseHeight[_fineToCoarse[source]];
			plates[fine] = plate;
			heights[fine] = height;
			types[fine] = type;
			provinces[fine] = province;
			ages[fine] = projection.OceanAgeMa[coarse];
		});
		if (Array.Exists(uplift, h => h > 0))
		{
			float[] eroded = ErosionOps.Build(ball, _seed, _terrain, continental, uplift);
			for (int i = 0; i < n; i++)
				if (continental[i]) heights[i] += eroded[i] * scale;
		}
		CrustField crust = CrustProjectionBuilder.BuildCrust(types, provinces, _initial.Crust.Provinces);
		PlateBoundarySet boundaries = PlateBoundaryBuilder.Build(ball, plates, _initial.Plates);
		EvolutionBoundaryField field = EvolutionBoundaryOps.Build(_grid.RadiusKm,
			cell => Unit(ball.CellDirs[cell]), boundaries, crust, frame.Motions, _initial.Plates, _spec, ages);
		var boundary = PlateBoundaryFields.FromEvolution(boundaries, field, _initial.Plates, _spec.ActiveSpeedKmPerMa);
		return new WorldSurfaceInput(plates, _initial.Plates, crust, heights, boundaries, boundary);
	}

	static SpherePointD Unit(Vector3 direction)
	{
		double x = direction.X, y = direction.Y, z = direction.Z;
		double length = Math.Sqrt(x * x + y * y + z * z);
		return new SpherePointD(x / length, y / length, z / length);
	}
}
