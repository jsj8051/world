using System;
using World.Tectonics;          // MaterialDensity（八物质密度表）+ Units
using World.Utils.H3;

namespace World.NewHexWorld.Plate
{
	// 板块物质场（设计-03 §2.1 / §6）：物质随板走，归属由 PlateId 标记，平流时整格搬运。
	//
	// 八物质场（与老 Crust 同名同序，单位 = 质量面密度 kg/m²）：
	//   Sediment / Sedimentary / Metamorphic / FelsicPlutonic / FelsicVolcanic / MaficVolcanic /
	//   MaficPlutonic / Age
	// 守恒组（合并时相加 = 碰撞增厚）= 前 5 场；顶层决定组（取密度最小板）= MaficVolcanic /
	// MaficPlutonic / Age（03 §2.1）。
	//
	// 派生量口径：
	//   厚度 = Σ(质量 / ρ_i)
	//   密度 = 总质量 / 厚度
	//   浮力 = (ρ − ρ_mantle) · g        （≤ 0：比地幔重 → 负浮力 → 下拽）
	//   均衡位移 = t − t · ρ / ρ_mantle  （Airy）
	//
	// 纪律：Age 存 My（不是秒）；浮力/密度不做顶层裁剪——顶层语义只作用于平流合并，本类只报物理量。
	public sealed class H3PlateFields
	{
		public readonly float[] Sediment;
		public readonly float[] Sedimentary;
		public readonly float[] Metamorphic;
		public readonly float[] FelsicPlutonic;
		public readonly float[] FelsicVolcanic;
		public readonly float[] MaficVolcanic;
		public readonly float[] MaficPlutonic;
		public readonly float[] Age;          // 年龄（**My**，非常用秒；见类注释 ①）
		public readonly int[] PlateId;        // 每格归属板（-1 = 无主：平流撕裂出的空洞，待裂谷填新洋壳）

		public int Count => PlateId.Length;

		public H3PlateFields(int count)
		{
			Sediment = new float[count];
			Sedimentary = new float[count];
			Metamorphic = new float[count];
			FelsicPlutonic = new float[count];
			FelsicVolcanic = new float[count];
			MaficVolcanic = new float[count];
			MaficPlutonic = new float[count];
			Age = new float[count];
			PlateId = new int[count];
			Array.Fill(PlateId, -1);
			// 池数组构造时建好（字段引用只读、数组稳定，每步多处调用零分配）。
			_allPools = new[] { Sediment, Sedimentary, Metamorphic, FelsicPlutonic, FelsicVolcanic,
				MaficVolcanic, MaficPlutonic, Age };
			_conservedPools = new[] { Sediment, Sedimentary, Metamorphic, FelsicPlutonic, FelsicVolcanic };
			_topDecidedPools = new[] { MaficVolcanic, MaficPlutonic, Age };
		}

		public void Clear()
		{
			Array.Clear(Sediment, 0, Count);
			Array.Clear(Sedimentary, 0, Count);
			Array.Clear(Metamorphic, 0, Count);
			Array.Clear(FelsicPlutonic, 0, Count);
			Array.Clear(FelsicVolcanic, 0, Count);
			Array.Clear(MaficVolcanic, 0, Count);
			Array.Clear(MaficPlutonic, 0, Count);
			Array.Clear(Age, 0, Count);
			Array.Fill(PlateId, -1);
		}

		// ── 场分组（合并口径的直接依据；03 §2.1）──
		readonly float[][] _allPools;
		readonly float[][] _conservedPools;
		readonly float[][] _topDecidedPools;

		// 全部 8 场（advection 搬运 / 快照用）。返回**共享数组**（构造时建好）——只读消费，
		// 逐元素写合法（元素就是字段本体）；不要缓存返回值以外的副本语义。
		public float[][] AllPools() => _allPools;

		// 守恒组 5 场：合并时**相加**（两侧地壳叠起来 = 碰撞增厚）。
		public float[][] ConservedPools() => _conservedPools;

		// 顶层决定组 3 场：合并时取密度最小（最浮）的那块板。
		public float[][] TopDecidedPools() => _topDecidedPools;

		// ── 派生量 ──

