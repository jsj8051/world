namespace World.Data;

/// <summary>
/// ① 地形阶段配置 —— **参数实例**（可变类；参数管理器持有唯一一份）。
/// 属性初值 = **出厂档**，必须与正库默认档 `res/params/world_params.json` 同值。
///
/// 高度 = 陆面（基线 + 三档幅值 × 各自变化场 + 山带 / 老山丘陵 / 高原底座，
/// 再经分形细化 + 虚拟侵蚀）或海底（洋中脊 / 深海平原 / 大陆架 / 大陆坡 / 海沟 / 岛弧）。
/// **陆壳的格铺陆面、洋壳的格铺海底** ⇒ 陆面基线穿过海面，**海岸线就是高度场的结果**。
/// `SeaLevelM` 是**海拔标高**（米，海面高度本身），不是"判陆阈值"（判陆阈值已随陆地场一起删除）。
/// </summary>
public sealed class TerrainSpec
{
	// ── 海陆基准 ──

	/// <summary>海面标高（米）——洋壳的格平这一档，也是「高于它才是陆」的判据
	/// （世界事实 `FinalLand = 高度 &gt; SeaLevelM`、水文的「elev ≤ SeaLevelM 即海」）。
	/// 出厂值 0。</summary>
	public float SeaLevelM { get; set; } = 0f;

	// ── 陆面幅度 ──

	/// <summary>陆面常量基线（米）——陆壳的高度基准。它**穿过海面**（落负值即海底）⇒ 海岸线由它定。
	/// 出厂值 180（配三档幅值：合成 σ ≈ 263 m ⇒ 约 1/4 的陆壳落在海面之下；
	/// 基线高于 σ 之半 ⇒ 海面切在分布偏低位，海陆由大尺度场主导，海岸成片而非碎斑）。</summary>
	public float LandBaseM { get; set; } = 180f;

	/// <summary>大尺度起伏幅值（米）。出厂值 900——**海陆分异的主导项**：其 σ 占合成 σ 的 99%，
	/// 配合大基波长（8000 km）⇒ 全球约 2~4 块大陆，海岸线由它定、平滑成片。</summary>
	public float LargeVariationM { get; set; } = 900f;

	/// <summary>中尺度起伏幅值（米；丘陵 / 山谷，基波长 600 km）——**打破"大色块"的那一层**：
	/// 大尺度项（8000 km 波长）与大陆同量级，光靠它陆面就是几坨平滑大块、档界切不出层次；
	/// 本档给出几百公里一级的丘谷，档界随之变得支离。出厂值 300。</summary>
	public float MediumVariationM { get; set; } = 300f;

	/// <summary>细节起伏幅值（米，基波长 90 km）——谷坡与碎斑。出厂值 120。</summary>
	public float RegionalNoiseM { get; set; } = 120f;

	// ── 三档变化场：基波长（km；相隔这么久一个特征）+ 倍频层数（越细）──

	/// <summary>大尺度基波长（km）——大地形起伏（大陆尺度）。出厂值 8000。</summary>
	public float LargeWavelengthKm { get; set; } = 8000f;

	/// <summary>大尺度层数。出厂值 2。</summary>
	public int LargeOctaves { get; set; } = 2;

	/// <summary>中尺度基波长（km）——丘陵 / 山谷。出厂值 600。</summary>
	public float MediumWavelengthKm { get; set; } = 600f;

	/// <summary>中尺度层数。出厂值 3。</summary>
	public int MediumOctaves { get; set; } = 3;

	/// <summary>细节基波长（km）。出厂值 90。</summary>
	public float RegionalWavelengthKm { get; set; } = 90f;

	/// <summary>细节层数。出厂值 2。</summary>
	public int RegionalOctaves { get; set; } = 2;

	// ── 构造项：山带（汇聚挤压 ⇒ 按角色分档隆起）──

