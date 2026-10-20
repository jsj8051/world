namespace World.Data;

/// <summary>
/// 陆块布局档：决定哪些板块当陆壳、它们怎么排，从而决定大陆连成一片还是分散开。
/// 只影响壳型分配与 ① 阶段的陆面基线：陆壳块数基准由 <see cref="PlateSpec.ContinentalRatio"/> 定，
/// 各布局档在此基础上增减；海岸线仍由 ① 阶段的高度穿过海面定。
/// 默认走 <see cref="Auto"/>（按种子在四档之间加权抽取），调试单形态时在 <see cref="WorldSpec.Layout"/> 里直接指定。
/// </summary>
public enum ContinentLayout
{
	/// <summary>按 <see cref="WorldSpec"/> 的四档权重用种子抽一档（世界生成的默认值）。</summary>
	Auto = 0,

	/// <summary>少数几块分处不同方位的大陆：优先选彼此不相邻的陆壳板块（之间夹洋壳）⇒ 有大洋隔开（地球样）。</summary>
	Earthlike = 1,

	/// <summary>陆壳板块彼此相邻连成一片：一整块超级大陆，中间没有大洋（盘古样）。</summary>
	Pangea = 2,

	/// <summary>陆壳块数最多且分散，同时 ① 阶段把陆面基线压下去
	/// ⇒ 大片浅海 + 成串岛屿（群岛样）。</summary>
	Archipelago = 3,

	/// <summary>比地球档更多块、彼此分得更均匀的大陆（多大陆样）。</summary>
	ManyContinents = 4,
}
