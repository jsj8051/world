using System;

namespace World.Data;

/// <summary>一块板块的演化状态：板块号、物理欧拉角速度与累计球面姿态。壳型不在这里。</summary>
public sealed class PlateMotion
{
	/// <summary>板块号（等于板块表下标）。</summary>
	public int PlateId { get; }
	/// <summary>欧拉矢量（rad/Ma）：方向为极轴、模为角速率；零矢量合法。</summary>
	public SphereVectorD OmegaRadPerMa { get; }
	/// <summary>从零步到当前的累计姿态（单位四元数）。</summary>
	public RotationD Orientation { get; }

	/// <summary>建立板块演化状态；非有限分量或非法板块号即抛。</summary>
	/// <exception cref="ArgumentNullException">欧拉矢量或姿态为空。</exception>
	/// <exception cref="ArgumentOutOfRangeException">板块号越界或分量非有限。</exception>
	/// <example><code>var m = new PlateMotion(0, new SphereVectorD(0, 0, 0.01), RotationD.Identity);</code></example>
	public PlateMotion(int plateId, SphereVectorD omegaRadPerMa, RotationD orientation)
	{
		PlateId = plateId;
		OmegaRadPerMa = omegaRadPerMa ?? throw new ArgumentNullException(nameof(omegaRadPerMa));
		Orientation = orientation ?? throw new ArgumentNullException(nameof(orientation));
		OmegaRadPerMa.Validate();
		Orientation.Validate();
	}

	/// <summary>校验板块号范围；姿态与欧拉矢量的合法性在构造时已检查。</summary>
	/// <exception cref="ArgumentOutOfRangeException">板块号越界。</exception>
	/// <example><code>motion.Validate(8);</code></example>
	public void Validate(int plateCount)
	{
		if (plateCount < 1 || (uint)PlateId >= (uint)plateCount)
			throw new ArgumentOutOfRangeException(nameof(PlateId), $"板块号 {PlateId} 越界（板块数 {plateCount}）");
	}
}
