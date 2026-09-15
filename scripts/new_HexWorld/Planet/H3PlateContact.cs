using Godot;
using World.Tectonics;                 // MaterialDensity

namespace World.NewHexWorld.Plate
{
	// 板间接触判定（v1.8 抽出共用）：平流 Pass C 的链头落地规则与运动学的"顶死零功修正"
	// 必须是**同一条判据**——运动学剔除的就是平流即将顶死的格，两处各写一份迟早口径漂移。
	// 语义（03 §3.2 流链元胞，v1.7 定稿）：链头撞异板物质，更密则俯冲埋入，不比目标密则顶死。
	internal static class H3PlateContact
	{
		// 沿流向取落点邻居：切向投影最大者（邻居序固定 → 平局保先到者，确定性）。
		// 运动学传边界法线（驱动方向已钉死 = 法线方向，H3PlateMotion 头注），平流传实际速度的切向。
		public static int FlowNeighbor(Vector3[] centers, int[][] neighbors, int cell, Vector3 dir)
		{
			int best = -1;
			float bestDot = -2f;
			Vector3 radial = centers[cell].Normalized();
			foreach (int nb in neighbors[cell])
			{
				Vector3 toNeighbor = centers[nb] - centers[cell];
				Vector3 tangential = toNeighbor - radial * toNeighbor.Dot(radial);
				float length = tangential.Length();
				if (length < 1e-12f) continue;
				float candidate = dir.Dot(tangential / length);
				if (candidate > bestDot) { bestDot = candidate; best = nb; }
			}
			return best;
		}

		// 接触顶死判据：来料格 from 撞上格 target，来料不比目标密（Density(target) >= Density(from)）
		// → 顶死；更密则俯冲（= 本判据取反）。调用方负责"异板物质"前提
		// （PlateId[target] >= 0 且 != PlateId[from]）——无主格/同板格不是接触。
		public static bool JamsInto(H3PlateFields fields, MaterialDensity material, int from, int target)
			=> JamsInto(fields.Density(from, material), fields.Density(target, material));

		// 密度直给版（04 批次 1）：调用方持有本步派生缓存时走这条，免两次七场求和——
		// 判据本体在此，两条入口共用（防口径漂移）。
		public static bool JamsInto(float densityFrom, float densityTarget)
			=> densityTarget >= densityFrom;
	}
}
