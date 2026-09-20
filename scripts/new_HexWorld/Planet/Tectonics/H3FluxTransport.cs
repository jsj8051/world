using System;
using System.Collections.Generic;
using Godot;
using World.Tectonics;                     // MaterialDensity（密度表单一出处）

namespace World.NewHexWorld.Plate
{
	// 通量化物质运输（设计-07 P3 核心）：速度场的欧拉 upwind 通量输运，替代跳格搬运。
	//
	// 与旧跳格平流（H3PlateAdvection）的语义对照：
	//   · 跳格"整格沿流向走一格 + 起跳相位" → 逐边 upwind 通量（物质按速度连续流出，CFL 子步进）；
	//   · 顶死裁决（Pass C 链头停） → 通量阻断：大陆来料撞异板 = 阻断（物质堆在自己格 = 造山加厚）；
	//   · 俯冲埋入 → 俯冲汇：洋壳来料流入异板 = 该份额不落到对方格，而是销毁回地幔 + 板片账户入账
	//     + 弧回流长英质只给洋→陆的上盘陆格（按埋入 mafic 计，与跳格路径同口径——记 ArcFelsicReturned
	//     账，接线方 book 进伺服）。
	//   · 离散空洞注壳 → 薄柱注壳：真离散板缘（异板邻边发散）上的薄柱格注入 age=0 新洋壳（记创建账），
	//     每步注壳总量受当步俯冲回地幔量钳制（预算闭环，P3 第二批.5 白化球教训——无差别注壳让
	//     "注壳+弧回流"的净增生跑赢"俯冲销毁"，地壳净增、全星隆起白化）。
	// 守恒纪律：搬运组内严格守恒（纯转移）；质量台账只有两户进出——俯冲回地幔（销毁）与薄柱注壳（创建），
	// 由调用方（接线批次）记入 CrustCreatedTotal/RecycledToMantle 与板片账户。
	//
	// 过渡语义（登记）：格年龄 Age 不随通量迁移（岩石圈热年龄视为格位属性，P3b 重审）；
	// PlateId 标签不随通量翻色（随物质走的归属规则留给接线批次的多数决/生命周期）。
	// 纯函数式双缓冲：Step(ball, source, target, ...) 读 source 写 target，source 不动。
	public sealed class H3FluxTransport
	{
		// ── 旋钮（判读后可调）──
		/// <summary>单子步最大流出份额（CFL）：每格每子步流出的柱份额上限。</summary>
		public float MaxOutflowFraction = 0.5f;
		/// <summary>俯冲再循环：埋入层沉积类（sediment/sedimentary）回地幔份额（旧 1 = 全额）。</summary>
		public float RecycleSedimentFraction = 1f;
		/// <summary>俯冲再循环：长英质刮削份额（旧 0.2）。</summary>
		public float RecycleFelsicFraction = 0.2f;
		/// <summary>弧岩浆回流系数：**洋→陆**俯冲每埋入 1 kg 镁铁质，向上盘陆格从地幔
		/// 新生长英质火山岩的比例（与跳格路径同门控同基数；洋-洋俯冲不触发）。</summary>
		public float ArcFelsicReturnFraction = 0.5f;
		/// <summary>薄柱注壳阈值（m）：柱厚低于此注入 age=0 新洋壳至参考厚（旧"离散空洞注壳"的连续版）。</summary>
		public float NewCrustThicknessThresholdM = 3550f;
		/// <summary>离散发育阈值（km/My）：异板邻边的法向分离速度超过它才算真离散板缘
		/// （④ 注壳资格门槛；与跳格路径 H3DynamicTectonics.SpreadingSpeedKmPerMy 同源同值）。</summary>
		public float SpreadingSpeedKmPerMy = 0.05f;

