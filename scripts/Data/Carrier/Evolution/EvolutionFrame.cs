using System;
using System.Collections.Generic;

namespace World.Data;

/// <summary>
/// 一次已提交的地质状态：定义、时钟、板块姿态、材料、地幔池、逐格投影与收支账本。
/// 成员在构造时固定；跨成员的步号、板块数、库存与积分总量在这一层校验。
/// </summary>
public sealed class EvolutionFrame
{
	readonly IReadOnlyList<PlateMotion> _motions;

	/// <summary>冻结定义。</summary>
	public EvolutionDefinition Definition { get; }
	/// <summary>当前时钟。</summary>
	public EvolutionClock Clock { get; }
	/// <summary>逐板块运动状态（长度 = 板块数）。</summary>
	public IReadOnlyList<PlateMotion> Motions => _motions;
	/// <summary>地壳材料真源。</summary>
	public EvolutionCrust Material { get; }
	/// <summary>全球被动地幔库存。</summary>
	public MantleReservoir Mantle { get; }
	/// <summary>逐格只读投影。</summary>
	public CrustProjection Projection { get; }
	/// <summary>物质收支账本。</summary>
	public EvolutionBudget Budget { get; }
	/// <summary>本步在无相应构造过程处修正的面积，单位 km²。修正不改变地壳体积。</summary>
	public double CoverageCorrectionKm2 { get; }

	/// <summary>建立帧快照；只做引用装配，不复制大数组（各成员本身已不可变）。</summary>
	/// <exception cref="ArgumentNullException">任一成员为空。</exception>
	/// <example><code>var frame = new EvolutionFrame(definition, clock, motions, material, mantle, projection, budget);</code></example>
	public EvolutionFrame(EvolutionDefinition definition, EvolutionClock clock,
		IReadOnlyList<PlateMotion> motions, EvolutionCrust material, MantleReservoir mantle,
		CrustProjection projection, EvolutionBudget budget, double coverageCorrectionKm2 = 0)
	{
		Definition = definition ?? throw new ArgumentNullException(nameof(definition));
		Clock = clock ?? throw new ArgumentNullException(nameof(clock));
		if (motions == null) throw new ArgumentNullException(nameof(motions));
		var copy = new PlateMotion[motions.Count];
		for (int i = 0; i < copy.Length; i++)
			copy[i] = motions[i] ?? throw new ArgumentException($"第 {i} 个板块运动为空", nameof(motions));
		_motions = Array.AsReadOnly(copy);
		Material = material ?? throw new ArgumentNullException(nameof(material));
		Mantle = mantle ?? throw new ArgumentNullException(nameof(mantle));
		Projection = projection ?? throw new ArgumentNullException(nameof(projection));
		Budget = budget ?? throw new ArgumentNullException(nameof(budget));
		if (!double.IsFinite(coverageCorrectionKm2) || coverageCorrectionKm2 < 0)
			throw new ArgumentOutOfRangeException(nameof(coverageCorrectionKm2));
		CoverageCorrectionKm2 = coverageCorrectionKm2;
	}

	/// <summary>校验跨成员一致性：步号、板块数、库存与账本、材料引用。</summary>
	/// <exception cref="ArgumentException">任一成员与帧不一致。</exception>
	/// <example><code>frame.Validate();</code></example>
	public void Validate()
		=> Validate(includeSurface: true);

	internal void Validate(bool includeSurface)
	{
		Definition.Validate();
		WorldSpec frozen = Definition.CreateSpecCopy();
		EvolutionSpec spec = frozen.Evolution;
		if (Clock.StepIndex != Projection.StepIndex)
			throw new ArgumentException($"时钟步号 {Clock.StepIndex} ≠ 投影步号 {Projection.StepIndex}");
		if (Clock.StepYears != spec.StepYears)
			throw new ArgumentException("时钟步长与冻结配置不一致");
		if (_motions.Count != Definition.PlateCount)
			throw new ArgumentException($"板块运动数 {_motions.Count} ≠ 冻结板块数 {Definition.PlateCount}");
		for (int i = 0; i < _motions.Count; i++)
		{
			_motions[i].Validate(Definition.PlateCount);
			if (_motions[i].PlateId != i)
				throw new ArgumentException($"第 {i} 个板块运动的板块号 {_motions[i].PlateId} 不是 {i}");
		}
		if (Projection.PlateCount != Definition.PlateCount)
			throw new ArgumentException($"投影板块数 {Projection.PlateCount} ≠ 冻结板块数 {Definition.PlateCount}");
		if (Math.Abs(Budget.CurrentMantleVolume - Mantle.StoredVolumeKm3) > 1e-9)
			throw new ArgumentException(
				$"账本当前库存 {Budget.CurrentMantleVolume:G17} ≠ 地幔池 {Mantle.StoredVolumeKm3:G17}");
		Budget.Validate(spec.VolumeAbsoluteToleranceKm3, spec.VolumeRelativeTolerance);
		if (Material.PlateCount != Definition.PlateCount)
			throw new ArgumentException("材料槽板块数与冻结定义不一致");
		Projection.Validate(Material.CellCount, Definition.PlateCount, includeSurface);
		double crustVolume = Material.VolumeKm3;
		if (Math.Abs(crustVolume - Budget.CurrentCrustVolume)
			> spec.VolumeAbsoluteToleranceKm3 + spec.VolumeRelativeTolerance * crustVolume)
			throw new ArgumentException(
				$"材料体积 {crustVolume:G17} 与账本当前地壳体积 {Budget.CurrentCrustVolume:G17} 不一致");
	}
}