	/// <summary>山带高度的**归一目标**（米）：全场构造抬升的第 99.7 百分位映射到它。
	/// 于是"最高的山"恒有这一档，不随板块速度的整体大小漂移（不同种子 / 不同板块数之间可比）。
	/// 出厂值 4800。</summary>
	public float MountainPeakM { get; set; } = 4800f;

	/// <summary>压峰折点（× <see cref="MountainPeakM"/>）：超过它的抬升按
	/// <see cref="MountainKneeCompress"/> 压缩 ⇒ 不出现离谱的万米高峰。出厂值 0.85。</summary>
	public float MountainKnee { get; set; } = 0.85f;

	/// <summary>折点以上的压缩系数（0.45 ⇒ 超出部分只留 45%）。出厂值 0.45。</summary>
	public float MountainKneeCompress { get; set; } = 0.45f;

	/// <summary>陆×陆**上盘**山带强度（× <see cref="MountainPeakM"/>）。出厂值 1.65。</summary>
	public float MountainUpperCoef { get; set; } = 1.65f;

	/// <summary>陆×陆**下盘**山带强度——下盘同样成山（碰撞带两侧都是山）。出厂值 1.65。</summary>
	public float MountainLowerCoef { get; set; } = 1.65f;

	/// <summary>陆×洋**陆侧**山带强度（安第斯型窄山链）。出厂值 1.45。</summary>
	public float MountainArcCoef { get; set; } = 1.45f;

	/// <summary>**俯冲侧**山带强度（低矮的增生楔）。出厂值 0.35。</summary>
	public float MountainSlabCoef { get; set; } = 0.35f;

	/// <summary>陆×陆山带的**基础**带宽（km）——自边界向陆内的衰减尺度（弱汇聚段取它）。出厂值 260。</summary>
	public float MountainUpperBandKm { get; set; } = 260f;

	/// <summary>山带随汇聚强度**加宽的**量（km）：带宽 = 基础带宽 + 本值 × 汇聚强度
	/// ⇒ 撞得越狠，山系铺得越开（满强度 660 km）。出厂值 400。</summary>
	public float MountainBandConvSpreadKm { get; set; } = 400f;

	/// <summary>陆×洋陆侧山带的**内移距离**（km）——山链不贴海岸，退到内陆这一段（海岸留给大陆架）。出厂值 176。</summary>
	public float MountainArcInlandKm { get; set; } = 176f;

	/// <summary>陆×洋陆侧山带的带宽（km）。出厂值 100。</summary>
	public float MountainArcBandKm { get; set; } = 100f;

	/// <summary>俯冲侧增生楔的带宽（km）。出厂值 117。</summary>
	public float MountainSlabBandKm { get; set; } = 117f;

	/// <summary>山脊噪声基波长（km）——沿带调制，造出山口与高峰（否则是一堵等高墙）。出厂值 700。</summary>
	public float MountainRidgeWavelengthKm { get; set; } = 700f;

	/// <summary>山脊噪声层数。出厂值 3。</summary>
	public int MountainRidgeOctaves { get; set; } = 3;

	/// <summary>山脊调制下限 ∈ [0,1]（噪声谷处的抬升比例；1 = 不调制 ⇒ 等高墙）。
	/// 取低值 ⇒ 山带能**断成一段一段**（有山口、有缺口），而不是绕一圈不断的墙。出厂值 0.15。</summary>
	public float MountainRidgeFloor { get; set; } = 0.15f;

	/// <summary>构造抬升场的一层平滑权重 ∈ [0,1]：<c>u ← (1−w)·自身 + w·邻居均值</c>。
	/// 在归一**之前**做 ⇒ 只削毛刺与段端台阶，不改峰高目标。0 = 关断。出厂值 0.3。</summary>
	public float MountainSmoothWeight { get; set; } = 0.3f;

	// ── 构造项：古老山系与丘陵（远离当代边界的残丘）──

	/// <summary>古老山系的高度（米）——已停止抬升、只剩脊状残丘的老山带（比当代造山带矮一截；
	/// 加性叠在构造抬升上，**不参与压峰** ⇒ 老山 + 主峰可以超过 <see cref="MountainPeakM"/>）。
	/// 出厂值 2000。</summary>
	public float OldMountainHeightM { get; set; } = 2000f;