		/// <summary>总质量面密度（kg/m²）= 八场之和。</summary>
		public float TotalMass(int cell) =>
			Sediment[cell] + Sedimentary[cell] + Metamorphic[cell]
			+ FelsicPlutonic[cell] + FelsicVolcanic[cell]
			+ MaficVolcanic[cell] + MaficPlutonic[cell];

		/// <summary>洋壳 mafic 密度随年龄的冷却律：0 → MaficVolcanicMin(2890，年轻洋壳)；
		/// MaficAgeSaturationMy → MaficVolcanicMax(3300，老洋壳)。</summary>
		public const float MaficAgeSaturationMy = 250f;

		public static float MaficDensityAtAge(float ageMy, MaterialDensity material)
		{
			float fraction = Math.Clamp(ageMy / MaficAgeSaturationMy, 0f, 1f);
			return material.MaficVolcanicMin + (material.MaficVolcanicMax - material.MaficVolcanicMin) * fraction;
		}

		/// <summary>厚度（m）= Σ(质量 / 该场密度)。mafic 用**随年龄冷却的密度**（见上）。</summary>
		public float Thickness(int cell, MaterialDensity material)
		{
			float maficDensity = MaficDensityAtAge(Age[cell], material);
			return Sediment[cell] / material.Sediment
				+ Sedimentary[cell] / material.Sedimentary
				+ Metamorphic[cell] / material.Metamorphic
				+ FelsicPlutonic[cell] / material.FelsicPlutonic
				+ FelsicVolcanic[cell] / material.FelsicVolcanic
				+ MaficVolcanic[cell] / maficDensity
				+ MaficPlutonic[cell] / maficDensity;
		}

		/// <summary>密度（kg/m³）= 总质量 / 厚度；厚度为零 → 回落到年轻洋壳密度。</summary>
		public float Density(int cell, MaterialDensity material)
		{
			float thickness = Thickness(cell, material);
			if (thickness <= 1e-6f) return material.MaficVolcanicMin;
			return TotalMass(cell) / thickness;
		}

		/// <summary>物质口径判陆：长英质厚 > 0（渲染口径的判陆在 Crust.IsLand，按海拔）。</summary>
		public bool IsLand(int cell) => FelsicPlutonic[cell] + FelsicVolcanic[cell] > 0f;

		/// <summary>长英质总厚（m）= 深成 + 火山。写回 new_HexWorld 渲染用 Crust 时用。</summary>
		public float FelsicTotalMass(int cell) => FelsicPlutonic[cell] + FelsicVolcanic[cell];

		/// <summary>镁铁质总厚（m）= 火山 + 深成。</summary>
		public float MaficTotalMass(int cell) => MaficVolcanic[cell] + MaficPlutonic[cell];

		// ── 逐格写入（初始化 / 裂谷填新洋壳 / 消减清空）──

		/// <summary>整格清空（消减回地幔、或作为新洋壳写入前的复位）。</summary>
		public void ClearCell(int cell)
		{
			Sediment[cell] = 0f;
			Sedimentary[cell] = 0f;
			Metamorphic[cell] = 0f;
			FelsicPlutonic[cell] = 0f;
			FelsicVolcanic[cell] = 0f;
			MaficVolcanic[cell] = 0f;
			MaficPlutonic[cell] = 0f;
			Age[cell] = 0f;
		}

		/// <summary>整格复制（平流空洞的陆内重采样补料：从最近陆邻居整格拷贝，含板号）。</summary>
		public void CopyCell(int from, int to)
		{
			Sediment[to] = Sediment[from];
			Sedimentary[to] = Sedimentary[from];
			Metamorphic[to] = Metamorphic[from];
			FelsicPlutonic[to] = FelsicPlutonic[from];
			FelsicVolcanic[to] = FelsicVolcanic[from];
			MaficVolcanic[to] = MaficVolcanic[from];
			MaficPlutonic[to] = MaficPlutonic[from];
			Age[to] = Age[from];
			PlateId[to] = PlateId[from];
		}

		/// <summary>写入新洋壳（裂谷 / 洋中脊轴，age = 0 → 最年轻、密度最低 → 热沉降最浅）：
		/// 只填 MaficVolcanic，其余清零。</summary>
		public void SetNewOceanicCrust(int cell, float maficMassPerArea, int plateId)
		{
			ClearCell(cell);
			MaficVolcanic[cell] = maficMassPerArea;
			PlateId[cell] = plateId;
		}
	}
}
