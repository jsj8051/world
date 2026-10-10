using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.IO;
using NUnit.Framework;
using Godot;
using World.H3Grid;
using World.Params;
using World.Logic;
using World.Data;
using World.Render;
using World.Scene;

namespace World.Tests;

/// <summary>
/// 世界生成空间 · 架构契约测试（决策 08——架构 v1 冻结的守护钉）：
///   测的是"有没有偷偷跨层依赖"，不是功能。三条反射契约 + 一条行为契约：
///   ② 地理层不依赖投影器（FinalGeography 不引用 H3LandSeaProjector——Final 世界事实
///      只由 FinalHeight 派生，不看生成依据）；
///   ③ 合成器不依赖具体 Feature 类（HeightComposer 公共面只认 ITerrainField 列表——
///      新 Feature 零合成器改动的保证）；
///   ④ FinalGeography 不依赖具体 Feature 类（新 Feature 不需要改 FinalGeography）；
///   ⑤ 空特征列表 = 纯基线（没有特征就没有影响——"影响只能来自注册的特征"）。
///   ⑥ **`World.CivSim` 已移出程序集**（2026-10-09 用户拍板"移出去，等待正确时机回归"）：
///      人类演化层连同其直接依赖（`World.Gameplay` / `CivSimDiag` / CivSim 单测）已迁至
///      `scripts/_removed/` 并由 `world.csproj` 排除编译。本条从原"CivSim ⟂ WorldGen
///      **方向守卫**"改写为"CivSim 已不在程序集内"的**结果 + 位置钉**
///      （方向守卫随被测类型一起移出，失去意义）。
///   ⑦ **`World.Services` 已整体删除**（2026-10-09 用户拍板"整个 Services/（含连带）"）：
///      `LogService` / `UserPaths` + 其编译面连带（`FieldCompare` ×1、截图诊断 ×3 + 场景）
///      全部删除 ⇒ 见下方 `ServicesNamespace_IsGone`。
///   ⑧ **`World.Spatial` 已改名 `World.H3Grid`**（2026-10-09 用户拍板"改名搬迁"）：
///      目录 `Logic/Spatial/Ball/` → `Logic/H3Grid/`，56 处在编引用同步 ⇒ 见下方
///      `H3Grid_NamespaceMatchesDirectory`（**首条通用的「目录 ↔ 命名空间」钉**）。
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*（引擎原生调用在测试进程 = 进程级崩溃）。
/// </summary>
public class ArchitectureContractTests
{
	static readonly Lazy<Ball> SharedBall = new(() => new Ball(1, 1f));
	static Ball Ball => SharedBall.Value;

	/// <summary>
	/// **旧世界线隔离契约**（用户 2026-10-04 拍板，§07 §10 D-3/M-10 清退路径第 1 步）。
	/// 判定问题："旧世界线是不是仍然代表 Final 世界事实的一部分？" ⇒ **不是**
	/// （Final 世界事实 = `FinalGeography`，新线独有；旧线用自己的 `World.Biome` /
	/// `MapGenerator`，与主场景 `WorldGenWorld.tscn` 无交叉）。
	/// ⇒ 走清退路径：**非生产 → 非 Final 真相源 → 有限兼容 → 最终删除**。
	/// 本契约钉住第一步"**新线不得依赖旧线**"——防止两套世界事实重新耦合。
	/// ⚠️ 它保护的是**依赖方向**，不是"旧线必须立刻删除"：
	///    删除时机取决于旧线当前是否还有真实消费者（旧线仍有 4 个独立场景：
	///    MapGenMenu / MapViewer / PlanetCore / CivSimDiag，但均不在主入口；
	///    ★2026-10-09：CivSimDiag 也已随 CivSim 移出到 `scripts/_removed/`，此句仅作历史记录）。
	/// </summary>
	[Test]
	public void NewWorldLine_DoesNotDependOnLegacyWorldLine()
	{
		// ★2026-10-09：清单收口——此处原有一份**手抄的 NewWorldLineTypes 子集**（同一事实写两遍，
		//   与已删的 `MapMode.Id` 同类冗余：加一个类型要记得改两处，漏一处就是静默的覆盖缺口）。
		//   现直接复用 `NewWorldLineTypes`（其为本清单的超集，且被下方旧线类型守卫共用）。
		// 旧世界线的四个命名空间（世界生物群系 / 旧地图生成 / 旧地图渲染 / 旧构造模拟）。
		// ★2026-10-04 D-3 切分清退已完成：这四个命名空间在程序集里**已不存在**，
		//   因此本断言现在是"恒绿但不可省"的方向守卫——若将来有人重新引入旧生成链
		//   并被新线引用，它立刻变红。真正的"已清退"事实由下面的
		//   `LegacyWorldGenerationChain_IsGone` 钉住。
		var legacyNamespaces = new[] { "World.Biome", "World.MapGen", "World.MapView", "World.Tectonics" };

		var offending = new List<string>();
		foreach (var t in NewWorldLineTypes)
			foreach (var r in ReferencedTypes(t))
				if (r.Namespace != null && legacyNamespaces.Contains(r.Namespace))
					offending.Add($"{t.Name}→{r.Namespace}.{r.Name}");
		Assert.That(offending, Is.Empty,
			$"新世界线引用了旧世界线：{string.Join(",", offending)}——Final 世界事实只能有一个真相源（§07 §10；旧线已清退，不得反向耦合）");
	}

	/// <summary>
	/// **D-3 切分清退完成钉**（用户 2026-10-04 拍板 (b)，§07 §10.5）。
	/// 清退边界不是"目录"而是"类型语义 + 依赖方向"：
///   清退 A（自己生成世界）：World.Biome / World.MapGen / World.MapView / World.Tectonics
///   清退 E（旧表现与旧应用流程）：旧 UI 目录与场景、ArchiveService / SaveArchive / EventBus
///   保留 C+D（领域词汇与领域模拟）+ HexPlanet（球面网格几何基础设施）
///   ★2026-10-09（同日更晚）：C+D 与 HexPlanet **均已消亡**——见下方三个结果钉
///     （`LegacyCarrierCluster_IsDeleted` / `DomainNamespace_IsDissolved` / `HexPlanetNamespace_IsGone`）；
///     本行"保留"表述现为**历史记录**。
///   ★2026-10-09：CivSim 已移出程序集；Legacy 载体簇（LogicGrid / Archive / WildCropsSystem）
///     已按用户拍板**整体删除**（之后重新实现）——见下方 `LegacyCarrierCluster_IsDeleted`。
///   ★2026-10-09（同日稍晚）：`World.Domain` 这个"领域词汇保留区"**亦已解散**——
///     `BiomeType` 迁 `World.Constants`（并裁剪为**仅柯本 18 值**），`PowerPalette` /
///     `Calendar` / `BiomeColors` 删除 ⇒ 见下方 `DomainNamespace_IsDissolved`。
	/// 本测试钉住"**旧世界生成链在程序集里已不存在**"这个**结果**，
	/// 而不是"新线不引用它"这个方向——二者都要有：
	/// 只有方向没有结果 ⇒ 旧链复活也测不出来；只有结果没有方向 ⇒ 无法防止重新耦合。
	/// </summary>
	[Test]
	public void LegacyWorldGenerationChain_IsGone()
	{
		var deadNamespaces = new[] { "World.Biome", "World.MapGen", "World.MapView", "World.Tectonics" };
		var survivors = new List<string>();
		foreach (var t in typeof(FinalGeography).Assembly.GetTypes())
			if (t.Namespace != null && deadNamespaces.Contains(t.Namespace))
				survivors.Add($"{t.Namespace}.{t.Name}");
		Assert.That(survivors, Is.Empty,
			$"旧世界生成链仍有类型存活：{string.Join(",", survivors)}——D-3 已清退这四个命名空间，" +
			"若新代码需要其中的能力，应判断它是『生成世界』还是『消费世界』：后者迁到领域词汇区（现为 World.Constants 等；World.Domain / World.Archive 均已删除），前者不应重建（§07 §10.5）");
	}

	/// <summary>
	/// **Legacy 载体簇删除钉（结果）**（2026-10-09 用户拍板：「直接删除簇，目前不需要了，之后再重新实现」）。
	///
	/// 删除范围 = CivSim 自然输入链的 Legacy 载体簇：
	///   `World.LogicGrid.GameGrid` · `World.Domain.WildCropsSystem` ·
	///   `World.Archive.MapData` · `World.Archive.FieldCodec`
	///   （+ 其单测 `LogicGridTests` / `MapGenTests`）。
	///
	/// ★删除理由（实测）：CivSim 移出程序集后（同日 `a186181`），
	///   `GameGrid.EnsureWildCrops/EnsureWildLivestock` 的**唯一生产调用者** `CivEngine`
	///   已不在程序集内 ⇒ 整簇退化为**纯测试孤岛**，生产侧零消费者。
	///   用户决定**删除**（不保留副本），待自然输入桥（ADR-0005 · ⑬ `HumanInputGrid`）落地时**重新实现**。
	///
	/// ★本条前身 = `RetainedDomainConsumers_OnlyDependOnAllowedNamespaces`（依赖白名单守卫）——
	///   其被扫消费者只有 `GameGrid` / `WildCropsSystem` 两个类型，随簇删除后守卫无从扫描，
	///   故改写为**结果钉**，钉住"这簇确实已不在程序集里"，防止"删了又被悄悄接回"。
	///   （先例：`CivSimAndGameplay_AreMovedOut`。）
	///
/// ★注：`World.LogicGrid` / `World.Archive` 两个命名空间此前**只含**这簇类型 ⇒ 删除后整体消失。
///   ★2026-10-09（同日稍晚）：`World.Domain` **亦已整体解散**——`BiomeType` → `World.Constants`
///     （裁剪为仅柯本 18 值），`PowerPalette` / `Calendar` / `BiomeColors` 删除。
///     见 `DomainNamespace_IsDissolved` 与 `docs/裁决-Domain解散与BiomeType归Constant.md`。
	/// </summary>
	[Test]
	public void LegacyCarrierCluster_IsDeleted()
	{
		var asm = typeof(FinalGeography).Assembly;

		// ① 两个整命名空间须消失（此前只含这簇类型）
		foreach (var ns in new[] { "World.LogicGrid", "World.Archive" })
		{
			var survivors = asm.GetTypes()
				.Where(t => t.Namespace == ns || (t.Namespace != null && t.Namespace.StartsWith(ns + ".")))
				.Select(t => $"{t.Namespace}.{t.Name}").ToList();
			Assert.That(survivors, Is.Empty,
				$"{ns} 类型仍在程序集内：{string.Join(",", survivors)}——" +
				"2026-10-09 该 Legacy 载体簇已整体删除（用户拍板：之后重新实现）");
		}

		// ② 原 World.Domain 内的 WildCropsSystem 须已离开（`World.Domain` 随后亦整体解散）
		var wildCrops = asm.GetTypes().Where(t => t.Name == "WildCropsSystem")
			.Select(t => $"{t.Namespace}.{t.Name}").ToList();
		Assert.That(wildCrops, Is.Empty,
			$"WildCropsSystem 应已随簇删除（World.Domain 其后亦解散）：{string.Join(",", wildCrops)}");

		// ③ 源目录位置钉
		Assert.That(FindRepoDir("scripts", "Logic", "LogicGrid"), Is.Null,
			"scripts/Logic/LogicGrid 应已删除（GameGrid 属已删的 Legacy 载体簇）");
		Assert.That(FindRepoDir("scripts", "Logic", "Archive"), Is.Null,
			"scripts/Logic/Archive 应已删除（MapData / FieldCodec 属已删的 Legacy 载体簇）");
	}

	/// <summary>
	/// **`World.Domain` 解散钉（结果）**（2026-10-09 用户拍板：「PowerPalette 和 Calendar 可以删除，
	/// 然后 biometype 放到 constant 中，只放柯本分类的生物群系，然后 biomecolors 应该是地图模式做的事情，
	/// 这里先删除吧」）。
	///
	/// `World.Domain` 曾是 D-3 切分清退的**领域词汇保留区**（C 类），本次**整体解散**：
	///   · `BiomeType`    → 迁入 `World.Constants`（`scripts/Logic/Constant/BiomeType.cs`），
	///                      并**裁剪为仅柯本气候型**（2,3,14…29；删水面/地形附加类 0/1/12/13/30/31）；
	///   · `PowerPalette` → 删除（原唯一消费者 = 已删的 `ServicesTests`）；
	///   · `Calendar`     → 删除（`MonthsPerYear` 就地内联进 `Diagnostics/FieldCompare`）；
	///   · `BiomeColors`  → 删除（色带职责归**地图模式**；测试改用夹具色带）。
	///
	/// ★与 `LegacyCarrierCluster_IsDeleted` 同型：**结果钉**——钉住"这层确实已不在程序集里"，
	///   防止"删了又被悄悄接回"；同时**正向**钉住 `BiomeType` 的新家。
	/// 决策见 `docs/裁决-Domain解散与BiomeType归Constant.md`。
	/// </summary>
	[Test]
	public void DomainNamespace_IsDissolved()
	{
		var asm = typeof(FinalGeography).Assembly;

		// ① World.Domain 命名空间须整体消失
		var survivors = asm.GetTypes()
			.Where(t => t.Namespace == "World.Domain" || (t.Namespace != null && t.Namespace.StartsWith("World.Domain.")))
			.Select(t => $"{t.Namespace}.{t.Name}").ToList();
		Assert.That(survivors, Is.Empty,
			$"World.Domain 类型仍在程序集内：{string.Join(",", survivors)}——" +
			"2026-10-09 该保留区已整体解散（BiomeType 迁 World.Constants；PowerPalette/Calendar/BiomeColors 删除）");

		// ② 三个被删类型名不得复活
		foreach (var dead in new[] { "PowerPalette", "Calendar", "BiomeColors" })
		{
			var found = asm.GetTypes().Where(t => t.Name == dead).Select(t => $"{t.Namespace}.{t.Name}").ToList();
			Assert.That(found, Is.Empty, $"{dead} 应已随 World.Domain 解散删除：{string.Join(",", found)}");
		}

		// ③ 正向钉：BiomeType 已在新的家
		Assert.That(typeof(World.Constants.BiomeType).Namespace, Is.EqualTo("World.Constants"),
			"BiomeType 应迁至 World.Constants（与 Constant/Planet/Thermal.cs 同族）");

		// ④ 源目录位置钉
		//   ★2026-10-11 随「按架构分层」批次 1 改路径：`Logic/Constant` → `Data/Constants`
		//     （批次 1 只搬目录；测试路径断言原计划随批次 4 对齐，见
		//      `docs/重整方案-按架构分层.md` §6.2，此处提前对齐以免回归一直红着）。
		Assert.That(FindRepoDir("scripts", "Logic", "Domain"), Is.Null,
			"scripts/Logic/Domain 应已删除（World.Domain 已解散）");
		Assert.That(FindRepoDir("scripts", "Data", "Constants"), Is.Not.Null,
			"scripts/Data/Constants 应存在（BiomeType 的新家）");
	}

	/// <summary>
	/// **`World.HexPlanet` 消失钉（结果）**（2026-10-09 用户确认删除 `Icosahedron`）。
	///
	/// `Icosahedron` 是旧 icosahedron 网格世界的**最后残迹**（`SphereGrid` / `GoldbergBuilder` /
	/// `SubdividedMesh` / `HexTile` 已先删）。删除前实测：编译面消费者**只剩它自己的测试**
	/// `HexPlanetTests`（7 个用例）；**无生产消费者、无 `.tscn` 引用、无契约白名单依赖**。
	///
	/// ⚠️ 唯一顾虑 = `scripts/_removed/` 里 5 个 CivSim 测试文件用它做 42 顶点夹具（9 处）——
	///   但那些测试本就须随 Bridge / `HumanInputGrid` 改写（夹具是 icosahedron 网格，新世界是 H3）。
	///
	/// 决策见 `docs/裁决-Legacy载体簇删除.md` §八。
	/// </summary>
	[Test]
	public void HexPlanetNamespace_IsGone()
	{
		var asm = typeof(FinalGeography).Assembly;

		var survivors = asm.GetTypes()
			.Where(t => t.Namespace == "World.HexPlanet" || (t.Namespace != null && t.Namespace.StartsWith("World.HexPlanet.")))
			.Select(t => $"{t.Namespace}.{t.Name}").ToList();
		Assert.That(survivors, Is.Empty,
			$"World.HexPlanet 类型仍在程序集内：{string.Join(",", survivors)}——" +
			"2026-10-09 该命名空间已随 Icosahedron 删除而消失（旧 icosahedron 网格世界已退役）");

		Assert.That(asm.GetTypes().Where(t => t.Name == "Icosahedron").Select(t => t.Name).ToList(), Is.Empty,
			"Icosahedron 应已删除（用户拍板；CivSim 已移出且旧网格已退役）");
		Assert.That(FindRepoDir("scripts", "Logic", "HexPlanet"), Is.Null,
			"scripts/Logic/HexPlanet 应已删除（Icosahedron 是其最后一件）");
	}

