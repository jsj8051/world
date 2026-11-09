using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using World.Data;

namespace World.Logic;

/// <summary>从活动共享边生成带符号、分侧的地形剖面。细网格按物理距离采样同一条边。</summary>
internal sealed class EvolutionRelief
{
	readonly record struct Profile(int FaceIndex, int PlateA, int PlateB, int Under,
		double Amplitude, double RiftWidth, double CollisionWidth, double CollisionPeak,
		double HeightBudgetA, double HeightBudgetB,
		double ArcPeak, bool IslandArc, double HalfSpreadingSpeed, bool OceanA, bool OceanB);

	readonly EvolutionGrid _grid;
	readonly EvolutionReliefGeometry _geometry;
	readonly CrustType[] _surfaceTypes;
	readonly int[] _plateOfCell;
	readonly float[] _materialHeight;
	readonly double[] _oceanBoundarySpeed;
	readonly List<Profile> _profiles = new();
	readonly Lazy<List<int>[]> _nearby;
	readonly Lazy<double[]> _redistributedDelta;
	readonly double[] _redistributedHeight;
	readonly double _arcReach, _trenchReach, _trenchDepth, _riftDepth, _relaxation;
	readonly double _ridgeReach, _newOceanElevation, _coolingDepth, _maxCoolingAge;

