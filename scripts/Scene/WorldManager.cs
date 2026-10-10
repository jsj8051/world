using Godot;
using System.Collections.Generic;
using World.Constants;             // PlanetGeometry（球半径的唯一定义）
using World.Data;                  // WorldSpec（世界定义实例：不自己取，由根管理器注入）
using World.Utils;                 // NodeDependency（Require / Check 扩展）
using World.Render;
using World.Logic;                   // WorldGenSimulation / RiverGeometry / RiverGraph（世界事实与水文事实）

namespace World.Scene;

/// <summary>
/// **世界管理器**（`WorldRoot > WorldManager`）：世界这一块的持有者——**世界事实的唯一入口**。
/// 它自己就是"世界本体"：持 <see cref="Sim"/>（纯逻辑入口）＋ 两个尺度常量，
/// 装配层与界面/渲染侧一律经 `World.Sim` 取事实（不镜像副本）。
/// <para>★**2026-10-11 收编**：原 `WorldManager > WorldGenPlanet > Sim` 是"一层转手 + 一层构造"
/// 的叠包装（`WorldGenPlanet` 被掏空到只剩 `Sim` 与常量），用户拍板一次收掉两层 ⇒ 树深 4 → 3，
/// 少一个节点、少一个 `[Export]`、少一条跨场景依赖（`WorldGenPlanet.tscn` 已删）。
/// 这也是债务清单 A-7 的落地：那一层问"包装还要不要"，答案是把被包装的内容提到包装者身上。</para>
/// <para>★职责边界：只管"世界是什么"——不持相机、不收发输入、不发事件、不碰渲染与界面、
/// 无逐帧工作（剔除/LOD 归 `RenderManager`，点选也归它）。</para>
/// </summary>
public partial class WorldManager : Node3D
{
	/// <summary>
	/// **世界生产分辨率 = res4（冻结）**（§07 §5.1 F）。res4 是**世界本身的空间离散尺度**
	/// （288,122 格，格边长 ≈26 km），各模拟系统都应适配这个尺度。
	/// ★**不得通过降低生产分辨率来规避某个子系统的尺度适配缺陷**（§07 §5.5）——
	/// 正确处置是让子系统适配 res4（如水文 D-11），否则滑向"不工作就换档"的架构倒退。
	/// res1~res4 同时用作**诊断实验档**（跑尺度行为测试），不是从中挑生产档。
	/// ⇒ 由 `SpatialScaleTests.ProductionRes_IsRes4_Frozen` 钉住（含"必须是编译期常量"）。
	/// </summary>
	public const int ProductionRes = 4;

	/// <summary>
	/// **纯逻辑入口**（引擎无关：球壳数据层 + 全部生成产物 + 管线序列）。全部世界事实
	/// （`Final` / `Regions` / `DisplayElevation` / …）都从它取：`Sim.Facts.Final`，**不镜像**。
	/// ★全量重算 = `Sim.Run()`（**复用同一实例**——地图模式持它引用并现取字段）。
	/// </summary>
	public WorldGenSimulation Sim { get; private set; }

	/// <summary>已初始化过（<see cref="Initialize"/> 的幂等判据；根管理器据此可安全重复调用）。</summary>
	public bool IsInitialized { get; private set; }

	/// <summary>
	/// **世界定义实例**（由根管理器注入）：参数实例归 `World.Params.WorldParams` 管，
	/// 本层不读盘、不造副本。★它一直被传给 `Sim.Run` ⇒ 参数一改、再 `Regenerate()` 即是新世界。
	/// </summary>
	WorldSpec _spec;

	/// <summary>
	/// **显式初始化**——建逻辑入口 + 跑一次全链生成。**幂等**。
	/// ★必须在**渲染 / 界面管理器之前**：模式表与初始取色都读 `Sim`。
	/// </summary>
	/// <param name="spec">世界定义实例（根管理器从参数管理器取来注入；世界定义只有一份）。</param>
	public void Initialize(WorldSpec spec)
	{
		if (IsInitialized) return;   // 幂等

		_spec = spec;
		Sim = new WorldGenSimulation(ProductionRes, PlanetGeometry.ProductionRadius);
		Regenerate();                // 世界生成（重活在这一步）
		IsInitialized = true;
	}

	/// <summary>
	/// **全量重算**：把世界定义经 <see cref="WorldGenSimulation.Run"/> 注入管线。
	/// ★这是"改完参数生效"的唯一入口（参数实例改了字段，这里再跑一次就读到新值）。
	/// ★只算数据；算完**必须**由装配层调 `RenderManager.RebakeView()`（→ `WorldView.Rebake`）重烘表现
	/// （那条不变量现在由 `WorldView.Rebake` 一个方法承担，不再是两处注释的约定）。
	/// ⚠️ **当前零调用者**（除 `Initialize` 内那一次）："参数面板改完即时生效"这条链尚未接线，
	///    接线方式与验收见 `docs/债务清单-架构与分层.md` 的 **A-8**。
	/// </summary>
	public void Regenerate() => Sim.Run(_spec);

	// ── 依赖解析 / 校验（判据收在 `World.Utils.NodeDependency`，四个管理器共用一份）────

	// ★本类**没有依赖**：世界本体就是它自己（原先那条 `PlanetPath` 随收编一起删除）。
	//   但 `_GetConfigurationWarnings` 的汇总机制仍需**存在**（根管理器会遍历三个子管理器调用它）
	//   ⇒ 保留一个空实现，并写明"无依赖"是**结论**而不是忘了填。

	/// <summary>编辑器依赖校验：本管理器无子依赖（世界本体即自身）⇒ 恒无警告。</summary>
	public override string[] _GetConfigurationWarnings() => System.Array.Empty<string>();

	/// <summary>
	/// **取景半径一致性校验**（编辑器与根管理器调用）：取景与裁剪都 ∝ 球半径
	/// （`OrbitalCamera` 的取景半径 vs `PlanetGeometry.ProductionRadius`），不一致时画面会错位。
	/// ★2026-10-11 单源之后，两侧缺省取同一个常量 ⇒ 运行期已不可能漂移；
	///   本方法现在只服务"有人在场景里手改 `_planetRadius`"这一种失真。
	/// ★暂留本类：调用方只有根管理器一处（它同时看得见相机与球半径）。
	/// </summary>
	public static string[] RadiusMismatchWarnings(float cameraRadius, float planetRadius)
		=> Mathf.IsEqualApprox(cameraRadius, planetRadius)
			? System.Array.Empty<string>()
			: new[] { $"取景半径不一致：相机 {cameraRadius} ≠ 球半径 {planetRadius}（取景/裁剪 ∝ R）" };
}
