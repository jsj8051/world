using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using World.Data;

namespace World.Logic;

/// <summary>从活动共享边生成带符号、分侧的地形剖面。细网格按物理距离采样同一条边。</summary>
internal sealed class EvolutionRelief
{
	readonly record struct Profile(int FaceIndex, int PlateA, int PlateB, int Under,
		double Amplitude, double RiftWidth,
		double HeightBudgetA, double HeightBudgetB,
		double ArcPeak, bool IslandArc, double HalfSpreadingSpeed, bool OceanA, bool OceanB);

	readonly EvolutionGrid _grid;
	readonly EvolutionReliefGeometry _geometry;
	readonly CrustType[] _surfaceTypes;
	readonly int[] _plateOfCell;
	readonly float[] _materialHeight;
	readonly EvolutionCrust _material;
	readonly double[] _oceanBoundarySpeed;
	readonly List<Profile> _profiles = new();
	readonly Lazy<List<int>[]> _nearby;
	readonly Lazy<double[]> _redistributedDelta;
	readonly double[] _redistributedHeight;
	readonly double _arcReach, _trenchReach, _trenchDepth, _riftDepth, _relaxation;
	readonly double _ridgeReach, _newOceanElevation, _coolingDepth, _maxCoolingAge;

	public EvolutionRelief(EvolutionGrid grid, EvolutionReliefGeometry geometry, PlateBoundarySet boundaries,
		EvolutionBoundaryField field, CrustType[] surfaceTypes, int[] plateOfCell, float[] materialHeight,
		EvolutionCrust material, double[] strain, double[] arcVolume, EvolutionSpec spec, double timeMa)
	{
		_grid = grid;
		_geometry = geometry;
		_surfaceTypes = surfaceTypes;
		_plateOfCell = plateOfCell;
		_materialHeight = materialHeight;
		_material = material;
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
			if (field.UnderthrustingPlateIds[i] < 0 && field.MotionKinds[i] != EvolutionMotionKind.Divergent)
				continue;
			var (start, end) = grid.FacesStartingAt(edge.CellA);
			for (int face = start; face < end; face++)
				if (grid.Faces[face].B == edge.CellB) boundaryFaces.Add((face, i));
		}
		// 分段边按共享弧段顺序累加，保留火山弧的长度分配。
		boundaryFaces.Sort();
		var arcLength = new double[grid.CellCount];
		foreach (var (faceIndex, i) in boundaryFaces)
		{
			EvolutionFace face = grid.Faces[faceIndex];
			int under = field.UnderthrustingPlateIds[i];
			if (under >= 0)
				arcLength[boundaries.Edges[i].PlateA == under ? face.B : face.A] += face.LengthKm;
		}
		for (int cell = 0; cell < grid.CellCount; cell++)
		{
			if (arcLength[cell] > 0)
				_redistributedHeight[cell] += 1000 * spec.VolcanicReliefFraction * arcVolume[cell] / grid.AreasKm2[cell];
		}
		foreach (var (faceIndex, i) in boundaryFaces)
		{
			EvolutionFace face = grid.Faces[faceIndex];
			EvolutionMotionKind motion = field.MotionKinds[i];
			PlateBoundaryEdge edge = boundaries.Edges[i];
			int under = field.UnderthrustingPlateIds[i];
			bool divergent = motion == EvolutionMotionKind.Divergent;
			var (typeA, typeB) = field.SideTypes(i, surfaceTypes[face.A], surfaceTypes[face.B]);
			bool oceanA = typeA == CrustType.Oceanic;
			bool oceanB = typeB == CrustType.Oceanic;
			bool rifting = divergent && !oceanA && !oceanB;
			double halfSpreadingSpeed = divergent && (oceanA || oceanB) ? 0.5 * field.NormalSpeedKmPerMa[i] : 0;
			double strength = Math.Min(1, Math.Abs(field.NormalSpeedKmPerMa[i]) / (4 * spec.ActiveSpeedKmPerMa));
			double rift = rifting ? Math.Clamp(Math.Max(strain[face.A], strain[face.B]) / spec.RiftBreakupStrain, 0, 1) : 0;
			double budgetA = 0, budgetB = 0, arcPeak = 0;
			bool island = false;
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
			_profiles.Add(new Profile(faceIndex, edge.PlateA, edge.PlateB, under, amplitude, 60 + 120 * rift,
				budgetA, budgetB, arcPeak, island, halfSpreadingSpeed, oceanA, oceanB));
		}

