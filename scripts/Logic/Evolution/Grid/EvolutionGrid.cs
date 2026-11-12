using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using World.Data;
using World.H3Grid;
using World.Utils.H3;

namespace World.Logic;

/// <summary>
/// 演化用的固定球面网格：每格一个逆时针简单多边形、邻接表、格心、面积与最小邻距。
/// 生产路径由 <see cref="Ball"/> 的 H3 边界物化；测试可用任意合法多边形集合构造。
/// </summary>
public sealed class EvolutionGrid
{
	readonly ReadOnlyCollection<SpherePointD[]> _cells;
	readonly ReadOnlyCollection<int[]> _neighbors;
	readonly ReadOnlyCollection<SpherePointD> _centers;
	readonly ReadOnlyCollection<double> _areasKm2;
	readonly ReadOnlyCollection<double> _cellRadiusRad;
	readonly Dictionary<(int, int), (double X, double Y, double Z, double Length)> _contacts = new();
	readonly int[] _faceStart;
	internal EvolutionFace[] Faces { get; }

	/// <summary>格数。</summary>
	public int CellCount => _cells.Count;
	/// <summary>物理球半径（km）。</summary>
	public double RadiusKm { get; }
	/// <summary>H3 分辨率档（诊断与定义用）。</summary>
	public int Resolution { get; }
	/// <summary>逐格覆盖多边形（逆时针）。</summary>
	public IReadOnlyList<SpherePointD[]> Cells => _cells;
	/// <summary>逐格邻接下标（对称、升序、无自环）。</summary>
	public IReadOnlyList<int[]> Neighbors => _neighbors;
	/// <summary>逐格格心方向。</summary>
	public IReadOnlyList<SpherePointD> Centers => _centers;
	/// <summary>逐格面积（km²）。</summary>
	public IReadOnlyList<double> AreasKm2 => _areasKm2;
	/// <summary>逐格包围角半径（rad；格心到最远顶点）。</summary>
	public IReadOnlyList<double> CellRadiusRad => _cellRadiusRad;
	/// <summary>相邻格心间最小距离（km）；单步位移上限用它折算。</summary>
	public double MinNeighborDistanceKm { get; }

	/// <summary>由 H3 球壳物化每格多边形与邻接；<paramref name="radiusKm"/> 必须是物理半径（不是显示半径）。</summary>
	/// <exception cref="ArgumentException">网格为空或几何退化。</exception>
	/// <example><code>var grid = EvolutionGrid.FromBall(ball, 6371);</code></example>
	public static EvolutionGrid FromBall(Ball ball, double radiusKm, double angularToleranceRad = 1e-10)
	{
		if (ball == null) throw new ArgumentNullException(nameof(ball));
		int n = ball.CellIds.Length;
		var cells = new SpherePointD[n][];
		var centers = new SpherePointD[n];
		for (int i = 0; i < n; i++)
		{
			LatLng[] boundary = H3.CellToBoundary(ball.CellIds[i]);
			var ring = new SpherePointD[boundary.Length];
			for (int v = 0; v < boundary.Length; v++)
				ring[v] = Unit(boundary[v].Lat, boundary[v].Lng);
			if (SpherePolygonOps.SignedAreaSteradians(ring) < 0) Array.Reverse(ring);
			cells[i] = ring;
			Godot.Vector3 d = ball.CellDirs[i];
			centers[i] = UnitVec(d.X, d.Y, d.Z);
		}
		return new EvolutionGrid(cells, ball.CellNeighbors, centers, radiusKm, ball.Res, angularToleranceRad);
	}

