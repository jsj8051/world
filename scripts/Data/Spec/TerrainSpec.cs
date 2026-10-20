namespace World.Data;

/// <summary>
/// ① 地形阶段配置：可变参数实例，参数管理器持有唯一一份；属性初值 = 出厂档，须与正库
/// <c>res/params/world_params.json</c> 同值。
/// 高度 = 陆面（基线 + 三档变化场 + 构造项，再经分形细化与虚拟侵蚀）或海底；陆壳的格铺陆面、
/// 洋壳的格铺海底 ⇒ 海面切过陆面，海岸线就是高度场的结果。
/// 最终海拔 = 生成基准 × <see cref="VerticalScale"/>（水平几何不随之改变）。
/// 噪声基波长与层数、分位阈值、域扭曲层数、大陆架宽度系数、山带截断范围
/// 等纹理尺度 / 数值手段的实现常量不进本表。
/// </summary>
public sealed class TerrainSpec
{
	// ── 海陆基准 ──

	/// <summary>海面标高（米）：高于它才是陆（世界事实 <c>FinalLand = 高度 &gt; SeaLevelM</c>、
	/// 水文 <c>elev ≤ SeaLevelM</c> 即海）。洋壳格铺的是海底，不由本值定深。出厂值 0。</summary>
	public float SeaLevelM { get; set; } = 0f;

	/// <summary>垂直倍率：生成基准高度写入 <c>HeightM</c> 前统一乘它。
	/// 出厂 7：把低平台基准抬到地球尺度（峰顶约 3.9 km、海沟约 −3.9 km）；
	/// 只放大垂直方向，水平波长与海岸线不变。</summary>
	public float VerticalScale { get; set; } = 7f;

	// ── 陆面幅度 ──

	/// <summary>陆面常量基线（米）：陆壳的高度基准，它穿过海面（落负值即海底）⇒ 海岸线由它定。
	/// 出厂值 9.6：配三档幅值，合成 σ ≈ 14.9 m ⇒ 约 1/4 的陆壳落在海面之下；基线高于 σ 之半
	/// ⇒ 海面切在分布偏低位，海陆由大尺度场主导，海岸成片而非碎斑。</summary>
	public float LandBaseM { get; set; } = 9.6f;

	/// <summary>群岛档把陆面基线再压多少米（只在 <see cref="World.Data.ContinentLayout.Archipelago"/>
	/// 时由 ⓪ 折算、① 应用；0 = 不压）：压下去后陆壳大半落到海面以下 ⇒ 大片浅海 + 成串岛屿。
	/// 出厂值 38：基线 9.6 − 38 = −28.4，按合成 σ ≈ 14.9 m ⇒ 约 3% 陆壳露出水面（按 4 seed 的陆地占比定）。</summary>
	public float ArchipelagoBaseDropM { get; set; } = 38f;

	/// <summary>大尺度起伏幅值（米），海陆分异的主导项：σ 占合成 σ 的 86%，配合 550 km 基波长
	/// ⇒ 全球约 2~4 块大陆，海岸线由它定、平滑成片。出厂值 47.8。</summary>
	public float LargeVariationM { get; set; } = 47.8f;

	/// <summary>中尺度起伏幅值（米；丘陵 / 山谷，基波长 32 km）——打破大色块的那层：
	/// 大尺度项与大陆同量级，单靠它档界切不出层次；本档给出几十公里一级的丘谷。出厂值 15.9。</summary>
	public float MediumVariationM { get; set; } = 15.9f;

	/// <summary>细节起伏幅值（米，基波长 <see cref="RegionalWavelengthKm"/>）——丘间碎起伏。出厂值 10.6。</summary>
	public float RegionalNoiseM { get; set; } = 10.6f;

	// ── 三档变化场：基波长（km；相隔这么久一个特征）+ 倍频层数（越细）──

	/// <summary>大尺度基波长（km）——大地形起伏（大陆尺度）。出厂值 550。</summary>
	public float LargeWavelengthKm { get; set; } = 550f;

