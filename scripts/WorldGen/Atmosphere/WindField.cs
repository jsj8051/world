using System;

namespace World.WorldGen;

/// <summary>
/// WindField 事实容器（⑯ 批次 2；设计 §五）。与 <see cref="MonthlyTemperature"/> 同纪律：
/// **状态/事实，不是模型**（生产者是 <see cref="WindFieldModel"/>）；cell 维与 <c>ball.CellIds</c> 逐位对齐；
/// 每格都有值（无 NaN / 无 null）；res4 内存 ≈ 288,122 × 9 B ≈ **2.6 MB**。
///
/// ★存储口径（契约 W-S1/W-S2）：三个派生统计量，**不建** 12 月 u/v 场。
/// ★命名（拍板 ②）：<c>DirectionTo</c> = 气流**去向**扇区（BearingTo 语义），与气象学来向相反。
/// </summary>
public sealed class WindField
{
	/// <summary>扇区总数（设计 W-O1：16 扇区；8 方向被否决——P4-5e 平流指向精度不足）。</summary>
	public const int SectorCount = 16;

	/// <summary>格数（与 <c>ball.CellIds</c> 长度一致）。</summary>
	public int CellCount { get; private set; }

	/// <summary>
	/// 逐格盛行风向（**去向**扇区）：0 = 静风哨兵（全年无风带内 / 无盛行方向 / 速度低于门槛），1..16 = 扇区
	/// （扇区 s 覆盖 [(s−1)·22.5°, s·22.5°)，中心 (s−0.5)·22.5°，正北顺时针）。
	/// </summary>
	public byte[] DirectionTo { get; private set; } = Array.Empty<byte>();

	/// <summary>逐格平均风速（m/s）：12 个月带基速的算术平均（无风带月按 0.4× 计）；钳位 [0, <see cref="WindParameters.MaxSpeedMs"/>]。</summary>
	public float[] SpeedMs { get; private set; } = Array.Empty<float>();

	/// <summary>
	/// 逐格季风反转指数 ∈[0,1]（W-O3）。★批次 2 恒 0——季风机制属批次 4（设计 §十一），
	/// 本数组先行占位以钉死**数据布局与容量**（W-S2：消费者所需的字段族一次到位）。
	/// </summary>
	public float[] MonsoonIndex { get; private set; } = Array.Empty<float>();

	internal static WindField Create(int cellCount)
	{
		var f = new WindField
		{
			CellCount = cellCount,
			DirectionTo = new byte[cellCount],
			SpeedMs = new float[cellCount],
			MonsoonIndex = new float[cellCount],
		};
		return f;
	}
}
