using World.Data;
using World.H3Grid;

namespace World.Logic;

/// <summary>
/// 板块阶段：把世界定义交给 <see cref="PlateManager"/> 跑完 ⓪ 的全部步骤，
/// 再把边界集派生为逐边场（运动 / 类型 / 两侧角色）。
/// 本类不持算法；逐格数组都在 <see cref="PlateManager"/> 与 <see cref="PlateBoundaryFields"/> 里。
/// </summary>
public sealed class PlatePipeline
{
	/// <summary>板块实现（全部产物由它持有）。</summary>
	readonly PlateManager _impl = new();

	/// <summary>逐格板块号。</summary>
	public int[] PlateOfCell => _impl.PlateOfCell;
	/// <summary>板块表（欧拉矢量 / 密度）。</summary>
	public Plate[] Plates => _impl.Plates;

	/// <summary>逐格壳型与陆壳省。</summary>
	public CrustField Crust => _impl.Crust;

	/// <summary>板块边界集（共享边 + 板块对 + 逐格关联）。</summary>
	public PlateBoundarySet Boundaries => _impl.Boundaries;

	/// <summary>板块边界场（逐边运动 / 类型 / 两侧角色）。</summary>
	public PlateBoundaryFields Boundary { get; private set; } = PlateBoundaryFields.Empty;

	/// <summary>本次世界的陆块布局档（Auto 已抽定）。</summary>
	public ContinentLayout Layout => _impl.Layout;

	/// <summary>陆面基线偏移（米；群岛档 = <c>ArchipelagoBaseDropM</c>，其余 0）。
	/// ① 只收这个数，不认识布局枚举。</summary>
	public float LandBaseDropM => _impl.LandBaseDropM;

	/// <param name="spec">世界定义（种子 + 布局档 / 四档权重 + 板块配置 + 地形配置的群岛压基线）。</param>
	public void Run(Ball ball, WorldSpec spec)
	{
		_impl.Run(ball, spec);
		Boundary = PlateBoundaryFields.Build(ball, _impl.Boundaries, _impl.Crust, _impl.Plates);
	}
}