	/// <summary>大尺度层数。出厂值 3（最细层 137 km，远高于采样下限）。</summary>
	public int LargeOctaves { get; set; } = 3;

	/// <summary>中尺度基波长（km）——丘陵 / 山谷。出厂值 32。</summary>
	public float MediumWavelengthKm { get; set; } = 32f;

	/// <summary>中尺度层数。出厂值 3。</summary>
	public int MediumOctaves { get; set; } = 3;

	/// <summary>细节基波长（km）：最细的倍频层必须 ≥ 3 倍邻格中心距（邻格 ≈ 2.47 km ⇒ ≥ 7.4 km），
	/// 更细的层采不到、只会沿六边形格轴混叠成条纹。出厂值 17（两层 ⇒ 最细 8.5 km ≈ 3.4 格）。</summary>
	public float RegionalWavelengthKm { get; set; } = 17f;

	/// <summary>细节层数。出厂值 2。</summary>
	public int RegionalOctaves { get; set; } = 2;

	// ── 陆面域扭曲（采样方向先被低频噪声推开，再取三档变化场）──

	/// <summary>陆面 / 海岸的大尺度域扭曲幅度（km；0 = 关断）：采样方向沿切向推开再取值
	/// ⇒ 大陆轮廓与海岸线出现弯曲的湾岬，而不是沿 Voronoi 边界的直尺状。出厂值 32。</summary>
	public float LandWarpKm { get; set; } = 32f;

	/// <summary>大尺度域扭曲的特征波长（km）——位移场本身的尺度。出厂值 220。</summary>
	public float LandWarpWavelengthKm { get; set; } = 220f;

	/// <summary>近岸域扭曲幅度（km；0 = 关断）：只在 |高度−海面| 小的格上加权，
	/// 给海岸线加 10–30 km 级的湾、岬、小岛。出厂值 9。</summary>
	public float CoastWarpKm { get; set; } = 9f;

	/// <summary>近岸域扭曲的特征波长（km）。出厂值 55。</summary>
	public float CoastWarpWavelengthKm { get; set; } = 55f;

	/// <summary>近岸域扭曲生效带的下界（米，|高度−海面|）：低于它全量扭曲。出厂值 8。</summary>
	public float CoastWarpBandLowM { get; set; } = 8f;

	/// <summary>近岸域扭曲生效带的上界（米，|高度−海面|）：高于它完全关断，之间平滑收束
	/// （须大于 <see cref="CoastWarpBandLowM"/>）。出厂值 30。</summary>
	public float CoastWarpBandHighM { get; set; } = 30f;

	// ── 构造项：山带（汇聚挤压 ⇒ 按角色分档隆起）──

	/// <summary>山带高度的归一目标（米）：陆壳格上构造抬升的第 99.7 百分位映射到它，
	/// 于是最高的山恒有这一档，不随板块速度整体大小漂移（不同 seed / 板块数之间可比）。出厂值 480。</summary>
	public float MountainPeakM { get; set; } = 480f;

	/// <summary>压峰折点（× <see cref="MountainPeakM"/>）：超过它的抬升按
	/// <see cref="MountainKneeCompress"/> 压缩 ⇒ 不出现离谱的尖峰。出厂值 0.85。</summary>
	public float MountainKnee { get; set; } = 0.85f;

	/// <summary>折点以上的压缩系数（0.50 ⇒ 超出部分只留 50%）。出厂值 0.50。</summary>
	public float MountainKneeCompress { get; set; } = 0.50f;

	/// <summary>陆×陆上盘山带强度（× <see cref="MountainPeakM"/>）。出厂值 1.65。</summary>
	public float MountainUpperCoef { get; set; } = 1.65f;

	/// <summary>陆×陆下盘山带强度（下盘同样成山：碰撞带两侧都是山）。出厂值 1.65。</summary>
	public float MountainLowerCoef { get; set; } = 1.65f;

	/// <summary>陆×洋陆侧山带强度（安第斯型窄山链）。出厂值 1.45。</summary>
	public float MountainArcCoef { get; set; } = 1.45f;

