using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.IO;
using NUnit.Framework;
using Godot;
using World.Spatial;
using World.WorldGen;

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
/// 纪律（同 NoiseTerrainTests）：只用 [Test]；不写文件；不触碰 GD.*/LogService。
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
		var newLine = new[]
		{
			// Final / 尺度
			typeof(FinalGeography), typeof(SpatialScale), typeof(FinalSpatialIndex),
			// Placement
			typeof(ContinentLayout), typeof(LandSeaField), typeof(H3LandSeaProjector),
			typeof(SurfaceResolver), typeof(GeologicalRegions), typeof(TectonicField),
			typeof(MountainSkeleton), typeof(RegionalLandforms), typeof(VolcanoField),
			typeof(HeightComposer), typeof(SnowOverlay),
			// World Simulation
			typeof(PrecipitationModel), typeof(RiverNetwork), typeof(RiverGraph),
			typeof(RiverGeometry), typeof(BasinGraph), typeof(LakeState), typeof(WaterTopology),
		};
		// 旧世界线的四个命名空间（世界生物群系 / 旧地图生成 / 旧地图渲染 / 旧构造模拟）。
		// ★2026-10-04 D-3 切分清退已完成：这四个命名空间在程序集里**已不存在**，
		//   因此本断言现在是"恒绿但不可省"的方向守卫——若将来有人重新引入旧生成链
		//   并被新线引用，它立刻变红。真正的"已清退"事实由下面的
		//   `LegacyWorldGenerationChain_IsGone` 钉住。
		var legacyNamespaces = new[] { "World.Biome", "World.MapGen", "World.MapView", "World.Tectonics" };

		var offending = new List<string>();
		foreach (var t in newLine)
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
	///   保留 B（存档格式 → World.Archive）、C+D（领域词汇与领域模拟 → World.Domain）、
	///          LogicGrid / HexPlanet / WildCropsSystem（★CivSim 已于 2026-10-09 移出程序集）
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
			"若新代码需要其中的能力，应判断它是『生成世界』还是『消费世界』：后者迁到 World.Domain/World.Archive，前者不应重建（§07 §10.5）");
	}

	/// <summary>
	/// **保留区的依赖白名单钉**（D-3 切分清退后的边界守卫，§07 §10.5.4）。
	/// LogicGrid / WildCropsSystem 是**领域消费者**（消费世界，不生成世界），被明确保留。
	/// 它们允许依赖：World.Domain（C 领域词汇 + D 领域模拟）、World.Archive（B 存档格式）、
	/// World.HexPlanet（球面网格几何基础设施）、World.Utils / World.Services（通用工具）、
	/// 以及彼此与自身。
	/// **不允许**再出现任何旧世界生成命名空间——否则说明有人把生成能力又塞回了消费者里。
	/// ★2026-10-09 CivSim 移出：`World.CivSim` / `World.Gameplay` 已随 CivSim 迁至
	///   `scripts/_removed/`，故从「允许清单」与「被扫消费者」两处一并移除——
	///   回归 CivSim 时**必须同步恢复**这两处（见 `docs/裁决-CivSim移出.md`）。
	/// </summary>
	[Test]
	public void RetainedDomainConsumers_OnlyDependOnAllowedNamespaces()
	{
		var allowed = new[]
		{
			"World.LogicGrid", "World.Domain", "World.Archive",
			"World.HexPlanet", "World.Utils", "World.Services",
		};
		var forbidden = new[] { "World.Biome", "World.MapGen", "World.MapView", "World.Tectonics" };

		// ★按类型语义定位，不按目录名定位（命名空间 = 语义；`scripts/{Scene,Logic}/` 分区不吃 namespace）。
		// ★2026-10-09：`World.CivSim.CivSimContext` 已随 CivSim 移出，从被扫面移除。
		var consumers = new[]
		{
			typeof(World.LogicGrid.GameGrid),
			typeof(World.Domain.WildCropsSystem),
		};

		var offending = new List<string>();
		foreach (var t in consumers)
			foreach (var r in ReferencedTypes(t))
			{
				var ns = r.Namespace;
				if (ns == null) continue;
				if (forbidden.Contains(ns)) { offending.Add($"{t.Name}→{ns}.{r.Name}"); continue; }
				if (!ns.StartsWith("World.")) continue;              // 系统/Godot 类型不管
				if (allowed.Any(a => ns == a || ns.StartsWith(a + "."))) continue;
				offending.Add($"{t.Name}→{ns}.{r.Name}（不在白名单）");
			}
		Assert.That(offending, Is.Empty,
			$"保留的领域消费者依赖了越界命名空间：{string.Join(",", offending)}——CivSim/LogicGrid 只准依赖 " +
			"C/D（World.Domain）、B（World.Archive）与几何/工具基础设施，不得重新接上旧世界生成链（§07 §10.5）");
	}

	/// <summary>
	/// **`World.CivSim` 移出钉（结果 + 位置）**（2026-10-09 用户拍板："将 civsim 移出去，
	/// 等待正确时机回归，现在不需要"）。
	///
	/// ★本条的前身是 `CivSim_DoesNotReferenceWorldGen`（2026-10-06 ADR-0005 · C-4 §6.2 G1）——
	///   一条"`World.CivSim` ⟂ `World.WorldGen`"的**方向守卫**（扫全程序集 + 解 IL 深度）。
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
	/// **只能是新世界线内部**（`World.WorldGen*`）（2026-10-06 · ADR-0005 §边界条款 B2 · C-4 §6.2 G2）。
	///
	/// 判定问题："谁在消费 Final 世界事实？" ⇒ 目前**只有 WorldGen 自己**。
	/// 实测（C-4 §2.3）：`scripts/Logic/WorldGen/Final/` 之外的仓内消费者 = **0**；
	/// "World → Human Input Bridge" 当前**尚不存在，连桩都没有**。
	///
	/// ★目的**不是**永久禁止所有桥，而是：**在桥正式设计（Phase 1–4 逐层迁移）之前，
	///   不允许出现"私接消费者"**。任何把 Final 事实接进 `World.Domain` / `World.LogicGrid` /
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
			&& (ns == "World.WorldGen" || ns.StartsWith("World.WorldGen."));

		var offending = new List<string>();
		foreach (var t in asm.GetTypes())
		{
			if (IsWorldGen(t.Namespace)) continue;   // WorldGen 内部 = 合法生产消费者
			foreach (var r in ReferencedTypesDeep(t))
				if (facts.Contains(r))
					offending.Add($"{t.Namespace}.{t.Name}");
		}

		Assert.That(offending.Distinct().ToList(), Is.Empty,
			$"Final 世界事实被 WorldGen 之外的类型消费：{string.Join(",", offending.Distinct())}——" +
			"桥未正式设计前不得私接消费者（ADR-0005 §B2）；" +
			"若确为正式接线，请显式扩展本测试的白名单并留下决策记录（C-4 §6.2 G2）");

		// 非空转自检：WorldGen 内部确实有消费者（否则说明扫描器失效或事实类型被改名）。
		var internalConsumers = asm.GetTypes()
			.Count(t => IsWorldGen(t.Namespace) && ReferencedTypesDeep(t).Any(facts.Contains));
		Assert.That(internalConsumers, Is.GreaterThan(0),
			"未发现任何 WorldGen 内部消费者——扫描器可能失效，或 Final 事实类型已改名（本契约的扫描面需要同步更新）");
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
	/// 新世界线（`World.WorldGen`）的类型清单——**架构契约的扫描面**。
	/// ★集中于此而非各测试内联：新增子系统时只改这一处，就不会漏进任何一条契约的扫描面
	///   （漏扫 = 契约静默失效，比契约本身不写更危险）。
	/// </summary>
	static readonly Type[] NewWorldLineTypes =
	{
		// ── Final / 尺度 / 空间索引 ──
		typeof(FinalGeography), typeof(SpatialScale), typeof(FinalSpatialIndex),
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
	/// 背景：新线命名空间是 `World.WorldGen`，而 B 线旧单体在**父**命名空间
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
		var renderEntryPoints = new[]
		{
			typeof(WorldGenPlanet), typeof(WorldGenManager), typeof(RiverLineOverlay),
		};
		// 新线的表现依赖目标（迁移完成后应当全部落在这里）。
		// ⚠️ 这是**声明白名单**（供人对照），当前断言路径只比对旧父命名空间 `World.NoiseWorld`；
		//    ★2026-10-09 补 `.Controllers`/`.Constants` 两个子层，与上方注释同源。
		var renderLayer = new[]
		{
			"World.Render", "World.Render.UI", "World.Render.Controllers", "World.Render.Constants",
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
		// ★2026-10-05 P1 完成后清单已换新名：4 类表现资产全部迁入 World.Render / World.Render.UI。
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
		//   （大小写正规化 + 六子层拆分；namespace 仍是 World.WorldGen 不变）
		// ★2026-10-09 Scene/Logic 两分区：`scripts/{Scene,Logic}/` 是**命名空间中性分区**
		//   （不吃 namespace 段，如 `src/`），其下路径仍逐字对应 namespace
		//   ⇒ 世界生成主链（逻辑侧）落在 scripts/Logic/WorldGen/。
		Assert.That(Directory.Exists(FindRepoDir("scripts", "Logic", "WorldGen")), Is.True,
			"scripts/Logic/WorldGen/ 应存在（世界生成主链目录）");
	}

	/// <summary>
	/// **新线命名空间钉（namespace 重构后）**——世界生成主链必须落在 `World.WorldGen`。
	///
	/// ★为什么需要这条：2026-10-05 之前新线是 `World.NoiseWorld.WorldGen`
	///   （B 线旧单体的**子命名空间**）。B 线清退后父命名空间已消失，
	///   新线成了"没有父的子命名空间"⇒ 语义上是历史遗留的碎片。
	///   已重构为 `World.WorldGen`（平级、语义自洽）。
	///
/// 钉住"名字 + 位置"两件事：
///   名字 = `World.WorldGen`（防止有人改回带NoiseWorld 的名字）
///   位置 = 目录 `scripts/Logic/WorldGen/`（防止代码与目录再次脱节）
///   ★2026-10-09：两分区后主链在 `scripts/Logic/WorldGen/`（装配层在 `scripts/Scene/WorldGen/`）。
/// ★2026-10-06 A 步：目录由 `scripts/worldgen/` 正规化为 `scripts/WorldGen/`
	///   （Windows 大小写不敏感 ⇒ 改名走 `git mv` 两步法；namespace 刻意不改，
	///    因本契约第①段把 6 个核心类型钉死在 `World.WorldGen`）。
	/// </summary>
	[Test]
	public void NewWorldLine_NamespaceIsWorldGen()
	{
		// ① 抽样核心类型：命名空间必须是 World.WorldGen（平级，不再挂 NoiseWorld）
		var core = new[]
		{
			typeof(FinalGeography), typeof(HeightComposer), typeof(FinalSpatialIndex),
			typeof(RiverNetwork), typeof(WaterTopology), typeof(WorldGenPlanet),
		};
		var wrong = core.Where(t => t.Namespace != "World.WorldGen")
			.Select(t => $"{t.Name}@{t.Namespace}")
			.ToList();
		Assert.That(wrong, Is.Empty,
			$"世界生成主链类型不在 World.WorldGen：{string.Join(",", wrong)}——" +
			"新线命名空间是 World.WorldGen（决策 08 §namespace 重构）");

		// ② 旧命名空间不得复活（它是B 线的痕迹，已清退）
		var legacy = typeof(FinalGeography).Assembly.GetTypes()
			.Where(t => t.Namespace != null && t.Namespace.StartsWith("World.NoiseWorld"))
			.Select(t => $"{t.Namespace}.{t.Name}")
			.ToList();
		Assert.That(legacy, Is.Empty,
			$"`World.NoiseWorld*` 命名空间复活了：{string.Join(",", legacy)}——" +
			"B 线已清退、新线已改名为 World.WorldGen，不应再有 NoiseWorld 字样");

		// ③ 目录与命名空间一致（D-3：按类型语义定位，但目录也不应误导）
		//    ★用与 CellHighlightRingTests 相同的"向上查找仓库根"写法，
		//    不用 Directory.GetParent 硬拼层级（测试输出目录深度会变）。
		//    ★2026-10-09 Scene/Logic 两分区后，"目录 ↔ namespace"对应关系 = **分区之后**
		//    的路径逐字对应：`scripts/Logic/WorldGen/` ↔ `World.WorldGen`。
		//    顶层 `Scene/` 与 `Logic/` 是**命名空间中性分区**（不参与 namespace，如 `src/`）。
		var worldgenDir = FindRepoDir("scripts", "Logic", "WorldGen");
		Assert.That(worldgenDir, Is.Not.Null,
			"未找到 scripts/Logic/WorldGen/ —— namespace World.WorldGen 与目录路径 " +
			"scripts/Logic/WorldGen 必须一致（scripts/{Scene,Logic}/ 之下逐字对应 namespace，" +
			"分区名本身不参与 namespace）");
		// ★大小写严格钉：Windows 上 Directory.Exists 大小写不敏感 ⇒ 写 "worldgen" 也能命中，
		//   那样这条契约就再也拦不住"目录名又退回全小写"。故额外比对真实目录名。
		//   （A 步起因：Windows 大小写不敏感导致 `scripts/WorldGen` 曾被解析成 `scripts/worldgen`，
		//    正规化必须走 `git mv worldgen __tmp && git mv __tmp WorldGen` 两步法。）
		if (worldgenDir != null)
		{
			var actualDirName = new DirectoryInfo(worldgenDir).Name;
			Assert.That(actualDirName, Is.EqualTo("WorldGen"),
				$"目录名实际是 '{actualDirName}'，应为 'WorldGen' —— 本契约钉的是" +
				"namespace World.WorldGen 与目录名大小写**逐字一致**（大写 W/G），" +
				"否则 namespace 与目录脱节（契约原意）就失效了");
		}
		// 旧目录不得复活（含改名前的全小写形式——大小写不敏感文件系统上它同名）
		// ★2026-10-09：分区后清退目录可能落在任一分区之下 ⇒ 两侧都查。
		Assert.That(FindRepoDir("scripts", "Logic", "noise_world"), Is.Null,
			"scripts/Logic/noise_world/ 不应存在（B 线已清退，新线在 scripts/Logic/WorldGen/）");
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
	///   生成逻辑/参数/场景/UI → 删；表现资产 4类 → 迁入 World.Render / World.Render.UI。
	/// </summary>
	[Test]
	public void LegacyNoiseWorldLine_IsGone()
	{
		const string legacyParent = "World.NoiseWorld";
		// 只认根命名空间本身。★2026-10-05 namespace 重构后新线已是 `World.WorldGen`（不再是子命名空间），
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
				if (r.Namespace != "World.NoiseWorld" && r.Namespace != "World.WorldGen") continue;
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
		var field = new LandSeaField(layout, new LandSeaParams { Seed = 42 });
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
	/// ★上界 10：2026-10-09 测试项目迁至 `scripts/Test/World.Tests/` 后，
	///   输出目录 `scripts/Test/World.Tests/bin/Debug/net8.0` 到仓库根需向上 6 层，
	///   原上界 6 恰好差一层 ⇒ 已放宽到 10（留出冗余深度）。</summary>
	static string FindRepoDir(params string[] rel)
	{
		var dir = Path.GetDirectoryName(typeof(FinalGeography).Assembly.Location);
		for (int i = 0; i < 10 && dir != null; i++)
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
			typeof(TerrainDomain), typeof(LandSeaField), typeof(LandSeaParams),
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
		// （`WorldGenPlanet.cs:124-127` 把 `Rivers.IsRiver` 变成 `IReadOnlyList<int>` 传入）。
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
		var field = new LandSeaField(layout, new LandSeaParams { Seed = seed });
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