		void RecordOceanBoundary(int cell, double speed)
		{
			if (surfaceTypes[cell] == CrustType.Oceanic && Math.Abs(speed) > Math.Abs(_oceanBoundarySpeed[cell]))
				_oceanBoundarySpeed[cell] = speed;
		}
	}

	// 按边长分配已有的火山弧抬升，抬升只进入上盘。
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
		return plate != profile.Under ? ArcWeight(distance) : 0;
	}

	double ArcWeight(double distance) => Kernel(distance - 0.8 * _arcReach, 0.6 * _arcReach);

	internal double RedistributionDeltaAt(int cell)
	{
		double[] delta = _redistributedDelta.Value;
		return delta.Length == 0 ? 0 : delta[cell];
	}

	internal sealed record CellRelief(double[] SharedDeltaM, IReadOnlyDictionary<long, double> OceanTrenchDeltaM)
	{
		public double TrenchDeltaAt(int cell, int plate) =>
			OceanTrenchDeltaM != null && OceanTrenchDeltaM.TryGetValue(OceanKey(cell, plate), out double delta) ? delta : 0;
	}

	/// <summary>共享地形修正按格归并。海沟沉降按格、下盘板块分别归并。</summary>
	public CellRelief BuildCellRelief()
	{
		var shared = new double[_grid.CellCount];
		var active = new List<int>(_profiles.Count);
		for (int i = 0; i < _profiles.Count; i++)
		{
			Profile profile = _profiles[i];
			if (profile.Under >= 0 || profile.HalfSpreadingSpeed > 0 || profile.Amplitude != 0)
				active.Add(i);
		}
		if (active.Count == 0)
		{
			for (int cell = 0; cell < shared.Length; cell++) shared[cell] = RedistributionDeltaAt(cell);
			return new CellRelief(shared, null);
		}

		int workers = Math.Min(EvolutionWork.MaxWorkers, (active.Count - 1) / 256 + 1);
		var deltas = new (double Uplift, double Subsidence)[workers][];
		var trenches = new Dictionary<long, double>[workers];
		if (workers == 1) Rasterize(0);
		else Parallel.For(0, workers, Rasterize);

		EvolutionWork.ForCells(shared.Length, (start, end) =>
		{
			for (int cell = start; cell < end; cell++)
			{
				var (uplift, subsidence) = deltas[0][cell];
				for (int worker = 1; worker < workers; worker++)
				{
					uplift = Math.Max(uplift, deltas[worker][cell].Uplift);
					subsidence = Math.Max(subsidence, deltas[worker][cell].Subsidence);
				}
				shared[cell] = RedistributionDeltaAt(cell) + (uplift - subsidence) * _relaxation;
				deltas[0][cell].Subsidence = subsidence;
			}
		});
		var oceanTrench = new Dictionary<long, double>();
		foreach (var workerTrenches in trenches)
			foreach (var (key, depth) in workerTrenches)
			{
				int cell = (int)(key >> 32);
				double extra = Math.Max(0, depth - deltas[0][cell].Subsidence) * _relaxation;
				if (extra > 0 && (!oceanTrench.TryGetValue(key, out double existing) || -extra < existing))
					oceanTrench[key] = -extra;
			}
		return new CellRelief(shared, oceanTrench);

		void Rasterize(int worker)
		{
			var delta = new (double Uplift, double Subsidence)[shared.Length];
			var trench = new Dictionary<long, double>();
			deltas[worker] = delta;
			trenches[worker] = trench;
			var visited = new int[shared.Length];
			var queue = new Queue<int>();
			var scratch = new List<EvolutionReliefGeometry.Sample>();
			int start = worker * active.Count / workers;
			int end = (worker + 1) * active.Count / workers;
			for (int i = start; i < end; i++)
			{
				Profile profile = _profiles[active[i]];
				foreach (var (cell, distance) in _geometry.Samples(profile.FaceIndex, visited, queue, scratch))
				{
					if (profile.Under >= 0)
					{
						double depth = TrenchDepth(profile, cell, distance);
						long key = OceanKey(cell, profile.Under);
						if (depth > 0 && (!trench.TryGetValue(key, out double existing) || depth > existing))
							trench[key] = depth;
						continue;
					}
					int plate = _plateOfCell[cell];
					if (plate != profile.PlateA && plate != profile.PlateB) continue;
					ref var change = ref delta[cell];
					ApplySharedProfile(profile, cell, plate, distance, ref change.Uplift, ref change.Subsidence);
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

	public float At(int cell, SpherePointD point, int plate, bool fine = false, double ridgeDetail = 1,
		CrustType? sampledType = null)
	{
		var nearby = _nearby.Value[cell];
		if (nearby == null) return fine ? 0 : (float)RedistributionDeltaAt(cell);
		double uplift = 0, subsidence = 0, ridges = 0;
		foreach (int index in nearby)
		{
			Profile profile = _profiles[index];
			if (plate != profile.PlateA && plate != profile.PlateB) continue;
			double distance = Math.Abs(EvolutionReliefGeometry.SignedDistanceKm(
				_grid.Faces[profile.FaceIndex], point, _grid.RadiusKm));
			if (profile.Under < 0)
				ApplySharedProfile(profile, cell, plate, distance, ref uplift, ref subsidence);
			if (fine && profile.Under >= 0 && plate != profile.Under)
			{
				ridges = Math.Max(ridges, profile.ArcPeak
					* (profile.IslandArc ? ridgeDetail * ridgeDetail : ridgeDetail)
					* ArcWeight(distance));
			}
		}
		double trenchHeightArea = 0, area = 0;
		foreach (ref readonly var entry in _material.Rows.Row(cell))
		{
			var type = (CrustType)(entry.Key % 2);
			int owner = entry.Key / 2;
			if (sampledType.HasValue && (type != sampledType || owner != plate)) continue;
			area += entry.Slot.Area;
			if (type != CrustType.Oceanic) continue;
			double trench = 0;
			foreach (int index in nearby)
			{
				Profile profile = _profiles[index];
				if (profile.Under != owner) continue;
				double distance = Math.Abs(EvolutionReliefGeometry.SignedDistanceKm(
					_grid.Faces[profile.FaceIndex], point, _grid.RadiusKm));
				trench = Math.Max(trench, TrenchDepth(profile, cell, distance));
			}
			trenchHeightArea -= entry.Slot.Area * (Math.Max(0, trench - subsidence) * _relaxation);
		}
		double shared = (fine ? ridges : RedistributionDeltaAt(cell)) + (uplift - subsidence) * _relaxation;
		return (float)(shared + (area > 0 ? trenchHeightArea / area : 0));
	}

	void ApplySharedProfile(Profile profile, int cell, int plate, double distance, ref double uplift, ref double subsidence)
	{
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

	double TrenchDepth(Profile profile, int cell, double distance)
	{
		double offset = distance - 0.25 * _trenchReach;
		if (offset >= _trenchReach || _oceanBoundarySpeed[cell] > 0
			|| _material.Get(cell, profile.Under, CrustType.Oceanic).Area <= 0)
			return 0;
		return profile.Amplitude * Kernel(offset, _trenchReach);
	}

	static long OceanKey(int cell, int plate) => ((long)cell << 32) | (uint)plate;

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