	/// <summary>丘陵区的高度（米）——成片的缓丘，把大陆内部从"一大块平原"里救出来。出厂值 600。</summary>
	public float HillsHeightM { get; set; } = 600f;

	// ── 构造项：大陆裂谷 ──

	/// <summary>张裂带把抬升压低的强度 ∈ [0,1]（1 = 压成零）——张裂处不该跟着旁边的山一起被抬高。出厂值 0.75。</summary>
	public float RiftSuppressStrength { get; set; } = 0.75f;

	/// <summary>张裂带压低的带宽（km，高斯）。出厂值 391。</summary>
	public float RiftBandKm { get; set; } = 391f;

	// ── 构造项：高原底座（山带背后的一大片平坦高地）──

	/// <summary>陆×陆碰撞带背后高原的**内伸距离**（km）——自边界向陆内铺多远。出厂值 548。</summary>
	public float PlateauUpperReachKm { get; set; } = 548f;

	/// <summary>陆×洋俯冲带上盘高原的内伸距离（km）。出厂值 313。</summary>
	public float PlateauArcReachKm { get; set; } = 313f;

	/// <summary>内伸距离沿走向的抖动幅度（km）——高原"时有时无"、不成一条等宽的带。出厂值 665。</summary>
	public float PlateauReachSpreadKm { get; set; } = 665f;

	/// <summary>高原内缘的上升距离（km）——太贴边界不起（那一圈留给山带本身）。出厂值 235。</summary>
	public float PlateauInnerRampKm { get; set; } = 235f;

	/// <summary>高原外缘的收束跨度（km）。出厂值 313。</summary>
	public float PlateauOuterRampKm { get; set; } = 313f;

	/// <summary>陆×陆碰撞带高原的高度（米）。出厂值 3400。</summary>
	public float PlateauUpperHeightM { get; set; } = 3400f;

	/// <summary>陆×洋俯冲带上盘高原的高度（米）。出厂值 2600。</summary>
	public float PlateauArcHeightM { get; set; } = 2600f;

	/// <summary>高原起势的汇聚强度下限（低于它不起高原）。出厂值 0.25。</summary>
	public float PlateauConvMin { get; set; } = 0.25f;

	/// <summary>高原达到满高所需的汇聚强度。出厂值 0.8。</summary>
	public float PlateauConvFull { get; set; } = 0.8f;

	/// <summary>大陆内部零星台地的高度（米）——非洲 / 巴西那样几百到一千多米的盾状台地。出厂值 1400。</summary>
	public float PlateauShieldHeightM { get; set; } = 1400f;

	/// <summary>高原贴近海岸时的收束高度（米）：陆面高出海面不超过这么多就收掉高原
	/// ⇒ 海岸不成悬崖。出厂值 120。</summary>
	public float PlateauCoastFadeM { get; set; } = 120f;

	/// <summary>高原的平滑遍数（削掉距离场的折线痕迹）。出厂值 1。</summary>
	public int PlateauSmoothPasses { get; set; } = 1;

	// ── 构造项：山体细化与虚拟侵蚀 ──

	/// <summary>山体的**分形细化幅度** ∈ [0,1)：把构造抬升乘上
	/// <c>1 + 本值 × (ridged 噪声 × 2 − 1)</c>，再按其均值归位 ⇒ 只改形状、不改平均幅度。
	/// 0 = 关断（山体保持光滑）。出厂值 0.35。</summary>
	public float MountainDetailAmount { get; set; } = 0.35f;

	/// <summary>虚拟侵蚀的**最大下切**（米）——汇水量最大的那一档切这么多。
	/// 出厂值 900。</summary>
	public float ErosionDepthM { get; set; } = 900f;

	/// <summary>侵蚀的**汇水量指数**：<c>(A/A_max)^本值</c>。越小则支流也切得动（谷网密），
	/// 越大则只有主河道下切（谷网疏而深）。出厂值 0.5。</summary>
	public float ErosionExponent { get; set; } = 0.5f;