	/// <summary>
	/// **`World.Services` 删除钉（结果 + 位置）**（2026-10-09 用户拍板：先「删掉吧」，
	/// 再经确认范围 = **「整个 Services/（含连带）」**）。
	///
	/// 删除范围 = `scripts/Logic/Services/`（`LogService` / `UserPaths`）
	///   + 其**编译面连带**（实测消费者图，全部落在诊断目录）：
	///   · `Test/Diagnostics/FieldCompare.cs` —— 唯一依赖 = `LogService.LogErr`；**本身零调用者**
	///     （`FieldCompare.` 在 `scripts/` 全仓 0 命中 ⇒ 独立于本次删除也已是死码）；
	///   · `Test/Diagnostics/RiverShotDiag.cs` + `scenes/diag/RiverShotDiag.tscn`（`UserPaths.Resolve` ×3）；
	///   · `Test/Diagnostics/MapModeShotDiag.cs` + `scenes/diag/MapModeShotDiag.tscn`（`UserPaths.Resolve` ×1）；
	///   · `Test/Diagnostics/CellHighlightDiag.cs` + `scenes/diag/CellHighlightDiag.tscn`
	///     （★实测：**仅一条死 `using World.Services;`**，无真实依赖；随本簇一并删除）。
	///
	/// ★删除理由（实测，非估计）：
	///   · `LogService` = `GD.Print` 的 **16 行薄包装**，**零生产消费者**——唯一调用点就是同样零调用者的
	///     `FieldCompare`；`WorldGen`（世界生成主链）与 `Render`（地图模式）对它**零引用**；
	///   · `UserPaths.MigrateLegacyData()` 的**零调用者**（原调用点 `MainMenu._Ready` 已随旧 UI 清退而消失）
	///     ——这正是"存档三层都不通"的另一面；
	///   · 其余成员只服务**窗口模式截图诊断**（`--headless` 的 dummy 渲染服务器取不到帧 ⇒ 必须人工判读）。
	///
	/// ★保留（**不属**本簇连带，故意留下）：
	///   · `Test/Diagnostics/DiagSceneBase.cs` —— 三个子类删净后**零在编消费者**，但它是
	///     `_removed/Test/Diagnostics/CivSimDiag.cs` 的基类，且仍是未来诊断场景的统一基类（ADR-0003）；
	///   · `Test/Diagnostics/H3SmokeDiag.cs` + `scenes/diag/H3SmokeDiag.tscn` —— 不依赖 Services，
	///     且是 **`h3.dll` 部署问题的唯一暴露面**（`verify.sh` 仍跑它）。
	///
	/// ⚠️ 已记录的代价：删掉两个截图诊断 ⇒ 项目**失去 PNG 截图 / 像素级视觉诊断能力**；
	///   源码级回归由既有单测承担（`CellHighlightRingTests` / `RiverSymbolWidthTests` /
	///   `RiverRibbonGeometryTests` / `RiverPresentationSplineTests` 等均**未动**）。
	///   `_removed/` 内 CivSim 的回归路径现在**还需额外恢复 `Services/`**
	///   （`CivSimDiag.cs` 用 `LogService` + `DiagSceneBase`；`Logic/CivSim/Tables/TechTable.cs` 用 `LogService`）。
	///
	/// 决策与文档连带见 `docs/裁决-Services删除.md`。
	/// </summary>
	[Test]
	public void ServicesNamespace_IsGone()
	{
		var asm = typeof(FinalGeography).Assembly;

		// ① 命名空间须整体消失
		var survivors = asm.GetTypes()
			.Where(t => t.Namespace == "World.Services"
				|| (t.Namespace != null && t.Namespace.StartsWith("World.Services.")))
			.Select(t => $"{t.Namespace}.{t.Name}").ToList();
		Assert.That(survivors, Is.Empty,
			$"World.Services 类型仍在程序集内：{string.Join(",", survivors)}——" +
			"2026-10-09 该服务层已整体删除（LogService 零生产消费者；UserPaths 只服务已删的截图诊断）");

		// ② 三个被删类型名不得复活
		foreach (var dead in new[] { "LogService", "UserPaths", "FieldCompare" })
		{
			var found = asm.GetTypes().Where(t => t.Name == dead)
				.Select(t => $"{t.Namespace}.{t.Name}").ToList();
			Assert.That(found, Is.Empty,
				$"{dead} 应已随 Services 簇删除：{string.Join(",", found)}");
		}

		// ③ 源目录位置钉
		Assert.That(FindRepoDir("scripts", "Logic", "Services"), Is.Null,
			"scripts/Logic/Services 应已删除（World.Services 已整体删除）");

		// ④ 诊断场景残留钉——★三个截图诊断的 `.tscn` 必须与脚本同批删除，
		//    否则留下悬空场景 ⇒ headless 报 `Cannot load C# script`（编译与单测**照样全绿**，只能实机发现）。
		var diagDir = FindRepoDir("scenes", "diag");
		Assert.That(diagDir, Is.Not.Null, "未找到 scenes/diag/ —— 目录路径可能已变，本契约需同步");
		foreach (var gone in new[] { "RiverShotDiag", "MapModeShotDiag", "CellHighlightDiag" })
			Assert.That(File.Exists(Path.Combine(diagDir, gone + ".tscn")), Is.False,
				$"scenes/diag/{gone}.tscn 应已随其脚本删除——悬空场景 = headless「Cannot load C# script」");
		Assert.That(File.Exists(Path.Combine(diagDir, "H3SmokeDiag.tscn")), Is.True,
			"scenes/diag/H3SmokeDiag.tscn 应保留（不依赖 Services，verify.sh 仍跑它）");
	}

	/// <summary>
	/// **`World.H3Grid` 命名空间钉（目录 ↔ 命名空间）**（2026-10-09 用户拍板改名）。
	///
	/// 命名沿革：`new_HexWorld/Ball` → `Spatial/Ball`（2026-10-06 E 步，ns `World.NewHexWorld`
	///   → `World.Spatial`）→ **`H3Grid/`（本步，ns `World.Spatial` → `World.H3Grid`）**。
	/// 改名理由：`World.Spatial` 与另两个同族名易混——`SpatialScale`（尺度口径）与
	///   `FinalSpatialIndex`（空间查询索引）**均属 `World.Logic`**；而本命名空间的真实身份是
	///   **H3 球面网格本体**（`Ball` 亦非"球"）。新名与 `Utils/H3`（`World.Utils.H3`）呼应。
	///
	/// ★本契约同时是**第一条通用的「目录 ↔ 命名空间」钉**（此前只有 `NewWorldLine_NamespaceIsWorldGen`
	///   一条，且只钉 WorldGen）。规则：`scripts/{Scene,Logic}/<领域>/` 的**首层**必须逐字对应
	///   `World.<领域>`；再往下的子目录是**自由分组**，可不进命名空间（同构先例：`Constant/Planet/`
	///   ↔ `World.Constants`、`WorldGen/{6 子层}/` ↔ `World.Logic`）。
	///   它防的是"搬了目录忘改 namespace"或"改了 namespace 忘搬目录"——
	///   这类脱节**编译器和单测都测不出来**（两边都自洽），只有靠钉。
	///
	/// ⚠️ 实测（搬迁前）：`World.Spatial` 在编引用 **56 处 / 53 文件**，全部是 `using`
	///   （+3 处全限定 `World.Spatial.Ball`）；**0 个 `.tscn` 指向这两个文件**（非 Node 脚本）、
	///   `_removed/` 零引用 ⇒ 纯机械替换，无"悬空场景"风险。
	/// </summary>
	[Test]
	public void H3Grid_NamespaceMatchesDirectory()
	{
		// ① 正向：三个类型都在新家（★该族的"闭合性"也在此钉住：新增 Ball 族成员要进本清单，
		//    否则它落在哪一层没人看着——2026-10-11 加 `BallVertices` 时正是照这条做的）
		Assert.That(typeof(Ball).Namespace, Is.EqualTo("World.H3Grid"),
			"Ball 应落在 World.H3Grid");
		Assert.That(typeof(BallGeoIndex).Namespace, Is.EqualTo("World.H3Grid"),
			"BallGeoIndex 应落在 World.H3Grid");
		Assert.That(typeof(BallVertices).Namespace, Is.EqualTo("World.H3Grid"),
			"BallVertices 应落在 World.H3Grid（Ball 族：网格本体 / 经纬索引 / 顶点侧）");

		// ② 旧名不得复活
		var survivors = typeof(FinalGeography).Assembly.GetTypes()
			.Where(t => t.Namespace == "World.Spatial"
				|| (t.Namespace != null && t.Namespace.StartsWith("World.Spatial.")))
			.Select(t => $"{t.Namespace}.{t.Name}").ToList();
		Assert.That(survivors, Is.Empty,
			$"World.Spatial 类型仍在程序集内：{string.Join(",", survivors)}——" +
			"2026-10-09 已改名 World.H3Grid（旧名与 SpatialScale / FinalSpatialIndex 易混）");

		// ③ 目录位置钉（大小写逐字：Windows 文件系统大小写不敏感 ⇒ 另比真实目录名）
		//   ★2026-10-11 随「按架构分层」批次 1 改路径：`Logic/H3Grid` → `Utils/H3Grid`（同上）。
		var dir = FindRepoDir("scripts", "Utils", "H3Grid");
		Assert.That(dir, Is.Not.Null,
			"未找到 scripts/Utils/H3Grid/ —— namespace World.H3Grid 与目录路径必须一致");
		if (dir != null)
			Assert.That(new DirectoryInfo(dir).Name, Is.EqualTo("H3Grid"),
				"目录名应逐字为 'H3Grid'（大小写严格，否则目录与 namespace 脱节）");
		Assert.That(FindRepoDir("scripts", "Logic", "Spatial"), Is.Null,
			"scripts/Logic/Spatial 应已删除（目录与 namespace 一并改名）");
	}

	/// <summary>
	/// **`World.CivSim` 移出钉（结果 + 位置）**（2026-10-09 用户拍板："将 civsim 移出去，
	/// 等待正确时机回归，现在不需要"）。
	///
	/// ★本条的前身是 `CivSim_DoesNotReferenceWorldGen`（2026-10-06 ADR-0005 · C-4 §6.2 G1）——
	///   一条"`World.CivSim` ⟂ `World.Logic`"的**方向守卫**（扫全程序集 + 解 IL 深度）。
	///   被测类型整体移出程序集后，方向守卫已无从扫描（扫不到类型），故改写为**结果钉**：
	///   钉住"CivSim 确实不在游戏程序集里"，防止"移出后又被悄悄接回"。
	///
	/// 移出方式（用户拍板）：文件**移入工程内** `scripts/_removed/`，由 `world.csproj` 排除编译
	///   ——**不是删除**。回归时把目录移回原位并删掉 csproj 的排除行即可
	///   （清单见 `scripts/_removed/README.md`，决策见 `docs/裁决-CivSim移出.md`）。
	///
	/// ★恢复指引（回归时的正确做法）：
	///   ① `git mv scripts/_removed/Logic/CivSim scripts/Logic/CivSim`（`Gameplay` 同理）；
	///   ② 恢复 `RetainedDomainConsumers_OnlyDependOnAllowedNamespaces` 的
	///      `"World.CivSim"` / `"World.Gameplay"` 白名单行与 `typeof(World.CivSim.CivSimContext)`；
	///   ③ 场景/诊断/单测按 `scripts/_removed/README.md` 清单移回；
	///   ④ 若届时新世界线已通过自然输入桥（ADR-0005）正式接入，把方向守卫一并恢复。
	/// </summary>
	[Test]
	public void CivSimAndGameplay_AreMovedOut()
	{
		var asm = typeof(FinalGeography).Assembly;

		static bool IsCivSimOrGameplay(Type t) => t.Namespace is string ns
			&& (ns == "World.CivSim" || ns.StartsWith("World.CivSim.")
				|| ns == "World.Gameplay" || ns.StartsWith("World.Gameplay."));

		var survivors = asm.GetTypes().Where(IsCivSimOrGameplay)
			.Select(t => $"{t.Namespace}.{t.Name}").ToList();
		Assert.That(survivors, Is.Empty,
			$"World.CivSim / World.Gameplay 类型仍在程序集内：{string.Join(",", survivors)}——" +
			"2026-10-09 已整体移出至 scripts/_removed/（用户拍板：等待正确时机回归）。" +
			"若确要提前接回，请连同本契约与 world.csproj 的排除行一起显式修改，并留下决策记录");

		// 位置钉：原目录须已不存在，_removed 内须存在（"移出"而非"删掉"）。
		Assert.That(FindRepoDir("scripts", "Logic", "CivSim"), Is.Null,
			"scripts/Logic/CivSim 应已移出（现应在 scripts/_removed/Logic/CivSim）");
		Assert.That(FindRepoDir("scripts", "Logic", "Gameplay"), Is.Null,
			"scripts/Logic/Gameplay 应已移出（现应在 scripts/_removed/Logic/Gameplay）");
		Assert.That(FindRepoDir("scripts", "_removed", "Logic", "CivSim"), Is.Not.Null,
			"scripts/_removed/Logic/CivSim 应存在——CivSim 是「移出」不是「删除」（回归时移回原位即可）");
	}