	/// <summary>俯冲侧山带强度（低矮的增生楔）。出厂值 0.35。</summary>
	public float MountainSlabCoef { get; set; } = 0.35f;

	/// <summary>陆×陆山带的基础带宽（km）：自边界向陆内的衰减尺度（弱汇聚段取它）。出厂值 10。</summary>
	public float MountainUpperBandKm { get; set; } = 10f;

	/// <summary>山带随汇聚强度加宽的量（km）：带宽 = 基础带宽 + 本值 × 汇聚强度
	/// ⇒ 撞得越狠，山系铺得越开（满强度 16 km）。出厂值 6。</summary>
	public float MountainBandConvSpreadKm { get; set; } = 6f;

	/// <summary>陆×洋陆侧山带的内移距离（km）：山链不贴海岸，退到内陆这一段（海岸留给大陆架）。出厂值 14。</summary>
	public float MountainArcInlandKm { get; set; } = 14f;

	/// <summary>陆×洋陆侧山带的带宽（km）。出厂值 9。</summary>
	public float MountainArcBandKm { get; set; } = 9f;

	/// <summary>俯冲侧增生楔的带宽（km）。出厂值 7。</summary>
	public float MountainSlabBandKm { get; set; } = 7f;

	/// <summary>山脊噪声基波长（km）——沿带调制，造出山口与高峰（否则是一堵等高墙）。出厂值 70。</summary>
	public float MountainRidgeWavelengthKm { get; set; } = 70f;

	/// <summary>山脊噪声层数。出厂值 3。</summary>
	public int MountainRidgeOctaves { get; set; } = 3;

	/// <summary>山脊调制下限 ∈ [0,1]（噪声谷处的抬升比例；1 = 不调制 ⇒ 等高墙）。
	/// 取低值 ⇒ 山带能断成一段一段（有山口、有缺口），而不是绕一圈不断的墙。出厂值 0.18。</summary>
	public float MountainRidgeFloor { get; set; } = 0.18f;

	/// <summary>山脊骨架的尖锐度：脊骨架取本值次幂（越大脊越尖、谷越宽；1 = 线性）。
	/// 有效范围 1–3（超出按端点截断）。出厂值 2.2。</summary>
	public float MountainRidgeSharpness { get; set; } = 2.2f;

	/// <summary>山带距离场的横向弯曲幅度（km；0 = 沿板块边界直线走）。
	/// 只偏移距离值、不改板块归属 ⇒ 山带走向弯曲但不会产生飞地。有效范围 0–15（超出按端点截断）。出厂值 9。</summary>
	public float MountainBeltWarpKm { get; set; } = 9f;

	/// <summary>山带剖面的不对称度 ∈ [0,0.45]：陆×洋山链海侧陡、陆侧缓；
	/// 陆×陆山带边界侧陡、向内陆拖长。0 = 对称高斯。出厂值 0.32。</summary>
	public float MountainAsymmetry { get; set; } = 0.32f;

	/// <summary>构造抬升场的一层平滑权重 ∈ [0,1]：<c>u ← (1−w)·自身 + w·邻居均值</c>。
	/// 在归一之前做 ⇒ 只削毛刺与段端台阶，不改峰高目标。0 = 关断。出厂值 0.20。</summary>
	public float MountainSmoothWeight { get; set; } = 0.20f;

	// ── 构造项：古老山系与丘陵（远离当代边界的残丘）──

	/// <summary>古老山系的高度（米）：已停止抬升、只剩脊状残丘的老山带（比当代造山带矮一截）；
	/// 加性叠在构造抬升上，不参与压峰 ⇒ 老山 + 主峰可以超过 <see cref="MountainPeakM"/>。出厂值 70。</summary>
	public float OldMountainHeightM { get; set; } = 70f;

	/// <summary>丘陵区的高度（米）——成片的缓丘，把大陆内部从"一大块平原"里救出来。出厂值 30。</summary>
	public float HillsHeightM { get; set; } = 30f;

	// ── 构造项：大陆裂谷 ──

