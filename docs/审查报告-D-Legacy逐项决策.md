# D 步取证报告：Legacy 三目录逐项决策

- 日期：2026-10-06
- 性质：**取证报告 + 执行结果记录**（§1–§6 为取证原文，§7 为执行后追加）
- 基线：`072d415`（结构阶段收口后，工作区已归零）
- 目的：对 D 的对象逐项证明「是否真的没人依赖」，再给出保留 / 清退 / 封存建议
- **执行状态：✅ 已完成**（`9f2dc52` D-A / `f4cc534` D-B / 本文档 D-C）

---

## 0. 结论先行

**D 的对象里，只有 2 个文件有真实生产消费者，其余 5 个构成孤立闭包。**

| 文件 | 生产消费者 | 测试消费者 | 建议 |
|---|---|---|---|
| `HexPlanet/Icosahedron.cs` | **3 处真实代码** | **9+ 测试文件** | ✅ **保留**（基础设施） |
| `HexPlanet/SphereGrid.cs` | **1 处真实代码** | 0（仅注释） | ✅ **保留** |
| `HexPlanet/GoldbergBuilder.cs` | **0** | HexPlanetTests | 🔶 清退候选 |
| `HexPlanet/SubdividedMesh.cs` | 仅闭包内 | HexPlanetTests | 🔶 清退候选（随闭包） |
| `HexPlanet/HexTile.cs` | 仅闭包内 | HexPlanetTests（注释） | 🔶 清退候选（随闭包） |
| `PlanetLOD/ChunkMeshBuilder.cs` | **0** | **0** | 🔶 清退候选（最确定） |
| `Surface/PlanetColors.cs` | **0** | ServicesTests | 🔶 清退候选（独立） |

**⚠️ 关键纠正**：本次取证推翻了此前报告中的三处判断（见 §4），
其中最重要的是 **`HexPlanet` 并非整体 Legacy** —— 它的 5 个文件里有 2 个是活跃基础设施。

---

## 1. 取证口径（为什么必须区分三类引用）

依赖判定不能只看"文件里出现了类名"。本次严格执行三层过滤：

| 引用类型 | 计入依赖 | 理由 |
|---|---|---|
| **代码引用**（`Icosahedron.Subdivide(...)`） | ✅ 计入 | 真实编译依赖 |
| **测试引用**（`tests/**`） | ✅ 单独列出 | 决定测试是否需要连带处理 |
| **注释引用**（`/// ... Icosahedron 拓扑 ...`） | ❌ 不计入 | 不产生编译依赖 |

实现：`grep -rn "类名\." scripts/ ... | grep -vE ":\s*///"` 排除纯注释行。

> 这条口径直接改变了结论：`LogicGrid/GameGrid.cs:203` 提到 Icosahedron，
> 但**是纯注释**（"均匀近似 4πR²/N——Icosahedron 胞面积几乎相等"），
> 若按朴素 grep 会误判为"LogicGrid 依赖 HexPlanet"。

---

## 2. 逐项取证

### 2.1 `HexPlanet/Icosahedron.cs` — ✅ 必须保留

**真实生产消费者（3 处代码，全部非注释）**：

| 位置 | 用途 |
|---|---|
| `LogicGrid/GameMapArchive.cs:159` | `Icosahedron.VertexCountForLong(grid.GridN)` —— 存档校验预期顶点数 |
| `Diagnostics/ArchiveDiag.cs:116` | `Icosahedron.GridNFromVertexCount(map.Verts.Length)` —— 反推开档网格规模 |
| `HexPlanet/SphereGrid.cs:46` | `Icosahedron.Subdivide(...)` —— SphereGrid 的几何来源 |

**测试消费者（9+ 文件，作为"合成小网格"夹具）**：

```
tests/World.Tests/CivEventTests.cs:35,163          Icosahedron.Subdivide(2, 6371f, ...)
tests/World.Tests/CivOverlayTests.cs:26
tests/World.Tests/CivSimMechanics2Tests.cs:50
tests/World.Tests/CivSimMechanicTests.cs:48
tests/World.Tests/CivSimWarTests.cs:33
tests/World.Tests/LogicGridTests.cs:112,187,188
tests/World.Tests/HexPlanetTests.cs:35,44,53,60,... (7 个测试)
tests/World.Tests/MapGenTests.cs:20 (注释)
```

