using System;
using System.Collections.Generic;
using Godot;
using World.Tectonics;                 // MaterialDensity

namespace World.NewHexWorld.Plate
{
	// 均衡位移 / 岩石圈挠曲 / 水量守恒海平面（设计-03 §2.2 第 9–10 步 / §6）。
	//
	// ⚠️ **与老实现的口径差异（改进 3，2026-09-11 用户批"洋底走 02 §4 热沉降律、与密度链解耦"）**：
	// 老实现把"地形"和"驱动力"压在同一个年龄变量上——`均衡位移 = t − t·ρ/ρ_mantle`，于是无裂谷时
	// 洋壳全老化到密度上限 → 位移全同（平坦 −455 m）→ 海平面二分失效 → "全是海洋"（代码注释自承）。
	// 而且该式对洋底标定极差：t=7100/ρ=2890 时只有 +427 m（真实洋底 −4 km）——老实现只好再叠一层
	// "洋壳年龄噪声梯度"的补丁。
	//
	// 本类改成**基准 + 增量**两段式（与 02 §5.1 的增量 Airy 口径同构）：
	//   基准：陆格 = LandBaselineM；洋格 = −(RidgeAxisDepthM + SubsidenceCoefM·√age)   ← 02 §4 律，age 封顶
	//   增量：Δh = kAiry · (厚度 − 该类型参考厚度)
	//         kAiry = 1 − ρ_felsic/ρ_mantle（用本模块的 MaterialDensity 表算，不新造常量）
// 于是：① 洋底绝对深度正确（−2.5 → −6.8 km 随年龄）；② 碰撞增厚（陆格堆到 60 km）给
// +5~6 km 高原；③ 洋壳堆叠（弧）能出水成岛；④ 海平面不再依赖"密度饱和"这种脆弱链条。
//
// **v1.9.1（2026-09-12）陆格基准改相对海平面**：洋底基准是绝对水深而海平面解在 −3 km 量级，
// 陆格基准若也写绝对坐标（旧 +800），渲染海拔 = 位移 − 海平面 会把大陆整体抬高一个海平面深度
// （v1.9 板级陆洋初始化下实测露出均值 +5.6 km，地球真实 ~0.84 km）。现陆格 = SeaLevel + 800 + 增量。
	public sealed class H3Isostasy
	{
	// ── 基准与参考厚度（可调旋钮；口径 = 初始地壳生成用的那套厚度，03 §8）──
	public const float LandBaselineM = 800f;              // 陆格基准 = **相对海平面**的自由板高度（同 01 的 LandElevationM；v1.9.1 起）
	// 陆壳参考厚 = 01 §3.3 的陆壳初始厚（35 km；地球大陆平均 35–40 km）——它同时是**自由板 800 m
	// 对应的壳厚**：陆壳做到这个厚度就落在自由板上，厚出来的部分按 kAiry 抬升。
	// ⚠️ v1.11 由 28300 抬到 35000：28300 是老 Init 山地模板曲线的**膝点**（最薄的低地壳），
	// 不是大陆平均厚——参考薄了 6.7 km，等于让所有被增厚的碰撞带**平白多抬 kAiry×6700 ≈ 1.0 km**，
	// 峰值于是在 70 km 帽处饱和到 **+7.2 km**（02 §5.2 用户拍板的设计峰值是 +5.3 km / 峰值壳厚 60 km）。
	public const float LandReferenceThicknessM = 35000f;  // 陆壳参考厚度（01 §3.3 陆壳初始厚）
		public const float OceanReferenceThicknessM = 7100f;  // 洋壳参考厚度（老 Init 的洋壳模板值）
		public const float RidgeAxisDepthM = 2500f;           // 脊轴水深（age = 0；02 §4）
		public const float SubsidenceCoefM = 350f;            // 热沉降系数（m/√My；02 §4）
		public const float AgeCapMy = 150f;                   // 年龄封顶（02 §4）