	/// <summary>单步下切**不超过当前抬升的这一档比例**（防止把山削穿成沟槽）。出厂值 0.6。</summary>
	public float ErosionMaxCutFraction { get; set; } = 0.6f;

	/// <summary>侵蚀迭代步数（0 = 关断）——每步都重算一遍填洼 / 排水 / 汇流。
	/// 步数越多谷越深越成网，也越慢。出厂值 3。</summary>
	public int ErosionSteps { get; set; } = 3;

	// ── 海底：深度范围 ──

	/// <summary>最浅的海（米，钳上界）——任何海底都不会比它更浅。出厂值 −20。</summary>
	public float OceanShallowestM { get; set; } = -20f;

	/// <summary>最深的海（米，钳下界）——海沟的最终下限。出厂值 −10500。</summary>
	public float OceanDeepestM { get; set; } = -10500f;

	// ── 海底：洋中脊与洋壳年龄 ──

	/// <summary>洋中脊脊顶深度（米）。出厂值 −2700。</summary>
	public float RidgeCrestDepthM { get; set; } = -2700f;

	/// <summary>洋壳"年龄满值"处比脊顶再深的米数——**深度 ∝ √年龄**，
	/// 故最老洋底 = <see cref="RidgeCrestDepthM"/> − 本值（出厂 −5400）。出厂值 2700。</summary>
	public float RidgeAgeDepthM { get; set; } = 2700f;

	/// <summary>洋壳"年龄满值"对应的**离洋中脊距离**（km）——超过它就按最老洋底算。出厂值 3100。</summary>
	public float RidgeAgeSpanKm { get; set; } = 3100f;

	/// <summary>脊顶额外隆起（米，× 该处张裂强度）。出厂值 500。</summary>
	public float RidgeCrestRiseM { get; set; } = 500f;

	/// <summary>脊顶隆起的带宽（km，高斯）。出厂值 270。</summary>
	public float RidgeBandKm { get; set; } = 270f;

	// ── 海底：深海丘陵 ──

	/// <summary>深海丘陵：大尺度起伏幅值（米）。出厂值 300。</summary>
	public float AbyssHillLargeM { get; set; } = 300f;

	/// <summary>深海丘陵：细尺度起伏幅值（米）。出厂值 90。</summary>
	public float AbyssHillFineM { get; set; } = 90f;

	// ── 海底：大陆架与大陆坡 ──

	/// <summary>贴岸处的大陆架深度（米）。出厂值 −30。</summary>
	public float ShelfShallowestM { get; set; } = -30f;

	/// <summary>大陆架外缘深度（米）——到此外缘为止线性变深。出厂值 −180。</summary>
	public float ShelfDeepestM { get; set; } = -180f;

	/// <summary>大陆架最小宽度（km；俯冲带一侧）。出厂值 60。</summary>
	public float ShelfMinWidthKm { get; set; } = 60f;

	/// <summary>大陆架最大宽度（km；被动边缘）。出厂值 565。</summary>
	public float ShelfMaxWidthKm { get; set; } = 565f;

	/// <summary>大陆坡底部深度（米）——大陆架外缘再往下接到这里。出厂值 −6680。</summary>
	public float SlopeBottomM { get; set; } = -6680f;

	/// <summary>大陆坡的水平跨度（km）。出厂值 470。</summary>
	public float SlopeSpanKm { get; set; } = 470f;

	/// <summary>「离岸近 ⇒ 大陆架放宽」的判定距离（km）——离岸小于它就取宽档。出厂值 400。</summary>
	public float MarginWidenKm { get; set; } = 400f;

	/// <summary>汇聚（主动边缘）调制大陆架宽度的带宽（km，高斯）——此处大陆架被压窄。出厂值 585。</summary>
	public float ActiveMarginBandKm { get; set; } = 585f;

	// ── 海底：岛弧（汇聚上盘的洋壳）──

