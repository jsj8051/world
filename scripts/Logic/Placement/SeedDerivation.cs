namespace World.Logic;

/// <summary>
/// 单一种子派生表：主种子 × 用途标签 ⇒ 各子系统独立随机流，避免散落的魔数撞车。
/// 派生 = `seed ^ tag`。新增子系统 ⇒ 在表尾加一个**值不与既有项重复**的常量（测试钉唯一性）。
/// </summary>
public static class SeedDerivation
{
	// ── PlatePipeline（⓪ 板块：均匀随机种子 + 球面 Voronoi）──
	/// <summary>板块**种子点**撒布流（`PlateManager.BuildSeeds`；每点消费 3 次抽样：θ / φ / 随机速率）。</summary>
	public const int Plate_Seeds = 0x914E;
	/// <summary>板块**边界扰动**噪声母流（`PlateManager.GrowWithResistance` 的 fBm 阻力场）。</summary>
	public const int Plate_Distort = 0x914F;
	/// <summary>板块**壳型**判定流（`PlateManager.AssignCrustTypes` 的陆壳挑选抖动）。</summary>
	public const int Plate_Crust = 0x9150;
	/// <summary>板块**密度**流（`PlateManager.BuildSeeds`；每块消费 1 次抽样）。</summary>
	public const int Plate_Density = 0x9151;

	// ── TerrainManager（① 陆面：高度合成的三档变化场）──
	/// <summary>大尺度基线噪声（8000 km；海陆分异的主导项）。</summary>
	public const int Terrain_Large = 0x1A6E;
	/// <summary>中尺度噪声（600 km）。</summary>
	public const int Terrain_Medium = 0x4E02;
	/// <summary>细节噪声（90 km）。</summary>
	public const int Terrain_Detail = 0x4E01;

	// ── HighlandOps（① 高地：山脊调制 / 老山 / 丘陵 / 高原 / 盾状台地）──
	/// <summary>山脊沿带调制噪声（山口的来源）。</summary>
	public const int Terrain_Ridge = 0x7A11;
	/// <summary>古老山系的山脊噪声（ridged）。</summary>
	public const int Terrain_OldRange = 0x7A12;
	/// <summary>丘陵场（兼作老山系的"这一带本来就有起伏"门）。</summary>
	public const int Terrain_Hills = 0x7A13;
	/// <summary>高原内伸距离沿走向的抖动。</summary>
	public const int Terrain_Reach = 0x7A14;
	/// <summary>高原沿走向"时有时无"的调制。</summary>
	public const int Terrain_Along = 0x7A15;
	/// <summary>大陆内部零星盾状台地。</summary>
	public const int Terrain_Shield = 0x7A16;

	// ── ErosionOps（① 山体细化与虚拟侵蚀）──
	/// <summary>山体的分形细化噪声（ridged，叠在山脊调制之上）。</summary>
	public const int Terrain_MountainDetail = 0x7A17;

	// ── OceanDepthOps（① 海底：洋中脊 / 大陆架 / 岛弧的噪声）──
	/// <summary>深海丘陵·大尺度起伏。</summary>
	public const int Ocean_Sea = 0x2B10;
	/// <summary>深海丘陵·细尺度起伏。</summary>
	public const int Ocean_SeaFine = 0x2B11;
	/// <summary>大陆架宽度的地方性抖动。</summary>
	public const int Ocean_Shelf = 0x2B12;
	/// <summary>岛弧沿带的"串珠"（哪里隆起成弧）。</summary>
	public const int Ocean_ArcBead = 0x2B13;
	/// <summary>岛弧成岛（串珠上哪里真的露出水面）。</summary>
	public const int Ocean_ArcIsle = 0x2B14;

	// ── SnowOverlay ──
	/// <summary>局部雪线气候噪声（2500 km）。</summary>
	public const int Snow_Climate = 0x5E0B;

	/// <summary>派生：主种子 × 用途标签 ⇒ 子系统独立流。
	/// 恒等退化解：`Derive(seed, 0) == seed`（未列出的子系统 = 直接用主种子）。</summary>
	public static int Derive(int seed, int tag) => seed ^ tag;

	/// <summary>全表标签（唯一性与覆盖率检查用）。</summary>
	public static readonly int[] AllTags =
	{
		Plate_Seeds, Plate_Distort, Plate_Crust, Plate_Density,
		Terrain_Large, Terrain_Medium, Terrain_Detail,
		Terrain_Ridge, Terrain_OldRange, Terrain_Hills, Terrain_Reach, Terrain_Along, Terrain_Shield,
		Terrain_MountainDetail,
		Ocean_Sea, Ocean_SeaFine, Ocean_Shelf, Ocean_ArcBead, Ocean_ArcIsle,
		Snow_Climate,
	};
}