	public EvolutionRelief(EvolutionGrid grid, EvolutionReliefGeometry geometry, PlateBoundarySet boundaries,
		EvolutionBoundaryField field, CrustType[] surfaceTypes, int[] plateOfCell, float[] materialHeight,
		double[] strain, double[] shortening, double[] collisionVolume,
		double[] arcVolume, EvolutionSpec spec, double timeMa)
	{
		_grid = grid;
		_geometry = geometry;
		_surfaceTypes = surfaceTypes;
		_plateOfCell = plateOfCell;
		_materialHeight = materialHeight;
		_arcReach = spec.ArcReachKm;
		_trenchReach = spec.TrenchReachKm;
		_trenchDepth = spec.TrenchReliefScaleM;
		_riftDepth = spec.RiftReliefScaleM;
		_ridgeReach = spec.OceanRidgeReachKm;
		_newOceanElevation = spec.NewOceanElevationM;
		_coolingDepth = spec.CoolingDepthMPerSqrtMa;
		_maxCoolingAge = spec.MaxCoolingAgeMa;
		_relaxation = 1 - Math.Exp(-timeMa / spec.TectonicRelaxationMa);
		_nearby = new Lazy<List<int>[]>(BuildNearby);
		_redistributedDelta = new Lazy<double[]>(BuildRedistributedDelta);
		_redistributedHeight = new double[grid.CellCount];
		if (timeMa == 0) return;
		_oceanBoundarySpeed = new double[grid.CellCount];

		var boundaryFaces = new List<(int Face, int Edge)>(boundaries.Edges.Length);
		for (int i = 0; i < boundaries.Edges.Length; i++)
		{
			PlateBoundaryEdge edge = boundaries.Edges[i];
			if (field.MotionKinds[i] is not (EvolutionMotionKind.Convergent or EvolutionMotionKind.Divergent))
				continue;
			// 与材料结算一致，同一格遇到不同边界时取绝对法向速率最大的那一条。
			double speed = field.NormalSpeedKmPerMa[i];
			RecordOceanBoundary(edge.CellA, speed);
			RecordOceanBoundary(edge.CellB, speed);
			var (start, end) = grid.FacesStartingAt(edge.CellA);
			for (int face = start; face < end; face++)
				if (grid.Faces[face].B == edge.CellB) boundaryFaces.Add((face, i));
		}
		// 分段边按原共享弧段顺序累加，保留碰撞和火山的长度分配。
		boundaryFaces.Sort();
		var collisionLength = new double[grid.CellCount];
		var arcLength = new double[grid.CellCount];
		foreach (var (faceIndex, i) in boundaryFaces)
		{
			EvolutionFace face = grid.Faces[faceIndex];
			if (field.ConvergenceKinds[i] == ConvergenceKind.Collision)
			{
				collisionLength[face.A] += face.LengthKm;
				collisionLength[face.B] += face.LengthKm;
			}
			int under = field.UnderthrustingPlateIds[i];
			if (under >= 0)
				arcLength[boundaries.Edges[i].PlateA == under ? face.B : face.A] += face.LengthKm;
		}
		double buoyancy = 1 - spec.ContinentalDensityKgM3 / spec.MantleDensityKgM3;
		for (int cell = 0; cell < grid.CellCount; cell++)
		{
			if (collisionLength[cell] > 0)
				_redistributedHeight[cell] += 1000 * buoyancy * collisionVolume[cell] / grid.AreasKm2[cell];
			if (arcLength[cell] > 0)
				_redistributedHeight[cell] += 1000 * spec.VolcanicReliefFraction * arcVolume[cell] / grid.AreasKm2[cell];
		}
		foreach (var (faceIndex, i) in boundaryFaces)
		{
			EvolutionFace face = grid.Faces[faceIndex];
			EvolutionMotionKind motion = field.MotionKinds[i];
			PlateBoundaryEdge edge = boundaries.Edges[i];
			int under = field.UnderthrustingPlateIds[i];
			bool collision = field.ConvergenceKinds[i] == ConvergenceKind.Collision;
			bool divergent = motion == EvolutionMotionKind.Divergent;
			var (typeA, typeB) = field.SideTypes(i, surfaceTypes[face.A], surfaceTypes[face.B]);
			bool oceanA = typeA == CrustType.Oceanic;
			bool oceanB = typeB == CrustType.Oceanic;
			bool rifting = divergent && !oceanA && !oceanB;
			double halfSpreadingSpeed = divergent && (oceanA || oceanB) ? 0.5 * field.NormalSpeedKmPerMa[i] : 0;
			if (under < 0 && !collision && !divergent)
				continue;
			double strength = Math.Min(1, Math.Abs(field.NormalSpeedKmPerMa[i]) / (4 * spec.ActiveSpeedKmPerMa));
			double rift = rifting ? Math.Clamp(Math.Max(strain[face.A], strain[face.B]) / spec.RiftBreakupStrain, 0, 1) : 0;
			double width = 0, collisionPeak = 0, budgetA = 0, budgetB = 0, arcPeak = 0;
			bool island = false;
			if (collision)
			{
				width = Math.Clamp(100 + 0.67 * Math.Max(shortening[face.A], shortening[face.B]), 100, 350);
				budgetA = 1000 * buoyancy * collisionVolume[face.A] * face.LengthKm / collisionLength[face.A];
				budgetB = 1000 * buoyancy * collisionVolume[face.B] * face.LengthKm / collisionLength[face.B];
				collisionPeak = 2 * (budgetA + budgetB) / (face.LengthKm * width);
			}
			if (under >= 0)
			{
				bool overIsA = edge.PlateB == under;
				int over = overIsA ? face.A : face.B;
				double arcPerKm = 1000 * spec.VolcanicReliefFraction * arcVolume[over] / arcLength[over];
				arcPeak = arcPerKm / (0.6 * _arcReach);
				if (overIsA) budgetA = arcPerKm * face.LengthKm;
				else budgetB = arcPerKm * face.LengthKm;
				island = overIsA ? oceanA : oceanB;
			}
			double amplitude = under >= 0 ? _trenchDepth * strength
				: rifting ? _riftDepth * strength * Math.Sqrt(rift) : 0;
			_profiles.Add(new Profile(faceIndex, edge.PlateA, edge.PlateB, under, amplitude, 60 + 120 * rift, width,
				collisionPeak, budgetA, budgetB, arcPeak, island, halfSpreadingSpeed, oceanA, oceanB));
		}

		void RecordOceanBoundary(int cell, double speed)
		{
			if (surfaceTypes[cell] == CrustType.Oceanic && Math.Abs(speed) > Math.Abs(_oceanBoundarySpeed[cell]))
				_oceanBoundarySpeed[cell] = speed;
		}
	}