**判定**：`Icosahedron` 是**全项目唯一公式源**（`10n²+2`，见 `HexPlanetTests:26` 注释
"顶点/面数公式(全项目唯一公式源)"），且是 CivSim / LogicGrid 测试夹具的基础。
**删它会导致 9+ 个测试文件无法编译。**

⚠️ 注意：`Diagnostics/ArchiveDiag.cs` **不是孤儿** —— 它被
`CivSimDiag.cs:42,64` 与 `LogicGridDiag.cs:22,29` 引用，而这两个都有 `.tscn` 场景。
⇒ `ArchiveDiag → Icosahedron` 与 `ArchiveDiag → SphereGrid` 是**活跃依赖链**。

### 2.2 `HexPlanet/SphereGrid.cs` — ✅ 保留

**真实生产消费者**：`Diagnostics/ArchiveDiag.cs:20,23,117`
（`new SphereGrid(n)` + 作为 `DiagContext.Grid` 字段类型）

```csharp
public readonly SphereGrid Grid;  // 与存档同拓扑重建网格（ArchiveDiag.cs:20）
var grid = new SphereGrid(n);     // ArchiveDiag.cs:117
```

**测试消费者**：0（`LogicGridTests.cs:123` 仅注释提及"SphereGrid 同款"）

**判定**：`ArchiveDiag` 是活跃诊断工具（被两个场景类使用），删 `SphereGrid` 会破编译。

### 2.3 `HexPlanet/GoldbergBuilder.cs` — 🔶 清退候选

**生产消费者**：**0**
**测试消费者**：`tests/World.Tests/HexPlanetTests.cs`（5 处 `new GoldbergBuilder`，对应 3 个测试）

**内部依赖**（它消费谁）：`Icosahedron` / `SubdividedMesh` / `HexTile`

**判定依据**：生产零引用是硬证据；测试引用只说明"它曾被测过"，不构成保留理由。
⚠️ 但注意：它构造的「Icosahedron 顶点 → 六边形/五边形 tile」能力，
与 **`LogicGrid/GameGrid`（同样是 `10n²+2` 顶点胞）功能重叠** ——
`GameGrid` 用 `Icosahedron.Subdivide` 直接建胞，未经过 `GoldbergBuilder`。

### 2.4 `HexPlanet/SubdividedMesh.cs` — 🔶 清退候选（**随闭包**）

**生产消费者**：仅 `GoldbergBuilder.cs:18`（构造参数类型）
**测试消费者**：`HexPlanetTests.cs`（8 处，对应 2 个测试）

⚠️ **它不是独立候选** —— 若清退 `GoldbergBuilder`，`SubdividedMesh` 立即失去唯一生产消费者。
但它**被测试直接实例化**（`new SubdividedMesh(verts, indices)`），所以闭包判定时必须一并处理。

### 2.5 `HexPlanet/HexTile.cs` — 🔶 清退候选（**随闭包**）

**生产消费者**（闭包内 2 个）：
- `GoldbergBuilder.cs:12,21,85,101`
- `PlanetLOD/ChunkMeshBuilder.cs:27,142,143,175,176`

⚠️ `HexTile` 是 `GoldbergBuilder` 与 `ChunkMeshBuilder` 的**共同依赖** ——
所以它的去留由这两个共同决定：**只有两者都清退，`HexTile` 才失去全部消费者。**

### 2.6 `PlanetLOD/ChunkMeshBuilder.cs` — 🔶 清退候选（**最确定**）

**全仓引用（含 `.cs` / `.tscn` / `.tres`）**：**0**（连测试都没有）

**判定**：D 的对象里**唯一"三零"文件**（零生产、零测试、零场景）。
它消费 `HexTile`，是 `HexTile` 的第二个消费者。

