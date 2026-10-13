using System;
using World.Data;            // Plate（板块载体）

namespace World.Logic;

/// <summary>
/// 板块陆地身份回填：按板块统计陆格占比，过阈值即视为陆壳板块，写回 <see cref="Plate.IsLand"/>。
/// </summary>
public static class PlateLandFill
{
	/// <param name="plates">板块表（就地回填 `IsLand`）。</param>
	/// <param name="plateOfCell">逐格板块号（每格恒有归属）。</param>
	/// <param name="land">逐格陆地掩码（true = 陆）。</param>
	/// <param name="isLandThreshold">板块陆格占比 &gt; 它则视为陆壳板块。</param>
	public static void Fill(Plate[] plates, int[] plateOfCell, bool[] land, float isLandThreshold)
	{
		if (plates == null) throw new ArgumentNullException(nameof(plates));
		if (plateOfCell == null) throw new ArgumentNullException(nameof(plateOfCell));
		if (land == null) throw new ArgumentNullException(nameof(land));
		if (plateOfCell.Length != land.Length)
			throw new ArgumentException($"板块号长度 {plateOfCell.Length} ≠ 掩码长度 {land.Length}");

		int n = plateOfCell.Length;
		var total = new int[plates.Length];
		var landCount = new int[plates.Length];

		for (int i = 0; i < n; i++)
		{
			int p = plateOfCell[i];
			if (p < 0 || p >= plates.Length)
				throw new InvalidOperationException($"格 {i} 的板块号 {p} 越界（板块数 {plates.Length}）");
			total[p]++;
			if (land[i]) landCount[p]++;
		}

		// 空板块（total == 0）视为非陆壳（防御）
		for (int p = 0; p < plates.Length; p++)
		{
			float ratio = total[p] > 0 ? (float)landCount[p] / total[p] : 0f;
			plates[p].IsLand = ratio > isLandThreshold;
		}
	}
}