	/// <summary>张裂带把抬升压低的强度 ∈ [0,1]（1 = 压成零）——张裂处不该跟着旁边的山一起被抬高。出厂值 0.75。</summary>
	public float RiftSuppressStrength { get; set; } = 0.75f;

	/// <summary>张裂带压低的带宽（km，高斯）。出厂值 38。</summary>
	public float RiftBandKm { get; set; } = 38f;

	// ── 构造项：高原底座（山带背后的一大片平坦高地）──

	/// <summary>陆×陆碰撞带背后高原的内伸距离（km）：自边界向陆内铺多远。出厂值 40。</summary>
	public float PlateauUpperReachKm { get; set; } = 40f;

	/// <summary>陆×洋俯冲带上盘高原的内伸距离（km）。出厂值 22。</summary>
	public float PlateauArcReachKm { get; set; } = 22f;

	/// <summary>内伸距离沿走向的抖动幅度（km）——高原"时有时无"、不成一条等宽的带。出厂值 42。</summary>
	public float PlateauReachSpreadKm { get; set; } = 42f;

	/// <summary>高原内缘的上升距离（km）——太贴边界不起（那一圈留给山带本身）。出厂值 14。</summary>
	public float PlateauInnerRampKm { get; set; } = 14f;

	/// <summary>高原外缘的收束跨度（km）。出厂值 22。</summary>
	public float PlateauOuterRampKm { get; set; } = 22f;

	/// <summary>陆×陆碰撞带高原的高度（米）。出厂值 130。</summary>
	public float PlateauUpperHeightM { get; set; } = 130f;

	/// <summary>陆×洋俯冲带上盘高原的高度（米）。出厂值 100。</summary>
	public float PlateauArcHeightM { get; set; } = 100f;

	/// <summary>高原起势的汇聚强度下限（低于它不起高原）。出厂值 0.25。</summary>
	public float PlateauConvMin { get; set; } = 0.25f;

	/// <summary>高原达到满高所需的汇聚强度。出厂值 0.8。</summary>
	public float PlateauConvFull { get; set; } = 0.8f;

	/// <summary>大陆内部零星台地的高度（米）。出厂值 55。</summary>
	public float PlateauShieldHeightM { get; set; } = 55f;

	/// <summary>高原贴近海岸时的收束高度（米）：陆面高出海面不足这么多时按比例压掉高原
	/// ⇒ 海岸不成悬崖。出厂值 6.4。</summary>
	public float PlateauCoastFadeM { get; set; } = 6.4f;

	/// <summary>高原的平滑遍数（削掉距离场的折线痕迹）。出厂值 1。</summary>
	public int PlateauSmoothPasses { get; set; } = 1;

	// ── 构造项：山体细化与虚拟侵蚀 ──

	/// <summary>山体的分形细化幅度 ∈ [0,1)：把构造抬升乘上 <c>1 + 本值 × fBm 噪声</c>
	/// （噪声 ≈ [−1,1]、基波长 27 km），再按其均值归位 ⇒ 只改形状、不改平均幅度，把山带打散成几个山块。
	/// 0 = 关断（山体保持光滑）。出厂值 0.65。</summary>
	public float MountainDetailAmount { get; set; } = 0.65f;

	/// <summary>陡坡上的细节抑制强度 ∈ [0,0.9]：<c>detailWeight = 1 / (1 + 本值·smoothstep(slopeRef,slopeMax,slope))</c>。
	/// 陡壁不叠满高频噪声（真实陡壁是岩层 / 沟槽，不是毛毯），缓坡不受影响。0 = 关断。出厂值 0.35。</summary>
	public float MountainDetailSuppression { get; set; } = 0.35f;

	/// <summary>虚拟侵蚀的最大下切（米）：汇水量最大的那一档切这么多。出厂值 35。</summary>
	public float ErosionDepthM { get; set; } = 35f;

	/// <summary>侵蚀的汇水量指数：<c>(A / P99(A))^本值</c>。越小则支流也切得动（谷网密），
	/// 越大则只有主河道下切（谷网疏而深）。出厂值 0.5。</summary>
	public float ErosionExponent { get; set; } = 0.5f;