### 2.7 `Surface/PlanetColors.cs` — 🔶 清退候选（**独立**）

**生产消费者（`scripts/`）**：**0**
**测试消费者**：`tests/World.Tests/ServicesTests.cs`（10 处 `PlanetColors.ElevationToColor`）

**判定**：与 HexPlanet 闭包**无依赖关系**，可独立处理。
⚠️ 它的测试在 `ServicesTests.cs` 里与其它测试混放，清退时需**精确删测试方法**而非整文件。

---

## 3. 清退闭包分析

### 3.1 闭包 A：HexPlanet 孤立子图

```
        Icosahedron  ← 【保留，见 §2.1】
             ↑
      SubdividedMesh ──┐
             ↑         │
      GoldbergBuilder ─┤  ← 生产零消费者（闭包根）
             ↑         │
         HexTile ←─────┤
             ↑         │
    ChunkMeshBuilder ──┘  ← 三零（闭包根）
```

**关键**：`SubdividedMesh` 与 `HexTile` **不是独立候选** —— 它们只被闭包根和彼此引用。
闭包根（`GoldbergBuilder` / `ChunkMeshBuilder`）消失后，它们自动失去消费者。

**外部依赖**：整个闭包只依赖 `Icosahedron`（保留）⇒ **闭包可整体切除，不影响 Icosahedron 的其它消费者。**

### 3.2 闭包 B：PlanetColors（独立）

```
PlanetColors  ← 生产零消费者
      ↑
ServicesTests（10 处断言）
```

与闭包 A 无交集，独立决策。

### 3.3 测试连带处理（**易漏项**）

⚠️ **`HexPlanetTests.cs` 不能整文件删除** —— 它同时覆盖保留项与清退项：

| 测试 | 主题 | 处置 |
|---|---|---|
| `VertexCountFor_MatchesFormula` | Icosahedron | **保留** |
| `VertexCountForLong_MatchesFormula` | Icosahedron | **保留** |
| `GridNFromVertexCount_IsInverseOfVertexCountFor` | Icosahedron | **保留** |
| `VertexCountForLong_HandlesLargeN_NoOverflow` | Icosahedron | **保留** |
| `Subdivide_ProducesExpectedCounts` | Icosahedron | **保留** |
| `Subdivide_AllVerticesOnSphere` | Icosahedron | **保留** |
| `Subdivide_UniqueVerticesAndValidIndices` | Icosahedron | **保留** |
| `SubdividedMesh_DeduplicatesAndSharedEdgeIsNeighbor` | SubdividedMesh | 🔶 随闭包删 |
| `SubdividedMesh_TriNeighbors_...` | SubdividedMesh | 🔶 随闭包删 |
| `GoldbergBuilder_ClassicCounts` | GoldbergBuilder | 🔶 随闭包删 |
| `GoldbergBuilder_NeighborsSymmetricAndCornersMatch` | GoldbergBuilder | 🔶 随闭包删 |
| `GoldbergBuilder_ManualIcosahedron_AllPentagons` | GoldbergBuilder | 🔶 随闭包删 |
| `HexTile_FieldsConsistent` | HexTile | 🔶 随闭包删 |
| `ModuleTest_SubdividedSphere_AllDegrees5Or6` | 模块级 | ⚠️ 需逐个确认 |

**`ServicesTests.cs`**：同理，`PlanetColors` 的 10 处断言需精确删除，保留文件内其它测试。

---

## 4. ⚠️ 对此前结论的三处纠正

本次取证推翻了此前报告（`docs/功能清单与结构重规划.md` §2 S3）中的判断：