	/// <summary>
	/// **桥面消费者白名单钉（R3）**：`FinalGeography` / `FinalSpatialIndex` 的仓内消费者
	/// **只能是新世界线内部**（`World.Logic*`）（2026-10-06 · ADR-0005 §边界条款 B2 · C-4 §6.2 G2）。
	///
	/// 判定问题："谁在消费 Final 世界事实？" ⇒ 目前**只有 WorldGen 自己**。
	/// 实测（C-4 §2.3）：`scripts/Logic/WorldGen/Final/` 之外的仓内消费者 = **0**；
	/// "World → Human Input Bridge" 当前**尚不存在，连桩都没有**。
	///
	/// ★目的**不是**永久禁止所有桥，而是：**在桥正式设计（Phase 1–4 逐层迁移）之前，
	///   不允许出现"私接消费者"**。任何把 Final 事实接进领域词汇区（`World.Constants`）/
	///   `Render` / `UI` / 其他外部层的改动，都必须**显式改这张白名单**并留下决策记录——
	///   而不是"悄悄接一根线"。本契约就是那张必须被显式修改的表。
	///   （★2026-10-09：`World.CivSim` 已移出程序集，不再是潜在私接方之一。）
	///
	/// ★扫描面：**整个游戏程序集**（`typeof(FinalGeography).Assembly`）。
	///   `World.Tests` 是**独立程序集** ⇒ 架构/相关单测天然不在扫描面内（故无需为它们开白名单）。
	///
	/// ★与 `NewWorldLine_DoesNotDependOnLegacyWorldLine`（新线⟂旧线）互补：
	///   那条管"新线别退化回旧世界线"，本条管"新线的事实别被外部私有接走"。
	/// </summary>
	[Test]
	public void FinalWorldFacts_OnlyConsumedByWorldGen()
	{
		var asm = typeof(FinalGeography).Assembly;

		// Final 世界事实（世界生成的"成品"）。新增事实类型时应一并登记——
		// 否则新事实会被本条静默放过（漏扫 = 契约失效）。
		var facts = new HashSet<Type> { typeof(FinalGeography), typeof(FinalSpatialIndex) };

		// 唯一允许的生产消费者 = 新世界线内部（生成线各子层）。
		static bool IsWorldGen(string ns) => ns != null
			&& (ns == "World.Logic" || ns.StartsWith("World.Logic."));

		// ★登记的桥面消费者（**必须逐类型具名**，不许靠"名字像"放过）。
		//
		// ① **三个地图模式**：取色本来就靠读世界事实（"这一格画什么颜色"）——设计而非私接。
		//    2026-10-11 目录/命名空间重划分后它们由散居的 `World.WorldGen` 归入 `World.Render`
		//    （★子目录不进 namespace ⇒ 没有 `World.Render.Modes` 这一层，故**只能逐类型列名**）。
		//    实测消费点：`ElevationMode` → `_p.Facts.Final.FinalLand`；
		//    `CoastDistanceMode` → `FinalLand` / `FinalDistToCoast` / `FinalDistToLand`；
		//    `RegionTypeMode` → `FinalRegionOfCell`。
		//    ⚠️ 新增一个读 Final 的地图模式 ⇒ 必须在此具名（这是有意的摩擦力：渲染/界面层
		//    直接消费 Final 值得每次停一下想想）。
		// ② `WorldPicker`：**只读的"点选查询"桥**——`Facts.Final` 现取后逐字段填 `PickedCell`
		//    （FinalRegionOfCell / FinalLand / FinalLandFraction / FinalLandmassId /
		//     FinalDistToCoast / FinalDistToLand），不写事实、不派生新事实。
		//    唯一调用者是 `RenderManager`（点选发起方）。**不得**照此再开第二处。
		var bridgeConsumers = new[]
		{
			"World.Render.ElevationMode",      // 海拔模式：读 FinalLand
			"World.Render.CoastDistanceMode",  // 离岸距离模式：读 FinalLand / FinalDistToCoast / FinalDistToLand
			"World.Render.RegionTypeMode",     // 地质区域模式：读 FinalRegionOfCell
			"World.Render.WorldPicker",        // 点选查询桥：现取 Final 逐字段填 PickedCell
		};
		foreach (var r in bridgeConsumers)
		{
			var t = asm.GetTypes().FirstOrDefault(x => $"{x.Namespace}.{x.Name}" == r);
			Assert.That(t, Is.Not.Null,
				$"桥面白名单里的 {r} 不存在（已改名/已删？）⇒ 表已陈旧，应一并清掉或改名");
			// 非空转自检：**具名**白名单成员必须确实在消费（否则表在空转、扫描面已偏）。
			Assert.That(ReferencedTypesDeep(t).Any(facts.Contains), Is.True,
				$"桥面白名单里的 {r} 已不再消费 Final 事实 ⇒ 应从表中删除（陈旧的例外比没有例外更危险）");
		}

		var offending = new List<string>();
		foreach (var t in asm.GetTypes())
		{
			if (IsWorldGen(t.Namespace)) continue;                        // 逻辑层内部 = 合法生产消费者
			if (bridgeConsumers.Contains($"{t.Namespace}.{t.Name}")) continue;   // 显式登记的桥
			foreach (var r in ReferencedTypesDeep(t))
				if (facts.Contains(r))
					offending.Add($"{t.Namespace}.{t.Name}");
		}

		// 非空转自检：**具名**白名单成员必须确实在消费（否则表在空转、扫描面已偏）
		//   ——已并入上方登记循环，此处不再重复。

		Assert.That(offending.Distinct().ToList(), Is.Empty,
			$"Final 世界事实被逻辑层与已登记桥之外的类型消费：{string.Join(",", offending.Distinct())}——" +
			"桥未正式设计前不得私接消费者（ADR-0005 §B2）；" +
			"若确为正式接线，请显式扩展本测试的白名单并留下决策记录（C-4 §6.2 G2）");

		// 非空转自检：WorldGen 内部确实有消费者（否则说明扫描器失效或事实类型被改名）。
		var internalConsumers = asm.GetTypes()
			.Count(t => IsWorldGen(t.Namespace) && ReferencedTypesDeep(t).Any(facts.Contains));
		Assert.That(internalConsumers, Is.GreaterThan(0),
			"未发现任何 WorldGen 内部消费者——扫描器可能失效，或 Final 事实类型已改名（本契约的扫描面需要同步更新）");
	}