	/// <summary>单步下切不超过当前抬升的这一档比例（防止把山削穿成沟槽；有效范围 0–1）。出厂值 0.6。</summary>
	public float ErosionMaxCutFraction { get; set; } = 0.6f;

	/// <summary>侵蚀迭代步数（0 = 关断）——每步都重算一遍填洼 / 排水 / 汇流。
	/// 步数越多谷越深越成网，也越慢。出厂值 4。</summary>
	public int ErosionSteps { get; set; } = 4;

	/// <summary>侵蚀产物的沉积比例 ∈ [0,0.45]：沿排水路径把泥沙按落差沉积到下游
	/// ⇒ 谷底变平、山麓出现冲积扇。0 = 只切不填。出厂值 0.25。</summary>
	public float ErosionDeposition { get; set; } = 0.25f;

	/// <summary>平原浅下切（米；0 = 关断）：在离海 ≥2 格、离海面 ≥3 m 的陆地上，按汇水量切出浅槽，
	/// 让水系在平原上留下河谷。值过大（&gt;15 m）会切碎平原、海岸后退。出厂值 5。</summary>
	public float PlainsIncisionM { get; set; } = 5f;

	// ── 陆地分布重映射（海面以上，保号单调）──

	/// <summary>hypsometric gamma：对陆地相对高度做 <c>x^本值</c>（x = 相对海面的高度 / 陆地 p99.5）。
	/// 1 = 不改；&gt;1 ⇒ 低地更平、山地相对更高。只作用于海面以上，海岸线不动。出厂值 1.20。</summary>
	public float HypsometricGamma { get; set; } = 1.2f;

	/// <summary>hypsometric 曲线尾部压缩系数（x &gt; 1 时 <c>y = 1 + (x−1)·本值</c>；1 = 不压）。
	/// 越小峰顶越平；有效范围 0–1。出厂值 0.8。</summary>
	public float HypsometricTailCompress { get; set; } = 0.8f;

	// ── 海底：深度范围 ──

	/// <summary>最浅的海（米，生成基准；最终再乘 <see cref="VerticalScale"/>）：任何海底都不会比它更浅。出厂值 −1.1。</summary>
	public float OceanShallowestM { get; set; } = -1.1f;

	/// <summary>最深的海（米，生成基准下界；最终再乘 <see cref="VerticalScale"/>）：海沟的最终下限。出厂值 −558。</summary>
	public float OceanDeepestM { get; set; } = -558f;

	// ── 海底：洋中脊与洋壳年龄 ──

	/// <summary>洋中脊脊顶深度（米）。出厂值 −143.5。</summary>
	public float RidgeCrestDepthM { get; set; } = -143.5f;

	/// <summary>洋壳"年龄满值"处比脊顶再深的米数：深度 ∝ √年龄，
	/// 故最老洋底 = <see cref="RidgeCrestDepthM"/> − 本值（出厂 −287）。出厂值 143.5。</summary>
	public float RidgeAgeDepthM { get; set; } = 143.5f;

	/// <summary>洋壳"年龄满值"对应的离洋中脊距离（km）：超过它就按最老洋底算。出厂值 165。</summary>
	public float RidgeAgeSpanKm { get; set; } = 165f;

	/// <summary>脊顶额外隆起（米，× 该处张裂强度）。出厂值 26.6。</summary>
	public float RidgeCrestRiseM { get; set; } = 26.6f;

	/// <summary>脊顶隆起的带宽（km，高斯）。出厂值 30。</summary>
	public float RidgeBandKm { get; set; } = 30f;

	// ── 海底：深海丘陵 ──

	/// <summary>深海丘陵：大尺度起伏幅值（米）。出厂值 15.9。</summary>
	public float AbyssHillLargeM { get; set; } = 15.9f;

	/// <summary>深海丘陵：细尺度起伏幅值（米）。出厂值 4.8。</summary>
	public float AbyssHillFineM { get; set; } = 4.8f;

	// ── 海底：大陆架与大陆坡 ──

