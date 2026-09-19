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
	//     + 弧回流长英质给上盘（记 ArcFelsicReturned 账，接线方 book 进伺服）；
	//   · 离散空洞注壳 → 薄柱注壳：格柱被流走变薄到阈值以下时注入 age=0 新洋壳（记创建账）。
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
		/// <summary>弧岩浆回流系数：俯冲质量 × 此比 = 上盘格新生长英质火山岩。</summary>
		public float ArcFelsicReturnFraction = 0.5f;
		/// <summary>薄柱注壳阈值（m）：柱厚低于此注入 age=0 新洋壳至参考厚（旧"离散空洞注壳"的连续版）。</summary>
		public float NewCrustThicknessThresholdM = 3550f;

		// ── 输出（Step 后有效；接线方记台账）──
		/// <summary>本步薄柱注壳创建质量（kg/m²·格 口径累计；创建账）。</summary>
		public double CreatedMassLastStep { get; private set; }
		/// <summary>本步俯冲回地幔质量（销毁账）。</summary>
		public double RecycledToMantleLastStep { get; private set; }
		/// <summary>本步弧回流长英质质量（上盘格新生的陆壳入口；接线方 book 进 FelsicArcReturnedCum）。</summary>
		public double ArcFelsicReturnedMassLastStep { get; private set; }
		/// <summary>本步俯冲边数（判读口）。</summary>
		public int SubductEdgesLastStep { get; private set; }
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
			ArcFelsicReturnedMassLastStep = 0;
			SubductEdgesLastStep = 0;
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
			var subductMass = new double[n];          // 每格被俯冲销毁的柱份额累计（按池比例分账）
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
					Vector3 midRadial = radial;
					float vi = TangentialSpeed(velocityRadPerMy[i], midRadial, t);   // i 的物质流向 j 的速度分量
					float vj = TangentialSpeed(velocityRadPerMy[j], midRadial, t);
					float dPhys = (centers[j] - centers[i]).Length() * sceneToM;
					// rad/My → 弧长 m/My（×R）；份额 = 本步位移 / 格距
					float f = MathF.Abs(vi) * radiusM * stepMy / dPhys;
					float fj = MathF.Abs(vj) * radiusM * stepMy / dPhys;

					int pi = source.PlateId[i], pj = source.PlateId[j];
					bool foreign = pi >= 0 && pj >= 0 && pi != pj;
					if (foreign && vi > 0f)
					{
						// i 的来料撞 j：密度裁决——顶死 = 阻断（物质堆在 i = 造山）；否则俯冲汇
						bool jam = H3PlateContact.JamsInto(
							source.Density(i, material),
							source.Density(j, material),
							source.IsLand(i),
							source.IsLand(j))
							|| (source.IsLand(j) && !source.IsLand(i) && source.Density(j, material) >= source.Density(i, material));
						if (jam) f = 0f;
						else
						{
							// 俯冲汇：i 的流出份额入俯冲账（销毁 + 板片），不落到 j
							subductMass[i] += f;
							SubductEdgesLastStep++;
							f = 0f;
						}
					}
					if (foreign && vj > 0f)
					{
						bool jam = H3PlateContact.JamsInto(
							source.Density(j, material), source.Density(i, material),
							source.IsLand(j), source.IsLand(i))
							|| (source.IsLand(i) && !source.IsLand(j) && source.Density(i, material) >= source.Density(j, material));
						if (jam) fj = 0f;
						else
						{
							subductMass[j] += fj;
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

			// ── ③ 俯冲记账：被俯冲份额按池比例分账（沉积/长英质回地幔份额、其余销毁）+ 板片 + 弧回流 ──
			for (int i = 0; i < n; i++)
			{
				if (subductMass[i] <= 0f) continue;
				float f = (float)Math.Clamp(subductMass[i], 0.0, 1.0);
				double total = 0, recycled = 0;
				total += Accumulate(source, target, i, f, p => p.Sediment, material.Sediment, RecycleSedimentFraction, ref recycled);
				total += Accumulate(source, target, i, f, p => p.Sedimentary, material.Sedimentary, RecycleSedimentFraction, ref recycled);
				total += Accumulate(source, target, i, f, p => p.Metamorphic, material.Metamorphic, RecycleFelsicFraction, ref recycled);
				total += Accumulate(source, target, i, f, p => p.FelsicPlutonic, material.FelsicPlutonic, RecycleFelsicFraction, ref recycled);
				total += Accumulate(source, target, i, f, p => p.FelsicVolcanic, material.FelsicVolcanic, RecycleFelsicFraction, ref recycled);
				total += Accumulate(source, target, i, f, p => p.MaficVolcanic, material.MaficVolcanicMin, 1f, ref recycled);
				total += Accumulate(source, target, i, f, p => p.MaficPlutonic, material.MaficVolcanicMin, 1f, ref recycled);
				RecycledToMantleLastStep += recycled;

				// 板片账户：质量 × 流向（格 i 的流速方向）；弧回流给本格（上盘判定接线批次细化）
				Vector3 dir = velocityRadPerMy[i];
				if (dir.LengthSquared() > 1e-18f)
					SlabInflow.Add((source.PlateId[i], total, dir.Normalized()));
				float arc = (float)(total * ArcFelsicReturnFraction);
				if (arc > 0f)
				{
					target.FelsicVolcanic[i] += arc;
					ArcFelsicReturnedMassLastStep += arc;
				}
			}

			// ── ④ 薄柱注壳：柱厚低于阈值 → 注 age=0 新洋壳至参考厚（创建账）──
			for (int i = 0; i < n; i++)
			{
				if (source.PlateId[i] < 0) continue;
				float maficMass = target.MaficVolcanic[i] + target.MaficPlutonic[i];
				float thickness = maficMass / material.MaficVolcanicMin;
				if (thickness >= NewCrustThicknessThresholdM) continue;
				float injectMass = (NewCrustThicknessThresholdM - thickness) * material.MaficVolcanicMin;
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

		// 从 target 格 i 按份额 f 移除池 p 的质量 → 返回该池被俯冲的质量（kg/m²），recycleShare 部分回地幔
		double Accumulate(H3PlateFields source, H3PlateFields target, int i, float f,
			Func<H3PlateFields, float[]> pool, float density, float recycleShare, ref double recycled)
		{
			double mass = pool(target)[i] * f;             // 该池被俯冲的质量（kg/m²）
			pool(target)[i] -= pool(target)[i] * f;
			recycled += mass * recycleShare;
			return mass;
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