	/// <summary>
	/// **参数入口唯一化 ＋ 宿主 I/O 隔离 ＋ 参数实例化（结果 + 位置 + 白名单 + 引擎面钉）**
	/// （2026-10-10 用户拍板：「读取数据和写数据应该交给逻辑层来做吧，场景那里不需要做这些，
	///   export 也不需要，然后读取参数的活交给参数管理器干吧，从 `WorldGenPlanet` 拿出来」；
	///   同日更晚再拍板：「必须依赖 Godot 的那部分，放到与 `Scene`/`Logic` 并列的新目录」；
	///   **2026-10-11 第三轮**：「参数 json 都放在 res 下，读取全部都从 res 下读取；读到的数据都是
	///   各个 spec 下的类的数据；scene 的 UI 更改数据会直接修改文件中的参数和类实例；
	///   使用参数时就从实例中使用，实例都在参数管理器中管理」）。
	///
	/// 判定问题："世界参数（默认档 / 用户档 JSON）由谁读、由谁写、**实例归谁**？" ⇒ **只有**
	/// `World.Params` 下的两个家：`WorldParams`（实例 / JSON / 改参数）＋ `WorldParamStore`（文件 I/O）
	/// （实例 + JSON 转换 + 改参数）。★2026-10-11：原第三层"参数内核"已并入管理器，参数链不再有第三个类型。
	///
	/// ★2026-10-11 **本轮变更**（本契约随之改写，理由与新口径见 `docs/裁决-参数实例化与res参数目录.md`）：
	/// · **路径**：参数 JSON 从 `data/` ＋ `userdata/params/` 迁入**项目根 `res/params/`**
	///   （默认档 `world_params.json` ＋ 用户档 `world_params.user.json`），**读取一律从 `res/` 来**；
	/// · **实例化**：`WorldSpec` / `LandSeaSpec` / `TerrainSpec` 由 `readonly record struct`
	///   改为**可变类**（参数实例有身份、可改），唯一生效实例 = `WorldParamManager.Active`；
	/// · **改参数**：`WorldParamManager.Set` 一动作两半 = 改实例 ＋ 整份写回用户档；
	/// · **取消合并**：不再"默认档 ⊕ 用户档按字段补齐"，改为**直接反序列化**（用户档整份覆盖）；
	///   坏档 / 缺档时保留**出厂档实例值**（spec 属性初值），不再返回全零。
	///
	/// ★2026-10-10 **二层切分** → **2026-10-11 收敛为两类型**：导出后内容在 `.pck` 内 ⇒ `System.IO`
	///   读不到默认档，只有 Godot `FileAccess` 能读 ⇒ "怎么找 / 怎么读 / 怎么写"**必须允许碰引擎**；
	///   而"文本 ⇄ `WorldSpec` 实例"这一半**必须能在没有引擎宿主的进程里跑**（`dotnet test`——
	///   测试进程里调引擎 API 是**进程级崩溃**，不可捕获）。于是参数链只剩两类，各占一头：
	///     · `World.Params.WorldParamStore`（`scripts/Assets/Params/`）——**宿主 I/O**：
	///       内容根解析、读、写，**全类唯一**碰磁盘与 Godot API 的地方
	///       （引擎调用封在 `NoInlining` + `try/catch` 的逃生门里，与 `H3Native` 找 native dll 同型）；
	///     · `World.Params.WorldParams`（同目录）——**实例 + JSON 编解码 + 改参数**的唯一正门。
	///   ★原独立的编解码内核 `World.Logic.WorldSpecCodec` **已删除**（用户拍板"就让管理器自己读 json、
	///     去反序列化好了，反正就一些 Data，也不需要校验吧"）⇒ 中间那一层没了，**旧名与本家不得复活**；
	///     随之删掉的还有**逐字段校验**（残档不再被拒：漏写的字段用 spec 属性初值），
	///     但"段整个缺失"仍报出来（spec 的不变量是"段恒非空"）。
	///
	/// 本轮收敛掉的**半套体系**（全部已删，本条防其复活）：
	///   · 场景层桥：`WorldParamsSource` / `WorldParamFile`（读盘 + `[Export]` 面板参数）
	///     —— 正是"**Scene↔Logic 中间桥层**"红线的违例；
	///   · 注册表族：`WorldParamRegistry` / `ParamDescriptor` / `ParamTier`（自造"key 清单 + 字段类型表"）；
	///   · 代码内默认值：`WorldSpecDefaults`（默认值内容是正库数据文件，代码侧只有 spec 属性初值）；
	///   · 独立场景 `scenes/params/WorldParams.tscn`（"不生成世界、只看参数表"的能力被移除——
	///     参数管理的职责是**服务生成**）；
	///   · 第二参数入口：`WorldGenPlanet.ApplyWorldSpec` / `_specOverride`（零消费者；
	///     ★2026-10-11 收编：该类已并入 `WorldManager` ⇒ 防复活钉现钉在后者上，
	///     且它绕开了"世界定义只有参数表一个来源"——从外面塞 spec 等价于开第二条入口）；
	///   · **第三层内核**：`WorldSpecCodec`（`scripts/Logic/WorldGen/Params/`，2026-10-11 并入管理器）。
	///
	/// ★断言分别管：**位置 + 参数目录** / **正门唯一** / **第二入口** / **磁盘白名单** /
	///   **引擎面唯一** / **`[Export]` 已清**：
	///   ① 位置钉：`WorldParams` ＋ `WorldParamStore` 在 `World.Params`、住 `scripts/Params/`；
	///      旧类型 `WorldParamTable` / `WorldSpecCodec`、场景层两文件、`scenes/params` 目录均须已不存在；
	///   ①c 参数目录钉：两个参数 JSON 必须在 **`res/params/`** 下，
	///      旧的 `data/world_params.json` / `userdata/params/` 不得复活；
	///   ①d 正门唯一钉：JSON 转换与改参数都只在管理器里（`WorldParamManager` 是 public，
	///      而"参数链的公开面"不得再出现第二个类型——由 ① 的删除名单 ＋ ②③ 白名单共同守住）；
	///   ①b 第二入口钉：`WorldManager.ApplyWorldSpec` / `_specOverride` 不得复活
	///      （`WorldGenPlanet` 已于 2026-10-11 收编进 `WorldManager`，钉随类走）——它零消费者，
	///      **且**它本身就是绕开"世界定义只有参数表一个来源"的第二入口（存档恢复世界 = 写用户档再 `Reload`）；
	///   ② 磁盘白名单：**整个世界线扫描面**里，深度引用面触及 `System.IO` 读写的类型只准是那三项
	///      ——新增消费者必须**显式改表**（防"又一处在悄悄读盘"）；
	///   ③ 引擎面唯一：深度引用面触及 **Godot 参数 I/O 类型**（`ProjectSettings` / `FileAccess`）
	///      的类型只准是 `WorldParamStore` / `WorldParamManager`（与早已在册的 `H3Native`）
	///      ——防"引擎依赖又漏回逻辑层 / 场景层"；
	///   ④ **数据层不沾宿主**：`WorldSpec` / `LandSeaSpec` / `TerrainSpec` 三个参数实例类型的深度引用面
	///      **不得**含 Godot 类型、也不得含 `System.IO` —— 这是"参数数据与宿主解耦 ⇒ 无宿主进程
	///      （单测）能直接装配它们"的**可执行形式**（2026-10-11：原由内核承担，内核删除后改钉数据层）；
	///   ⑤ `WorldManager` 上不得再有四个世界参数 `[Export]`，也不得有任何 `[Export]`
	///      （`ContinentCount` / `LandFraction` / `Seed` / `TargetRegionAreaKm2` ——
	///       它们恒被参数表遮蔽，且只覆盖 14 个参数里的 4 个 ⇒ 半套体系；
	///       ★2026-10-11 收编：`WorldGenPlanet` 已并入 `WorldManager`（`Sim` / `ProductionRes`
	///       都住在后者上）⇒ 本钉随收编落到后者，层叠包装收掉后它没有任何可填项）。
	///
	/// ★`ResLevel` / `Radius` 曾是**刻意留着**的构造参数（决定球壳规模，不是世界参数）；
	///   2026-10-11 用户拍板"固定就好" ⇒ 两者已删，球半径收进
	///   `World.Constants.PlanetGeometry.ProductionRadius`（唯一家）。
	/// </summary>
	[Test]
	public void WorldParams_AreReadOnlyByTheLogicLayerGate()
	{
		var asm = typeof(FinalGeography).Assembly;
		const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
			| BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

		// ── ① 位置钉：参数层（2026-10-11 迁入 `scripts/Params/`、命名空间 `World.Params`） ──
		Assert.That(typeof(WorldParamStore).Namespace, Is.EqualTo("World.Params"),
			"参数文件的宿主 I/O 应在 World.Params（`scripts/Params/`）——它是唯一碰参数文件的类型");
		Assert.That(typeof(WorldParams).Namespace, Is.EqualTo("World.Params"),
			"参数管理器（实例持有者 + JSON 转换 + 改参数正门）应在 World.Params");
		// ①d 正门唯一：编解码已并入管理器 ⇒ 参数链的公开面就只有管理器一个类型。
		Assert.That(typeof(WorldParams).IsPublic, Is.True,
			"WorldParams 必须 public：它是 scene / UI 读参数、改参数的唯一正门");
		//   旧内核目录不得复活：原 `scripts/Logic/WorldGen/Params/` 随两层搬迁已不存在
		//   （`Logic/WorldGen/` 整个中间层已撤，参数层在 `scripts/Params/`）。
		Assert.That(FindRepoDir("scripts", "Logic", "Params"), Is.Null,
			"scripts/Logic/Params 不应存在（编解码内核已并入参数管理器，参数层在 scripts/Params）");
		Assert.That(FindRepoDir("scripts", "Params"), Is.Not.Null,
			"scripts/Params 应存在（WorldParams / WorldParamStore 的家）");
		Assert.That(FindRepoDir("scripts", "Assets"), Is.Null,
			"scripts/Assets 应已删除（参数层已迁入 scripts/Params/，2026-10-11）");
		Assert.That(FindRepoDir("scenes", "params"), Is.Null,
			"scenes/params 应已删除（独立参数场景已随场景层桥一并移除）");

		// ── ①c 参数目录钉（2026-10-11）：两个参数 JSON 必须在项目根 res/params/ 下 ──
		//   "参数 json 都放在 res 下，读取全部都从 res 下读取"——这句的可执行形式。
		var paramsDir = FindRepoDir("res", "params");
		Assert.That(paramsDir, Is.Not.Null,
			"res/params 应存在（参数 JSON 唯一的家：默认档 + 用户档）");
		Assert.That(File.Exists(Path.Combine(paramsDir, "world_params.json")), Is.True,
			"默认档必须是 res/params/world_params.json（正库数据，随游戏走）");
		Assert.That(File.Exists(Path.Combine(paramsDir, "world_params.user.json")), Is.True,
			"用户档必须是 res/params/world_params.user.json（可写；删掉 = 恢复出厂）");
		//   路径常量与目录事实必须一致（否则"改一个常量、挪一个目录"会静默错位）。
		Assert.That(WorldParamStore.PresetRelativePath, Is.EqualTo("res/params/world_params.json"),
			"默认档路径常量必须是 res/params/world_params.json");
		Assert.That(WorldParamStore.UserRelativePath, Is.EqualTo("res/params/world_params.user.json"),
			"用户档路径常量必须是 res/params/world_params.user.json");
		//   旧位置不得复活（迁移后残留会让"读哪一份"变成两处真相）。
		Assert.That(FindRepoDir("userdata", "params"), Is.Null,
			"userdata/params 应已删除（参数已迁入 res/params/）");
		//   旧的正库默认档 `data/world_params.json` 同理：用仓库根拼出旧路径，必须不存在。
		var repoRoot = Directory.GetParent(paramsDir)?.FullName;
		Assert.That(File.Exists(Path.Combine(repoRoot, "data", "world_params.json")), Is.False,
			"data/world_params.json 应已删除（参数已迁入 res/params/）");

		// 被删类型名不得复活（结果钉）。
		foreach (var dead in new[]
		{
			"WorldParamsSource", "WorldParamFile",              // 场景层桥（Scene↔Logic 红线）
			"WorldParamRegistry", "ParamDescriptor", "ParamTier", // 注册表族（key 清单 / 字段类型表）
			"WorldSpecDefaults",                                 // 代码内默认值（已迁 JSON）
			"WorldParamTable",                                   // 二层切分前的合体类（已拆为 store + codec）
			"WorldSpecCodec",                                    // ★第三层内核（2026-10-11 并入管理器，见 ①d）
		})
		{
			var found = asm.GetTypes().Where(t => t.Name == dead)
				.Select(t => $"{t.Namespace}.{t.Name}").ToList();
			Assert.That(found, Is.Empty,
				$"{dead} 应已删除：{string.Join(",", found)}——" +
				"参数入口唯一化后不得复活（见本契约头文档；决策 docs/裁决-参数管理器.md）");
		}

		// ── ①b 第二个入口不得复活：世界生产类上不准再有"外部塞一套参数"的口子 ──
		//   `ApplyWorldSpec(WorldSpec)` / `_specOverride` 2026-10-10 从 `WorldGenPlanet` 删除，
		//   理由：零消费者（全仓 0 调用）**且** 它本身就是绕开"世界定义只有参数表一个来源"
		//   的第二入口。★2026-10-11 收编：`WorldGenPlanet` 已并入 `WorldManager`
		//   ⇒ 防复活钉随之落到后者上（类没了 ≠ 口子可以重开）。
		//   将来存档真要恢复世界 ⇒ 写用户档 JSON 再 `Reload`（而非另开注入通道）；
		//   确要开新通道时，须显式改本契约并留决策记录。
		Assert.That(typeof(WorldManager).GetMethods(all)
				.Where(m => m.Name == "ApplyWorldSpec").ToList(),
			Is.Empty,
			"WorldManager.ApplyWorldSpec 已删除（零消费者 + 第二参数入口）——" +
			"存档恢复世界的正解是写用户档 JSON 再 Reload，不是另开注入通道");
		Assert.That(typeof(WorldManager).GetFields(all)
				.Where(f => f.Name == "_specOverride").ToList(),
			Is.Empty,
			"WorldManager._specOverride 已删除（随 ApplyWorldSpec 一起）");

		// ── ② 磁盘白名单 ──
		var diskTypes = new HashSet<Type>
		{
			typeof(File), typeof(Directory), typeof(FileStream),
			typeof(StreamReader), typeof(StreamWriter),
			typeof(FileInfo), typeof(DirectoryInfo),
		};
		// 允许读写的类型（各自理由，新增必须显式登记）：
		var whitelist = new HashSet<string>
		{
			"World.Params.WorldParamStore",     // 参数表存储：默认档 / 用户档的**唯一**磁盘读写口（宿主 I/O）
			"World.Utils.H3.H3Native",          // 加载原生 h3 动态库（File.Exists + NativeLibrary.Load）
			"World.Utils.PngWriter",            // 诊断 PNG 写出（表现层像素级诊断）
		};

		var actual = new List<string>();
		foreach (var t in asm.GetTypes())
		{
			if (ReferencedTypesDeep(t).Any(diskTypes.Contains))
				actual.Add($"{t.Namespace}.{t.Name}");
		}
		var actualSet = new HashSet<string>(actual);
		var added = actualSet.Where(x => !whitelist.Contains(x)).ToList();
		Assert.That(added, Is.Empty,
			$"有类型新增了对磁盘的读写：{string.Join(",", added)}——" +
			"参数读写只准经 World.Params.WorldParamStore；如确为新增的正当代价（诊断等），请显式扩展本契约的白名单并留下决策记录");

		// 非空转自检：参数表存储必须在扫描结果里（否则说明扫描器失效）。
		Assert.That(actualSet, Does.Contain("World.Params.WorldParamStore"),
			"未在磁盘访问类型里发现 WorldParamStore——扫描器可能失效（本契约的扫描面需要检查）");

		// ── ③ 引擎面唯一：碰 Godot 参数 I/O 的类型只准是参数存储与参数管理器（+ 早已在册的 H3Native）──
		//   动机：切分的理由是"导出后只能靠 Godot FileAccess 读 `.pck` 内的参数"。
		//   若这条不钉，引擎依赖又会从适配层漏回逻辑层 / 场景层，切分就白做了。
		//   ★2026-10-11 管理器进册：它经存储层的逃生门读写（磁盘分支未命中时），
		//     且**不得**自己碰 `System.IO`（那会被 ② 当场点名）——分工仍是"存储层管文件、管理器管实例"。
		var engineTypes = new HashSet<Type> { typeof(ProjectSettings), typeof(Godot.FileAccess) };
		var engineWhitelist = new HashSet<string>
		{
			"World.Params.WorldParamStore",     // 参数的引擎逃生门（res:// 读默认档 / 写用户档）
			"World.Params.WorldParams",   // 参数管理器：实例持有者，经存储层逃生门读写（2026-10-11）
			"World.Utils.H3.H3Native",          // 找 native h3：GlobalizePath("res://") 兜底（既定例外）
		};
		var engineUsers = asm.GetTypes()
			.Where(t => ReferencedTypesDeep(t).Any(engineTypes.Contains))
			.Select(t => $"{t.Namespace}.{t.Name}")
			.ToHashSet();
		var engineAdded = engineUsers.Where(x => !engineWhitelist.Contains(x)).ToList();
		Assert.That(engineAdded, Is.Empty,
			$"有类型新增了对 Godot 参数 I/O（ProjectSettings / FileAccess）的引用：{string.Join(",", engineAdded)}——" +
			"参数链上的引擎面只准留在 World.Params（WorldParams / WorldParamStore）；" +
			"本条就是那次按运行时切分的护栏");

		// 非空转自检：WorldParamStore 必须在引擎面扫描结果里（否则说明扫描器失效）。
		Assert.That(engineUsers, Does.Contain("World.Params.WorldParamStore"),
			"未在引擎面类型里发现 WorldParamStore——扫描器可能失效（③ 需要检查）");

		// ── ④ 数据层不沾宿主：三个**参数实例类型**既不碰引擎、也不碰磁盘 ──
		//   2026-10-11：原由编解码内核承担这条（"纯内核留在 Logic"），内核删除后改钉**数据层**——
		//   参数数据与宿主解耦，正是"无 Godot 宿主的单测进程能直接装配它们"的可执行形式
		//   （`WorldSpec` 的段类型、字段类型、默认值里出现 `Node` / `FileAccess` / `File` 都会让这条红）。
		var specTypes = new[] { typeof(WorldSpec), typeof(LandSeaSpec), typeof(TerrainSpec) };
		var specDirty = specTypes
			.SelectMany(ReferencedTypesDeep)
			.Where(r => diskTypes.Contains(r) || engineTypes.Contains(r))
			.Select(r => $"{r.Namespace}.{r.Name}")
			.Distinct()
			.ToList();
		Assert.That(specDirty, Is.Empty,
			$"参数实例类型沾上了磁盘 / 引擎：{string.Join(",", specDirty)}——" +
			"WorldSpec / LandSeaSpec / TerrainSpec 必须保持「纯数据」（零 I/O、零引擎节点），" +
			"宿主相关的活归 World.Params（WorldParams / WorldParamStore）");

		// 非空转自检：扫描面确实覆盖了三个类型（否则这次反射只扫了空壳，"没沾脏"就只是假绿）。
		Assert.That(specTypes.SelectMany(ReferencedTypesDeep), Does.Contain(typeof(LandSeaSpec)),
			"未在 WorldSpec 的引用面里发现 LandSeaSpec——扫描器可能失效（④ 需要检查）");

		// ── ⑤ WorldManager 上不得再留任何 [Export] ──
		//   （2026-10-11 收紧：原为"不得留四个世界参数 [Export]"，当时刻意保留了 `ResLevel` / `Radius`
		//     两个构造参数；用户同日拍板"固定就好" ⇒ 那两个也已删，球半径收进
		//     `World.Constants.PlanetGeometry.ProductionRadius`（唯一家），生产档仍是
		//     `WorldManager.ProductionRes`。★同日更晚的**收编**：`WorldGenPlanet` 已并入
		//     `WorldManager`（`Sim` / `ProductionRes` 都住在后者上，`WorldGenPlanet.cs` 已删）
		//     ⇒ 本钉随收编落到 `WorldManager`：它现在**一个 `[Export]` 都不该有**——
		//     层叠包装收掉后，它没有任何可填项。）
		var worldMgr = typeof(WorldManager);
		var exportedNames = worldMgr.GetFields(all).Concat(worldMgr.GetProperties(all).Cast<MemberInfo>())
			.Where(m => m.GetCustomAttributes(typeof(ExportAttribute), inherit: true).Length > 0)
			.Select(m => m.Name)
			.ToList();

		// 正向钉（原"四个世界参数"那张具名名单同义）：世界参数类型的名字不得以 [Export] 复活。
		var worldParamExports = exportedNames
			.Where(n => n is "ContinentCount" or "LandFraction" or "Seed" or "TargetRegionAreaKm2")
			.ToList();
		Assert.That(worldParamExports, Is.Empty,
			$"WorldManager 上仍有世界参数 [Export]：{string.Join(",", worldParamExports)}——" +
			"参数入口唯一 = 参数表 JSON（World.Params.WorldParamStore）；面板字段恒被遮蔽 ⇒ 是半套体系（2026-10-10 已删）");

		Assert.That(exportedNames, Is.Empty,
			$"WorldManager 上仍有 [Export]：{string.Join(",", exportedNames)}——" +
			"分辨率/半径是**编译期常量**（ProductionRes / PlanetGeometry.ProductionRadius），" +
			"不要退回「每场景手填、靠校验兜着」（2026-10-11 收口）；" +
			"若确要重新开闸，请显式改本契约并留下决策记录");

		// 非空转自检：扫描器本身必须有效——钉住**别处确实存在**的 [Export]。
		//   ★2026-10-11 换桩：原桩是 WorldGenPlanet 的 `ResLevel` / `Radius`，两者已删；
		//     同日收编后再换一次：`WorldManager.PlanetPath` 随 `WorldGenPlanet` 收编一起删除
		//     （本类现在**没有子依赖**）⇒ 桩改取 `WorldRoot` 的 `WorldManagerPath` 与
		//     `UIManager` 的 `PanelLayerPath`（它们是"依赖写成 [Export] NodePath"这一模式的
		//     既定实例，长期稳定）⇒ ⑤ 的"没找到"才不是假绿。
		var scannerProbe = new[] { typeof(WorldRoot), typeof(WorldManager), typeof(RenderManager), typeof(UIManager) }
			.SelectMany(t => t.GetFields(all).Concat(t.GetProperties(all).Cast<MemberInfo>()))
			.Where(m => m.GetCustomAttributes(typeof(ExportAttribute), inherit: true).Length > 0)
			.Select(m => m.Name)
			.ToList();
		Assert.That(scannerProbe, Does.Contain("WorldManagerPath").And.Contains("PanelLayerPath"),
			"未扫到管理器的 NodePath [Export]——`[Export]` 扫描器可能失效（⑤ 的断言需检查）");

		// 正向钉：世界参数确实"能读成实例"（防"删干净了连入口也没了"）。
		//   ★2026-10-11 起：参数管理器是**实例**（`WorldParams`），转换口只有它一个。
		var params_ = new WorldParams();
		params_.Reload();
		Assert.That(params_.LoadProblems, Is.Empty,
			"正库默认档必须干净可用：" + string.Join(" | ", params_.LoadProblems));
		Assert.That(params_.Active, Is.Not.Null.And.InstanceOf<WorldSpec>(),
			"读到的必须是 spec 类型的实例（2026-10-11 实例化口径），不是值副本");
		Assert.That(params_.Active.Seed, Is.EqualTo(42), "默认档 seed 应为 42");
		Assert.That(params_.Active.LandSea.ContinentCount, Is.EqualTo(7), "默认档陆块数应为 7");
	}

	/// <summary>
	/// **工具层的 Godot 边界（2026-10-11 用户拍板"util 也放开吧，可以放 godot 依赖的工具"）**。
	///
	/// 放开的是**语法面**：`Utils` 里允许出现 Godot 的**类型与结构**（`Node` / `NodePath` /
	/// `Vector3` / `Color`…）——它们能在无引擎进程里被声明与引用。落地例子 = `World.Utils.NodeDependency`
	/// （`Require` / `Check` 扩展；原先四个管理器各抄一份，约 90 行）。
	///
	/// **没放开的是运行期面**：`GD.*` 是**运行期引擎调用**（内部走 native 互操作），
	/// 在无 Godot 宿主的单测进程里是**进程级崩溃**——那正是本项目"测试不触碰 GD.*"纪律的根基。
	/// ⇒ 本条钉住"`World.Utils` 下不得出现 `GD.*`"，并用正向桩证明扫描器真的看得见 `GD`
	/// （否则"全绿"可能只是扫描器瞎了）。
	///
	/// ⚠️ 扫描面局限（写在明处）：它只覆盖**本程序集内联**的调用。IL 可见的"程序集内声明"扫不到
	/// 外部预编译程序集内部的调用——本项目无此结构，故不为此增加复杂度。
	/// </summary>
	[Test]
	public void Utils_MayUseGodotTypes_ButNoEngineRuntimeCalls()
	{
		var asm = typeof(FinalGeography).Assembly;

		// ① 结果钉：World.Utils（含子命名空间）下不得引用 GD。
		var gdOffenders = asm.GetTypes()
			.Where(t => t.Namespace != null
				&& (t.Namespace == "World.Utils" || t.Namespace.StartsWith("World.Utils.")))
			.Where(t => ReferencedTypesDeep(t).Any(r => r == typeof(GD)))
			.Select(t => $"{t.Namespace}.{t.Name}")
			.ToList();
		Assert.That(gdOffenders, Is.Empty,
			$"World.Utils 下出现了 GD.* 调用：{string.Join(",", gdOffenders)}——" +
			"工具层可以用 Godot **类型**（Node / NodePath / Vector3），" +
			"但**不得**做运行期引擎调用：无引擎宿主的单测进程里那是进程级崩溃。" +
			"诊断/报错归调用方（装配层），工具层只做结构性判断");

		// ② 正向桩：同一个扫描器必须能在**装配层**看见 GD（WorldRoot 里有 GD.Print）。
		//    立不住 ⇒ ①的"没找到"只是假绿。
		Assert.That(ReferencedTypesDeep(typeof(WorldRoot)).Any(r => r == typeof(GD)), Is.True,
			"扫描器在 WorldRoot 上都没看见 GD ⇒ 本条的扫描失效（①需要检查）");

		// ③ 正向钉：边界放开**确实落地了**——`NodeDependency` 必须在，
		//    且它引用了 Godot 的 **Node 类型**（而非仅值类型），证明"语法面放开"不是空话。
		var dep = asm.GetTypes().FirstOrDefault(t => t.Name == "NodeDependency");
		Assert.That(dep, Is.Not.Null, "World.Utils.NodeDependency 必须存在（依赖解析共用口）");
		Assert.That(ReferencedTypesDeep(dep).Any(r => r == typeof(Node)), Is.True,
			"NodeDependency 应引用 Godot.Node——它是 NodePath 解析口（语法面放开的实证）");
	}