		public float SeaLevel { get; private set; }
		/// <summary>水量守恒的**基准平均海洋深度**（m；按洋面积平均，04 批次 6 由 2000 改 3700 =
		/// 地球真值 3688 取整、与 `H3Plate.OceanDepthM` 同源）。旧值 2000 是老实现位移口径（±500 m）的遗留：
		/// 配上本模型的热沉降剖面（脊轴 −2500 → 老洋底 −6786，自然均值 ≈ −5400 m），海平面被反解到 −3~−4 km ⇒
		/// 脊轴整条露出水面成"长条陆地"、弧格一沾长英质就从 −4 km 跳到 +0.6 km（相变悬崖）。
		/// ⚠️ **2026-09-15 修分母口径前**，求解目标乘的是全球格数 ⇒ 实际平均水深 = TOD/洋占比
		/// （60% 洋时 6157 m，且随演化洋面积缩小海平面暴涨到 +32 km——世界整体通胀的元凶）。
		/// 修正后实测（res1/8板/seed42 初始）：海底 −1321~−4839 m 均值 −3700 ✓，海平面（位移坐标）≈ −2.2 km，
		/// 脊轴在水下几百米（同地球量级），全程 600 My 稳定在 −2.0~−2.8 km。</summary>
		public const float BaseMeanOceanDepthM = 3700f;
	public double TotalOceanDepth;                        // 水量守恒常量（平均海洋深度 m）

	public float[] Displacement;                          // 均衡位移（m，相对基准面）

	// 洋格索引缓存（性能，v1.9.2）：海平面只由洋格体积决定（陆格恒高于海平面，不入体积和），
	// 二分只扫洋格。由最近一次 ComputeRaw 填充；调用方须保证位移/陆性未变——管线内成立
	// （Step 第 6 步填充，第 8 步挠曲后重解时字段未变）。
	readonly List<int> _oceanCells = new List<int>();
	readonly float[] _flex;                          // 挠曲负载场（04 批次 1：预分配复用，原每次 new float[n]×2）
	readonly float[] _flexBuffer;

		public H3Isostasy(int cellCount)
		{
			Displacement = new float[cellCount];
			_flex = new float[cellCount];
			_flexBuffer = new float[cellCount];
		}

		public static float AiryFactor(MaterialDensity material) => 1f - material.FelsicPlutonic / material.Mantle;

	/// <summary>逐格均衡位移（**基准 + 增量**，见类注释）→ Displacement。
	/// 陆格基准是**相对海平面**的（v1.9.1 修口径错位：LandBaselineM 语义是"低地海拔"，而海平面
	/// 由洋格体积解出、落在位移坐标的 −3 km 量级——此前陆格基准被写进绝对坐标，大陆整体被抬高
	/// 一个海平面深度，实测露出 +5.6 km）。两遍收敛：海平面只由洋格体积决定（陆格恒高于海平面，
	/// 不入体积和），先按当前 SeaLevel 算全场 → 解海平面 → 陆格用新海平面重写即精确。</summary>
	public float[] ComputeDisplacement(Ball ball, H3PlateFields fields, MaterialDensity material)
	{
		ComputeRaw(ball, fields, material);
		SolveSeaLevelByVolume();
		ComputeRaw(ball, fields, material);
		return Displacement;
	}

	// 单遍位移：洋格 = 绝对热沉降基准 + Airy 增量；陆格 = SeaLevel + LandBaselineM + Airy 增量。
	// 顺带缓存洋格索引（海平面二分只扫洋格）。
	void ComputeRaw(Ball ball, H3PlateFields fields, MaterialDensity material)
	{
		int n = ball.CellIds.Length;
		float airy = AiryFactor(material);
		_oceanCells.Clear();
		for (int i = 0; i < n; i++)
		{
			float thickness = fields.Thickness(i, material);
			float age = Math.Min(fields.Age[i], AgeCapMy);
			if (fields.IsLand(i))
				Displacement[i] = SeaLevel
					+ LandBaselineM
					+ airy * (thickness - LandReferenceThicknessM);
			else
			{
				Displacement[i] = -(RidgeAxisDepthM + SubsidenceCoefM * MathF.Sqrt(MathF.Max(age, 0f)))
					+ airy * (thickness - OceanReferenceThicknessM);
				_oceanCells.Add(i);
			}
		}
	}

		/// <summary>海平面重解后把**陆格位移跟到新海平面上**——陆格位移 = 海平面 + 自由板 + Airy 增量，
		/// 与海平面**强耦合**（v1.9.1 口径），而装饰项（挠曲下沉、海沟下挖）是加在面上的几何量、不随之重算。
		/// ⚠️ 缺了这一步会出**百余米的系统偏移**：`SolveSeaLevelByVolume` 用"已挠曲/已下挖"的洋底重解海平面
		/// （实测单步移动 ~118 m），而陆格位移留在旧海平面上 ⇒ 渲染海拔 = 位移 − 海平面 整体偏掉这个差值
		/// （2026-09-14 由「造山高原面比厚度帽的 Airy 当量高 118 m」的断言反查出来）。
		/// 口径同 `ComputeDisplacement` 的两遍收敛（那里的第二遍就是同一件事的全场版）。</summary>
		public void RebaseLandToSeaLevel(Ball ball, H3PlateFields fields, float previousSeaLevel)
		{
			float delta = SeaLevel - previousSeaLevel;
			if (delta == 0f) return;
			for (int i = 0; i < Displacement.Length; i++)
				if (fields.IsLand(i)) Displacement[i] += delta;
		}

