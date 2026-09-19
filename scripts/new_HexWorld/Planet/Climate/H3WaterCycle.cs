using System;
using Godot;

namespace World.NewHexWorld.Plate
{
	// 水量均衡：水循环三库存账本——海水 + 云 + 河 = 总水量（定值），
	// 与地壳质量账本（H3DynamicTectonics：初始+创建−消减）同一纪律。
	//
	// 稳态年循环的账目结构（全部以"米×格"记账——均匀格面假设与 H3Isostasy 海平面二分同口径，
	// 面积在分子分母间消去，不引入格面积常量）：
	//   通量闭合：洋面蒸发是全球唯一水源，λ 缩放降水结构场使 Σ降水 = Σ蒸发（λ = ΣE/ΣP_raw）；
	//   储存：云 = Σ降水 × 大气停留时间（8 天，水汽收支口径）；河 = 入海路径逐格出流量 × 河道
	//         停留时间（15 天，地球河流库存 ~2000 km³ / 年径流 ~47000 km³ 比值）——两者从总水量中
	//         【预留】，洋格海平面目标均深 = TotalOceanDepth − 预留均深（见 H3Isostasy 重载）；
	//   内流盆地：汇流止于洼地的水不计河道库存——年尺度上就地蒸发回归大气（稳态不占长期储存），
	//         通量层面已含在 λ 闭合内。
	// 蒸发标定：全球洋面均值锚定地球水循环 1200 mm/yr（洋面年蒸发真值量级），温度只造**空间差**
	// （f = exp(4.5%/°C × ΔT)，暖池 ~3000 / 极地海 ~300，与实测量级同向；不用模型绝对温度定标——
	// 本模型全球均值偏冷（纬度基准 52·cos¹·¹−22），直接乘绝对温度会把全球降水压到 ~200 mm）。
	public sealed class H3WaterCycle
	{
		// ── 标定常量（地球锚定）──
		public const float EvapRefMmPerYear = 1200f;   // 全球洋面平均蒸发（mm/yr，地球水循环口径）
		const float EvapTempSlopePerC = 0.045f;        // 蒸发温度响应（4.5%/°C，Clausius-Clapeyron 量级折中）
		public const float AtmResidenceYears = 8f / 365f;    // 水汽在大气中的停留时间（~8 天）
		public const float RiverResidenceYears = 15f / 365f; // 河道停留时间（2000/47000 km³/yr ≈ 15.5 天取整）

		// ── 产出场（下标与 Ball.CellIds 对齐；Run 后只读）──
		public float[] PrecipMmYear;          // 年降水（闭合后，全球 Σ = 全球 Σ 蒸发）
		public float[] EvapMmYear;            // 年蒸发（洋格；陆格 0）
		public float[] RiverDischargeMmYear;  // 入海路径逐格出流量（诊断；非入海路径/洋格 = 0）
		public int[] FlowTarget;              // 逐格陡降流向（下游格下标；洼地汇/洋格 = -1）——
		                                      // 河流走廊细分（H3Rivers）按它把流向边追成子格链

		// ── 账本（米×格；供测试对账与 UI 诊断）──
		public double TotalEvapM;             // Σ_洋格 E（m·cell）
		public double TotalPrecipM;           // Σ_全球 P（m·cell；λ 闭合后 ≈ TotalEvapM，差 = float 记账噪声）
		public double AtmStorageM;            // 云库存（m·cell）
		public double RiverStorageM;          // 河库存（m·cell，只计入海路径）
		public int OceanCellCount;            // 洋格数（预留均深分母）

		/// <summary>从总水量中为云/河预留的均深（m/洋格）——**显示/诊断口径**（UI 读它更直观）；
		/// 海平面重解请传 <see cref="ReservedVolumeM"/>（体积口径，账本逐字闭合；见下）。</summary>
		public double ReservedDepthPerOceanCellM => OceanCellCount > 0 ? (AtmStorageM + RiverStorageM) / OceanCellCount : 0;

		/// <summary>从总水量中为云/河预留的体积（米×格）——海平面重解的目标减项。
		/// 不能传均深：容器格数（被淹没格数）与 OceanCellCount 不等，均深 × 格数 ≠ 库存体积。</summary>
		public double ReservedVolumeM => AtmStorageM + RiverStorageM;