	/// <summary>岛弧海底脊顶深度（米）。出厂值 −1600。</summary>
	public float ArcCrestDepthM { get; set; } = -1600f;

	/// <summary>岛弧隆起量（米，从脊顶往下算）。出厂值 2200。</summary>
	public float ArcRiseM { get; set; } = 2200f;

	/// <summary>岛弧带相对边界的内移距离（km）——岛弧不贴在海沟上。出厂值 215。</summary>
	public float ArcOffsetKm { get; set; } = 215f;

	/// <summary>岛弧带宽（km，高斯）。出厂值 175。</summary>
	public float ArcBandKm { get; set; } = 175f;

	/// <summary>岛弧成岛峰值（米）——沿带噪声 bead 峰处叠这一高度，抬过海面即成串岛。
	/// 须 &gt; −<see cref="ArcCrestDepthM"/> 才会露出水面（出厂 2400 ⇒ 峰顶约 +800 m）。出厂值 2400。</summary>
	public float ArcIslandPeakM { get; set; } = 2400f;

	// ── 海底：海沟（汇聚下盘的洋壳）──

	/// <summary>海沟基础深度（米，在<b>平滑后</b>的海底上再挖下去）。出厂值 3200。</summary>
	public float TrenchDepthM { get; set; } = 3200f;

	/// <summary>海沟随汇聚强度额外加深（米）。出厂值 3300。</summary>
	public float TrenchConvDepthM { get; set; } = 3300f;

	/// <summary>海沟带宽（km，高斯）。出厂值 175。</summary>
	public float TrenchBandKm { get; set; } = 175f;

	/// <summary>离岸小于此距离（km）不挖海沟（太浅的近岸挖了不成样子）。出厂值 40。</summary>
	public float TrenchMinCoastKm { get; set; } = 40f;

	/// <summary>离岸大于此距离（km）海沟达满深。出厂值 270。</summary>
	public float TrenchFullCoastKm { get; set; } = 270f;

	// ── 海底：平滑 ──

	/// <summary>海底深度的平滑遍数（削掉距离场的折线痕迹；遍数越多折痕越淡，但大陆架/海沟也越糊）。
	/// 出厂值 8。</summary>
	public int OceanSmoothPasses { get; set; } = 8;

	/// <summary>海沟的平滑遍数（先平滑再挖，故海沟保持窄而深）。出厂值 1。</summary>
	public int TrenchSmoothPasses { get; set; } = 1;