		// ── 输出（Step 后有效；接线方记台账）──
		/// <summary>本步薄柱注壳创建质量（kg/m²·格 口径累计；创建账）。</summary>
		public double CreatedMassLastStep { get; private set; }
		/// <summary>本步俯冲回地幔质量（销毁账）。</summary>
		public double RecycledToMantleLastStep { get; private set; }
		/// <summary>本步随俯冲回地幔的守恒组质量（沉积类全额 + 变质/长英质刮削；已并入
		/// RecycledToMantleLastStep）——陆壳收支伺服的输入，接线方 book 进 RecycledConservedTotal
		/// （漏记则伺服永不触发 = 长跑水世界防线哑火，P4 台账补齐）。</summary>
		public double RecycledConservedMassLastStep { get; private set; }
		/// <summary>本步长英质刮削（守恒组销毁里的变质/长英质部分；弧回流的配平对手，判读口；
		/// 接线方 book 进 FelsicScrapedCum——与跳格路径 FelsicScrapedMassLastStep 同式）。</summary>
		public double FelsicScrapedMassLastStep { get; private set; }
		/// <summary>本步弧回流长英质质量（上盘格新生的陆壳入口；接线方 book 进 FelsicArcReturnedCum）。</summary>
		public double ArcFelsicReturnedMassLastStep { get; private set; }
		/// <summary>本步俯冲边数（判读口）。</summary>
		public int SubductEdgesLastStep { get; private set; }
		/// <summary>本步注壳预算 = 当步俯冲回地幔总量（威尔逊旋回闭环：注壳不得超过它；判读口）。</summary>
		public double InjectionBudgetLastStep { get; private set; }
		/// <summary>本步薄柱注壳需求总量（真离散板缘 + 其余薄柱；判读口：需求 &lt; 预算 = 结余不花）。</summary>
		public double InjectionDemandLastStep { get; private set; }
		/// <summary>本步真离散板缘上的薄柱格数（预算优先档；判读口）。</summary>
		public int RidgeCellsLastStep { get; private set; }
		/// <summary>本步板片账户入账条目（板号, 质量面密度, 流向单位向量）——接线方转 AddSlab。</summary>
		public readonly List<(int plate, double massPerArea, Vector3 dir)> SlabInflow = new();

		readonly Ball _ball;

		public H3FluxTransport(Ball ball) => _ball = ball;