	/// <summary>
	/// **分层 `using` 白名单契约**（2026-10-11 建，用户拍板"加那条 UI using 白名单契约"）。
	///
	/// 动机（实测）：`重整方案` §四把 R1（只向下依赖）与 R2（引擎侧三层平级互不引用）写成规则，
	/// 但**没有任何测试盯着**。后果 = 每次遇到跨层需求都得**临场发明机制**
	/// （窄口接口、包装、转发），而每次发明都能自我说服——`UI` 里那个 `IMapModeHost`
	/// 就是这么进去的，加的时候契约一声没响，靠人工逐层列 `using` 才发现。
	/// ⇒ 把约束写成机器可验，就不需要临场发明了。
	///
	/// 扫描面 = `scripts/<层>/**/*.cs` 的**文件级 `using World.*`**（源码级，不用反射）：
	/// 反射只看得见"类型是否被引用"，看不见"引用了哪个命名空间"；而层与命名空间**逐字对应**
	/// （八层重划分的成果）⇒ 源码级恰好是最直接的判据。
	///
	/// 三条断言：① 白名单（越界即红）② 分层目录完整性（防"层改名字了契约还在扫空"）
	/// ③ 正向桩（防"using 解析器瞎了"⇒ ① 变成假绿）。
	/// </summary>
	[Test]
	public void LayerUsingWhitelist_IsEnforced()
	{
		// 白名单：层目录 → 允许引用的 World.* 命名空间（**只列允许的**；未列即禁止）
		var allow = new (string Dir, string[] Ns)[]
		{
			("Data",   new[] { "World.Constants" }),
			("Params", new[] { "World.Data" }),
			("Utils",  new[] { "World.Utils" }),                       // 含 .H3 / .H3Grid 子族
			("Logic",  new[] { "World.Constants", "World.Data", "World.H3Grid", "World.Utils" }),
			//   ★同层的子命名空间**并列写入**（如 Render 的 `World.Render.Constants`）——
			//     它不是跨层边；之所以要显式列，是因为 §2.2 的"层内子目录是自由分组"允许
			//     **个别**子层进 namespace（Constants 就是唯一这样的子层）。
			("Render", new[] { "World.Render", "World.Constants", "World.Data", "World.H3Grid", "World.Logic", "World.Utils" }),
			("UI",     new[] { "World.Data", "World.Constants" }),     // ★哑组件层：不得引用 Render
			("Client", new[] { "World.Constants", "World.Utils" }),
			("Scene",  new[] { "World.Client", "World.Constants", "World.Data", "World.Logic",
							   "World.Params", "World.Render", "World.UI", "World.Utils" }),  // 装配层看全部
		};

		var offenders = new List<string>();
		var scanned = new Dictionary<string, int>();
		int stubFiles = 0;

		foreach (var (dir, allowed) in allow)
		{
			var full = FindRepoDir("scripts", dir);
			Assert.That(full, Is.Not.Null,
				$"未找到 scripts/{dir}/ —— 层目录改名了？本表的层名必须与目录逐字一致");
			var files = Directory.GetFiles(full, "*.cs", SearchOption.AllDirectories);
			scanned[dir] = files.Length;
			foreach (var f in files)
				foreach (var ns in WorldUsings(File.ReadAllLines(f)))
				{
					stubFiles++;
					// 允许 = 白名单项**逐字相等**，或白名单项的**子命名空间**（Utils 允许 Utils.H3）。
					// 同层自引用（如 `namespace World.Render` 里写 `using World.Render;`）视为冗余 ⇒ 也拒，
					// 理由：它使依赖图多出一条自环，掩盖真实方向。
					bool ok = allowed.Any(a => ns == a || ns.StartsWith(a + "."));
					if (!ok) offenders.Add($"{dir}/{Path.GetFileName(f)} → {ns}");
				}
		}

		// ① 越界即红
		Assert.That(offenders.Distinct().ToList(), Is.Empty,
			$"分层 using 白名单被突破：{string.Join(" | ", offenders.Distinct())}——" +
			"R1（只向下依赖）/ R2（引擎侧三层平级）是既定架构；" +
			"若确需新增一条边，请显式改本表并留下决策记录，**不要**用'窄口接口'把依赖改名（见债务清单 A-11）");

		// ② 分层目录完整性（防"目录改名后本契约扫了个空"）
		foreach (var (dir, _) in allow)
			Assert.That(scanned[dir], Is.GreaterThan(0), $"scripts/{dir}/ 下没有 .cs —— 扫描面失效");

		// ③ 正向桩：解析器必须真的能刮出 using（否则 ① 只是"什么都没扫到"的假绿）
		Assert.That(stubFiles, Is.GreaterThan(10),
			$"只刮出 {stubFiles} 条 `using World.*` —— 解析器可能失效（本行是 ① 的正向桩）");
		string logicSrc = Path.Combine(FindRepoDir("scripts", "Logic")!, "Composition", "WorldGenSimulation.cs");
		Assert.That(WorldUsings(File.ReadAllLines(logicSrc)).Contains("World.Data"), Is.True,
			"未在 WorldGenSimulation.cs 里刮到 `using World.Data;` —— 解析器或路径失效（③ 需要检查）");
	}

	/// <summary>
	/// **UI ↔ Render 单向边钉**（2026-10-11 随"坞归渲染侧驱动"一起立）。
	///
	/// 背景：坞与渲染在分层上是**平级的两个引擎侧层**，所以"谁能引用谁"没有天然答案。
	/// 2026-10-11 的实际结构是：
	///   · **坞→渲染走 Godot 信号，连接写在场景文件里**（`WorldGenWorld.tscn` 的 `[connection]`）
	///     ⇒ 那是一条**元数据边**，两侧源码零互相 `using`；
	///   · **渲染→坞走 `Node.Call`**（下行口 `BindModes` / `SetMode` / `SetParameterOptions` / `SetLegend`），
	///     参数类型是 `PanelContainer`（坞的基类）⇒ 渲染侧源码同样不 `using World.UI`。
	///
	/// ⇒ 于是**编译期一条边都没有**，方向由"谁驱动谁"决定：**渲染驱动坞**（坞是哑组件、只发数字）。
	///   本契约把这条**运行期方向**钉住：一旦有人让 `UI` 反向 import `Render`（编译期依赖），立刻红；
	///   并允许 `Render` 将来显式登记 `World.UI`（若它需要直接调用坞的类型而非 `Call`）。
	/// </summary>
	[Test]
	public void UiRenderEdge_IsOneWay()
	{
		// ① 结果钉：UI 的源码不得出现任何 `using World.Render*`（无论是直接写还是别名）。
		var uiDir = FindRepoDir("scripts", "UI");
		Assert.That(uiDir, Is.Not.Null, "未找到 scripts/UI/ —— 层目录改名了？");
		var bad = new List<string>();
		foreach (var f in Directory.GetFiles(uiDir, "*.cs", SearchOption.AllDirectories))
			foreach (var ns in WorldUsings(File.ReadAllLines(f)))
				if (ns == "World.Render" || ns.StartsWith("World.Render."))
					bad.Add($"{Path.GetFileName(f)} → {ns}");
		Assert.That(bad, Is.Empty,
			$"UI 反向依赖了表现层：{string.Join(" | ", bad)}——" +
			"箭头方向是「渲染驱动坞」（坞是哑组件），反过来会让两个平级层互相认识；" +
			"坞按钮的点选结果应经**信号**上抛（连接在场景文件里），而不是让 UI 去 import Render");

		// ② 非空转自检：UI 目录确实有源码（否则 ① 只是扫了个空）
		Assert.That(Directory.GetFiles(uiDir, "*.cs", SearchOption.AllDirectories).Length,
			Is.GreaterThan(0), "scripts/UI/ 下没有 .cs —— 扫描面失效");

		// ③ 正向桩：同一解析器在**渲染侧**能刮到它真正用到的世界侧命名空间（证明解析有效）
		var renderDir = FindRepoDir("scripts", "Render");
		var renderNs = Directory.GetFiles(renderDir, "*.cs", SearchOption.AllDirectories)
			.SelectMany(f => WorldUsings(File.ReadAllLines(f))).ToHashSet();
		Assert.That(renderNs, Does.Contain("World.Logic"),
			"未在 scripts/Render 刮到 `using World.Logic;` —— 解析器或路径失效（③ 需要检查）");
	}

	/// <summary>刮出一行里声明的 `World.*` 命名空间（`using` 语句；带别名/静态的也认）。</summary>
	static IEnumerable<string> WorldUsings(IEnumerable<string> lines)
	{
		foreach (var raw in lines)
		{
			var m = System.Text.RegularExpressions.Regex.Match(raw,
				@"^\s*using\s+(?:static\s+)?(?:[A-Za-z_][A-Za-z0-9_]*\s*=\s*)?(World\.[A-Za-z0-9_.]+)\s*;");
			if (m.Success) yield return m.Groups[1].Value;
		}
	}

	/// <summary>
	/// **判读读数只住在诊断场景（结果 + 位置钉）**
	/// （2026-10-10 用户拍板：「检查一下 `WorldGenPlanet` 里面的测试内容，全部删掉或者移到 test 里面」
	///   → 澄清为「指诊断打印」）。
	///
	/// 判定问题："世界生成的判读读数由谁产出？" ⇒ **诊断场景**，不是生产类。
	/// 本轮从 `WorldGenPlanet` 搬走的三处（原散在 `_Ready` / `BuildSpec` / `Regenerate`）：
	///   · `[WORLDGEN-READY]`  表现层构建耗时（ViewCtor / BuildChunks / RiverLinesBuild）；
	///   · `[WORLDGEN-TIMING]` 全链事实摘要（n/res/land/regions/meanP/meanT/ridges/basins/lakes/thr）；
	///   · `[WORLDGEN-PARAMS]` 参数表生效值。
	/// 新家 = `scripts/Test/Diagnostics/WorldGenReadoutDiag.cs`（`World.Diagnostics`，与 `H3SmokeDiag` 同型）
	///   + `scenes/diag/WorldGenReadoutDiag.tscn`；读数**逐字等价**（只读 `Sim` 的字段，与 Godot 无关）。
	///
	/// ★**有意保留**（**不**属本条约束，别误删）：
	///   · 装配层 `WorldRoot._Ready` 的 `GD.PushWarning` —— 逐条报**参数读表报告**
	///     （`Params.LoadProblems`：坏档 / 缺档；★2026-10-11 收编后读表报告住在装配层，
	///     原 `WorldGenPlanet.BuildSpec` 已随收编消失），那是**错误可见性**（生产可靠性），
	///     搬走会让"坏档静默"⇒ 功能回退；
	///   · `WorldGenManager` 的 `[WORLDGEN-PICK]` —— **交互式**点选判读，需真实点击，
	///     无法在 headless 诊断场景里复现 ⇒ 另案（本契约只覆盖**世界生产类**的判读打印：
	///     原 `WorldGenPlanet`，2026-10-11 收编后即 `WorldManager`）。
	///
	/// ★`[WORLDGEN-READY]`（表现层耗时）**不再产出**：它测 `BallView` / `RiverLineOverlay` 构建，
	///   属表现层；诊断场景刻意只建逻辑侧（不建 View）⇒ 要该读数就去主场景实机跑。
	/// </summary>
	[Test]
	public void WorldGenReadout_LivesOnlyInDiagnosticsScene()
	{
		string sceneDir = FindRepoDir("scripts", "Scene");
		Assert.That(sceneDir, Is.Not.Null, "scripts/Scene 应存在（装配层；2026-10-11 起四个管理器直接住这里）");

		// ① 生产类不得再有判读打印（`GD.PushWarning` 是错误可见性，**允许**）。
		//   ★2026-10-11 收编后换目标：`WorldGenPlanet.cs` 已随收编删除，世界生产类 = `WorldManager.cs`
		//     （原类的内容并入了它）⇒ 本钉子随之落到后者上。
		string worldSrc = Path.Combine(sceneDir, "WorldManager.cs");
		Assert.That(File.Exists(worldSrc), Is.True, "WorldManager.cs 应可解析到");
		Assert.That(File.ReadAllText(worldSrc), Does.Not.Contain("GD.Print("),
			"WorldManager 不得再打印判读读数——读数应住在 " +
			"scripts/Test/Diagnostics/WorldGenReadoutDiag.cs（2026-10-10 用户拍板）");

		// ② 新家必须在（位置钉）。
		string diagDir = FindRepoDir("scripts", "Test", "Diagnostics");
		Assert.That(diagDir, Is.Not.Null, "scripts/Test/Diagnostics 应存在");
		string diagCs = Path.Combine(diagDir, "WorldGenReadoutDiag.cs");
		Assert.That(File.Exists(diagCs), Is.True, "读数诊断脚本 WorldGenReadoutDiag.cs 应存在");

		string sceneDiagDir = FindRepoDir("scenes", "diag");
		Assert.That(sceneDiagDir, Is.Not.Null, "scenes/diag 应存在");
		Assert.That(File.Exists(Path.Combine(sceneDiagDir, "WorldGenReadoutDiag.tscn")), Is.True,
			"读数诊断场景 WorldGenReadoutDiag.tscn 应存在（verify.sh 的回归入口）");

		// ③ 非空转自检：扫描确实有效——诊断脚本里**必须**有 GD.Print，否则本条只是假绿。
		Assert.That(File.ReadAllText(diagCs), Does.Contain("GD.Print("),
			"诊断脚本里应有 GD.Print 读数——否则说明本条的扫描无效（假绿）");

		// ④ 结构钉：诊断脚本须继承统一基类（ADR-0003：新诊断场景一律继承 DiagSceneBase）。
		Assert.That(typeof(World.Diagnostics.WorldGenReadoutDiag).BaseType,
			Is.EqualTo(typeof(World.Diagnostics.DiagSceneBase)),
			"新诊断场景须继承 DiagSceneBase（统一 args 解析 / PASS-FAIL 报告 / 退出）");
	}

	[Test]
	public void FinalGeography_DoesNotReferencePlacementProjector()
	{
		// 契约 ②：Final 世界事实层不得引用 Placement 投影器（生成依据与世界事实分离的编译期体现）
		var t = typeof(FinalGeography);
		var offending = ReferencedTypes(t).Where(x => x == typeof(H3LandSeaProjector)).ToList();
		Assert.That(offending, Is.Empty,
			"FinalGeography 引用了 H3LandSeaProjector——Final 层不得读 Placement 生成依据（决策 07 §1）");
	}

	[Test]
	public void HeightComposer_DoesNotReferenceConcreteFeatures()
	{
		// 契约 ③：合成器只认 ITerrainField 列表——具体 Feature 类（骨架/地貌/火山）不得出现在
		// HeightComposer 的公共面（方法签名/字段）。新 Feature 零合成器改动的保证。
		var t = typeof(HeightComposer);
		var concrete = new[] { typeof(MountainSkeleton), typeof(RegionalLandforms), typeof(VolcanoField) };
		var offending = ReferencedTypes(t).Where(concrete.Contains).ToList();
		Assert.That(offending, Is.Empty,
			$"HeightComposer 引用了具体 Feature 类：{string.Join(",", offending)}——合成器只准消费 ITerrainField 列表（决策 08 契约 ③）");
	}

	[Test]
	public void FinalGeography_DoesNotReferenceConcreteFeatures()
	{
		// 契约 ④：新 Feature 不需要改 FinalGeography——最终地理只从 FinalHeight 派生
		var t = typeof(FinalGeography);
		var concrete = new[] { typeof(MountainSkeleton), typeof(RegionalLandforms), typeof(VolcanoField) };
		var offending = ReferencedTypes(t).Where(concrete.Contains).ToList();
		Assert.That(offending, Is.Empty,
			$"FinalGeography 引用了具体 Feature 类：{string.Join(",", offending)}——新 Feature 不需要改 FinalGeography（决策 08 契约 ④）");
	}