	// 按边长分配已有的抬升。碰撞抬升回到两侧陆壳，火山弧抬升只进入上盘。
	// 权重以格面积归一，抬升的面积积分在重分布前后相同。
	double[] BuildRedistributedDelta()
	{
		double[] delta = Array.Empty<double>();
		int[] visited = null;
		var queue = new Queue<int>();
		var scratch = new List<EvolutionReliefGeometry.Sample>();
		foreach (Profile profile in _profiles)
		{
			double budget = profile.HeightBudgetA + profile.HeightBudgetB;
			if (budget <= 0) continue;
			visited ??= new int[_grid.CellCount];
			var samples = _geometry.Samples(profile.FaceIndex, visited, queue, scratch);
			double weight = 0;
			foreach (var (cell, distance) in samples)
				weight += _grid.AreasKm2[cell] * RedistributionWeight(profile, cell, distance);
			// 剖面比格距窄时，保留原格的材料抬升。
			if (weight <= 0) continue;

			if (delta.Length == 0) delta = new double[_grid.CellCount];
			var face = _grid.Faces[profile.FaceIndex];
			delta[face.A] -= profile.HeightBudgetA / _grid.AreasKm2[face.A];
			delta[face.B] -= profile.HeightBudgetB / _grid.AreasKm2[face.B];
			double scale = budget / weight;
			foreach (var (cell, distance) in samples)
				delta[cell] += scale * RedistributionWeight(profile, cell, distance);
		}
		return delta;
	}

	double RedistributionWeight(Profile profile, int cell, double distance)
	{
		int plate = _plateOfCell[cell];
		if (plate != profile.PlateA && plate != profile.PlateB) return 0;
		if (profile.Under >= 0)
			return plate != profile.Under ? ArcWeight(distance) : 0;
		return _surfaceTypes[cell] == CrustType.Continental ? Kernel(distance, 0.5 * profile.CollisionWidth) : 0;
	}

	double ArcWeight(double distance) => Kernel(distance - 0.8 * _arcReach, 0.6 * _arcReach);

	internal double RedistributionDeltaAt(int cell)
	{
		double[] delta = _redistributedDelta.Value;
		return delta.Length == 0 ? 0 : delta[cell];
	}

	/// <summary>在模拟格心直接归并抬升与沉降，细网格任意点采样才需要逐格索引。</summary>
	public float[] AddToHeights()
	{
		float[] heights = _materialHeight;
		var surface = new float[heights.Length];
		var active = new List<int>(_profiles.Count);
		for (int i = 0; i < _profiles.Count; i++)
		{
			Profile profile = _profiles[i];
			if (profile.Under >= 0 || profile.HalfSpreadingSpeed > 0
				|| (profile.CollisionWidth == 0 && profile.Amplitude != 0))
				active.Add(i);
		}
		if (active.Count == 0)
		{
			for (int cell = 0; cell < surface.Length; cell++) surface[cell] = heights[cell] + (float)RedistributionDeltaAt(cell);
			return surface;
		}

		int workers = Math.Min(EvolutionWork.MaxWorkers, (active.Count - 1) / 256 + 1);
		var deltas = new (double Uplift, double Subsidence)[workers][];
		if (workers == 1) Rasterize(0);
		else Parallel.For(0, workers, Rasterize);

		EvolutionWork.ForCells(surface.Length, (start, end) =>
		{
			for (int cell = start; cell < end; cell++)
			{
				var (uplift, subsidence) = deltas[0][cell];
				for (int worker = 1; worker < workers; worker++)
				{
					uplift = Math.Max(uplift, deltas[worker][cell].Uplift);
					subsidence = Math.Max(subsidence, deltas[worker][cell].Subsidence);
				}
				surface[cell] = heights[cell] + (float)(RedistributionDeltaAt(cell) + (uplift - subsidence) * _relaxation);
			}
		});
		return surface;

		void Rasterize(int worker)
		{
			var delta = new (double Uplift, double Subsidence)[surface.Length];
			deltas[worker] = delta;
			var visited = new int[surface.Length];
			var queue = new Queue<int>();
			var scratch = new List<EvolutionReliefGeometry.Sample>();
			int start = worker * active.Count / workers;
			int end = (worker + 1) * active.Count / workers;
			for (int i = start; i < end; i++)
			{
				Profile profile = _profiles[active[i]];
				foreach (var (cell, distance) in _geometry.Samples(profile.FaceIndex, visited, queue, scratch))
				{
					int plate = _plateOfCell[cell];
					if (plate != profile.PlateA && plate != profile.PlateB) continue;
					ref var change = ref delta[cell];
					ApplyCellProfile(profile, cell, plate, distance, ref change.Uplift, ref change.Subsidence);
				}
			}
		}
	}