	/// <summary>逐字段复制到 <paramref name="target"/>（不动实例身份）。</summary>
	public void CopyTo(TerrainSpec target)
	{
		target.SeaLevelM = SeaLevelM;
		target.LandBaseM = LandBaseM;
		target.LargeVariationM = LargeVariationM;
		target.MediumVariationM = MediumVariationM;
		target.RegionalNoiseM = RegionalNoiseM;
		target.LargeWavelengthKm = LargeWavelengthKm;
		target.LargeOctaves = LargeOctaves;
		target.MediumWavelengthKm = MediumWavelengthKm;
		target.MediumOctaves = MediumOctaves;
		target.RegionalWavelengthKm = RegionalWavelengthKm;
		target.RegionalOctaves = RegionalOctaves;
		target.MountainPeakM = MountainPeakM;
		target.MountainKnee = MountainKnee;
		target.MountainKneeCompress = MountainKneeCompress;
		target.MountainUpperCoef = MountainUpperCoef;
		target.MountainLowerCoef = MountainLowerCoef;
		target.MountainArcCoef = MountainArcCoef;
		target.MountainSlabCoef = MountainSlabCoef;
		target.MountainUpperBandKm = MountainUpperBandKm;
		target.MountainBandConvSpreadKm = MountainBandConvSpreadKm;
		target.MountainArcInlandKm = MountainArcInlandKm;
		target.MountainArcBandKm = MountainArcBandKm;
		target.MountainSlabBandKm = MountainSlabBandKm;
		target.MountainRidgeWavelengthKm = MountainRidgeWavelengthKm;
		target.MountainRidgeOctaves = MountainRidgeOctaves;
		target.MountainRidgeFloor = MountainRidgeFloor;
		target.MountainSmoothWeight = MountainSmoothWeight;
		target.OldMountainHeightM = OldMountainHeightM;
		target.HillsHeightM = HillsHeightM;
		target.RiftSuppressStrength = RiftSuppressStrength;
		target.RiftBandKm = RiftBandKm;
		target.PlateauUpperReachKm = PlateauUpperReachKm;
		target.PlateauArcReachKm = PlateauArcReachKm;
		target.PlateauReachSpreadKm = PlateauReachSpreadKm;
		target.PlateauInnerRampKm = PlateauInnerRampKm;
		target.PlateauOuterRampKm = PlateauOuterRampKm;
		target.PlateauUpperHeightM = PlateauUpperHeightM;
		target.PlateauArcHeightM = PlateauArcHeightM;
		target.PlateauConvMin = PlateauConvMin;
		target.PlateauConvFull = PlateauConvFull;
		target.PlateauShieldHeightM = PlateauShieldHeightM;
		target.PlateauCoastFadeM = PlateauCoastFadeM;
		target.PlateauSmoothPasses = PlateauSmoothPasses;
		target.MountainDetailAmount = MountainDetailAmount;
		target.ErosionDepthM = ErosionDepthM;
		target.ErosionExponent = ErosionExponent;
		target.ErosionMaxCutFraction = ErosionMaxCutFraction;
		target.ErosionSteps = ErosionSteps;
		target.OceanShallowestM = OceanShallowestM;
		target.OceanDeepestM = OceanDeepestM;
		target.RidgeCrestDepthM = RidgeCrestDepthM;
		target.RidgeAgeDepthM = RidgeAgeDepthM;
		target.RidgeAgeSpanKm = RidgeAgeSpanKm;
		target.RidgeCrestRiseM = RidgeCrestRiseM;
		target.RidgeBandKm = RidgeBandKm;
		target.AbyssHillLargeM = AbyssHillLargeM;
		target.AbyssHillFineM = AbyssHillFineM;
		target.ShelfShallowestM = ShelfShallowestM;
		target.ShelfDeepestM = ShelfDeepestM;
		target.ShelfMinWidthKm = ShelfMinWidthKm;
		target.ShelfMaxWidthKm = ShelfMaxWidthKm;
		target.SlopeBottomM = SlopeBottomM;
		target.SlopeSpanKm = SlopeSpanKm;
		target.MarginWidenKm = MarginWidenKm;
		target.ActiveMarginBandKm = ActiveMarginBandKm;
		target.ArcCrestDepthM = ArcCrestDepthM;
		target.ArcRiseM = ArcRiseM;
		target.ArcOffsetKm = ArcOffsetKm;
		target.ArcBandKm = ArcBandKm;
		target.ArcIslandPeakM = ArcIslandPeakM;
		target.TrenchDepthM = TrenchDepthM;
		target.TrenchConvDepthM = TrenchConvDepthM;
		target.TrenchBandKm = TrenchBandKm;
		target.TrenchMinCoastKm = TrenchMinCoastKm;
		target.TrenchFullCoastKm = TrenchFullCoastKm;
		target.OceanSmoothPasses = OceanSmoothPasses;
		target.TrenchSmoothPasses = TrenchSmoothPasses;
	}