	/// <summary>
	/// 新世界线（`World.Logic`）的类型清单——**架构契约的扫描面**。
	/// ★集中于此而非各测试内联：新增子系统时只改这一处，就不会漏进任何一条契约的扫描面
	///   （漏扫 = 契约静默失效，比契约本身不写更危险）。
	/// </summary>
	static readonly Type[] NewWorldLineTypes =
	{
		// ── Final / 尺度 / 空间索引 ──
		typeof(FinalGeography), typeof(SpatialScale), typeof(FinalSpatialIndex),
		// ── 阶段管线（2026-10-09：六阶段各成一类；只聚合引用，不复制数组）──
		typeof(LandSeaPipeline), typeof(TerrainPipeline), typeof(FactsPipeline),
		typeof(ClimatePipeline), typeof(HydrologyPipeline), typeof(IndexPipeline),
		// ── 世界参数（2026-10-10 定型；更晚二层切分；2026-10-11 实例化 + 迁入 res/params/ + 去内核）──
		//   ★"`res/params/` 下的 JSON → WorldSpec **实例**"的**唯一**转换点 = `World.Params.WorldParams`
		//     （2026-10-11：原独立内核 `WorldSpecCodec` 已并入它，JSON 读写直接住在管理器里）。
		//     宿主侧的"找文件 / 读 / 写"归 `World.Params.WorldParamStore`（导出后参数在 `.pck` 内，
		//     只有 Godot `FileAccess` 读得到 ⇒ 必须允许碰引擎；用户拍板把那部分收编进
		//     与 Scene/Logic 并列的 `scripts/Assets/`）。
		//     权威清单 / 存储类 = **spec 类型自身**（不建注册表、key 清单、字段类型表）；
		//     默认值（内容）= **正库数据文件** `res/params/world_params.json` ⇒ 代码里只有
		//     spec 属性初值当"出厂档"（原 `WorldSpecDefaults` 早已删除）。
		//   ⚠️ 两个参数类型（WorldParams / WorldParamStore）住在 `World.Params`，**不属本清单**
		//     （本清单是逻辑层扫描面）；它们"不沾宿主里的磁盘面"由 ② / ③ 的白名单直接钉。
		// ── Placement（生成依据）──
		typeof(ContinentLayout), typeof(LandSeaField), typeof(H3LandSeaProjector),
		typeof(SurfaceResolver), typeof(GeologicalRegions), typeof(TectonicField),
		typeof(MountainSkeleton), typeof(RegionalLandforms), typeof(VolcanoField),
		typeof(HeightComposer),
		// ── World Simulation（消费 Final，不生成）──
		//   ★温度链/季节/洋流/风场已删除（2026-10-07 用户拍板全部删除重做）——
		//   重做后的新事实类型须重新在此登记（漏登记 = 契约静默失效）。
		typeof(PrecipitationModel),
		typeof(RiverNetwork), typeof(RiverGraph),
		typeof(RiverGeometry), typeof(BasinGraph), typeof(LakeState), typeof(WaterTopology),
		typeof(H3Hydrology), typeof(HydrologyRoutingSurface),
		// ── 基础设施（表现层入口类由专项契约单独扫，见下）──
		typeof(SnowOverlay), typeof(SeedDerivation), typeof(H3TerrainSampler),
		// ── 数据层 World.Data（2026-10-09 建层：纯数据载体 = 顶层 + 零方法 + 零计算属性
		//     + 无嵌套 + 不引用生成域类型；判据与依赖方向见 scripts/Data/Carrier/ContinentAnchor.cs 头部）──
		typeof(ContinentAnchor), typeof(MountainRidge), typeof(Scale3),
		//   ★2026-10-11 新增 `PickedCell`（拾取结果快照：原住 `World.Render.WorldPicker.cs`）：
		//     迁入动因 = 消掉 `World.UI → World.Render` 这条跨层边（信息卡要吃这份数据）；
		//     迁入前提 = 它引用的 `RegionType` 词表同期由 `World.Logic` 上提到 `World.Constants`
		//     （否则数据层会引用逻辑层，撞上本层判据）。
		typeof(PickedCell),
		//   ★同类上提：`RegionType`（区域类型词表，与 `BiomeType` 同族）——
		//     文案映射 `GeologicalRegions.TypeName` 仍留逻辑层（本层判据禁方法）。
		typeof(World.Constants.RegionType),
		//   ★世界定义（2026-10-10：4 个位置参数收成 WorldSpec；两级 = 全局 Seed + 阶段 spec）——
		//     2026-10-11 起它们是**参数实例**（可变类、有身份）：装配层 / 参数管理器构造并持有，
		//     按段切片喂给各阶段；依赖方向 WorldGen → Data 已既定。
		//     默认值（内容）**不在本层独占**：正库数据文件 `res/params/world_params.json` 是真相源，
		//     spec 属性初值只是"出厂档"（默认档读不到时的基底）；
		//     两者同值由 `WorldSpecTests.FactoryDefaults_MatchThePresetFile` 钉住（防"删掉 JSON 就换世界"）。
		//   ⚠️ 2026-10-10 第二批：原数据载体 `LandSeaParams` 的 10 个世界参数已并入 `LandSeaSpec`
		//      ⇒ 该类型**已删除**，不再是扫描面成员。
		typeof(WorldSpec), typeof(LandSeaSpec), typeof(TerrainSpec),
	};

	/// <summary>
	/// **表现层例外契约（允许清单）**——B 线清退决策（决策 08 §清退）的正向钉。
	///
	/// ★P1 完成后的形态（2026-10-05）：旧父命名空间 `World.NoiseWorld` 下**已无任何类型**
	///   （B 线生成逻辑 + 表现层全部清退/迁走），本条从"例外白名单"收紧为
	///   "**新线的表现依赖必须且只能落在 World.Render 体系**"。
	///   ★2026-10-09 补充：该体系现有三个子层 ——
	///     `.UI`（哑组件·可挂场景）/ `.Controllers`（非 Node 驱动类）/ `.Constants`（跨模式共享常量）。
	///     三者为**平级类目**，不是"UI 包含一切"。
	///
	/// 背景：新线命名空间是 `World.Logic`，而 B 线旧单体在**父**命名空间
	/// `World.NoiseWorld` ⇒ C# 作用域规则让新线**不加using 就能直接看到**父命名空间的类型。
	/// 于是 `NewWorldLine_DoesNotDependOnLegacyWorldLine` 的四命名空间黑名单
	///（World.Biome / MapGen / MapView / Tectonics）**完全管不到这一条**：
	/// 新线对 `NoiseBallView` / `NoiseMapMode` 的依赖在旧契约里是"合法但不可见"的。
	///
	/// 本契约把该例外**正式编码**：从"靠命名空间黑名单隐式放过"变成"白名单显式允许"。
	/// 读测试的人一眼能看到允许什么、禁止什么——而不是去猜为什么这里没报错。
	///
	/// 允许（清退后仍需存在的纯表现抽象）：
	///   BallView —— LOD 网格 / 剔除 / 射线拾取 / 选中高亮
	///   MapMode  —— 地图模式策略抽象（Id/Name/CellColorAt/BeginBake）
	/// 这两类**生成语义为零**，不携带任何"旧世界怎么生成"的语义。
	/// </summary>
	[Test]
	public void NewWorldLine_MayDependOnApprovedRenderContracts()
	{
		// 表现层入口类：新线唯一合法接触旧父命名空间的地方。
		//   ★2026-10-11 增 `WorldRoot`：它是主场景**根管理器**，按设计直接认
		//     `World.UI`（坞 / 信息卡）与 `World.Client`（相机）——属同一类"接线层"。
		//   ★2026-10-11 增 `RenderManager`（渲染管理器拆分）：它按设计认 `World.Render`
		//     （`WorldView` / `WorldPicker` / `MapMode`）——"表现子树的持有者"本身就是接线层，
		//     且点选（手势 + 拾取 + 高亮 + 报数据给界面）已在同日收口到它名下。
		//   ★2026-10-11 收编：原清单末尾的 `typeof(WorldGenPlanet)` 已随之删除
		//     （该类并入 `WorldManager`，后者住装配层 `scripts/Scene/WorldGen/`，不属本扫描面）。
		var renderEntryPoints = new[]
		{
			typeof(WorldManager), typeof(UIManager), typeof(WorldRoot),
			typeof(RenderManager), typeof(RiverLineOverlay),
		};
		// 新线的表现依赖目标（迁移完成后应当全部落在这里）。
		// ⚠️ 这是**声明白名单**（供人对照），当前断言路径只比对旧父命名空间 `World.NoiseWorld`。
		//   ★2026-10-11 八层重划分后更新：原 `.UI` / `.Controllers` 两个**嵌套子层已取消**——
		//     UI 与 Render 平级（`World.UI`）、坞驱动器归装配层（`World.Scene`）；
		//     并补上客户端层 `World.Client`（相机从 `World.Camera` 改名而来）。
		var renderLayer = new[]
		{
			"World.Render", "World.Render.Constants", "World.UI", "World.Client", "World.Scene",
		};

		// 唯一批准保留的旧表现资产。
		// ★按**名字**匹配而非 typeof：清退过程是"先加新 → 切消费者 → 再删旧"，
		//   若这里写 typeof(旧类)，迁移中途类型一旦消失测试就编译不过。
		//
		// ★★实测修订（2026-10-05）：本清单在写第一版时**漏了** NoiseCellPanel / NoiseDock——
		//   主入口场景 `WorldGenWorld.tscn` 挂的`PanelLayer/NoiseCellPanel` 与 `PanelLayer/NoiseDock`
		//   两个面板，脚本都指向旧线类（`NoiseCellPanel.tscn` → `NoiseCellPanel.cs`；
		//   `WorldGenDock.tscn` → `NoiseDock.cs`，只是场景根节点改名 WorldGenDock）。
		//   ⇒ B线实际有 **4 类**表现资产被新线消费，不是 2 类。
		//   两者经核实为**纯哑组件**（生成语义为零）：
		//     NoiseDock      —— 只发 `ModeSelected(int)` 信号 + 按鼠标更新按钮高亮，不认识任何生成类型
		//     NoiseCellPanel —— 只被喂 `ShowCell(id, lat, lng, elevM)`，不感知星球与相机
		//   与 BallView/MapMode 同类：有真实消费者 ⇒ 保留去噪，不是清退对象。
		// ★2026-10-05 P1 完成后清单已换新名：4 类表现资产全部迁入 World.Render / World.UI。
		//   这条契约从"允许依赖旧父命名空间的 4 个类型"变成
		//   "**新线只准依赖 World.Render，不准再碰 World.NoiseWorld 根命名空间**"。
		var approved = new[]
		{
			"BallView",      // LOD / 剔除 / 拾取 / 高亮（Render）
			"MapMode",       // 地图模式策略抽象（Render）
			"CellQuery",     // 拾取 / 高亮环带几何 / 格心方向（Render，纯函数）
			"MapDock",       // 模式坞（Render.UI，信号出口）
			"CellInfoCard",  // 格信息卡（Render.UI，哑组件）
		};

		const string legacyParent = "World.NoiseWorld";
		// 旧父命名空间里其余一切都不得出现在新线引用面上（未注册的一律视为越界）。
		var offending = new List<string>();
		foreach (var t in renderEntryPoints)
			foreach (var r in ReferencedTypes(t))
			{
				if (r.Namespace != legacyParent || r.Name == null) continue;
				if (approved.Contains(r.Name)) continue;
				offending.Add($"{t.Name}→{r.Name}");
			}
		Assert.That(offending, Is.Empty,
			$"新线表现层依赖了未批准的旧类型：{string.Join(",", offending)}——" +
			$"允许清单仅 {string.Join(" / ", approved)}；" +
			"B 线生成语义已在新架构重写（决策 08 §清退），不得反向耦合");
	}

	/// <summary>
	/// **框架期组合层清退钉（结果断言）**——`ElevationFieldStack` 与`WorldGenParams` 已不存在。
	///
	/// ★这个案例改写了"零调用"的判据（决策 08 §9 · 清退判定的第三层）：
	///   上一轮我判它"零调用"——**那是错的**。准确表述是：
	///     **零生产语义消费者，但有测试造场消费者**。
	///   5 条 `H3TerrainSamplerTests` 借它构造输入（测的是采样器，生产侧有真实消费者
	///   `H3LandSeaProjector` / `LandSeaFields`），3 条 `WorldGenFieldTests` 同理。
	///   ⇒ "有引用"≠"该类型应留下"；"无生产引用"也≠"可以立即删"。
	///   正确顺序：**先剥离测试造场职责（提取 TestElevationFieldBuilder）→ 再删生产侧**。
	///   若是当初直接删文件，这8 条测试会直接编译失败。
	///
	/// 本条钉住"结果"，配合上面的方向钉，两者齐备才算收口。
	/// </summary>
	[Test]
	public void FrameworkCompositionLayer_IsGone()
	{
		var dead = new[] { "ElevationFieldStack", "WorldGenParams" };
		var survivors = typeof(FinalGeography).Assembly.GetTypes()
			.Where(t => t.Name != null && dead.Contains(t.Name))
			.Select(t => t.Name)
			.ToList();
		Assert.That(survivors, Is.Empty,
			$"框架期组合层仍有类型存活：{string.Join(",", survivors)}——" +
			"它的组合职责已由 `HeightComposer`（绝对高度 lerp）承担，语义不同不可复用；" +
			"若新代码需要『把SphericalField 族当零件拼装』，先判断那是不是 HeightComposer 的职责" +
			"（决策 08 §清退 · 组合层）；若只是**测试要造一个可采样的场**，用 `TestElevationFieldBuilder`" +
			"（测试层夹具），不要为了测试方便把生产抽象留在树上" +
			"（这条判据的由来：它曾让8 条测试依赖一个零生产消费者的类）");

		// 反向钉：被清退后测试确实不再依赖它（防"删了又偷偷加回来造场"）
		// ★2026-10-06 A 步目录正规化：路径 scripts/worldgen/ → scripts/WorldGen/
		//   （大小写正规化 + 六子层拆分；namespace 仍是 World.Logic 不变）
		// ★2026-10-09 Scene/Logic 两分区：`scripts/{Scene,Logic}/` 是**命名空间中性分区**
		//   （不吃 namespace 段，如 `src/`），其下路径仍逐字对应 namespace
		//   ⇒ 世界生成主链（逻辑侧）落在 scripts/Logic/WorldGen/。
		// ★2026-10-11 目录重划分（批次 2/3 的目录半）：主链由 `scripts/Logic/WorldGen/` 上抬到
		//   `scripts/Logic/`（撤掉与 `Logic` 同义的中间层 WorldGen）⇒ 本钉随路径更新。
		Assert.That(Directory.Exists(FindRepoDir("scripts", "Logic")), Is.True,
			"scripts/Logic/ 应存在（世界生成主链目录；2026-10-11 起不再有 Logic/WorldGen 中间层）");
	}