	/// <summary>贴岸处的大陆架深度（米）。出厂值 −1.6。</summary>
	public float ShelfShallowestM { get; set; } = -1.6f;

	/// <summary>大陆架外缘深度（米）——到此外缘为止线性变深。出厂值 −9.6。</summary>
	public float ShelfDeepestM { get; set; } = -9.6f;

	/// <summary>大陆架最小宽度（km；俯冲带一侧）。出厂值 7。</summary>
	public float ShelfMinWidthKm { get; set; } = 7f;

	/// <summary>大陆架最大宽度（km；被动边缘）。出厂值 42。</summary>
	public float ShelfMaxWidthKm { get; set; } = 42f;

	/// <summary>大陆坡底部深度（米）——大陆架外缘再往下接到这里。出厂值 −355。</summary>
	public float SlopeBottomM { get; set; } = -355f;

	/// <summary>大陆坡的水平跨度（km）。出厂值 44。</summary>
	public float SlopeSpanKm { get; set; } = 44f;

	/// <summary>「离岸近 ⇒ 大陆架放宽」的判定距离（km）——离岸小于它就取宽档。出厂值 35。</summary>
	public float MarginWidenKm { get; set; } = 35f;

	/// <summary>汇聚（主动边缘）调制大陆架宽度的带宽（km，高斯）——此处大陆架被压窄。出厂值 49。</summary>
	public float ActiveMarginBandKm { get; set; } = 49f;

	// ── 海底：岛弧（汇聚上盘的洋壳）──

	/// <summary>岛弧海底脊顶深度（米）。出厂值 −85。</summary>
	public float ArcCrestDepthM { get; set; } = -85f;

	/// <summary>岛弧隆起量（米，从脊顶往下算）。出厂值 117。</summary>
	public float ArcRiseM { get; set; } = 117f;

	/// <summary>岛弧带相对边界的内移距离（km）——岛弧不贴在海沟上。出厂值 20。</summary>
	public float ArcOffsetKm { get; set; } = 20f;

	/// <summary>岛弧带宽（km，高斯）。出厂值 17。</summary>
	public float ArcBandKm { get; set; } = 17f;

	/// <summary>岛弧成岛峰值（米）——沿带噪声 bead 峰处叠这一高度，抬过海面即成串岛。
	/// 须 &gt; −<see cref="ArcCrestDepthM"/> 才会露出水面（出厂 128 ⇒ 峰顶约 +43 m）。出厂值 128。</summary>
	public float ArcIslandPeakM { get; set; } = 128f;

	// ── 海底：海沟（汇聚下盘的洋壳）──

	/// <summary>海沟基础深度（米，在平滑后的海底上再挖下去）。出厂值 170。</summary>
	public float TrenchDepthM { get; set; } = 170f;

	/// <summary>海沟随汇聚强度额外加深（米）。出厂值 175。</summary>
	public float TrenchConvDepthM { get; set; } = 175f;

	/// <summary>海沟带宽（km，高斯）。出厂值 17。</summary>
	public float TrenchBandKm { get; set; } = 17f;

	/// <summary>离岸小于此距离（km）不挖海沟（太浅的近岸挖了不成样子）。出厂值 7。</summary>
	public float TrenchMinCoastKm { get; set; } = 7f;

	/// <summary>离岸大于此距离（km）海沟达满深。出厂值 22。</summary>
	public float TrenchFullCoastKm { get; set; } = 22f;

	// ── 海底：平滑 ──

	/// <summary>海底深度的平滑遍数（削掉距离场的折线痕迹；遍数越多折痕越淡，但大陆架/海沟也越糊）。
	/// 出厂值 6。</summary>
	public int OceanSmoothPasses { get; set; } = 6;

	/// <summary>海沟的平滑遍数（先平滑再挖，故海沟保持窄而深）。出厂值 1。</summary>
	public int TrenchSmoothPasses { get; set; } = 1;

	/// <summary>复制全部公开可写属性到 <paramref name="target"/>（不动实例身份）。</summary>
	public void CopyTo(TerrainSpec target) => SpecCopy.CopyProperties(this, target);
}
