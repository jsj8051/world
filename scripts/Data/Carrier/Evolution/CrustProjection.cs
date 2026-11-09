using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using World.Logic;

namespace World.Data;

internal readonly record struct CrustSurfaceCell(float EmergentFraction, float LandHeightM, float WaterHeightM);

/// <summary>
/// 材料到 H3 的逐格只读投影：板块号、主导壳型、高程、海底年龄、洋壳覆盖率与边界。
/// 全部可由 <see cref="EvolutionCrust"/> 重建，不是第二份材料真源。
/// </summary>
public sealed class CrustProjection
{
	readonly int[] _plateIds;
	readonly ReadOnlyCollection<int> _plateOfCell;
	readonly Lazy<(IReadOnlyList<float> HeightM, EvolutionRelief Relief, IReadOnlyList<CrustSurfaceCell> Cells,
		double EmergentFraction, IReadOnlyList<CrustType> Types)> _surface;
	readonly ReadOnlyCollection<double?> _oceanAgeMa;
	readonly ReadOnlyCollection<double> _oceanicCoverage;
	readonly ReadOnlyCollection<double> _volcanicRateKmPerMa;

	/// <summary>本投影对应的地质步号。</summary>
	public long StepIndex { get; }
	/// <summary>板块数。</summary>
	public int PlateCount { get; }
	/// <summary>逐格主导板块号。</summary>
	public IReadOnlyList<int> PlateOfCell => _plateOfCell;
	/// <summary>逐格主导壳型与来源省。</summary>
	public CrustField Crust { get; }
	/// <summary>表层归属使用边界几何的格主板块。</summary>
	public IReadOnlyList<int> SurfacePlateOfCell => PlateOfCell;
	/// <summary>格主板块携带的主导壳型，与整格材料占比独立。</summary>
	public IReadOnlyList<CrustType> SurfaceCrustType => _surface.Value.Types ?? Crust.Type;
	/// <summary>逐格最终高程（m），需要完整地表时生成并缓存。</summary>
	public IReadOnlyList<float> HeightM => _surface.Value.HeightM;
	/// <summary>按球面格面积加权的露出比例。由壳相高程与海平面分别判定。</summary>
	public double EmergentLandFraction => _surface.Value.EmergentFraction;
	internal IReadOnlyList<CrustSurfaceCell> SurfaceCells => _surface.Value.Cells;
	/// <summary>逐格海底年龄（Ma）；纯陆壳为 null。</summary>
	public IReadOnlyList<double?> OceanAgeMa => _oceanAgeMa;
	/// <summary>逐格洋壳覆盖率 ∈ [0,1]。</summary>
	public IReadOnlyList<double> OceanicCoverage => _oceanicCoverage;
	/// <summary>最近构造子步的逐格火山厚度增生速率（km/Ma）。</summary>
	public IReadOnlyList<double> VolcanicRateKmPerMa => _volcanicRateKmPerMa;
	/// <summary>主导板块归属派生的边界集。</summary>
	public PlateBoundarySet PlateBoundaries { get; }
	/// <summary>逐边物理运动与过程分类。</summary>
	public EvolutionBoundaryField Boundary { get; }
	internal IReadOnlyList<float> MaterialHeightM { get; }
	internal EvolutionRelief Relief => _surface.Value.Relief;

	/// <summary>建立逐格投影快照；数组长度不一致即抛。</summary>
	/// <exception cref="ArgumentException">逐格数组长度、边界集或壳型格数不一致。</exception>
	/// <example><code>var p = new CrustProjection(0, 8, plateOfCell, crust, height, age, cover, rate, boundaries, field);</code></example>
	public CrustProjection(long stepIndex, int plateCount, IReadOnlyList<int> plateOfCell,
		CrustField crust, IReadOnlyList<float> heightM, IReadOnlyList<double?> oceanAgeMa,
		IReadOnlyList<double> oceanicCoverage, IReadOnlyList<double> volcanicRateKmPerMa,
		PlateBoundarySet plateBoundaries, EvolutionBoundaryField boundary)
		: this(stepIndex, plateCount, plateOfCell, crust, heightM, oceanAgeMa, oceanicCoverage,
			volcanicRateKmPerMa, plateBoundaries, boundary, null)
	{
	}