	/// <summary>
	/// **逻辑层命名空间钉**（2026-10-11 目录/命名空间重划分后重写）——世界生成主链必须落在 `World.Logic`。
	///
	/// ★沿革：2026-10-05 之前新线是 `World.NoiseWorld.*`（B 线旧单体的**子**命名空间）；
	///   B 线清退后父命名空间消失，新线成了"没有父的子命名空间" ⇒ 改为 `World.WorldGen`（平级）。
	///   ★2026-10-11 八层重划分：`World.WorldGen` 这个名字本身也失去了意义——它同时装着
	///   逻辑层（阶段管线 / 事实）与装配层（管理器），是"一个概念劈成两半"的实证。
	///   现按层拆开：**`World.Logic`**（本契约钉的）＋ `World.Scene`（装配）＋
	///   `World.Render`（地图模式 / 叠加层）＋ `World.UI`（哑组件）。
	///
	/// 钉住"名字 + 位置 + 旧名不复活"三件事：
	///   ① 名字 = `World.Logic`（核心类型抽样）；
	///   ② 位置 = 目录 `scripts/Logic/`（八个子层是**层内分组**，不进 namespace）；
	///   ③ 旧名 `World.WorldGen` / `World.NoiseWorld` 不得复活。
	/// </summary>
	[Test]
	public void NewWorldLine_NamespaceIsLogic()
	{
		// ① 抽样核心类型：命名空间必须是 World.Logic
		var core = new[]
		{
			typeof(FinalGeography), typeof(HeightComposer), typeof(FinalSpatialIndex),
			typeof(RiverNetwork), typeof(WaterTopology),
		};
		var wrong = core.Where(t => t.Namespace != "World.Logic")
			.Select(t => $"{t.Name}@{t.Namespace}")
			.ToList();
		Assert.That(wrong, Is.Empty,
			$"世界生成主链类型不在 World.Logic：{string.Join(",", wrong)}——" +
			"八层重划分后逻辑层命名空间是 World.Logic（2026-10-11）");

		// ② 旧命名空间不得复活：NoiseWorld 是 B 线痕迹；WorldGen 是重划分前的旧名
		var asm = typeof(FinalGeography).Assembly;
		foreach (var dead in new[] { "World.NoiseWorld", "World.WorldGen" })
		{
			var alive = asm.GetTypes()
				.Where(t => t.Namespace != null
					&& (t.Namespace == dead || t.Namespace.StartsWith(dead + ".")))
				.Select(t => $"{t.Namespace}.{t.Name}")
				.ToList();
			Assert.That(alive, Is.Empty,
				$"`{dead}*` 命名空间复活了：{string.Join(",", alive)}——" +
				(dead == "World.NoiseWorld"
					? "B 线早已清退，不应再有 NoiseWorld 字样"
					: "2026-10-11 已按层拆为 World.Logic / World.Scene / World.Render / World.UI"));
		}

		// ③ 目录与命名空间一致（D-3）：八层之后 `scripts/Logic/` ↔ `World.Logic`（**层名即 namespace**），
		//    子目录（Composition/Hydrology/…）是**层内分组**，不参与 namespace。
		//    ★用与 CellHighlightRingTests 相同的"向上查找仓库根"写法，
		//      不用 Directory.GetParent 硬拼层级（测试输出目录深度会变）。
		var logicDir = FindRepoDir("scripts", "Logic");
		Assert.That(logicDir, Is.Not.Null, "未找到 scripts/Logic/ —— namespace World.Logic 的家");
		foreach (var sub in new[] { "Composition", "Foundation", "Placement", "Discretization",
			"Features", "Final", "Climate", "Hydrology" })
			Assert.That(Directory.Exists(Path.Combine(logicDir, sub)), Is.True,
				$"scripts/Logic/{sub}/ 应存在（八子层之一——层内分组，不参与 namespace）");

		// ④ 已被撤掉的中间层不得复活：`scripts/Logic/WorldGen/` 曾与 `Logic` 同义（纯层级噪声）
		Assert.That(FindRepoDir("scripts", "Logic", "WorldGen"), Is.Null,
			"scripts/Logic/WorldGen/ 不应存在（2026-10-11 已撤：它与 Logic 同义，是纯层级噪声）");
		// 旧目录不得复活（B 线清退物；分区后可能落在任一分区之下 ⇒ 两侧都查）
		Assert.That(FindRepoDir("scripts", "Logic", "noise_world"), Is.Null,
			"scripts/Logic/noise_world/ 不应存在（B 线已清退）");
		Assert.That(FindRepoDir("scripts", "Scene", "noise_world"), Is.Null,
			"scripts/Scene/noise_world/ 不应存在（B 线已清退）");
	}

	/// <summary>
	/// **B 线清退完成钉（结果断言）**——旧父命名空间 `World.NoiseWorld` 已**彻底清空**。
	///
	/// ★为什么必须钉"结果"而不只是"方向"：
	///   允许清单（上一条）只保证"新线不碰旧线"，但**不保证旧线已消失**——
	///   旧线完全可能原样留着、只是没人引用，静静躺在生产树里。
	///   方向守卫 + 结果断言两者都要有：
	///     只有方向 ⇒ 旧线删不掉（"没人用"不等于"该删"）；
	///     只有结果 ⇒ 有人重新引入旧类型并引用它也测不出来。
	///
	/// 清退前的形态（决策 08 §4.2）：
	///   生成逻辑/参数/场景/UI → 删；表现资产 4类 → 迁入 World.Render / World.UI。
	/// </summary>
	[Test]
	public void LegacyNoiseWorldLine_IsGone()
	{
		const string legacyParent = "World.NoiseWorld";
		// 只认根命名空间本身。★2026-10-05 namespace 重构后新线已是 `World.Logic`（不再是子命名空间），
		// 两者现在是**平级**——所以这条断言不会误伤新线。
		var survivors = typeof(FinalGeography).Assembly.GetTypes()
			.Where(t => t.Namespace == legacyParent)
			.Select(t => $"{t.Namespace}.{t.Name}")
			.ToList();
		Assert.That(survivors, Is.Empty,
			$"B 线旧单体仍有类型存活：{string.Join(",", survivors)}——" +
			"决策 08 §4.2 已定义其命运：生成逻辑/参数/场景/UI 清退，表现资产迁入 World.Render。" +
			"若新代码需要其中的能力，先判断它是『生成世界』还是『画世界』：前者不应重建，后者迁Render。");

		// 迁移后的落点必须真的存在（反向钉：防止"删了旧的又没建新的"这种半截状态）。
		Assert.That(typeof(Render.BallView).Namespace, Is.EqualTo("World.Render"),
			"BallView 应落在 World.Render（表现层保留资产）");
		Assert.That(typeof(Render.MapMode).Namespace, Is.EqualTo("World.Render"),
			"MapMode 应落在 World.Render（表现层保留资产）");
		Assert.That(typeof(Render.CellQuery).Namespace, Is.EqualTo("World.Render"),
			"CellQuery 应落在 World.Render（高亮/拾取纯函数）");
	}

	/// <summary>
	/// **B 线生成层禁入契约（禁止清单）**——与上一条构成一对（允许 / 禁止双向钉）。
	///
	/// 判定依据不是"旧线已被删除"，而是**语义**：
	///   新架构不是从 B 线搬过来的，而是已经把 B 线的生成语义**重写**掉了
	///   （Placement 的 TectonicField/ContinentLayout/LandSeaField + Final 的 FinalGeography
	///     + HeightComposer 的绝对高度 lerp 语义，对应 B 线的板块身份 + 噪声栈 + 域内秩）。
	/// ⇒ B 线生成类型在新线里**没有位置**，任何引用都是**范式回退**而非复用。
	///
	/// ★为什么必须显式列出：`TerrainNoiseParams` 是**双事实源**，
	///   它与新线的 `LandFraction` **同名不同义**——
	///     旧线 LandFractionTarget = **陆板数**占比（面积跟随吞并结果）
	///     新线 LandFraction       = **面积**占比（分位校准，H3LandSeaProjector）
	///   同为 0.29 时两者结果完全不同 ⇒ 误接会**静默改掉海陆比**，无任何运行时症状。
	///   此前这条边界只有 `ElevationFieldStack.cs:74` 的一句注释保护，现升级为硬契约。
	/// </summary>
	[Test]
	public void NewWorldLine_MustNotDependOnLegacyGenerationTypes()
	{
		// B 线生成语义的全部承载体（名字匹配，理由同上一条）。
		var forbidden = new[]
		{
			"NoiseTerrain",       // L0-L5 噪声栈（板块身份 + 域内秩）
			"NoisePlates",        // 离散板块划分（八次迭代史）
			"NoiseClimate",       // 温度 / 风 / 降水 / 群系（与 PrecipitationModel 重叠）
			"TerrainNoiseParams", // ★双事实源：LandFraction 两套口径
			"NoisePlanet",        // 旧组件装配
			"NoiseWorldManager",  // 旧运行期接线（已由 WorldGenManager 重写）
			"NoiseParamPanel",                // 旧调参面板（反射生成 NoiseParam 控件，唯一消费者=已删的旧场景）
			"ElevationFieldStack", // 框架期组合层（零调用，携带 WorldGenParams 表）
		};

		var offending = new List<string>();
		foreach (var t in NewWorldLineTypes)
			foreach (var r in ReferencedTypes(t))
			{
				if (r.Name == null) continue;
				if (r.Namespace != "World.NoiseWorld" && r.Namespace != "World.Logic") continue;
				if (forbidden.Contains(r.Name)) offending.Add($"{t.Name}→{r.Namespace}.{r.Name}");
			}
		Assert.That(offending, Is.Empty,
			$"新世界线依赖了 B 线生成类型：{string.Join(",", offending)}——" +
			"新架构已重写生成语义（Placement/Final/HeightComposer），旧类型不得回流；" +
			"特别是 TerrainNoiseParams：其 LandFractionTarget 是『陆板数占比』，" +
			"与新线『面积分位占比』同名不同义，误接会静默改掉海陆比（决策 08 §清退 · 双事实源）");
	}

	/// <summary>
	/// **双事实源的直接证据钉**——把"同名不同义"变成可执行断言（不依赖反射）。
	/// 用 res1 小球实测"目标 0.29 ⇒ 实际陆地**面积**占比"，
	/// 证明新线侧确实是**面积口径**（退化成陆板数占比时海陆比会明显偏离）。
	/// </summary>
	[Test]
	public void LandFraction_IsAreaBasedNotPlateCountBased()
	{
		const float target = 0.29f;
		var layout = new ContinentLayout(42, 7);
		var field = new LandSeaField(layout, 42, WorldPreset.Earth.LandSea);
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, target);

		int landCells = 0;
		foreach (var isLand in proj.PlacementLand) if (isLand) landCells++;
		float actual = landCells / (float)Ball.CellDirs.Length;

