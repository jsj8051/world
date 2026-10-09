# 裁决 · Legacy 载体簇删除（GameGrid / WildCropsSystem / MapData / FieldCodec）

> 2026-10-09 · 用户拍板：「**直接删除簇吧，目前不需要了，之后再重新实现**」
> 前置：`docs/裁决-CivSim移出.md`（同日 `a186181` 把 CivSim 移出程序集）
> 关联：ADR-0005 · `docs/裁决-Bridge可行性裁决.md`（⑤）· `docs/裁决-HumanInputGrid载体契约.md`（⑥）· `docs/设计-NaturalInputBridge实现设计.md`（⑬）

---

## §〇 裁决（一句话）

**删除 CivSim 自然输入链上的 Legacy 载体簇（4 个类型 + 2 个单测文件）；不保留副本**（与 CivSim 的「移出」不同——CivSim 是移出待回归，本簇是删除待重写）。
待自然输入桥（ADR-0005 · ⑬ `HumanInputGrid`）落地时**重新实现**。

```
⑤⑥ 的处置：清退必须与 Bridge 同批 —— 本簇是该处置的**前置清退**（Bridge 尚未开工，但簇已先失去全部生产消费者）
⑬ 的处置：`HumanInputGrid` 取代 `GameGrid`；`WildCropsSystem.Compute` 的调用方改为 Bridge 投影层
```

> ★为什么可以提前（不等 Bridge）：本簇在 CivSim 移出后**已退化为纯测试孤岛**（生产侧零消费者，见 §二）。
> 用户在此前提上选择「删除 + 之后重写」，而非「移出保留」。

---

## §一 删除清单

| 原位置 | 类型（ns） | 角色 |
|---|---|---|
| `scripts/Logic/LogicGrid/GameGrid.cs` | `World.LogicGrid.GameGrid` | Legacy 逻辑网格 = CivSim 旧空间载体（Identity/Topology/Metric 三件套） |
| `scripts/Logic/Archive/MapData.cs` | `World.Archive.MapData` | Legacy `.mpa` 存档载体定义 |
| `scripts/Logic/Archive/FieldCodec.cs` | `World.Archive.FieldCodec` | byte ↔ 物理量编解码（月温/月降水） |
| `scripts/Logic/Domain/WildCropsSystem.cs` | `World.Domain.WildCropsSystem` | 野作物位 / 野牧位派生 |
| `scripts/Test/World.Tests/LogicGridTests.cs` | 单测 | GameGrid 全量 |
| `scripts/Test/World.Tests/MapGenTests.cs` | 单测 | FieldCodec / WildCropsSystem |

**`.cs` 与 `.cs.uid` 成对删除**（12 个文件）。目录 `scripts/Logic/LogicGrid/` 与 `scripts/Logic/Archive/` 随之消失（此前各只含上述文件）。

**命名空间后果**：`World.LogicGrid` / `World.Archive` 整体消失；`World.Domain` **保留**
（`BiomeColors` / `BiomeType` / `Calendar` / `PowerPalette`），仅 `WildCropsSystem` 离开。

---

## §二 依据（实测引用图）

在 **CivSim 移出之后**（`a186181`）重算，生产侧引用链已断：

| 类型 | 生产消费者（移出前） | 生产消费者（移出后） |
|---|---|---|
| `GameGrid` | `CivEngine.Run` 注入的 grid（~90 处成员访问） | **0**（仅注释提及） |
| `WildCropsSystem` | `GameGrid.EnsureWildCrops/EnsureWildLivestock` ← `CivEngine` | **0** |
| `FieldCodec` | `WildCropsSystem`（`ByteToTemp` / `ByteToRatio`） | **0** |
| `MapData` | `GameGrid.FromMapData` / `ToMapData` ← CivSim 读档 | **0** |

⇒ `GameGrid → WildCropsSystem → FieldCodec`（+ `MapData`）整簇**仅剩单测在调**。
按永久原则 1 第三问（「现在有真实消费者吗？」）：**否**。

> ⚠️ 注意 ⑤/⑥ 裁决曾以「`GameGrid` 有 **21 个生产消费者**」为由禁止提前清退——
> 该口径成文于 **2026-10-06/07**，**早于 CivSim 移出（2026-10-09）**，其消费者已全部离开程序集
> ⇒ **论据过期**。设计结论（Bridge 取代 `GameGrid`）不变，但「不能删」的前提已不成立。

---

## §三 连带改动