	internal CrustProjection(long stepIndex, int plateCount, IReadOnlyList<int> plateOfCell,
		CrustField crust, IReadOnlyList<float> heightM, IReadOnlyList<double?> oceanAgeMa,
		IReadOnlyList<double> oceanicCoverage, IReadOnlyList<double> volcanicRateKmPerMa,
		PlateBoundarySet plateBoundaries, EvolutionBoundaryField boundary,
		Func<(float[] HeightM, EvolutionRelief Relief, CrustSurfaceCell[] Cells, double EmergentFraction,
			CrustType[] SurfaceTypes)> buildSurface,
		bool takeOwnership = false)
	{
		if (stepIndex < 0) throw new ArgumentOutOfRangeException(nameof(stepIndex), "步号不得为负");
		if (plateCount < 1) throw new ArgumentOutOfRangeException(nameof(plateCount), "板块数必须为正");
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (crust == null) throw new ArgumentNullException(nameof(crust));
		if (heightM == null) throw new ArgumentNullException(nameof(heightM));
		if (oceanAgeMa == null) throw new ArgumentNullException(nameof(oceanAgeMa));
		if (oceanicCoverage == null) throw new ArgumentNullException(nameof(oceanicCoverage));
		if (volcanicRateKmPerMa == null) throw new ArgumentNullException(nameof(volcanicRateKmPerMa));
		if (plateBoundaries == null) throw new ArgumentNullException(nameof(plateBoundaries));
		if (boundary == null) throw new ArgumentNullException(nameof(boundary));

		int n = plateOfCell.Count;
		if (crust.Type.Length != n || heightM.Count != n || oceanAgeMa.Count != n
			|| oceanicCoverage.Count != n || volcanicRateKmPerMa.Count != n)
			throw new ArgumentException("逐格投影数组长度必须一致");

		StepIndex = stepIndex;
		PlateCount = plateCount;
		// 演化投影直接接收本帧新数组。公开构造口仍复制调用者的数据。
		_plateIds = Snapshot(plateOfCell, takeOwnership);
		_plateOfCell = Array.AsReadOnly(_plateIds);
		Crust = crust;
		MaterialHeightM = Array.AsReadOnly(Snapshot(heightM, takeOwnership));
		_oceanAgeMa = Array.AsReadOnly(Snapshot(oceanAgeMa, takeOwnership));
		_oceanicCoverage = Array.AsReadOnly(Snapshot(oceanicCoverage, takeOwnership));
		_volcanicRateKmPerMa = Array.AsReadOnly(Snapshot(volcanicRateKmPerMa, takeOwnership));
		PlateBoundaries = plateBoundaries;
		Boundary = boundary;
		_surface = new Lazy<(IReadOnlyList<float>, EvolutionRelief, IReadOnlyList<CrustSurfaceCell>, double,
			IReadOnlyList<CrustType>)>(() =>
		{
			if (buildSurface == null) return (MaterialHeightM, null, null, 0, null);
			var (surface, relief, cells, emergent, surfaceTypes) = buildSurface();
			if (surface.Length != n || cells.Length != n || surfaceTypes.Length != n)
				throw new ArgumentException("最终地表格数与材料投影不一致");
			if (!(emergent >= 0 && emergent <= 1)) throw new ArgumentException("露出陆地比例越界");
			for (int i = 0; i < surface.Length; i++)
				if (!float.IsFinite(surface[i])) throw new ArgumentException($"格 {i} 的最终高程不是有限数");
			return (Array.AsReadOnly(surface), relief, Array.AsReadOnly(cells), emergent, Array.AsReadOnly(surfaceTypes));
		});
	}

	/// <summary>校验逐格归属、壳型、数值范围与边界对齐，并完成地表校验。</summary>
	/// <exception cref="ArgumentException">任一项越界或不一致。</exception>
	/// <example><code>projection.Validate(288122, 8);</code></example>
	public void Validate(int expectedCellCount, int expectedPlateCount)
		=> Validate(expectedCellCount, expectedPlateCount, includeSurface: true);

	internal void Validate(int expectedCellCount, int expectedPlateCount, bool includeSurface)
	{
		if (expectedPlateCount != PlateCount)
			throw new ArgumentException($"投影板块数 {PlateCount} ≠ 期望 {expectedPlateCount}");
		if (_plateOfCell.Count != expectedCellCount)
			throw new ArgumentException($"投影格数 {_plateOfCell.Count} ≠ 期望 {expectedCellCount}");
		if (Crust.Type.Length != expectedCellCount)
			throw new ArgumentException($"投影壳型格数 {Crust.Type.Length} ≠ 期望 {expectedCellCount}");
		if (PlateBoundaries.CellEdgeStart.Length != expectedCellCount + 1)
			throw new ArgumentException("边界集关联表与格数不一致");
		PlateBoundaries.Validate(expectedCellCount, _plateIds);
		Boundary.Validate(PlateBoundaries.Edges.Length, PlateCount);

		for (int i = 0; i < expectedCellCount; i++)
		{
			if ((uint)_plateOfCell[i] >= (uint)PlateCount)
				throw new ArgumentOutOfRangeException(nameof(PlateOfCell), $"格 {i} 的板块号 {_plateOfCell[i]} 越界");
			if (!float.IsFinite(MaterialHeightM[i]))
				throw new ArgumentException($"格 {i} 的高程不是有限数");
			if (_oceanicCoverage[i] < 0 || _oceanicCoverage[i] > 1 || !double.IsFinite(_oceanicCoverage[i]))
				throw new ArgumentOutOfRangeException(nameof(OceanicCoverage), $"格 {i} 的洋壳覆盖率非法");
			if (_oceanAgeMa[i] != null && (!(_oceanAgeMa[i] >= 0) || !double.IsFinite(_oceanAgeMa[i].Value)))
				throw new ArgumentOutOfRangeException(nameof(OceanAgeMa), $"格 {i} 的海底年龄非法");
			if (_volcanicRateKmPerMa[i] < 0 || !double.IsFinite(_volcanicRateKmPerMa[i]))
				throw new ArgumentOutOfRangeException(nameof(VolcanicRateKmPerMa), $"格 {i} 的火山速率非法");
		}
		if (includeSurface) _ = HeightM;
	}

	static T[] Snapshot<T>(IReadOnlyList<T> values, bool takeOwnership)
	{
		if (takeOwnership && values is T[] array) return array;
		var copy = new T[values.Count];
		for (int i = 0; i < copy.Length; i++) copy[i] = values[i];
		return copy;
	}
}
