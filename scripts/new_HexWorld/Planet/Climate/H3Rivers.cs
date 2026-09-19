using System;
using System.Collections.Generic;
using Godot;
using World.Utils;                         // SphericalFbmNoise（EarthRadiusKm 物理半径）
using World.Utils.H3;

namespace World.NewHexWorld.Plate
{
	// 河流走廊：河流不是叠加线条，而是表面网格的一部分——走廊格（有河的基格）在显示网格里被替换成 res+1 的 7 个子格（H3 aperture-7，
	// CellToBoundary 给出截角后的真实多边形，与邻格严丝合缝），河路径占其中 1~2 个子格
	// （基格流向边 → 沿大圆采样 → LatLngToCell(res+1) → 命中的子格 = 河格）。河宽 = 子格宽
	// （res5 基 → res6 子格 ≈ 6.4 km，贴合真实大河；res3 基 → 45 km，机制验证档）。
	// 河格由渲染端走"河流材质"（shader 按区域数据 g 通道混水色），不做独立地图模式。
	//
	// 分级 = **物理汇水面积**（出流 ÷ 全球均降水 = 等效上游格数，× 格面积；分辨率无关）：
	//   ≥15 万 km² = 支流（≈易北河）/ ≥100 万 = 干流（≈多瑙河）/ ≥400 万 = 大河（≈密西西比）。
	// 分级只用于河格的深浅（材质档），不做用户可见的"水系"分类。
	// ⚠️ 粗分辨率单格面积即超支流档（res3 一格 120 万 km²）——凡入海径流皆河，是口径的诚实推论。
	public static class H3Rivers
	{
		public const float TributaryAreaKm2 = 1.5e5f;   // 支流（≈易北河流域）
		public const float MainstemAreaKm2 = 1.0e6f;    // 干流（≈多瑙河/尼罗河级）
		public const float TrunkAreaKm2 = 4.0e6f;       // 大河（≈密西西比/亚马逊级）

		/// <summary>河流等级 0..3：出流深 ÷ 全球均降水 = 等效汇水格数，× 格面积 = 汇水面积，
		/// 对照三档物理面积阈值。</summary>
		public static int GradeOf(float dischargeMm, float meanPrecipMm, int cellCount)
		{
			if (dischargeMm <= 0f || meanPrecipMm <= 1e-3f || cellCount <= 0) return 0;
			float cellAreaKm2 = 4f * MathF.PI * SphericalFbmNoise.EarthRadiusKm
				* SphericalFbmNoise.EarthRadiusKm / cellCount;
			float areaKm2 = dischargeMm / meanPrecipMm * cellAreaKm2;
			if (areaKm2 >= TrunkAreaKm2) return 3;
			if (areaKm2 >= MainstemAreaKm2) return 2;
			if (areaKm2 >= TributaryAreaKm2) return 1;
			return 0;
		}

		/// <summary>逐格等级（0 = 无河：洋格/汇水不足/内流路径）。</summary>
		public static byte[] ComputeGrades(float[] dischargeMm, float meanPrecipMm, int cellCount)
		{
			int n = dischargeMm?.Length ?? 0;
			var grades = new byte[n];
			for (int i = 0; i < n; i++) grades[i] = (byte)GradeOf(dischargeMm[i], meanPrecipMm, cellCount);
			return grades;
		}

		// ── 走廊构建 ──

		/// <summary>走廊构建产物：哪些基格被细分、每个子格的河档（渲染端烘焙与建网格共用）。</summary>
		public sealed class Corridors
		{
			/// <summary>基格 → 是否细分（下标 = Ball.CellIds 对齐）。</summary>
			public bool[] Corridor;
			/// <summary>走廊格的子格 id 表（外层序 = 基格升序，内层 = H3 CellToChildren 序）。</summary>
			public List<ulong[]> Children;
			/// <summary>与 Children 同构：每个子格的河档 0..3（河路径上的子格 = 所在流向边的出流格档）。</summary>
			public List<byte[]> ChildGrades;
		/// <summary>子格总数（Σ Children.Count；显示网格/数据纹理的容量口径）。</summary>
		public int ChildCount
		{
			get
			{
				if (Children == null) return 0;
				int sum = 0;
				foreach (var c in Children) sum += c.Length;
				return sum;
			}
		}
		}