| 文件 | 改动 |
|---|---|
| `Test/World.Tests/ArchitectureContractTests.cs` | 契约 `RetainedDomainConsumers_OnlyDependOnAllowedNamespaces`（白名单守卫，被扫消费者只有这两个类型）⇒ **改写为结果钉** `LegacyCarrierCluster_IsDeleted`（命名空间消失 + `WildCropsSystem` 消失 + 目录位置钉）。先例：`CivSimAndGameplay_AreMovedOut` |
| 同上（文档注释） | `LegacyWorldGenerationChain_IsGone` 的「保留清单」与 `FinalWorldFacts_OnlyConsumedByWorldGen` 的「私接方」注释同步 |
| `Test/World.Tests/ServicesTests.cs` | 删 3 个死 `using`（`HexPlanet`/`LogicGrid`/`Archive`）+ 2 个**从未被调用**的私有助手 `BuildMapData` / `Has` |
| `Logic/HexPlanet/SphereGrid.cs` | 删死 `using World.Archive;`（命名空间消失后为编译错误） |
| `Test/World.Tests/README.md` | 两行覆盖表标记为已删 |

**未动**（变更边界不同）：
- ⑤⑥⑬ 裁决文档正文（**已按纪律不改**——它们是带日期的决策记录，「论据过期」已在本文记录）；
- `World.Tests/README.md` 的其余腐化（多行仍列已删的 CivSim*/Climate*/Tectonics 测试）——属**独立文档债**。

---

## §四 验证

| 项 | 结果 |
|---|---|
| `dotnet build` | **0 错误** |
| `dotnet test scripts/Test/World.Tests/World.Tests.csproj` | **308 PASS / 0 FAIL**（345 − 37 删掉的用例） |
| `dotnet build scripts/Test/PerfBench/PerfBench.csproj` | **0 错误**（不在 `world.sln`，须手工验） |
| headless 世界生成 | 读数与基线**逐字段一致**：`n=288122 res=4 land=29.0% regions=95 meanP=1098 meanT=76.0C ridges=71 basins=6971 lakes=3439` ⇒ **零漂移** |
| `Cannot load C# script` | 无 |

---

## §五 重新实现时的入口（将来）

本簇是 CivSim 自然输入链的**输入侧**。重新实现时的正确形态**不是**恢复这四个文件，而是：

```
WorldGen 事实层 ──Bridge 投影层──> HumanInputGrid（World.Bridge）──> CivSim
```

- `GameGrid` 的角色 → **`HumanInputGrid`**（⑥ 已定：Identity = H3 res4 CellId / Topology = GridDisk(k=1) / Metric = `SpatialScale`）；
- `WildCropsSystem.Compute` 算法可复用 → 但**调用方改为 Bridge 投影层**（⑬ §3.3.1），结果写入载体；
- `MapData` / `FieldCodec` → Bridge 落地后由**新存档格式**取代（不再读 Legacy `.mpa`）。

⇒ 恢复点 = **⑬ Bridge 的 B0/B1**（见 `docs/设计-NaturalInputBridge实现设计.md`）。

---

## §六 git 恢复

四个源文件与两个单测均**已入库**，可随时取回：

```bash
# 本裁决对应的删除 commit（见 git log）
git log --oneline -1 -- scripts/Logic/LogicGrid/GameGrid.cs
# 取回任一文件（不改动工作区其它部分）
git checkout <删除前的commit>^ -- scripts/Logic/LogicGrid/GameGrid.cs
```

> 注意：取回后**契约 `LegacyCarrierCluster_IsDeleted` 会立刻变红**——这是有意设计（删除是显式决策，回归也须显式）。

---

## §七 后续（同日）：连带孤儿清理

本簇删除后暴露出 **3 个零消费者孤儿**（它们的消费者分别是被删的本簇成员 / 已移出的 CivSim / 已删的诊断场景）。
用户当日指示「删」，已**另行删除**（独立 commit `a6fb682`，按永久原则 13 分开提交）：

| 文件 | 原消费者 |
|---|---|
| `Logic/Constant/Planet/PlanetConstants.cs` | `MapData`（本簇，已删） |
| `Logic/Diagnostics/PerfLog.cs` | `CivEngine`（随 CivSim 移出） |
| `Logic/HexPlanet/SphereGrid.cs` | 已删的诊断场景 |

⇒ `scripts/Logic/Diagnostics/` 目录随之消失；`Logic/Constant/Planet/` 保留 `Thermal.cs`；
`Logic/HexPlanet/` 保留 `Icosahedron.cs`（`HexPlanetTests` 在用）。

★连带解决了**待办③**：`PlanetConstants.EarthRadiusKm` 与 `SphericalFbmNoise.EarthRadiusKm` 的重复
⇒ 重复源已移除，常量现只存于 `SphericalFbmNoise`。