		/// <summary>走一步通量输运：读 source 写 target（双缓冲，source 不动）。
		/// velocityRadPerMy = 逐格切向速度（rad/My，外部驱动场的刚体投影）。</summary>
		public void Step(H3PlateFields source, H3PlateFields target, Vector3[] velocityRadPerMy,
			MaterialDensity material, float stepMy, int stepIndex)
		{
			int n = source.Count;
			CreatedMassLastStep = 0;
			RecycledToMantleLastStep = 0;
			RecycledConservedMassLastStep = 0;
			FelsicScrapedMassLastStep = 0;
			ArcFelsicReturnedMassLastStep = 0;
			SubductEdgesLastStep = 0;
			InjectionBudgetLastStep = 0;
			InjectionDemandLastStep = 0;
			RidgeCellsLastStep = 0;
			SlabInflow.Clear();

			CopyInto(source, target);
			// 归属/年龄照抄 source（过渡语义：标签与热年龄不随通量迁移，登记于头注）；
			// 先拷再运输——④ 的注壳 age=0 才不会被覆盖。
			Array.Copy(source.PlateId, target.PlateId, n);
			Array.Copy(source.Age, target.Age, n);

			var centers = _ball.CellCenters;
			var neighbors = _ball.CellNeighbors;
			float radiusM = H3PlateMotion.EarthRadiusKm * 1000f;
			float sceneToM = radiusM / _ball.Radius;
			// ── ① 逐边通量裁决与份额（对无向边一次）──
			// f[i][k] = 格 i 沿第 k 邻边流出给 j 的柱份额（0 = 无/阻断/俯冲汇另行记账）
			var frac = new float[n][];
			var subductEntries = new List<(int from, int to, float fraction)>();   // 俯冲边：来料格→上盘格
			var ridgeCell = new bool[n];                // 真离散板缘格（④ 注壳资格；异板邻边发散）
			for (int i = 0; i < n; i++) frac[i] = new float[neighbors[i].Length];

			for (int i = 0; i < n; i++)
			{
				var nb = neighbors[i];
				for (int k = 0; k < nb.Length; k++)
				{
					int j = nb[k];
					if (j <= i) continue;
					Vector3 radial = (centers[i] + centers[j]).Normalized();
					Vector3 t = (centers[j] - centers[i]) - radial * (centers[j] - centers[i]).Dot(radial);
					float dl = t.Length();
					if (dl <= 1e-12f) continue;
					t /= dl;
					float vi = TangentialSpeed(velocityRadPerMy[i], radial, t);   // i 的物质流向 j 的速度分量
					float vj = TangentialSpeed(velocityRadPerMy[j], radial, t);
					float dPhys = (centers[j] - centers[i]).Length() * sceneToM;
					// rad/My → 弧长 m/My（×R）；份额 = 本步位移 / 格距
					float f = MathF.Abs(vi) * radiusM * stepMy / dPhys;
					float fj = MathF.Abs(vj) * radiusM * stepMy / dPhys;

					int pi = source.PlateId[i], pj = source.PlateId[j];
					bool foreign = pi >= 0 && pj >= 0 && pi != pj;
					// 真离散板缘（P3 第二批.5）：异板边 + 法向相对速度分离（t̂ 从 i 指向 j，
					// (vj−vi) 为正 = 两格相互远离）≥ 离散发育阈值。洋中脊/陆内裂谷离散侧——
					// 注壳只落这些格；板内（同板）假离散与汇聚边一律无注壳资格。
					if (foreign && (vj - vi) * H3PlateMotion.EarthRadiusKm >= SpreadingSpeedKmPerMy)
					{
						ridgeCell[i] = true;
						ridgeCell[j] = true;
					}
					if (foreign && vi > 0f)
					{
						// i 的来料撞 j：密度裁决——顶死 = 阻断（物质堆在 i = 造山）；否则俯冲汇
						if (H3PlateContact.JamsInto(
								source.Density(i, material), source.Density(j, material),
								source.IsLand(i), source.IsLand(j)))
							f = 0f;
						else
						{
							subductEntries.Add((i, j, f));                        // 俯冲汇：不落到 j，见 ③
							SubductEdgesLastStep++;
							f = 0f;
						}
					}
					if (foreign && vj > 0f)
					{
						if (H3PlateContact.JamsInto(
								source.Density(j, material), source.Density(i, material),
								source.IsLand(j), source.IsLand(i)))
							fj = 0f;
						else
						{
							subductEntries.Add((j, i, fj));
							SubductEdgesLastStep++;
							fj = 0f;
						}
					}
					frac[i][k] = f;
					int back = FindBackSlot(j, i);
					if (back >= 0) frac[j][back] = fj;
				}
			}

			// ── ② 每格流出预算钳制 + 子步进搬运（upwind：池份额随柱份额走）──
			int subSteps = 1;
			var outBudget = new float[n];
			for (int i = 0; i < n; i++)
			{
				float sum = 0;
				foreach (var f in frac[i]) sum += f;
				outBudget[i] = sum;
				if (sum > MaxOutflowFraction)
				{
					int need = (int)MathF.Ceiling(sum / MaxOutflowFraction);
					subSteps = Math.Max(subSteps, need);
				}
			}

			for (int sub = 0; sub < subSteps; sub++)
			{
				for (int i = 0; i < n; i++)
				{
					var nb = neighbors[i];
					for (int k = 0; k < nb.Length; k++)
					{
						int j = nb[k];
						if (j <= i) continue;
						Transfer(frac[i][k] / subSteps, i, j);
						Transfer(frac[j][k] / subSteps, j, i);
					}
				}
			}

			// ── ③ 俯冲三分账（合账必平）：来料格按份额 f 移除柱 →
			//      弧回流（ArcFelsicReturn 份额，长英质落到上盘格）
			//    + 回地幔（沉积/长英质按再循环份额、镁铁质全额）
			//    + 增生（余额落到上盘格）。
			//    板片账户按全部移除质量入账（来料板名下，方向 = 来料流速）。
			float[] rem = new float[7];
			foreach (var (from, to, f) in subductEntries)
			{
				double total = 0;
				var pools = new[] { target.Sediment, target.Sedimentary, target.Metamorphic,
					target.FelsicPlutonic, target.FelsicVolcanic, target.MaficVolcanic, target.MaficPlutonic };
				for (int k = 0; k < 7; k++)
				{
					rem[k] = pools[k][from] * f;
					total += rem[k];
					pools[k][from] -= rem[k];
				}
				// 弧岩浆回流：**只对洋→陆俯冲**（上盘陆格、来料洋格）且按埋入 mafic 通量记账——
				// 与跳格路径（H3PlateAdvection Pass C）逐字同口径。白化球教训：通量路径曾对
				// 一切俯冲边付 0.5×整柱（洋-洋俯冲是通量路径的默认收敛结局，~1500 边/步），
				// 弧回流净增生跑赢全部销毁户，陆格 >5km 45%、全星白化。洋-洋不触发——不造
				// intra-oceanic 弧（口径漂移 = 设计-07 §1.2 的架构税，两侧各写一份迟早漂）。
				if (source.IsLand(to) && !source.IsLand(from))
				{
					float arc = (rem[5] + rem[6]) * ArcFelsicReturnFraction;
					if (arc > 0f)
					{
						target.FelsicVolcanic[to] += arc;
						ArcFelsicReturnedMassLastStep += arc;
					}
				}
				float[] recycleShares = { RecycleSedimentFraction, RecycleSedimentFraction, RecycleFelsicFraction,
					RecycleFelsicFraction, RecycleFelsicFraction, 1f, 1f };
				for (int k = 0; k < 7; k++)
				{
					float toMantle = rem[k] * recycleShares[k];
					RecycledToMantleLastStep += toMantle;
					if (k < 5) RecycledConservedMassLastStep += toMantle;   // 守恒组五池分账（伺服输入）
					pools[k][to] += rem[k] - toMantle;    // 余额增生到上盘格
				}
				FelsicScrapedMassLastStep += (double)(rem[2] + rem[3] + rem[4]) * RecycleFelsicFraction;
				Vector3 dir = velocityRadPerMy[from];
				if (dir.LengthSquared() > 1e-18f)
					SlabInflow.Add((source.PlateId[from], total, dir.Normalized()));
			}

			// ── ④ 薄柱注壳（P4 后修订——预算闭环保留、资格回调）：白化球的两刀是"无预算"与
			//      "弧回流失控"；"只落真离散板缘"是过度矫正——刚体场离散伪影掏薄的板内海域
			//      无人回填 ⇒ 洋壳净销毁、海平面缓漂，薄柱地形贴着下降的海面冒头（洋内金链、
			//      散点白斑、岸缘深带）。现在一切薄柱格都有注壳需求，预算内**真离散板缘优先**
			//      （洋中脊语义保留），余量回填其余薄柱——注壳总量仍 ≤ 当步俯冲回地幔量
			//      （威尔逊旋回按构造闭合，白化球不可能回归）。遍历序固定 ⇒ 确定性。
			double injectBudget = RecycledToMantleLastStep;
			var demand = new float[n];
			double demandRidge = 0, demandOther = 0;
			int ridgeThinCells = 0;
			for (int i = 0; i < n; i++)
			{
				if (source.PlateId[i] < 0) continue;
				float maficMass = target.MaficVolcanic[i] + target.MaficPlutonic[i];
				float thickness = maficMass / material.MaficVolcanicMin;
				if (thickness >= NewCrustThicknessThresholdM) continue;
				demand[i] = (NewCrustThicknessThresholdM - thickness) * material.MaficVolcanicMin;
				if (ridgeCell[i]) { demandRidge += demand[i]; ridgeThinCells++; }
				else demandOther += demand[i];
			}
			InjectionBudgetLastStep = injectBudget;
			InjectionDemandLastStep = demandRidge + demandOther;
			RidgeCellsLastStep = ridgeThinCells;
			float ridgeScale = demandRidge > 0 ? (float)Math.Min(1.0, injectBudget / demandRidge) : 0f;
			float otherScale = 0f;
			double ridgeSpent = Math.Min(injectBudget, demandRidge);   // 优先档实际花费（可为 0）
			if (demandOther > 0 && injectBudget - ridgeSpent > 0)
				otherScale = (float)Math.Min(1.0, (injectBudget - ridgeSpent) / demandOther);
			for (int i = 0; i < n; i++)
			{
				if (demand[i] <= 0f) continue;
				float injectMass = demand[i] * (ridgeCell[i] ? ridgeScale : otherScale);
				if (injectMass <= 0f) continue;
				target.MaficVolcanic[i] += injectMass;
				target.Age[i] = 0f;
				CreatedMassLastStep += injectMass;
			}

			static float TangentialSpeed(Vector3 v, Vector3 radial, Vector3 t)
				=> (v - radial * v.Dot(radial)).Dot(t);

			int FindBackSlot(int j, int i)
			{
				var nb = _ball.CellNeighbors[j];
				for (int k = 0; k < nb.Length; k++)
					if (nb[k] == i) return k;
				return -1;
			}

			void Transfer(float fraction, int from, int to)
			{
				if (fraction <= 0f) return;
				// 读演化态 target（同一子步内多条出边顺序扣减；预算钳制保证不透支）
				float m = target.Sediment[from] * fraction;
				target.Sediment[to] += m;
				target.Sediment[from] -= m;
				m = target.Sedimentary[from] * fraction;
				target.Sedimentary[to] += m;
				target.Sedimentary[from] -= m;
				m = target.Metamorphic[from] * fraction;
				target.Metamorphic[to] += m;
				target.Metamorphic[from] -= m;
				m = target.FelsicPlutonic[from] * fraction;
				target.FelsicPlutonic[to] += m;
				target.FelsicPlutonic[from] -= m;
				m = target.FelsicVolcanic[from] * fraction;
				target.FelsicVolcanic[to] += m;
				target.FelsicVolcanic[from] -= m;
				m = target.MaficVolcanic[from] * fraction;
				target.MaficVolcanic[to] += m;
				target.MaficVolcanic[from] -= m;
				m = target.MaficPlutonic[from] * fraction;
				target.MaficPlutonic[to] += m;
				target.MaficPlutonic[from] -= m;
			}
		}

		static void CopyInto(H3PlateFields from, H3PlateFields to)
		{
			int n = from.Count;
			Array.Copy(from.Sediment, to.Sediment, n);
			Array.Copy(from.Sedimentary, to.Sedimentary, n);
			Array.Copy(from.Metamorphic, to.Metamorphic, n);
			Array.Copy(from.FelsicPlutonic, to.FelsicPlutonic, n);
			Array.Copy(from.FelsicVolcanic, to.FelsicVolcanic, n);
			Array.Copy(from.MaficVolcanic, to.MaficVolcanic, n);
			Array.Copy(from.MaficPlutonic, to.MaficPlutonic, n);
			Array.Copy(from.Age, to.Age, n);
			Array.Copy(from.PlateId, to.PlateId, n);
		}
	}
}
