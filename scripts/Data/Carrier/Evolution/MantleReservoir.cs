using System;

namespace World.Data;

/// <summary>
/// 全球被动地幔物质池：只存等效材料体积，不模拟对流、温度或成分。
/// 俯冲材料入池，新洋壳与火山增生从池中释放。
/// </summary>
public sealed class MantleReservoir
{
	/// <summary>当前可释放库存（等效 km³），恒为非负有限数。</summary>
	public double StoredVolumeKm3 { get; }

	/// <summary>建立库存快照；负值或非有限值即抛。</summary>
	/// <exception cref="ArgumentOutOfRangeException">库存为负或非有限。</exception>
	/// <example><code>var pool = new MantleReservoir(1e10);</code></example>
	public MantleReservoir(double storedVolumeKm3)
	{
		if (!double.IsFinite(storedVolumeKm3) || storedVolumeKm3 < 0)
			throw new ArgumentOutOfRangeException(nameof(storedVolumeKm3), "地幔库存必须是非负有限数");
		StoredVolumeKm3 = storedVolumeKm3;
	}
}
