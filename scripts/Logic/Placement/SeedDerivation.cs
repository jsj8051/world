namespace World.Logic;

/// <summary>
/// 单一种子派生表：主种子 × 用途标签 ⇒ 各子系统独立随机流，避免散落的魔数撞车。
/// 派生 = `seed ^ tag`。新增子系统 ⇒ 在表尾加一个**值不与既有项重复**的常量（测试钉唯一性）。
/// </summary>
public static class SeedDerivation
{
	// ── GeologicalRegionManager（区域图：八类划分）──
	/// <summary>区域图判定场三条流的**母流**（带宽抖动 / 盆地块 / 缝合带宽度，构造时按固定次序派生）。</summary>
	public const int Regions_Noise = 0x600D;
	/// <summary>块体种子撒布流（缝合带网络的骨架；`BuildSutureDistance`）。</summary>
	public const int Regions_Blocks = 0xB10C;

	// ── PlatePipeline（⓪ 板块：均匀随机种子 + 球面 Voronoi）──
	/// <summary>板块**种子点**撒布流（`PlateManager.BuildSeeds`；每点消费 3 次抽样：θ / φ / 随机速率）。</summary>
	public const int Plate_Seeds = 0x914E;
	/// <summary>板块**边界扰动**噪声母流（`PlateManager.GrowWithResistance` 的 fBm 阻力场）。</summary>
	public const int Plate_Distort = 0x914F;
	/// <summary>板块**壳型**判定流（`PlateManager.AssignCrustTypes` 的陆壳挑选抖动）。</summary>
	public const int Plate_Crust = 0x9150;

	// ── HeightComposer ──
	/// <summary>大尺度基线噪声（5000 km）。</summary>
	public const int Composer_Large = 0x1A6E;
	/// <summary>中尺度噪声（600 km）。</summary>
	public const int Composer_Medium = 0x4E02;
	/// <summary>细节噪声（90 km）。</summary>
	public const int Composer_Detail = 0x4E01;

	// ── SnowOverlay ──
	/// <summary>局部雪线气候噪声（2500 km）。</summary>
	public const int Snow_Climate = 0x5E0B;

	/// <summary>派生：主种子 × 用途标签 ⇒ 子系统独立流。
	/// 恒等退化解：`Derive(seed, 0) == seed`（未列出的子系统 = 直接用主种子）。</summary>
	public static int Derive(int seed, int tag) => seed ^ tag;

	/// <summary>全表标签（唯一性与覆盖率检查用）。</summary>
	public static readonly int[] AllTags =
	{
		Plate_Seeds, Plate_Distort, Plate_Crust,
		Regions_Noise, Regions_Blocks,
		Composer_Large, Composer_Medium, Composer_Detail,
		Snow_Climate,
	};
}