| # | 此前说法 | 实测结果 | 影响 |
|---|---|---|---|
| 1 | "`CivSim` 依赖白名单**有意包含** `HexPlanet`" | ⚠️ `CivSim/Archive/CivMapArchive.cs:7` 有 `using World.HexPlanet;`，但**文件内零使用** ⇒ **死 using，非真实依赖** | 原判断把"契约白名单允许"误读为"实际使用"。**但 CivSim 的\*测试\*确实依赖 Icosahedron**（5 个测试文件） |
| 2 | "`HexPlanet/Icosahedron.cs` ← `LogicGrid/GameGrid` 假定同款几何" | ⚠️ `GameGrid.cs:203` 是**纯注释**；真实依赖在 `LogicGrid/GameMapArchive.cs:159`（代码） | 依赖存在但位置不同；`GameGrid` 本身不依赖 HexPlanet |
| 3 | "`HexPlanet` 仍是 `LogicGrid`/`CivSim` 的有效依赖"（作为"不属 Legacy"的理由） | ⚠️ 结论**成立**，但理由需修正：`LogicGrid` 真实依赖（`GameMapArchive` + 大量测试）；`CivSim` **生产不依赖**（死 using），**测试依赖** | 结论不变，证据链更准 |

> 这三处都属于"**按类型语义判依赖**"原则的应用 —— 物理目录名、契约白名单、注释提及
> 都不能替代真实编译依赖的实测。

---

## 5. 决策建议

### 5.1 建议方案（供拍板）

| 选项 | 内容 | 理由 |
|---|---|---|
| **D1** | 清退**闭包 A 根**：`GoldbergBuilder` + `ChunkMeshBuilder`，并连带 `SubdividedMesh` + `HexTile` + 对应的 7 个测试方法 | 生产零消费者，且闭包可整体切除 |
| **D2** | 清退 `PlanetColors` + `ServicesTests` 中对应 10 处断言 | 生产零消费者，与闭包 A 独立 |
| **D3** | **保留** `Icosahedron` + `SphereGrid`，并补充"为何保留"的文档说明 | 有真实生产消费者 + 9+ 测试夹具依赖 |

### 5.2 ⚠️ 执行前必须确认的前置条件

1. **`ModuleTest_SubdividedSphere_AllDegrees5Or6`（`HexPlanetTests:317`）的归属** ——
   它测的是 `SubdividedMesh` 还是纯 `Icosahedron`？若是后者，必须保留。**未确认前不得动。**
2. **`Surface` 目录清空后是否需要删除空目录** —— `PlanetColors` 是 `Surface/` 唯一文件，
   清退后目录为空。⚠️ 但 Godot `.uid` 与目录无绑定，删空目录是独立决定。
3. **`PlanetLOD` 目录同理** —— `ChunkMeshBuilder` 是其唯一文件。
4. **是否走"封存"而非"删除"** —— 项目惯例是"**资产封存代替代码迁移**"
   （见 `docs/newdecision/封存-NoiseWorld设计史料.md`）。
   `GoldbergBuilder` 的"Goldberg 多边形构造"与 `LogicGrid/GameGrid` 功能重叠，
   其设计经验是否值得先封存进文档？
5. **契约测试同步** —— `ArchitectureContractTests.cs:110` 白名单含 `"World.HexPlanet"`。
   清退后 `HexPlanet` 命名空间仍存在（保留 2 类），**白名单无需改**；
   但若有其它契约断言覆盖 `HexPlanet` 类型数，需同步（**待核实**）。

### 5.3 与 D 无关（不得混入）

按用户纪律，以下**不在 D 范围**，不得"顺手处理"：
- `Render/Camera/` 的 `OrbitalCamera`（属 B 步遗留命名决策，另行讨论）
- `new_HexWorld/Ball` → `Spatial/Ball`（属 E 步，24 处 using）
- 3 个已知的"未接线/悬空"项（`SnowOverlay` 等）

---

## 6. 遗留待确认清单

### 6.1 已由本报告查清（无需再问）

| # | 待确认项 | 结论 |
|---|---|---|
| 1 | `ModuleTest_SubdividedSphere_AllDegrees5Or6` 归属 | ✅ **属闭包 A**。源码（`HexPlanetTests:317-330`）里同时 `new SubdividedMesh(...)` + `new GoldbergBuilder(...)` ⇒ 随闭包清退 |
| 2 | 契约是否需同步 | ✅ **无需**。`ArchitectureContractTests` 对 `HexPlanet` 只有 3 处提及：`:78`/`:100` 是注释、`:110` 是**白名单字符串**（"允许"，非"要求"）。清退闭包 A 后 `World.HexPlanet` 命名空间仍存在（保留 2 类），白名单照旧有效 |
| 3 | `HexPlanetTests` 的删除/保留边界 | ✅ **保留 7 个 Icosahedron 测试，删 7 个闭包测试**。详见下表 |

