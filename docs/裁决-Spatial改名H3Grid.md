# 裁决：`World.Spatial` → `World.H3Grid`（目录 `Logic/Spatial/Ball/` → `Logic/H3Grid/`）

日期：2026-10-09
状态：**已执行**（代码 `90dc426` / 本文件为文档侧）
触发：用户先问「spatial 什么意思」，再问「这个目录的文件能放到其他目录下吗」→ 在三个落点里选 **C（改名搬迁）**
→ 命名空间经确认取 **`World.H3Grid`**（备选曾是 `World.Ball` / `World.Grid`）
关联：`docs/裁决-Domain解散与BiomeType归Constant.md`、`docs/裁决-Services删除.md`

---

## 〇 裁决表

| 项 | 改动 |
|---|---|
| 命名空间 | `World.Spatial` → **`World.H3Grid`** |
| 目录 | `scripts/Logic/Spatial/Ball/` → **`scripts/Logic/H3Grid/`**（`scripts/Logic/Spatial/` 消失） |
| 文件 | `Ball.cs` / `BallGeoIndex.cs`（各带 `.cs.uid`）原样搬迁，**未改内容**（只改 namespace 与头部沿革注释） |
| 引用 | 在编 **56 处 / 53 文件**同步（53 个 `using` + 3 处全限定 `World.Spatial.Ball`） |
| 契约 | 新增 `H3Grid_NamespaceMatchesDirectory` —— **首条通用的「目录 ↔ 命名空间」钉** |

**一句话**：这层是"H3 球面网格本体"，不是"球"、也不该顶着与 `SpatialScale` / `FinalSpatialIndex` 易混的泛名。

---

## 一 为什么改（三条理由）

1. **命名混淆（主因）**：仓内有三处含 "Spatial" 的名字，含义完全不同——
   | 名字 | 命名空间 | 是什么 |
   |---|---|---|
   | `Ball` / `BallGeoIndex` | 原 `World.Spatial` → 现 `World.H3Grid` | **球面网格本体**（格/顶点/邻接/方向） |
   | `SpatialScale` | `World.WorldGen` | **尺度口径**（A 拓扑 hop / B 几何 km / C 物理） |
   | `FinalSpatialIndex` | `World.WorldGen` | **空间查询索引**（`nearest`/`distance`/`within`） |
   ⇒ 后两者**本来就不在**这个命名空间里，却与它同名族 —— 这正是易错点。
2. **`Ball` 名也非良名**：它实际是"H3 球面**网格**"，不是"球"。若按类名命名（备选方案 `World.Ball`），
   会把"命名空间 = 领域/职能名"的既有风格破一个例外——现行清单
   （`World.Render` / `World.WorldGen` / `World.Utils` / `World.Constants` / `World.Camera` /
   `World.Diagnostics`）**无一以单个类命名**。
3. **新名有呼应**：`World.H3Grid` ↔ `World.Utils.H3`（H3 门面与原生层），语义链一致。

---

## 二 搬迁前实测（"零风险"的依据）

| 检查项 | 实测结果 |
|---|---|
| 在编引用 | `World.Spatial` **56 处 / 53 文件**；**全部是 `using`**（+3 处全限定 `World.Spatial.Ball`，在 `World.Tests.Local/Program.cs`） |
| `.tscn` 指向 | **0 个**（`Ball` / `BallGeoIndex` **不是 Node 脚本**）⇒ **无"悬空场景"风险**（对比：删 `Services` 时这是最大风险点） |
| `scripts/_removed/` | **零引用** ⇒ CivSim 回归路径不受影响 |
| `world.csproj` | SDK 通配，**无需改动** |
| 契约钉目录 | **无**任何契约钉 `scripts/Logic/Spatial`（`ArchitectureContractTests` 的 `FindRepoDir` 只钉了 `Logic/{LogicGrid,Archive,Domain,Constant,HexPlanet,WorldGen,CivSim,Gameplay,Services}` 与 `scenes/diag`） |
⇒ 结论：**纯机械替换**——唯一可能出错的形态就是"漏改一个 `using`"，而那会**直接编译失败**（坏得响亮，不是静默）。

> ★为什么"改名"比"扁平化"（`Spatial/{Ball,BallGeoIndex}.cs`）更值得做：
> 扁平化 0 代码改动，但**不解决命名混淆**；改名要动 56 处，但**把三处 spatial 的坑填掉一个**。
> 用户拍板取改名。

---

## 三 目录层级规则（本次同时钉下）

`scripts/{Scene,Logic,Test}/` 是**命名空间中性分区**（如 `src/`，名字不参与 namespace）。
其下的规则：

- **首层** `<领域>/` **必须逐字对应** `World.<领域>`；
- **更深的子目录是自由分组**，可以进、也可以不进命名空间。

实测同构先例：`Constant/Planet/` ↔ `World.Constants`；`WorldGen/{Composition,Discretization,Features,Final,Placement,Simulation}/`
↔ 全部 `World.WorldGen`；`Utils/{Noise,Math,IO,Color,Random}/` ↔ `World.Utils`（只有 `Utils/H3/` 是真 ns 段）。

⇒ 原 `Spatial/Ball/` **完全合规**（`Ball/` 是不进 ns 的分组），所以本裁决**不是**在修一个违规，
而是在**修一个易混的名字**。搬迁后 `H3Grid/` 无子目录（两个文件平铺）。