	List<int>[] BuildNearby()
	{
		var nearby = new List<int>[_grid.CellCount];
		if (_profiles.Count == 0) return nearby;
		var visited = new int[_grid.CellCount];
		var queue = new Queue<int>();
		var scratch = new List<EvolutionReliefGeometry.Sample>();
		for (int i = 0; i < _profiles.Count; i++)
			foreach (var sample in _geometry.Samples(_profiles[i].FaceIndex, visited, queue, scratch))
				(nearby[sample.Cell] ??= new List<int>()).Add(i);
		return nearby;
	}

	public float At(int cell, SpherePointD point, int plate, bool fine = false, double ridgeDetail = 1)
	{
		var nearby = _nearby.Value[cell];
		if (nearby == null) return fine ? 0 : (float)RedistributionDeltaAt(cell);
		double uplift = 0, subsidence = 0, ridges = 0;
		foreach (int index in nearby)
		{
			Profile profile = _profiles[index];
			if (plate != profile.PlateA && plate != profile.PlateB) continue;
			double signedDistance = EvolutionReliefGeometry.SignedDistanceKm(
				_grid.Faces[profile.FaceIndex], point, _grid.RadiusKm);
			double distance = Math.Abs(signedDistance);
			ApplyCellProfile(profile, cell, plate, distance, ref uplift, ref subsidence);
			if (fine && profile.Under >= 0 && plate != profile.Under)
			{
				ridges = Math.Max(ridges, profile.ArcPeak
					* (profile.IslandArc ? ridgeDetail * ridgeDetail : ridgeDetail)
					* ArcWeight(distance));
			}
			else if (fine && profile.CollisionWidth > 0)
			{
				double halfWidth = profile.CollisionWidth * (signedDistance < 0 ? 0.4 : 0.6);
				ridges = Math.Max(ridges, profile.CollisionPeak * ridgeDetail * Kernel(distance, halfWidth));
			}
		}
		return (float)((fine ? ridges : RedistributionDeltaAt(cell)) + (uplift - subsidence) * _relaxation);
	}

	void ApplyCellProfile(Profile profile, int cell, int plate, double distance, ref double uplift, ref double subsidence)
	{
		if (profile.Under >= 0)
		{
			if (plate == profile.Under && _oceanBoundarySpeed[cell] <= 0)
				ApplyTrench(profile, distance, ref subsidence);
			return;
		}
		if (profile.CollisionWidth > 0) return;
		bool ocean = _surfaceTypes[cell] == CrustType.Oceanic;
		double motion = _oceanBoundarySpeed[cell];
		if (profile.Amplitude != 0)
			ApplyRift(profile, distance, allowSubsidence: !ocean || motion <= 0, ref uplift, ref subsidence);

		bool oceanSide = plate == profile.PlateA ? profile.OceanA : profile.OceanB;
		if (ocean && oceanSide && motion >= 0 && profile.HalfSpreadingSpeed > 0 && distance < _ridgeReach)
		{
			// 新壳从轴部向两侧冷却。亚格宽度的年轻物质由连续剖面表达，材料槽和体积账本不变。
			double age = Math.Min(distance / profile.HalfSpreadingSpeed, _maxCoolingAge);
			double target = _newOceanElevation - _coolingDepth * Math.Sqrt(age);
			double rise = Math.Max(0, target - _materialHeight[cell]);
			uplift = Math.Max(uplift, rise * Kernel(distance, _ridgeReach));
		}
	}

	void ApplyTrench(Profile profile, double distance, ref double subsidence)
	{
		double offset = distance - 0.25 * _trenchReach;
		if (offset >= _trenchReach && double.IsFinite(profile.Amplitude)) return;
		subsidence = Math.Max(subsidence, profile.Amplitude * Kernel(offset, _trenchReach));
	}

	static void ApplyRift(Profile profile, double distance, bool allowSubsidence, ref double uplift, ref double subsidence)
	{
		double offset = distance - profile.RiftWidth;
		double shoulder = 0.45 * profile.RiftWidth;
		if (offset >= shoulder && double.IsFinite(profile.Amplitude)) return;
		if (allowSubsidence)
			subsidence = Math.Max(subsidence, profile.Amplitude * Kernel(distance, profile.RiftWidth));
		uplift = Math.Max(uplift, 0.35 * profile.Amplitude * Kernel(offset, shoulder));
	}

	public float RedistributedHeightM(int cell) => (float)_redistributedHeight[cell];

	internal static double Kernel(double distance, double width)
	{
		double t = Math.Clamp(Math.Abs(distance) / width, 0, 1);
		return 1 - t * t * (3 - 2 * t);
	}

}
