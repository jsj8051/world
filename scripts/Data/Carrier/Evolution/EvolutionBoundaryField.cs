using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace World.Data;

/// <summary>与 <see cref="PlateBoundarySet.Edges"/> 逐边对齐的物理运动与过程分类。</summary>
public sealed class EvolutionBoundaryField
{
	readonly ReadOnlyCollection<double> _normalSpeed;
	readonly ReadOnlyCollection<double> _tangentSpeed;
	readonly ReadOnlyCollection<EvolutionMotionKind> _motionKind;
	readonly ReadOnlyCollection<ConvergenceKind> _convergenceKind;
	readonly ReadOnlyCollection<int> _underthrustingPlateId;
	readonly (CrustType A, CrustType B)[] _sideTypes;

	/// <summary>法向相对速率（km/Ma；负汇聚、正张裂）。</summary>
	public IReadOnlyList<double> NormalSpeedKmPerMa => _normalSpeed;
	/// <summary>切向相对速率（km/Ma）。</summary>
	public IReadOnlyList<double> TangentSpeedKmPerMa => _tangentSpeed;
	/// <summary>逐边运动类型。</summary>
	public IReadOnlyList<EvolutionMotionKind> MotionKinds => _motionKind;
	/// <summary>汇聚边的碰撞 / 俯冲分类。</summary>
	public IReadOnlyList<ConvergenceKind> ConvergenceKinds => _convergenceKind;
	/// <summary>俯冲边被俯冲板块号；非俯冲为 −1。</summary>
	public IReadOnlyList<int> UnderthrustingPlateIds => _underthrustingPlateId;

	/// <summary>建立逐边快照；长度不一致或数值非法即抛。</summary>
	/// <exception cref="ArgumentException">数组长度不一致或含非法值。</exception>
	/// <example><code>var f = new EvolutionBoundaryField(normal, tangent, kinds, conv, under);</code></example>
	public EvolutionBoundaryField(IReadOnlyList<double> normalSpeedKmPerMa,
		IReadOnlyList<double> tangentSpeedKmPerMa,
		IReadOnlyList<EvolutionMotionKind> motionKinds,
		IReadOnlyList<ConvergenceKind> convergenceKinds,
		IReadOnlyList<int> underthrustingPlateIds)
	{
		if (normalSpeedKmPerMa == null) throw new ArgumentNullException(nameof(normalSpeedKmPerMa));
		if (tangentSpeedKmPerMa == null) throw new ArgumentNullException(nameof(tangentSpeedKmPerMa));
		if (motionKinds == null) throw new ArgumentNullException(nameof(motionKinds));
		if (convergenceKinds == null) throw new ArgumentNullException(nameof(convergenceKinds));
		if (underthrustingPlateIds == null) throw new ArgumentNullException(nameof(underthrustingPlateIds));

		int count = normalSpeedKmPerMa.Count;
		if (tangentSpeedKmPerMa.Count != count || motionKinds.Count != count
			|| convergenceKinds.Count != count || underthrustingPlateIds.Count != count)
			throw new ArgumentException("逐边场各数组长度必须一致");

		var normal = new double[count];
		var tangent = new double[count];
		var kinds = new EvolutionMotionKind[count];
		var convergence = new ConvergenceKind[count];
		var under = new int[count];
		for (int i = 0; i < count; i++) normal[i] = normalSpeedKmPerMa[i];
		for (int i = 0; i < count; i++) tangent[i] = tangentSpeedKmPerMa[i];
		for (int i = 0; i < count; i++) kinds[i] = motionKinds[i];
		for (int i = 0; i < count; i++) convergence[i] = convergenceKinds[i];
		for (int i = 0; i < count; i++) under[i] = underthrustingPlateIds[i];

		_normalSpeed = new ReadOnlyCollection<double>(normal);
		_tangentSpeed = new ReadOnlyCollection<double>(tangent);
		_motionKind = new ReadOnlyCollection<EvolutionMotionKind>(kinds);
		_convergenceKind = new ReadOnlyCollection<ConvergenceKind>(convergence);
		_underthrustingPlateId = new ReadOnlyCollection<int>(under);
	}

	internal EvolutionBoundaryField(double[] normal, double[] tangent, EvolutionMotionKind[] kinds,
		ConvergenceKind[] convergence, int[] under, (CrustType A, CrustType B)[] sideTypes)
		: this(normal, tangent, kinds, convergence, under)
	{
		if (sideTypes.Length != normal.Length) throw new ArgumentException("边界两侧壳型与边数不一致");
		_sideTypes = sideTypes;
	}

	internal (CrustType A, CrustType B) SideTypes(int edge, CrustType fallbackA, CrustType fallbackB) =>
		_sideTypes != null ? _sideTypes[edge] : (fallbackA, fallbackB);

	/// <summary>校验边数与板块引用；不重算运动。</summary>
	/// <exception cref="ArgumentException">边数不符或板块号越界。</exception>
	/// <example><code>field.Validate(120, 8);</code></example>
	public void Validate(int edgeCount, int plateCount)
	{
		if (_normalSpeed.Count != edgeCount)
			throw new ArgumentException($"逐边场边数 {_normalSpeed.Count} ≠ 边界集边数 {edgeCount}");
		for (int i = 0; i < _normalSpeed.Count; i++)
		{
			if (!double.IsFinite(_normalSpeed[i]) || !double.IsFinite(_tangentSpeed[i]))
				throw new ArgumentException($"边 {i} 的速率不是有限数");
			if (!Enum.IsDefined(_motionKind[i]))
				throw new ArgumentException($"边 {i} 的运动类型未定义");
			if (!Enum.IsDefined(_convergenceKind[i]))
				throw new ArgumentException($"边 {i} 的汇聚类型未定义");
			int under = _underthrustingPlateId[i];
			if (under != -1 && (uint)under >= (uint)plateCount)
				throw new ArgumentOutOfRangeException(nameof(UnderthrustingPlateIds),
					$"边 {i} 的俯冲板块号 {under} 越界");
			if (_convergenceKind[i] != ConvergenceKind.Subduction && under != -1)
				throw new ArgumentException($"边 {i} 非俯冲却带俯冲板块号 {under}");
		}
	}
}