---

## 四 契约（新增）

`ArchitectureContractTests.H3Grid_NamespaceMatchesDirectory`：

1. **正向**：`typeof(Ball).Namespace` 与 `typeof(BallGeoIndex).Namespace` 均须 = `World.H3Grid`；
2. **旧名不得复活**：程序集内不得再出现 `World.Spatial` / `World.Spatial.*` 类型；
3. **目录位置钉**：`scripts/Logic/H3Grid` 存在、且真实目录名**大小写逐字**为 `H3Grid`
   （Windows 文件系统大小写不敏感 ⇒ 必须另比 `DirectoryInfo.Name`，否则钉不住）；
   `scripts/Logic/Spatial` 须已不存在。

★**这是仓内第一条通用的「目录 ↔ 命名空间」钉**（此前只有 `NewWorldLine_NamespaceIsWorldGen`
一条，且只覆盖 WorldGen）。它防的是：**"搬了目录忘改 namespace" / "改了 namespace 忘搬目录"**——
这类脱节**编译器和单测都看不出来**（两边各自自洽），只有靠钉。

---

## 五 文档连带（本次已做）

| 文件 | 改动 |
|---|---|
| `docs/architecture.md` §9 | 命名空间清单 `World.Spatial` → `World.H3Grid`；补改名说明（含理由） |
| `docs/架构设计.md` §1 / §2 | §1 网格一行改 `World.H3Grid.Ball`；**§2「目录结构（现行）」整块按三分区重写**（原树仍在列 `HexPlanet/` `Domain/` `Archive/` `LogicGrid/` `CivSim/` `Spatial/Ball/` 等已删条目） |
| `docs/decisions/0005-natural-input-bridge.md` | 4 处（D3 邻接表对比、O3 两处、白名单讨论） |
| `docs/设计-NaturalInputBridge实现设计.md` | 5 处 + 路径 + **勘误**：原文"`Ball` / `SpatialScale` 已在此"错误——`SpatialScale` 其实在 `World.WorldGen` |
| `docs/裁决-HumanInputGrid载体契约.md` | O3 行 + 载体路径 |
| `docs/裁决-Bridge可行性裁决.md` | §3.3 标题 + 载体路径两处 |
| `Ball.cs` / `BallGeoIndex.cs` | 头部命名沿革注释（含改名理由） |
| `scripts/Test/World.Tests/README.md` | 无（未提及该命名空间） |

**未改（历史/封存文档，其"E 步 → `World.Spatial`"是当时的真实事实）**：
`docs/功能清单与结构重规划.md`、`docs/搬迁矩阵-WorldGen六子层.md`、`docs/迁移评估报告-strategy-to-world.md`、
`docs/审查报告-D-Legacy逐项决策.md`、`docs/审查报告-C4-人类演化层接线审查.md`、
`docs/newdecision/封存-HexPlanet-Goldberg闭包.md`（路径字符串）。
原因 = 原则 11「清退不范围膨胀」；它们多数已自带"⚠️ 历史"标。

---

## 六 验证（本次实测）

| 项 | 结果 |
|---|---|
| `dotnet build` | **0 警告 / 0 错误** |
| `dotnet test scripts/Test/World.Tests` | **281 PASS**（280 + 新增 1）/ 0 失败 |
| `dotnet build scripts/Test/PerfBench` | 0/0（**不在 `world.sln`**，须手工构建） |
| `dotnet build scripts/Test/World.Tests.Local` | 0/0（该工程含 3 处**全限定** `World.Spatial.Ball` 引用，是本次唯一"非 using"形态） |
| headless 世界生成 | `n=288122 res=4 land=29.0% regions=95 meanP=1098mm/年 meanT=76.0C ridges=71 basins=6971(endorheic 0) lakes=3439 thr=0.636` —— **逐字段同基线，零漂移** |
| `Cannot load C# script` / `SCRIPT ERROR` | **0 命中** |

> ★口径提醒：这类"只搬文件 + 换 namespace"的重构，**build 与单测全绿证明不了什么**（它们本来就该绿）。
> 真正的兜底是 **headless 实机读数逐字段比对**——所以本裁决把它列为必做项。

---

## 七 git 恢复（若需回滚）

```bash
git revert 90dc426     # 代码侧（含契约钉）
# 目录本身是 git mv，revert 会把它搬回 scripts/Logic/Spatial/Ball/
```

---

## 八 文档债（本次**未**修，登记在案）

- 上表"未改"的 6 份历史/封存文档仍含 `World.Spatial` 与 `Spatial/Ball/` 路径字符串。
- `docs/索引.md` 与 `docs/架构设计.md` 的**整篇陈旧**（承接 `裁决-Services删除.md` §七）——
  本次顺手把 `架构设计.md` §2 目录树修成现行，但其余章节（§5 存档三格式、§6 CivSim 保留区、
  §1 依赖白名单）仍描述已删除的东西；`docs/索引.md` 只做了与 `Services` 相关的定点标注。
- `docs/decisions/0005-natural-input-bridge.md` / `docs/设计-NaturalInputBridge实现设计.md` 的
  `World.Domain` / `BiomeType「24 类」` 残留仍在（`裁决-Domain解散与BiomeType归Constant.md` §六 已记）。
