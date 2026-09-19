using Godot;
using World.Tectonics;                 // MaterialDensity

namespace World.NewHexWorld.Plate
{
	// 板间接触判定（平流 Pass C 与运动学共用的同一条判据——运动学剔除的就是平流即将顶死的格，
	// 两处各写一份迟早口径漂移）。语义（03 §3.2 流链元胞）：链头撞异板物质，更密则俯冲埋入，
	// 不比目标密则顶死。
	internal static class H3PlateContact
	{
		// 沿流向取落点邻居：切向投影最大者（邻居序固定 → 平局保先到者，确定性）。
		// 运动学传边界法线（驱动方向 = 法线方向），平流传实际速度的切向。
		public static int FlowNeighbor(Vector3[] centers, int[][] neighbors, int cell, Vector3 dir)
			=> FlowNeighbor(centers, null, neighbors, cell, dir);

	// 单位方向直给版（模拟热路径）：格心方向构造期已归一（Ball.CellDirs）。
	// unitDirs 为 null 时回退现场归一化（测试直构路径）。
	public static int FlowNeighbor(Vector3[] centers, Vector3[] unitDirs, int[][] neighbors, int cell, Vector3 dir)
	{
		int best = -1;
		float bestDot = -2f;
		Vector3 radial = unitDirs != null ? unitDirs[cell] : centers[cell].Normalized();
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

	// 预计算表版（模拟热路径首选）：邻居切向单位方向来自 Ball.CellNeighborDirs（构造期一次算好，
	// 与上方现场投影逐位同式）——每步对每个起跳格免 6 次投影+sqrt+除法。邻居序与平局规则同源。
	public static int FlowNeighbor(Vector3[][] neighborDirs, int[][] neighbors, int cell, Vector3 dir)
	{
		int best = -1;
		float bestDot = -2f;
		var dirs = neighborDirs[cell];
		var nbs = neighbors[cell];
		for (int k = 0; k < nbs.Length; k++)
		{
			Vector3 tangential = dirs[k];
			if (tangential == Vector3.Zero) continue;          // 退化邻居（与现场版 continue 同语义）
			float candidate = dir.Dot(tangential);
			if (candidate > bestDot) { bestDot = candidate; best = nbs[k]; }
		}
		return best;
	}

		// 接触顶死判据：来料格 from 撞上格 target，且接触上有长英质（陆壳）参与，
		// 来料不比目标密（Density(target) >= Density(from)）→ 顶死；否则俯冲（= 本判据取反）。
		// 调用方负责"异板物质"前提（PlateId[target] >= 0 且 != PlateId[from]）——无主格/同板格不是接触。
		//
		// "需陆壳参与"前提：洋-洋汇聚必须俯冲、不生造山带（03 §3.5 规则表；地球洋内碰撞造山基本
		// 不存在）。若洋-洋也顶死，约一半洋-洋汇聚在海底堆 mafic，再经均衡抬升成海底假山系，
		// 且堆起来就永不俯冲、不进侵蚀 ⇒ 永久定格。
		// 陆侧语义：陆-陆等密顶死（造山）、陆撞洋顶死（活动陆缘造山）、洋撞陆俯冲（密度自然给出）。
		public static bool JamsInto(H3PlateFields fields, MaterialDensity material, int from, int target)
			=> JamsInto(fields.Density(from, material), fields.Density(target, material),
				fields.IsLand(from), fields.IsLand(target));

		// 密度 + 陆性直给版：调用方持有本步派生缓存时走这条，免两次七场求和——
		// 判据本体在此，两条入口共用（防口径漂移）。
		public static bool JamsInto(float densityFrom, float densityTarget, bool continentalFrom, bool continentalTarget)
			=> (continentalFrom || continentalTarget) && densityTarget >= densityFrom;
	}
}