**`HexPlanetTests.cs` 逐测试处置**：

| 测试 | 主题 | 处置 |
|---|---|---|
| `VertexCountFor_MatchesFormula` | Icosahedron | ✅ 保留 |
| `VertexCountForLong_MatchesFormula` | Icosahedron | ✅ 保留 |
| `GridNFromVertexCount_IsInverseOfVertexCountFor` | Icosahedron | ✅ 保留 |
| `VertexCountForLong_HandlesLargeN_NoOverflow` | Icosahedron | ✅ 保留 |
| `Subdivide_ProducesExpectedCounts` | Icosahedron | ✅ 保留 |
| `Subdivide_AllVerticesOnSphere` | Icosahedron | ✅ 保留 |
| `Subdivide_UniqueVerticesAndValidIndices` | Icosahedron | ✅ 保留 |
| `SubdividedMesh_DeduplicatesAndSharedEdgeIsNeighbor` | SubdividedMesh | 🔶 删 |
| `SubdividedMesh_TriNeighbors_SymmetricNoSelf_EveryEdgeShared` | SubdividedMesh | 🔶 删 |
| `GoldbergBuilder_ClassicCounts` | GoldbergBuilder | 🔶 删 |
| `GoldbergBuilder_NeighborsSymmetricAndCornersMatch` | GoldbergBuilder | 🔶 删 |
| `GoldbergBuilder_ManualIcosahedron_AllPentagons` | GoldbergBuilder | 🔶 删 |
| `HexTile_FieldsConsistent` | HexTile | 🔶 删 |
| `ModuleTest_SubdividedSphere_AllDegrees5Or6` | 闭包（已确认） | 🔶 删 |

⚠️ **删除后测试总数会从 511 减少**，这是预期结果：
减少量 = 闭包 A 的 7 个测试（含 `[TestCase]` 展开）+ `PlanetColors` 的相关断言。
**不是回归**，需在提交信息里说明预期减少量，避免被误读为测试丢失。

### 6.2 仍需你拍板（3 项）

| # | 待确认项 | 说明 |
|---|---|---|
| 1 | **封存 vs 删除** | 项目惯例是"**资产封存代替代码迁移**"（`docs/newdecision/封存-NoiseWorld设计史料.md`）。`GoldbergBuilder` 的"Goldberg 多边形构造"能力与 `LogicGrid/GameGrid`（同为 `10n²+2` 顶点胞）**功能重叠** ⇒ 其设计经验是否先封存进文档再删代码？ |
| 2 | **空目录处置** | `PlanetLOD/`（`ChunkMeshBuilder` 是唯一文件）与 `Surface/`（`PlanetColors` 是唯一文件）清退后变空目录。删目录是独立决定（`.uid` 与目录无绑定）。 |
| 3 | **执行范围** | 是否一次做完 D1+D2+D3，还是拆成独立提交（建议拆：闭包 A 一个、`PlanetColors` 一个、保留项文档说明一个）。 |

---

## 7. 执行结果（2026-10-06 追加）

### 7.1 三个独立提交

| 步骤 | commit | 变更 | 测试变化 |
|---|---|---|---|
| **D-A** | `9f2dc52` | `refactor(hexplanet)!: retire Goldberg closure` — 4 文件 + 4 `.uid` 删除，`HexPlanetTests` 部分删除 | 511 → **495**（−16） |
| **D-B** | `f4cc534` | `refactor(surface)!: retire PlanetColors` — 1 文件 + 1 `.uid` 删除，`ServicesTests` 部分删除 | 495 → **492**（−3） |
| **D-C** | 本文档 + 封存文档 | 决策记录 + 设计经验封存 | 无代码变更 |

### 7.2 测试减少量的逐项对账（★非回归，可验证）

