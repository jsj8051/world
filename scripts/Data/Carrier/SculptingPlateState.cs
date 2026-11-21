using System;
using World.Utils;

namespace World.Data;

/// <summary>板块持久状态；姿态相对本雕刻会话的参考世界，不含物理角速度。</summary>
public sealed class SculptingPlateState
{
	public int PlateId { get; }
	public double Density { get; }
	public SpherePointD ReferenceSeed { get; }
	public RotationD Orientation { get; }
	public bool IsExtinct { get; }
	internal PlateRotation RotationOverride { get; }

	/// <summary>建立不可变板块状态；ID 非负，密度有限，方向及姿态必须合法。</summary>
	/// <example><code>var plate = new SculptingPlateState(0, 3, seed, RotationD.Identity);</code></example>
	public SculptingPlateState(int plateId, double density, SpherePointD referenceSeed, RotationD orientation)
		: this(plateId, density, referenceSeed, orientation, false, null)
	{
	}

	SculptingPlateState(int plateId, double density, SpherePointD referenceSeed, RotationD orientation,
		bool isExtinct, PlateRotation rotationOverride)
	{
		if (plateId < 0) throw new ArgumentOutOfRangeException(nameof(plateId));
		if (!double.IsFinite(density)) throw new ArgumentOutOfRangeException(nameof(density));
		PlateId = plateId;
		Density = density;
		ReferenceSeed = referenceSeed ?? throw new ArgumentNullException(nameof(referenceSeed));
		Orientation = orientation ?? throw new ArgumentNullException(nameof(orientation));
		IsExtinct = isExtinct;
		RotationOverride = rotationOverride;
	}

	internal SculptingPlateState WithOrientation(RotationD orientation)
		=> new(PlateId, Density, ReferenceSeed, orientation, IsExtinct, RotationOverride);

	internal SculptingPlateState Extinguish()
		=> new(PlateId, Density, ReferenceSeed, Orientation, true, RotationOverride);

	internal SculptingPlateState WithFragmentMotion(SpherePointD referenceSeed, PlateRotation rotation)
		=> new(PlateId, Density, referenceSeed, Orientation, IsExtinct, rotation);
}