		/// <summary>建走廊（终态一次；纯函数确定性）。corridor = 河格 ∪ 河格的下游陆格（保证链
		/// 连续到海）；河路径追踪 = 每个河格沿自己流向边（格心 → 下游格心）大圆采样，命中的
		/// 子格（自己或下游的走廊子格）记河档 = 出流格档。</summary>
		public static Corridors Build(Ball ball, Crust crust, H3WaterCycle waterCycle)
		{
			int n = ball.CellIds.Length;
			var result = new Corridors { Corridor = new bool[n] };
			if (waterCycle?.RiverDischargeMmYear == null || waterCycle.FlowTarget == null)
				return result;   // 无水账本（未生成/退化）→ 无走廊，网格保持基形态

			float meanPrecipMm = waterCycle.TotalPrecipM > 0
				? (float)(waterCycle.TotalPrecipM / Math.Max(n, 1) * 1000.0)
				: 0f;
			var grades = ComputeGrades(waterCycle.RiverDischargeMmYear, meanPrecipMm, n);

			// ① 走廊格集：河格 + 河格的下游陆格（下游若是洋格不细分——河到海岸即止）
			var corridor = result.Corridor;
			for (int i = 0; i < n; i++)
			{
				if (grades[i] == 0) continue;
				corridor[i] = true;
				int t = waterCycle.FlowTarget[i];
				if (t >= 0 && crust.IsLand(t)) corridor[t] = true;
			}

			// ② 走廊格细分出子格（H3 aperture-7；CellToBoundary = 截角后真实多边形）
			int childRes = ball.Res + 1;
			result.Children = new List<ulong[]>();
			result.ChildGrades = new List<byte[]>();
			var childIdToCell = new Dictionary<ulong, (int rank, int childIdx)>();
			var childOfCell = new Dictionary<int, ulong[]>();
			int corridorRank = 0;
			for (int i = 0; i < n; i++)
			{
				if (!corridor[i]) continue;
				ulong[] children = H3.CellToChildren(ball.CellIds[i], childRes);
				childOfCell[i] = children;
				result.Children.Add(children);
				var childGrades = new byte[children.Length];
				result.ChildGrades.Add(childGrades);
				for (int c = 0; c < children.Length; c++)
					childIdToCell[children[c]] = (corridorRank, c);   // 存走廊位次（ChildGrades 的下标）
				corridorRank++;
			}

			// ③ 河路径追踪：河格 i 的流向边（格心 → 下游格心）大圆采样 → 子格命中即记档
			//    （命中自己或下游走廊格的子格都收——跨界样本不丢，链到海连续）。
			var centers = ball.CellCenters;
			const int SamplesPerEdge = 8;
			for (int i = 0; i < n; i++)
			{
				int grade = grades[i];
				if (grade == 0) continue;
				int t = waterCycle.FlowTarget[i];
				if (t < 0) continue;
				if (!childOfCell.ContainsKey(i) && !childOfCell.ContainsKey(t)) continue;

				Vector3 a = centers[i].Normalized();
				Vector3 b = centers[t].Normalized();
				for (int s = 0; s < SamplesPerEdge; s++)
				{
					Vector3 dir = a.Slerp(b, s / (float)(SamplesPerEdge - 1)).Normalized();
					double lat = Math.Asin(Math.Clamp(dir.Y, -1f, 1f));
					double lng = Math.Atan2(dir.Z, dir.X);
					ulong hit = H3.LatLngToCell(new LatLng(lat, lng), childRes);
					if (childIdToCell.TryGetValue(hit, out var slot))
						result.ChildGrades[slot.rank][slot.childIdx] =
							Math.Max(result.ChildGrades[slot.rank][slot.childIdx], (byte)grade);
				}
			}
			return result;
		}
	}
}
