namespace World.WorldGen;

// 世界生成空间 · **单一种子派生表**（收口 §07 D-6 / T9）。
//
// 背景：新世界线的每个子系统都要从主种子派生自己的随机流，此前 13 处魔数散落在
//   7 个生成器里（`seed ^ 0x600D` / `0x1111` / `0x30A5` …），既无法一眼看出
//   "派生空间有多大"，也无法防止新子系统复制粘贴到已占用的魔数（两条子流同源 ⇒
//   山系与火山出现伪相关，且这种 bug 只在个别 seed 上显现，极难定位）。
//
// ★退化解（永久架构原则，06 §3.6）：本表**只是把散落的魔数按原值集中**，不改变任何数值
//   ——`Derive` 就是原来的 `seed ^ tag`。因此改表前后，**同一个 seed 生成的世界逐位相同**
//   （全量测试绿即为证明）。若将来要换成更强的派生（如 splitmix64 混合），
//   **必须先证明旧世界的退化解**：给出开关，关掉时输出逐位等于 `seed ^ tag`。
//
// 用法：`new DeterministicRandom(SeedDerivation.Derive(seed, SeedDerivation.Regions_Noise))`
// 新增子系统 ⇒ 在表尾加一个**值不与既有项重复**的常量（测试 `Tags_AreUnique` 钉死）。
public static class SeedDerivation
{
	// ── GeologicalRegions ──
	/// <summary>区域布局噪声：尺寸权重 + 边界域扭曲三路（GeologicalRegions 构造）。</summary>
	public const int Regions_Noise = 0x600D;
	/// <summary>逐区域基础高度带抽样（BuildBaseElevationField）。</summary>
	public const int Regions_BaseElevation = 0xB4E5;
	/// <summary>区域类型分配 softmax 抽样（AssignTypes）。</summary>
	public const int Regions_TypeAssign = 0x5EED;

	// ── HeightComposer ──
	/// <summary>大尺度基线噪声（5000 km）。</summary>
	public const int Composer_Large = 0x1A6E;
	/// <summary>中尺度噪声（600 km）。</summary>
	public const int Composer_Medium = 0x4E02;
	/// <summary>细节噪声（90 km）。</summary>
	public const int Composer_Detail = 0x4E01;

	// ── MountainSkeleton ──
	/// <summary>崎岖度 / 轴向噪声流（构造期一次抽 2 条）。</summary>
	public const int Mountain_Noise = 0x9E57;
	/// <summary>主脊行走流。</summary>
	public const int Mountain_WalkMain = 0x1111;
	/// <summary>支脉行走流。</summary>
	public const int Mountain_WalkBranch = 0x2222;

	// ── RegionalLandforms ──
	/// <summary>高原/盆地顶面起伏噪声（Flatness ±12%）。</summary>
	public const int Landform_TopNoise = 0x71A0;

	// ── TectonicField ──
	/// <summary>构造方位噪声（6000 km）。</summary>
	public const int Tectonic_Orient = 0x7EC7;

	// ── VolcanoField ──
	/// <summary>火山放置流（构造场选址 + 间距排斥）。</summary>
	public const int Volcano_Place = 0x30A5;

	// ── SnowOverlay ──
	/// <summary>局部雪线气候噪声（2500 km）。</summary>
	public const int Snow_Climate = 0x5E0B;

	/// <summary>派生：主种子 × 用途标签 ⇒ 子系统独立流。
	/// 恒等退化解：`Derive(seed, 0) == seed`（未列出的子系统 = 直接用主种子）。</summary>
	public static int Derive(int seed, int tag) => seed ^ tag;

	/// <summary>全表标签（测试用：唯一性与覆盖率检查）。</summary>
	public static readonly int[] AllTags =
	{
		Regions_Noise, Regions_BaseElevation, Regions_TypeAssign,
		Composer_Large, Composer_Medium, Composer_Detail,
		Mountain_Noise, Mountain_WalkMain, Mountain_WalkBranch,
		Landform_TopNoise, Tectonic_Orient, Volcano_Place, Snow_Climate,
	};
}