D-A 的 −16 恰好等于被删 7 个方法的用例数：

| 方法 | 特性 | 用例 |
|---|---|---|
| `SubdividedMesh_DeduplicatesAndSharedEdgeIsNeighbor` | `[Test]` | 1 |
| `SubdividedMesh_TriNeighbors_SymmetricNoSelf_EveryEdgeShared` | `[TestCase×4]` | 4 |
| `GoldbergBuilder_ClassicCounts` | `[TestCase×3]` | 3 |
| `GoldbergBuilder_NeighborsSymmetricAndCornersMatch` | `[TestCase×4]` | 4 |
| `GoldbergBuilder_ManualIcosahedron_AllPentagons` | `[Test]` | 1 |
| `HexTile_FieldsConsistent` | `[TestCase×2]` | 2 |
| `ModuleTest_SubdividedSphere_AllDegrees5Or6` | `[TestCase×1]` | 1 |
| **合计** | | **16** |

D-B 的 −3 等于被删的 3 个 `[Test]`（`ElevationToColor_*`）。

**实测 492 = 511 − 16 − 3**，与逐方法核算完全一致。

### 7.3 最终状态

| 项 | 结果 |
|---|---|
| `scripts/HexPlanet/` | 保留 `Icosahedron.cs` + `SphereGrid.cs`（**目录保留**） |
| `scripts/PlanetLOD/` | ❌ **目录已删除**（唯一文件清退后置空） |
| `scripts/Surface/` | ❌ **目录已删除**（同上） |
| `scripts/` 一级目录 | 由 15 个减至 **13** 个 |
| 保留项依据 | 见 §2.1 / §2.2（有真实生产消费者 + 9+ 测试夹具依赖） |
| 设计经验封存 | `docs/newdecision/封存-HexPlanet-Goldberg闭包.md` |

### 7.4 验收（三个提交各自独立执行）

每步均通过：
- `dotnet build` → **0 警告 / 0 错误**
- `dotnet test` → 全绿（495 / 492，与预期减少量一致）
- 四向对账 → `.cs` / `.cs.uid` 同时删除，配比保持平衡
- 全仓无对被删类型的代码引用（残留均为说明性注释）
- Godot 4.7.1 headless → 实机加载**零 ERROR**
- `[WORLDGEN-TIMING] n=288122 res=4 land=29.0% regions=95 ridges=71 basins=6971(endorheic 0) lakes=3439 thr=0.636`
  → 与清退前**逐字一致**（证明清退未触及世界生成语义）

---

## 8. 本次 D 步固化的证据标准（可用于后续清退）

> **每次删除都必须有「消费者证据 + 替代关系/闭包证据 + 删除后验收」，禁止仅凭目录名或类名认定 Legacy。**

具体口径：

| 环节 | 要求 |
|---|---|
| **消费者证据** | `grep` **三层过滤**：代码引用 / 测试引用 / 注释引用分别统计。**注释引用不计入依赖** |
| **闭包证据** | 若目标被其它待清退文件消费，须给出**闭包图**并证明闭包可整体切除 |
| **替代证据** | 说明能力被谁取代（或明确"项目不再需要该能力"） |
| **测试证据** | 先判定测试是"测独立逻辑"还是"只测被清退类型"；后者随资产删，前者迁移 |
| **契约证据** | 检查 `ArchitectureContractTests` 是否硬编码该类型/命名空间；区分"允许"（白名单）与"要求" |
| **删除后验收** | build + test（**含预期减少量说明**）+ 四向对账 + Godot 实机 |

**本标准的必要性已被实证**：§4 记录的三处判断误差（`HexPlanet` / `CivSim` / `GameGrid`）
全部源于**没有把"代码 / 测试 / 注释"分层**——朴素 `grep` 会把注释里的类型名
当成依赖，也会把契约白名单的"允许"误读为"实际使用"。

---

*本报告 §1–§6 为纯取证产物（未改动代码）；§7 为执行后追加，§8 为方法论沉淀。*
*所有结论均附 `grep` 实测依据。*
