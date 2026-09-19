using System;
using World.Tectonics;                     // MaterialDensity（密度表单一出处）

namespace World.NewHexWorld.Plate
{
	// 重力位能场（设计-07 P0）：B 方案 §4.2 的"唯一驱动力出处"。
	//
	// 定义：GPE = g·∫ρ(z)·z·dz，z = 相对海平面的高程（向上为正，海面以下为负），从柱顶积分到
	// 共同补偿深度 W——W 以下全球同构，逐格差与 W 无关。GPE 的物理身份 = 每平米柱的位能（N·m/m² = N），
	// 相邻柱的 GPE 差 = 水平驱动力（N/m）：
	//   · 洋脊推力不再是外加力项：年轻洋柱 GPE 高 → 往老洋柱推（量级 1–4e12 N/m，锚点单测钉住）；
	//   · 大陆/高原 GPE 高于老洋底 → 陆内拉张、高原垮塌从同一场涌现。
	// 柱结构（自上而下，轻上重下）：水（海格）→ 沉积 → 沉积岩 → 长英质 → 变质 → 镁铁质
	//   → 岩石圈地幔（冷收缩密度盈余：H3ThermalColumn.NegativeBuoyancy 均匀摊到热岩石圈厚度上）
	//   → 正常地幔到 W。
	// ⚠️ 密度盈余方向：老冷岩石圈比被它顶替的软流圈【致密】（密度盈余 1–2%，见 H3ThermalColumn
	//   "负浮力在冷岩石圈上"注释）——它把老洋柱 GPE 压低，这正是洋脊推力的来源，方向别写反。
	// 纯函数、零状态、可单测（H3ThermalColumn 同款纪律）。
	public static class H3Gpe
	{
		public const float GravityMS2 = 9.8f;
		public const float CompensationDepthM = 150000f;   // W：共同基准深度（不参与梯度）
		/// <summary>地幔/软流圈密度：与热柱亏损同源（3300），不用 MaterialDensity.Mantle（3075 标定值）——
		/// 盈余项 N(age) 以 3300 为参考，混用会在老洋柱上凭空造出 200+ kg/m³ 的假盈余。</summary>
		public const float MantleDensityKgM3 = H3ThermalColumn.MantleDensityKgM3;

		/// <summary>单柱 GPE（N = 每平米柱的位能）。
		/// elevationM 相对海平面（海格为负，水柱自动补上）；四个质量面密度 = 池和
		///（长英质 = FelsicPlutonic+Volcanic，镁铁质 = MaficVolcanic+Plutonic）；ageMy = 热年龄
		///（陆格传 0：大陆岩石圈热稳态、无冷收缩盈余，与 Step 第 1 步的冻结口径一致）。</summary>
		public static float ColumnGpe(MaterialDensity material, float elevationM,
			float sediment, float sedimentary, float metamorphic, float felsic, float mafic,
			float ageMy)
		{
			float gpe = 0f;
			float z = 0f;                                  // 层顶高程游标（向上为正）

			if (elevationM < 0f) AddLayer(ref gpe, ref z, -elevationM, material.Ocean);
			else z = -elevationM;                          // 陆格：岩石柱顶在海面以上（高程为正 → 贡献为正）

			AddLayer(ref gpe, ref z, sediment / material.Sediment, material.Sediment);
			AddLayer(ref gpe, ref z, sedimentary / material.Sedimentary, material.Sedimentary);
			AddLayer(ref gpe, ref z, metamorphic / material.Metamorphic, material.Metamorphic);
			AddLayer(ref gpe, ref z, felsic / material.FelsicPlutonic, material.FelsicPlutonic);
			AddLayer(ref gpe, ref z, mafic / material.MaficVolcanicMin, material.MaficVolcanicMin);

			float age = MathF.Max(ageMy, H3ThermalColumn.MinAgeMy);
			float litho = H3ThermalColumn.LithosphereThicknessM(age);
			float excess = H3ThermalColumn.NegativeBuoyancyKgPerM2(age) / litho;
			float lithoBottomZ = MathF.Max(-litho, -CompensationDepthM);   // L 超 W 时截断（datum 一致性）
			if (z > lithoBottomZ) AddLayer(ref gpe, ref z, z - lithoBottomZ, MantleDensityKgM3 + excess);
			if (z > -CompensationDepthM) AddLayer(ref gpe, ref z, z + CompensationDepthM, MantleDensityKgM3);
			return gpe;
		}

		/// <summary>整场计算（into 与 fields/elevation 对齐）。elevationM = 均衡位移 − 海平面（调用方给）。</summary>
		public static void ComputeInto(H3PlateFields fields, float[] elevationM, MaterialDensity material, float[] into)
		{
			int n = fields.Count;
			for (int i = 0; i < n; i++)
			{
				into[i] = ColumnGpe(material, elevationM[i],
					fields.Sediment[i], fields.Sedimentary[i], fields.Metamorphic[i],
					fields.FelsicPlutonic[i] + fields.FelsicVolcanic[i],
					fields.MaficVolcanic[i] + fields.MaficPlutonic[i],
					fields.Age[i]);
			}
		}

		// 加一层：GPE += g·ρ·t·(层中心高程)；游标 z 下移。层中心高程 = z − t/2（z 向上为正）。
		static void AddLayer(ref float gpe, ref float z, float thicknessM, float rho)
		{
			if (thicknessM <= 0f) return;
			gpe += GravityMS2 * rho * thicknessM * (z - thicknessM * 0.5f);
			z -= thicknessM;
		}
	}
}