		/// <summary>岩石圈挠曲（老实现 ApplyFlexure 口径：山体负载 → 邻域下沉 = 前陆盆地）。
		/// 负载 = 正位移，8 轮邻域平滑扩散，位移减去 flex×FlexureFactor。
		/// ⚠️ 顺序纪律：挠曲是**加在均衡基准面上的装饰**，必须在海平面解出之后调用；若之后再重解海平面，
		/// 必须随之调 <see cref="RebaseLandToSeaLevel"/>（见其注释）。</summary>
		public void ApplyFlexure(Ball ball, float flexureFactor = 0.35f, int relaxationRounds = 8)
		{
			int n = ball.CellIds.Length;
			var neighbors = ball.CellNeighbors;
			var flex = _flex;
			var buffer = _flexBuffer;
			for (int i = 0; i < n; i++) flex[i] = Displacement[i] > 0f ? Displacement[i] : 0f;
			for (int round = 0; round < relaxationRounds; round++)
			{
				for (int i = 0; i < n; i++)
				{
					float sum = flex[i];
					foreach (int nb in neighbors[i]) sum += flex[nb];
					buffer[i] = sum / (neighbors[i].Length + 1);
				}
				Array.Copy(buffer, flex, n);
			}
			for (int i = 0; i < n; i++) Displacement[i] -= flex[i] * flexureFactor;
		}

		/// <summary>水量守恒海平面（老实现 SolveSeaLevelByVolume 口径）：二分找使"平均海洋深度 =
		/// TotalOceanDepth"的基准面。性能（v1.9.2）：只扫洋格缓存（陆格不入体积和，根不变）。
		/// ⚠️ **分母口径修正（2026-09-15）**：目标原乘**全球格数** Displacement.Length、总和却只扫洋格
		/// ⇒ 实际强制的是"水量 ÷ 全球格数 = TOD"，平均水深（按洋面积）被放大成 TOD/洋占比——60% 洋时
		/// 实测 6157 m（= 3700×842/506 严丝合缝），比地球锚定值 3700 深 66%；且随演化洋面积缩小继续
		/// 夸张（洋占 9.5% 时海平面解到 +32 km）。现改按**洋格数**计，TotalOceanDepth 语义 =
		/// "按洋面积平均的水深"（与地球 3688 m 同口径）。</summary>
		public float SolveSeaLevelByVolume()
		{
			if (TotalOceanDepth <= 0) return SeaLevel;
			var cells = _oceanCells;
			if (cells.Count == 0) return SeaLevel;             // 全陆世界：无水量约束（退化）
			// ⚠️ 搜索区间必须**双向包住解**：本模型的洋底基准是 −2.5~−6.8 km（02 §4 热沉降律），
			// 满足"全球平均水深 = TotalOceanDepth"的基准面是**负值**——老实现取 low = 0 只在
			// 它那套 ±0.5 km 的位移口径下成立（首跑实测：区间卡在 0 → 海平面恒 0，2026-09-11）。
			// 上界取 max_disp + TOD：mid 高过它时 Σ洋格深度 ≥ TOD×洋格数 = 目标，必已越过根。
			float low = float.MaxValue, high = float.MinValue;
			for (int k = 0; k < cells.Count; k++)
			{
				float d = Displacement[cells[k]];
				if (d < low) low = d;
				if (d > high) high = d;
			}
			low -= 1f;
			high += (float)TotalOceanDepth + 1f;
			double target = TotalOceanDepth * (double)cells.Count;   // 按洋格数：平均水深 = TOD（按洋面积）
			for (int iter = 0; iter < 30; iter++)
			{
				float mid = (low + high) * 0.5f;
				double sum = 0;
				for (int k = 0; k < cells.Count; k++)
				{
					float depth = mid - Displacement[cells[k]];
					if (depth > 0) sum += depth;
				}
				if (sum < target) low = mid; else high = mid;
			}
			SeaLevel = (low + high) * 0.5f;
			return SeaLevel;
		}

		/// <summary>陆占比（位移高于海平面的格占比）。</summary>
		public float LandFraction()
		{
			int land = 0;
			foreach (float d in Displacement) if (d > SeaLevel) land++;
			return Displacement.Length > 0 ? (float)land / Displacement.Length : 0f;
		}
	}
}