		/// <summary>跑水循环账本（纯函数：只读入参、不消耗 rng、同入参逐位一致）。
		/// <param name="precipRawMmYear">H3Precipitation 的结构场（λ=1 原始口径）。</param></summary>
		public static H3WaterCycle Run(Ball ball, Crust crust, float[] tempC, float[] precipRawMmYear)
		{
			int n = ball.CellIds.Length;
			var wc = new H3WaterCycle
			{
				PrecipMmYear = new float[n],
				EvapMmYear = new float[n],
				RiverDischargeMmYear = new float[n],
				FlowTarget = new int[n],
			};

			// 1) 洋面蒸发：均值锚定 EvapRef，温度只造空间差（归一化消掉模型绝对温度的冷偏差）
			var elev = crust.Elevation;
			double tSum = 0; int oceanCount = 0;
			for (int i = 0; i < n; i++)
			{
				if (crust.IsLand(i)) continue;
				tSum += tempC[i];
				oceanCount++;
			}
			wc.OceanCellCount = oceanCount;
			if (oceanCount == 0)
				return wc;   // 全陆退化：无水源无降水，三库存全零（海平面无约束，同 H3Isostasy 退化口）

			float tMean = (float)(tSum / oceanCount);
			double fSum = 0;
			var f = new float[n];
			for (int i = 0; i < n; i++)
			{
				if (crust.IsLand(i)) continue;
				f[i] = MathF.Exp(EvapTempSlopePerC * (tempC[i] - tMean));
				fSum += f[i];
			}
			float fMean = (float)(fSum / oceanCount);
			double evapSumM = 0;
			for (int i = 0; i < n; i++)
			{
				if (crust.IsLand(i)) continue;
				float e = EvapRefMmPerYear * f[i] / fMean;   // mm/yr
				wc.EvapMmYear[i] = e;
				evapSumM += e / 1000.0;
			}
			wc.TotalEvapM = evapSumM;

			// 2) λ 闭合：全球降水总量 = 全球蒸发总量（结构场只定"雨下在哪"，总量由水源定）
			double rawSumM = 0;
			for (int i = 0; i < n; i++) rawSumM += precipRawMmYear[i] / 1000.0;
			double lambda = rawSumM > 0 ? evapSumM / rawSumM : 0;
			double precipSumM = 0;
			for (int i = 0; i < n; i++)
			{
				float p = (float)(precipRawMmYear[i] * lambda);
				wc.PrecipMmYear[i] = p;
				precipSumM += p / 1000.0;
			}
			wc.TotalPrecipM = precipSumM;

			// 3) 汇流：陡降到海（每陆格流向最陡下降邻居，无更低位 → 内流洼地）。海拔降序处理 +
			//    下标升序破并列（确定性）；下游必先于上游处理（降序保证）→ 到海性一遍传播。
			//    排序键打包（高 32 = 海拔键取反 = 降序，低 32 = 下标 = 并列升序）走无委托快路径
			//    ——Comparer<int>.Create 的逐比较委托在 res5（~百万陆格 × log 次比较）终态跑两遍
			//    是百毫秒级常数（与 H3FluvialTransport 同款打包口径，键序与旧比较器同一全序）。
			var neighbors = ball.CellNeighbors;
			var target = new int[n];
			wc.FlowTarget = target;
			var order = new int[n];
			var sortKeys = new long[n];
			int landCount = 0;
			for (int i = 0; i < n; i++)
			{
				target[i] = -1;
				if (!crust.IsLand(i)) continue;
				order[landCount++] = i;
				float bestDrop = 0f;
				int best = -1;
				foreach (int nb in neighbors[i])
				{
					float drop = elev[i] - elev[nb];
					if (drop > bestDrop) { bestDrop = drop; best = nb; }   // 严格 >：并列取最先邻居（id 升序）
				}
				target[i] = best;
			}
			for (int k = 0; k < landCount; k++)
			{
				int i = order[k];
				int bits = BitConverter.SingleToInt32Bits(elev[i]);
				int orderedBits = bits ^ ((bits >> 31) & 0x7FFFFFFF);   // float 全序 → int 全序（升序）
				sortKeys[k] = ((long)(uint)~orderedBits << 32) | (uint)i;   // 取反 = 降序；并列下标升序
			}
			Array.Sort(sortKeys, order, 0, landCount);

			var reaches = new bool[n];
			var outflowM = new float[n];
			for (int i = 0; i < n; i++)
			{
				reaches[i] = !crust.IsLand(i);        // 洋格 = 出海口
				if (crust.IsLand(i)) outflowM[i] = wc.PrecipMmYear[i] / 1000f;
			}
			double riverSumM = 0;
			for (int k = 0; k < landCount; k++)
			{
				int i = order[k];
				int t = target[i];
				if (t < 0 || !reaches[t]) continue;   // 内流洼地：就地蒸发回归大气（不入河账）
				reaches[i] = true;
				outflowM[t] += outflowM[i];
				wc.RiverDischargeMmYear[i] = outflowM[i] * 1000f;
				riverSumM += outflowM[i] * RiverResidenceYears;
			}
			wc.RiverStorageM = riverSumM;

			// 4) 云库存（全球降水都在大气里走过一遭）
			double atmSumM = 0;
			for (int i = 0; i < n; i++) atmSumM += wc.PrecipMmYear[i] / 1000.0 * AtmResidenceYears;
			wc.AtmStorageM = atmSumM;
			return wc;
		}
	}
}