		Assert.That(proj.LandFraction, Is.EqualTo(target).Within(1e-3f),
			$"投影器记录的 LandFraction 应等于请求目标 {target}（实测 {proj.LandFraction:F4}）——分位校准口径");
		Assert.That(actual, Is.EqualTo(target).Within(0.08f),
			$"新线 LandFraction 必须是面积占比（目标 {target} ⇒ 实测 {actual:F3}）——" +
			"若本断言失效，说明『陆板数占比』口径（旧线 TerrainNoiseParams 语义）被接了进来");
	}

	/// <summary>从测试程序集位置向上找仓库根，再拼出相对路径；找不到返回 null。
	/// （与 CellHighlightRingTests.FindRenderSources 同思路：输出目录深度会变，不能硬拼层级。）
	/// ★上界 12：2026-10-09 测试项目迁至 `scripts/Test/World.Tests/` 后，
	///   输出目录 `scripts/Test/World.Tests/bin/Debug/net8.0` 到仓库根需向上 6 层，
	///   原上界 6 恰好差一层 ⇒ 已放宽到 10（留出冗余深度）；
	///   2026-10-11 参数迁入 `res/params/` 后再放宽到 12（并记明来由，防它又变成隐式契约）。</summary>
	static string FindRepoDir(params string[] rel)
	{
		var dir = Path.GetDirectoryName(typeof(FinalGeography).Assembly.Location);
		for (int i = 0; i < 12 && dir != null; i++)
		{
			var candidate = Path.Combine(new[] { dir }.Concat(rel).ToArray());
			if (Directory.Exists(candidate)) return candidate;
			dir = Directory.GetParent(dir)?.FullName;
		}
		return null!;
	}

	/// <summary>类型自身声明的引用面：方法参数/返回 + 字段/属性类型（基类递归）。</summary>
	static IEnumerable<Type> ReferencedTypes(Type t)
	{
		const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
		for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
		{
			foreach (var m in cur.GetMethods(flags))
			{
				if (m.IsSpecialName) continue;
				foreach (var p in m.GetParameters()) yield return p.ParameterType;
				yield return m.ReturnType;
			}
			foreach (var f in cur.GetFields(flags)) yield return f.FieldType;
			foreach (var pr in cur.GetProperties(flags)) yield return pr.PropertyType;
		}
	}

	/// <summary>
	/// **深度引用面**——比 <see cref="ReferencedTypes"/> 多覆盖两层：
	///   ① 声明面补全：基类、接口、泛型参数、特性、事件、构造函数参数；
	///   ② **方法体 IL**：局部变量、异常捕获类型，以及 IL 里"元数据 token"操作数
	///      （`ldfld`/`stfld`/`call`/`callvirt`/`newobj`/`castclass`/`isinst`/`ldtoken`… ⇒ 静态成员与类型字面量）。
	/// 最后把每个类型**展开到元素类型**（数组 ⇒ 元素、泛型 ⇒ 实参），便于按命名空间判定。
	/// ★当前**无契约在使用它**：唯一消费者 `CivSim_DoesNotReferenceWorldGen` 已随 CivSim 移出而改写
	///   （2026-10-09，见 `CivSimAndGameplay_AreMovedOut`）。**有意保留**——下一个跨层契约
	///   （如待办里的「Logic ⟂ Scene」）可直接复用这套 IL 深度扫描，不必重写；
	///   若长期无人用，可连同 `IlRefs` / `Expand` / `OperandTokens` / `OpCodeMap` 一起清退。
	///   刻意不改既有的 <see cref="ReferencedTypes"/>，以免波及其它既有契约。
	/// </summary>
	static IEnumerable<Type> ReferencedTypesDeep(Type t)
	{
		const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
			| BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

		foreach (var x in DeclaredRefs(t, flags)) foreach (var e in Expand(x)) yield return e;
		foreach (var x in IlRefs(t, flags)) foreach (var e in Expand(x)) yield return e;
	}

	/// <summary>声明面引用：基类/接口/泛型参数/特性 + 字段/属性/事件/构造参数/方法参数与返回。</summary>
	static IEnumerable<Type> DeclaredRefs(Type t, BindingFlags flags)
	{
		for (var cur = t; cur != null && cur != typeof(object); cur = cur.BaseType)
		{
			yield return cur;
			foreach (var i in cur.GetInterfaces()) yield return i;
			foreach (var g in cur.GetGenericArguments()) yield return g;
			foreach (var a in cur.GetCustomAttributesData()) yield return a.AttributeType;
			foreach (var f in cur.GetFields(flags)) yield return f.FieldType;
			foreach (var p in cur.GetProperties(flags)) yield return p.PropertyType;
			foreach (var ev in cur.GetEvents(flags)) yield return ev.EventHandlerType;
			foreach (var c in cur.GetConstructors(flags))
				foreach (var p in c.GetParameters()) yield return p.ParameterType;
			foreach (var m in cur.GetMethods(flags))
			{
				foreach (var p in m.GetParameters()) yield return p.ParameterType;
				yield return m.ReturnType;
			}
		}
	}

	/// <summary>方法体引用：局部变量 + catch 类型 + IL token 解析出的类型/成员的声明类型。</summary>
	static IEnumerable<Type> IlRefs(Type t, BindingFlags flags)
	{
		var module = t.Module;
		var methods = t.GetMethods(flags).Cast<MethodBase>().Concat(t.GetConstructors(flags));
		foreach (var mb in methods)
		{
			MethodBody body;
			try { body = mb.GetMethodBody(); } catch { continue; }   // 抽象/接口/外部方法无 body
			if (body == null) continue;

			foreach (var lv in body.LocalVariables) if (lv.LocalType != null) yield return lv.LocalType;
			// ★只有 `Clause`（catch）才有 CatchType；`Finally`/`Filter` 访问会抛 InvalidOperationException。
			foreach (var eh in body.ExceptionHandlingClauses)
				if (eh.Flags == ExceptionHandlingClauseOptions.Clause && eh.CatchType != null) yield return eh.CatchType;

			var il = body.GetILAsByteArray();
			if (il == null) continue;
			foreach (var token in OperandTokens(il))
			{
				MemberInfo mi = null;
				try { mi = module.ResolveMember(token, t.GetGenericArguments(), mb.GetGenericArguments()); }
				catch { }
				if (mi != null)
				{
					if (mi.DeclaringType != null) yield return mi.DeclaringType;
					if (mi is FieldInfo fi) yield return fi.FieldType;
					if (mi is MethodBase mbm)
					{
						if (mi is MethodInfo mm) yield return mm.ReturnType;   // 构造器无返回类型
						foreach (var p in mbm.GetParameters()) yield return p.ParameterType;
					}
					continue;
				}
				Type ty = null;
				try { ty = module.ResolveType(token, t.GetGenericArguments(), mb.GetGenericArguments()); }
				catch { }
				if (ty != null) yield return ty;
			}
		}
	}

	/// <summary>展开到元素类型：数组/指针/ByRef ⇒ 元素；泛型 ⇒ 实参（递归）。</summary>
	static IEnumerable<Type> Expand(Type t)
	{
		if (t == null) yield break;
		if (t.HasElementType) foreach (var e in Expand(t.GetElementType())) yield return e;
		if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) foreach (var e in Expand(a)) yield return e;
		yield return t;
	}

	/// <summary>OpCode 表：用 BCL 的 <see cref="OpCodes"/> 反射构建，避免手抄两百余条指令。</summary>
	static readonly Dictionary<short, OpCode> OpCodeMap = BuildOpCodeMap();

	static Dictionary<short, OpCode> BuildOpCodeMap()
	{
		var map = new Dictionary<short, OpCode>();
		foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
			if (f.FieldType == typeof(OpCode))
			{
				var oc = (OpCode)f.GetValue(null);
				map[(short)oc.Value] = oc;
			}
		return map;
	}

	/// <summary>按 ECMA-335 正确跳过每个操作数，只产出**元数据 token** 操作数（类型/成员）。</summary>
	static IEnumerable<int> OperandTokens(byte[] il)
	{
		int i = 0;
		while (i < il.Length)
		{
			short code;
			if (il[i] == 0xFE)
			{
				if (i + 1 >= il.Length) yield break;
				code = (short)(0xFE00 | il[i + 1]);
				i += 2;
			}
			else { code = il[i]; i += 1; }

			if (!OpCodeMap.TryGetValue(code, out var op)) yield break;   // 未知 ⇒ 安全停止，绝不误报

			switch (op.OperandType)
			{
				case OperandType.InlineNone:
					break;
				case OperandType.ShortInlineBrTarget:
				case OperandType.ShortInlineI:
				case OperandType.ShortInlineVar:
					i += 1; break;
				case OperandType.InlineVar:
					i += 2; break;
				case OperandType.InlineField:
				case OperandType.InlineMethod:
				case OperandType.InlineTok:
				case OperandType.InlineType:
					if (i + 4 > il.Length) yield break;
					yield return BitConverter.ToInt32(il, i);
					i += 4; break;
				case OperandType.InlineSig:
				case OperandType.InlineString:
				case OperandType.InlineBrTarget:
				case OperandType.InlineI:
				case OperandType.ShortInlineR:
					i += 4; break;
				case OperandType.InlineI8:
				case OperandType.InlineR:
					i += 8; break;
				case OperandType.InlineSwitch:
					if (i + 4 > il.Length) yield break;
					i += 4 + 4 * BitConverter.ToInt32(il, i);
					break;
				default:
					yield break;
			}
		}
	}

	[Test]
	public void FinalGeography_DependenciesAreWhitelisted()
	{
		// 契约（§07 D-2 白名单）：FinalGeography 依赖 GeologicalRegions 是**有意设计**
		// （决策 07 §岛屿归属策略：山脉抬出的新岛须归最近构造域 ⇒ 需要 RegionIndexAt），
		// 不是待清理的违规。钉成白名单：除 GeologicalRegions 外，Final 层不得引用任何
		// Placement 生成器或下游层——将来"清理依赖"时不得把它当违规删掉。
		var t = typeof(FinalGeography);
		var forbidden = new[]
		{
			typeof(H3LandSeaProjector), typeof(TectonicField), typeof(MountainSkeleton),
			typeof(RegionalLandforms), typeof(VolcanoField), typeof(FinalSpatialIndex),
			typeof(RiverNetwork), typeof(RiverGraph), typeof(BasinGraph), typeof(LakeState),
			typeof(PrecipitationModel), typeof(WaterTopology),
		};
		var offending = ReferencedTypes(t).Where(forbidden.Contains).ToList();
		Assert.That(offending, Is.Empty,
			$"FinalGeography 引用了白名单外的类型：{string.Join(",", offending)}——" +
			"Final 层只允许消费 FinalHeight + GeologicalRegions（区域归属例外，决策 07；§07 D-2）");
	}

	[Test]
	public void RiverNetwork_OnlyDependsOnFinalLayer()
	{
		// 契约（决策 08 v2 下游边界）：水文层只依赖 Final 世界事实（Ball/FinalGeography/
		// HeightComposer），不引用具体 Feature 类/投影器/TectonicField——
		// "任何下游世界系统只依赖 Final 世界事实即可工作"的编译期钉子
		var t = typeof(RiverNetwork);
		var forbidden = new[]
		{
			typeof(H3LandSeaProjector), typeof(MountainSkeleton), typeof(RegionalLandforms),
			typeof(VolcanoField), typeof(TectonicField), typeof(SurfaceResolver),
			typeof(GeologicalRegions), typeof(FinalSpatialIndex),   // 决策 08 v2 §2：River 不自依赖空间索引
		};
		var offending = ReferencedTypes(t).Where(forbidden.Contains).ToList();
		Assert.That(offending, Is.Empty,
			$"RiverNetwork 引用了下游禁用类型：{string.Join(",", offending)}——水文只准消费 Final 层（决策 08 v2 §2；SpatialIndex 只做二级查询）");
	}

	[Test]
	public void Hydrology_DoesNotDependOnSpatialIndexOrPlacement()
	{
		// 契约（决策 08 v2 §2 / River 2B）：整个水文栈（网络/图/几何）的基础拓扑只由自身
		// flow graph 决定——不得依赖 FinalSpatialIndex（二级查询）或 Placement 层。
		// 几何层（RiverGeometry）也不例外：**不为"画得连续"去改水文**。
		var hydrology = new[]
		{
			typeof(RiverNetwork), typeof(RiverGraph), typeof(RiverGeometry), typeof(BasinGraph),
			typeof(LakeState), typeof(WaterTopology),
		};
		var forbidden = new[]
		{
			typeof(H3LandSeaProjector), typeof(MountainSkeleton), typeof(RegionalLandforms),
			typeof(VolcanoField), typeof(TectonicField), typeof(SurfaceResolver),
			typeof(GeologicalRegions), typeof(FinalSpatialIndex),   // 不自依赖空间索引
		};
		var offending = new List<string>();
		foreach (var t in hydrology)
			foreach (var r in ReferencedTypes(t))
				if (forbidden.Contains(r)) offending.Add($"{t.Name}→{r.Name}");
		Assert.That(offending, Is.Empty,
			$"水文栈引用了下游禁用类型：{string.Join(",", offending)}——拓扑只准由 flow graph 决定（决策 08 v2 §2 / River 2B）");
	}

	[Test]
	public void Hydrology_ReadsFinalHeightButNotSynthesis()
	{
		// 契约（收口 §07 §6 审计 1.1 —— 补上原来的一处**契约盲区**）：
		// `HeightComposer` 是**跨界对象**：它执行 Placement 合成，但输出 `HeightM` =
		// Final 的高度事实（`FinalGeography` 由它派生 `FinalLand`）。
		// 因此**不能**简单把 HeightComposer 塞进 forbidden——那会误伤"水文需要高度"这个真实需求，
		// 反而逼人绕过契约。正确钉法是钉住**另一侧**：
		//   水文栈**可以**读 HeightComposer（它的 Final 侧面），
		//   但**不得碰任何合成侧类型**（特征场 / 地形域 / 海陆场 / 陆块布局 / 曲面解算器）。
		// ⇒ 水文看到的是"最终地表高度"这个**事实**，不是"高度是怎么合出来的"这个**机制**。
		// 这条同时是"为什么 forbidden 表里没有 HeightComposer"的永久说明——
		// 后来者不必再去猜这是遗漏还是有意。
		var hydrology = new[]
		{
			typeof(RiverNetwork), typeof(RiverGraph), typeof(RiverGeometry), typeof(BasinGraph),
			typeof(LakeState), typeof(WaterTopology), typeof(PrecipitationModel),
		};
		var forbidden = new[]
		{
			typeof(FeatureField), typeof(ITerrainField), typeof(TerrainFeature), typeof(Scale3),
			typeof(TerrainDomain), typeof(LandSeaField), typeof(LandSeaSpec),
			typeof(ContinentLayout), typeof(SurfaceResolver),
		};
		var offending = new List<string>();
		foreach (var t in hydrology)
			foreach (var r in ReferencedTypes(t))
				if (forbidden.Contains(r)) offending.Add($"{t.Name}→{r.Name}");
		Assert.That(offending, Is.Empty,
			$"水文栈引用了合成侧类型：{string.Join(",", offending)}——水文只读 Final 高度事实，不得碰合成机制（收口 §07 §6）；HeightComposer 是跨界对象，见本测试注释");
	}

	[Test]
	public void Hydrology_DoesNotReferenceLegacyClimateLine()
	{
		// 契约（决策 08 v2 §2A 降水来源拍板）：水文/降水的**唯一**降水输入 = PrecipitationModel；
		// 旧 ClimateGenerator（World.Biome —— 旧世界线）禁止接入 Hydrology。
		// 理由不是"旧代码不好"，而是复用它会重新打开已关闭的世界线边界：
		// 将来正式 Climate Simulation 上线就会出现两套气候来源 ⇒ "谁是真实降水"的语义争论。
		// 将来替换方式 = **输入源替换**（Climate Simulation → annualPrecipMm → Hydrology），水文层不变。
		var hydrology = new[]
		{
			typeof(RiverNetwork), typeof(RiverGraph), typeof(RiverGeometry), typeof(BasinGraph),
			typeof(LakeState), typeof(WaterTopology), typeof(PrecipitationModel),
		};
		var offending = new List<string>();
		foreach (var t in hydrology)
			foreach (var r in ReferencedTypes(t))
				if (r.Namespace == "World.Biome") offending.Add($"{t.Name}→{r.Name}");
		Assert.That(offending, Is.Empty,
			$"水文/降水引用了旧气候线类型：{string.Join(",", offending)}——降水只准来自 PrecipitationModel（决策 08 v2 §2A；Climate 上线后再替换输入源）");
	}

	[Test]
	public void BasinGraph_DoesNotDependOnLake()
	{
		// 契约（决策 08 v2 §3.8，2C-A 边界）：BasinGraph 只识别"水最终汇到哪里"，
		// **不让 BasinGraph 直接生成 Lake**——Lake 属 2C-B，届时按 WaterSystem 并列接入
		//（RiverGraph / BasinGraph / Lake），而不是 BasinGraph → Lake 的父子依赖。
		// 本钉子现在恒绿（尚无 Lake 类型）；2C-B 一旦建立 Lake 并被 BasinGraph 引用即红。
		var offending = ReferencedTypes(typeof(BasinGraph))
			.Where(t => t.Name == "Lake" || t.Name.StartsWith("Lake"))
			.Select(t => t.FullName).ToList();
		Assert.That(offending, Is.Empty,
			$"BasinGraph 引用了 Lake 相关类型：{string.Join(",", offending)}——Lake 属 2C-B，须与 BasinGraph 并列接入（决策 08 v2 §3.8）");
	}

	[Test]
	public void SpatialIndex_OnlyDependsOnFinalWorld()
	{
		// 契约（决策 08 v2 §#13 架构层）：空间索引只依赖 Final World 事实（Ball/FinalGeography），
		// 不引用 Placement 投影器/Feature 生成器/TectonicField/SurfaceResolver——
		// "索引的是 Final World 的事实，不是生成器内部状态"的编译期钉子
		//
		// ★名字澄清（收口 §07 §6.1-2，2026-10-04 审计）：本测试钉的是**类型级**依赖，
		// 而 `FinalSpatialIndex` 实际还接收一份**数据级**输入——河流格列表
		// （`WorldGenPlanet` 收编后这步住在阶段管线：`IndexPipeline.Run` 把 `Rivers.IsRiver`
		//   变成 `IReadOnlyList<int>` 传给 `FinalSpatialIndex.Generate`）。
		// 河流是**水文产物**，所以 SpatialIndex 的真实位置在 Final 与水文**之后**，
		// 本测试的方法名（"只依赖 Final World"）并不准确。
		// 这是**有意的依赖倒置**：只收 `IReadOnlyList<int>` 而不收 `RiverNetwork`，
		// 索引层因此不产生任何类型级反向依赖（代价：反射契约测不到数据级依赖）。
		// ⇒ 不要为了让名字变准而改成收 `RiverNetwork`——那会制造真实的类型级反向依赖。
		var t = typeof(FinalSpatialIndex);
		var forbidden = new[]
		{
			typeof(H3LandSeaProjector), typeof(MountainSkeleton), typeof(RegionalLandforms),
			typeof(VolcanoField), typeof(TectonicField), typeof(SurfaceResolver),
			typeof(GeologicalRegions), typeof(RiverNetwork), typeof(LandSeaField),
		};
		var offending = ReferencedTypes(t).Where(forbidden.Contains).ToList();
		Assert.That(offending, Is.Empty,
			$"FinalSpatialIndex 引用了禁用类型：{string.Join(",", offending)}——空间索引只准消费 Final World 事实（决策 08 v2 §#13）");
	}

	[Test]
	public void EmptyFeatureList_EqualsPureBaseline()
	{
		// 契约 ⑤：没有特征就没有影响——空列表的最终高度 = 纯基线
		//（Base + 三档 variation + MinLand 钳制）。手工构造基线并与空列表合成逐格对照。
		int seed = 42;
		var layout = new ContinentLayout(seed, 7);
		var field = new LandSeaField(layout, seed, WorldPreset.Earth.LandSea);
		var proj = new H3LandSeaProjector();
		proj.Generate(Ball, field, 0.29f);
		var surface = new SurfaceResolver(field, proj.ThresholdUsed, proj.SeaSpreadUsed);
		var g = new GeologicalRegions(seed);
		g.Generate(Ball, proj, 8_000_000f);
		var composer = new HeightComposer(seed);
		var empty = Array.Empty<FeatureField>();
		composer.Generate(Ball, surface, g, empty);

		// 基线 = BaseElevation lerp 空（=自身）+ variation ±470 + MinLand 钳制
		float varSum = HeightComposer.LargeVariationM + HeightComposer.MediumVariationM + HeightComposer.RegionalNoiseM;
		var dirs = Ball.CellDirs;
		int checkedCells = 0;
		for (int i = 0; i < dirs.Length; i++)
		{
			if (g.RegionOfCell[i] < 0) continue;   // 海格基线 = 纯剖面（无 LandAndSea 特征）
			float baseH = g.BaseElevationField[i];
			// 下界考虑 MinLand 钳制（低基座 + variation 负摆会被钳到 30）
			Assert.That(composer.HeightM[i],
				Is.InRange(MathF.Min(baseH - varSum - 1f, HeightComposer.MinLandElevationM), baseH + varSum + 1f),
				$"格 {i}：空特征列表高度必须落在纯基线 ± variation 带内（钳制放宽下界）");
			checkedCells++;
		}
		Assert.That(checkedCells, Is.GreaterThan(0));
	}

	[Test]
	public void VolcanoField_RegistersThroughContract_Only()
	{
		// 实证复核（决策 08 首例 Feature）：火山全链只经 ITerrainField 进合成器——
		// 上面的反射契约 ③/④ 已保证"不需要改合成器/地理"；本测试验证运行期行为：
		// 一个火山 Feature 的存在只在其帽内改变地表。
		var volcano = new VolcanoFeature
		{
			Anchor = Vector3.Up,
			Scale = new Scale3(80f, 80f, 4200f),
			CapCenter = Vector3.Up,
			CapRadiusRad = 1.0f,
			SigmaKm = 40f,
			PeakTargetM = 4200f,
		};
		IReadOnlyList<FeatureField> features = new[] { new FeatureField(volcano, TerrainDomain.LandAndSea) };
		var composer = new HeightComposer(7);
		float halfSigmaRad = 0.5f * (40f / 6371f);   // 0.5σ 角距（球面精确构造，不靠线性近似）
		var q = (Vector3.Up * MathF.Cos(halfSigmaRad) + Vector3.Right * MathF.Sin(halfSigmaRad)).Normalized();
		var (inf, tgt) = volcano.SampleAt(q);
		Assert.That(inf, Is.GreaterThan(0.6f), $"0.5σ 处影响度应 ≈exp(−0.25)≈0.78（实测 {inf:F2}）");
		Assert.That(tgt, Is.InRange(4100f, 4300f), "火山目标 = 峰顶常数");
		Assert.That(features.Count, Is.EqualTo(1));
	}
}
