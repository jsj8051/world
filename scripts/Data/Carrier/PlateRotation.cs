using System;
using World.Utils;

namespace World.Data;

/// <summary>一轮板块旋转指令；轴位于世界坐标，角度为本轮总弧度，不是每年的速率。</summary>
public sealed class PlateRotation
{
	public int PlateId { get; }
	public SpherePointD Axis { get; }
	public double AngleRadians { get; }

	/// <summary>指定任意有限旋转角，零、负角和超过一整圈的角度都合法。</summary>
	/// <example><code>var move = new PlateRotation(0, new SpherePointD(0, 1, 0), Math.PI);</code></example>
	public PlateRotation(int plateId, SpherePointD axis, double angleRadians)
	{
		if (plateId < 0) throw new ArgumentOutOfRangeException(nameof(plateId));
		if (!double.IsFinite(angleRadians)) throw new ArgumentOutOfRangeException(nameof(angleRadians));
		PlateId = plateId;
		Axis = axis ?? throw new ArgumentNullException(nameof(axis));
		AngleRadians = angleRadians;
	}
}