	/// <summary>由显式几何构造（格多边形、邻接、格心，单位与球半径一致）。</summary>
	/// <exception cref="ArgumentException">格数不一致、邻接越界或非对称、多边形退化。</exception>
	/// <example><code>var grid = new EvolutionGrid(cells, neighbors, centers, 6371, 0);</code></example>
	public EvolutionGrid(IReadOnlyList<SpherePointD[]> cells, IReadOnlyList<int[]> neighbors,
		IReadOnlyList<SpherePointD> centers, double radiusKm, int resolution = 4, double angularToleranceRad = 1e-10)
	{
		if (cells == null) throw new ArgumentNullException(nameof(cells));
		if (neighbors == null) throw new ArgumentNullException(nameof(neighbors));
		if (centers == null) throw new ArgumentNullException(nameof(centers));
		if (!(radiusKm > 0) || !double.IsFinite(radiusKm))
			throw new ArgumentOutOfRangeException(nameof(radiusKm));
		if (cells.Count == 0) throw new ArgumentException("网格至少 1 格", nameof(cells));
		if (resolution < 0) throw new ArgumentOutOfRangeException(nameof(resolution));
		if (!(angularToleranceRad > 0) || !double.IsFinite(angularToleranceRad))
			throw new ArgumentOutOfRangeException(nameof(angularToleranceRad));
		if (neighbors.Count != cells.Count || centers.Count != cells.Count)
			throw new ArgumentException("格、邻接、格心数不一致");

		int n = cells.Count;
		var copyCells = new SpherePointD[n][];
		var copyNeighbors = new int[n][];
		var copyCenters = new SpherePointD[n];
		var areas = new double[n];
		var radii = new double[n];
		for (int i = 0; i < n; i++)
		{
			SpherePointD[] ring = cells[i] ?? throw new ArgumentException($"格 {i} 多边形为空", nameof(cells));
			if (ring.Length < 3) throw new ArgumentException($"格 {i} 少于 3 个顶点", nameof(cells));
			var c = new SpherePointD[ring.Length];
			for (int v = 0; v < c.Length; v++)
				c[v] = ring[v] ?? throw new ArgumentException($"格 {i} 含空顶点", nameof(cells));
			if (SpherePolygonOps.SignedAreaSteradians(c) < 0) Array.Reverse(c);
			copyCells[i] = c;
			areas[i] = SpherePolygonOps.AreaKm2(c, radiusKm);
			copyCenters[i] = centers[i] ?? throw new ArgumentException($"格 {i} 格心为空", nameof(centers));
			double maxAngle = 0;
			foreach (SpherePointD p in c)
				maxAngle = Math.Max(maxAngle, Math.Acos(Math.Clamp(Dot(copyCenters[i], p), -1, 1)));
			radii[i] = maxAngle;
		}

		for (int i = 0; i < n; i++)
		{
			int[] raw = neighbors[i] ?? throw new ArgumentException($"格 {i} 邻接为空", nameof(neighbors));
			var sorted = new int[raw.Length];
			Array.Copy(raw, sorted, raw.Length);
			for (int k = 0; k < sorted.Length; k++)
			{
				if ((uint)sorted[k] >= (uint)n || sorted[k] == i)
					throw new ArgumentException($"格 {i} 的邻居 {sorted[k]} 越界或自环", nameof(neighbors));
			}
			Array.Sort(sorted);
			for (int k = 1; k < sorted.Length; k++)
			{
				if (sorted[k] == sorted[k - 1]) throw new ArgumentException($"格 {i} 邻接重复", nameof(neighbors));
			}
			for (int k = 0; k < sorted.Length; k++)
			{
				if (Array.IndexOf(neighbors[sorted[k]], i) < 0)
					throw new ArgumentException($"邻接不对称：{i}→{sorted[k]}", nameof(neighbors));
			}
			copyNeighbors[i] = sorted;
		}

		double minDistance = double.PositiveInfinity;
		for (int i = 0; i < n; i++)
		{
			foreach (int j in copyNeighbors[i])
			{
				double dot = Dot(copyCenters[i], copyCenters[j]);
				double angle = Math.Acos(Math.Clamp(dot, -1, 1));
				minDistance = Math.Min(minDistance, angle * radiusKm);
			}
		}
		if (!double.IsFinite(minDistance)) minDistance = float.MaxValue;

		RadiusKm = radiusKm;
		Resolution = resolution;
		MinNeighborDistanceKm = minDistance;
		_cells = new ReadOnlyCollection<SpherePointD[]>(copyCells);
		_neighbors = new ReadOnlyCollection<int[]>(copyNeighbors);
		_centers = new ReadOnlyCollection<SpherePointD>(copyCenters);
		_areasKm2 = new ReadOnlyCollection<double>(areas);
		_cellRadiusRad = new ReadOnlyCollection<double>(radii);
		Faces = EvolutionFace.Build(copyCells, copyNeighbors, radiusKm, angularToleranceRad);
		_faceStart = new int[n + 1];
		foreach (EvolutionFace face in Faces)
		{
			_faceStart[face.A + 1]++;
			var key = (face.A, face.B);
			_contacts.TryGetValue(key, out var total);
			_contacts[key] = (total.X + face.X, total.Y + face.Y, total.Z + face.Z, total.Length + face.LengthKm);
		}
		for (int cell = 0; cell < n; cell++) _faceStart[cell + 1] += _faceStart[cell];
	}

	// EvolutionFace.Build 按较小端点 A 的顺序产出共享弧段。
	internal (int Start, int End) FacesStartingAt(int cell) => (_faceStart[cell], _faceStart[cell + 1]);

	internal double NormalSpeed(int a, int b, SphereVectorD omegaA, SphereVectorD omegaB)
	{
		var edge = _contacts[(a, b)];
		return (edge.X * (omegaB.X - omegaA.X) + edge.Y * (omegaB.Y - omegaA.Y)
			+ edge.Z * (omegaB.Z - omegaA.Z)) / edge.Length;
	}

	internal (double A, double B, double LengthKm) NormalAreaRates(int a, int b,
		SphereVectorD omegaA, SphereVectorD omegaB)
	{
		var edge = _contacts[(a, b)];
		return (edge.X * omegaA.X + edge.Y * omegaA.Y + edge.Z * omegaA.Z,
			edge.X * omegaB.X + edge.Y * omegaB.Y + edge.Z * omegaB.Z, edge.Length);
	}

	/// <summary>网格指纹：格心、半径与分辨语义的摘要；用于重演一致性。</summary>
	/// <example><code>string fp = grid.Fingerprint();</code></example>
	public string Fingerprint()
	{
		using var sha = SHA256.Create();
		var builder = new StringBuilder();
		builder.Append(CultureInfo.InvariantCulture, $"res={Resolution};R={RadiusKm:R};");
		for (int i = 0; i < CellCount; i++)
		{
			AppendPoint(_centers[i]);
			foreach (SpherePointD point in _cells[i]) AppendPoint(point);
			builder.Append('|');
			foreach (int neighbor in _neighbors[i]) builder.Append(CultureInfo.InvariantCulture, $"{neighbor},");
			builder.Append(';');
		}
		byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
		return Convert.ToHexString(hash);

		void AppendPoint(SpherePointD point) =>
			builder.Append(CultureInfo.InvariantCulture, $"{point.X:R},{point.Y:R},{point.Z:R};");
	}

	static double Dot(SpherePointD a, SpherePointD b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

	static SpherePointD Unit(double latRad, double lngRad)
	{
		double cos = Math.Cos(latRad);
		return new SpherePointD(cos * Math.Cos(lngRad), Math.Sin(latRad), cos * Math.Sin(lngRad));
	}

	static SpherePointD UnitVec(double x, double y, double z)
	{
		double length = Math.Sqrt(x * x + y * y + z * z);
		if (!(length > 1e-12)) throw new ArgumentException("格心方向退化");
		return new SpherePointD(x / length, y / length, z / length);
	}
}