	/// <summary>独立副本（测试用；**绝不**拿管理器那份去改）。</summary>
	public TerrainSpec Clone() => new()
	{
		SeaLevelM = SeaLevelM,
		LandBaseM = LandBaseM,
		LargeVariationM = LargeVariationM,
		MediumVariationM = MediumVariationM,
		RegionalNoiseM = RegionalNoiseM,
		LargeWavelengthKm = LargeWavelengthKm,
		LargeOctaves = LargeOctaves,
		MediumWavelengthKm = MediumWavelengthKm,
		MediumOctaves = MediumOctaves,
		RegionalWavelengthKm = RegionalWavelengthKm,
		RegionalOctaves = RegionalOctaves,
		MountainPeakM = MountainPeakM,
		MountainKnee = MountainKnee,
		MountainKneeCompress = MountainKneeCompress,
		MountainUpperCoef = MountainUpperCoef,
		MountainLowerCoef = MountainLowerCoef,
		MountainArcCoef = MountainArcCoef,
		MountainSlabCoef = MountainSlabCoef,
		MountainUpperBandKm = MountainUpperBandKm,
		MountainBandConvSpreadKm = MountainBandConvSpreadKm,
		MountainArcInlandKm = MountainArcInlandKm,
		MountainArcBandKm = MountainArcBandKm,
		MountainSlabBandKm = MountainSlabBandKm,
		MountainRidgeWavelengthKm = MountainRidgeWavelengthKm,
		MountainRidgeOctaves = MountainRidgeOctaves,
		MountainRidgeFloor = MountainRidgeFloor,
		MountainSmoothWeight = MountainSmoothWeight,
		OldMountainHeightM = OldMountainHeightM,
		HillsHeightM = HillsHeightM,
		RiftSuppressStrength = RiftSuppressStrength,
		RiftBandKm = RiftBandKm,
		PlateauUpperReachKm = PlateauUpperReachKm,
		PlateauArcReachKm = PlateauArcReachKm,
		PlateauReachSpreadKm = PlateauReachSpreadKm,
		PlateauInnerRampKm = PlateauInnerRampKm,
		PlateauOuterRampKm = PlateauOuterRampKm,
		PlateauUpperHeightM = PlateauUpperHeightM,
		PlateauArcHeightM = PlateauArcHeightM,
		PlateauConvMin = PlateauConvMin,
		PlateauConvFull = PlateauConvFull,
		PlateauShieldHeightM = PlateauShieldHeightM,
		PlateauCoastFadeM = PlateauCoastFadeM,
		PlateauSmoothPasses = PlateauSmoothPasses,
		MountainDetailAmount = MountainDetailAmount,
		ErosionDepthM = ErosionDepthM,
		ErosionExponent = ErosionExponent,
		ErosionMaxCutFraction = ErosionMaxCutFraction,
		ErosionSteps = ErosionSteps,
		OceanShallowestM = OceanShallowestM,
		OceanDeepestM = OceanDeepestM,
		RidgeCrestDepthM = RidgeCrestDepthM,
		RidgeAgeDepthM = RidgeAgeDepthM,
		RidgeAgeSpanKm = RidgeAgeSpanKm,
		RidgeCrestRiseM = RidgeCrestRiseM,
		RidgeBandKm = RidgeBandKm,
		AbyssHillLargeM = AbyssHillLargeM,
		AbyssHillFineM = AbyssHillFineM,
		ShelfShallowestM = ShelfShallowestM,
		ShelfDeepestM = ShelfDeepestM,
		ShelfMinWidthKm = ShelfMinWidthKm,
		ShelfMaxWidthKm = ShelfMaxWidthKm,
		SlopeBottomM = SlopeBottomM,
		SlopeSpanKm = SlopeSpanKm,
		MarginWidenKm = MarginWidenKm,
		ActiveMarginBandKm = ActiveMarginBandKm,
		ArcCrestDepthM = ArcCrestDepthM,
		ArcRiseM = ArcRiseM,
		ArcOffsetKm = ArcOffsetKm,
		ArcBandKm = ArcBandKm,
		ArcIslandPeakM = ArcIslandPeakM,
		TrenchDepthM = TrenchDepthM,
		TrenchConvDepthM = TrenchConvDepthM,
		TrenchBandKm = TrenchBandKm,
		TrenchMinCoastKm = TrenchMinCoastKm,
		TrenchFullCoastKm = TrenchFullCoastKm,
		OceanSmoothPasses = OceanSmoothPasses,
		TrenchSmoothPasses = TrenchSmoothPasses,
	};
}
