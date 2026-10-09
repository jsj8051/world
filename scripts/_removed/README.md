# scripts/_removed —— 已移出但**保留**的代码

本目录存放**已从编译中移出、但按用户决定「等待正确时机回归」的代码**。
它们**不是删除**：文件、`.cs.uid` 与目录结构原样保留，只是被 `world.csproj` 整体排除编译
（`<Compile Remove="scripts/_removed/**/*.cs" />`）。

> 判定口径：**移出** = 移出编译面但留在仓库内（本目录）；**删除** = 连文件一起去掉（走 git 历史）。
> 本目录只放前者。

---

## 当前内容（2026-10-09）：CivSim 全闭包

用户拍板：**「将 civsim 移出去，等待正确时机回归，现在不需要」**。
范围 = CivSim + 其**直接依赖**（直接 `using World.CivSim` 或只服务 CivSim 的资产）。

| 原位置 | 现位置 | 内容 |
|---|---|---|
| `scripts/Logic/CivSim/` | `scripts/_removed/Logic/CivSim/` | CivSim 本体 **60 个 .cs**（Engine / Entities / Mechanics / Tables / Policies / Concepts / Events / Observation / Support） |
| `scripts/Logic/Gameplay/` | `scripts/_removed/Logic/Gameplay/` | 玩家命令层 **3 个 .cs**（PlayerCommand / PlayerCommands / PlayerSession）——直接 `using World.CivSim`，唯一消费者是 `CivEngine` |
| `scripts/Test/Diagnostics/CivSimDiag.cs` + `.Builders.cs` + `.Scenarios.cs` | `scripts/_removed/Test/Diagnostics/` | CivSim 诊断场景脚本 **3 个** |
| `scenes/diag/CivSimDiag.tscn` | `scripts/_removed/scenes/diag/` | 诊断场景文件（已无脚本可指） |
| `CivEventTests` / `CivOverlayTests` / `CivSimMechanicTests` / `CivSimMechanics2Tests` / `CivSimModelTests` / `CivSimWarTests` / `ConceptRegistryTests` / `PlayerCommandsTests` / `StateMechanismTests` | `scripts/_removed/Test/World.Tests/` | CivSim 单测 **9 个 .cs** |

**留在原位**（有非 CivSim 消费者，或属自然层/基础设施，故**不在**移出范围）：
`Logic/LogicGrid/GameGrid.cs`、`Logic/Domain/WildCropsSystem.cs`、`Logic/Archive/{MapData,FieldCodec}.cs`、
`Logic/Diagnostics/PerfLog.cs`、`Logic/HexPlanet/`。

> ★2026-10-09 更新：上面这 5 项**其后已全部删除**（Legacy 载体簇 `4a7cbc2` + 孤儿 `a6fb682` +
> `Icosahedron` `a8495f9`）——"留在原位"只是**移出当时的**状态记录，不是现行事实。

**连带改动**（详见 `docs/裁决-CivSim移出.md`）：
- `world.csproj` 加两条 `scripts/_removed/**` 排除；
- `Test/World.Tests/ArchitectureContractTests.cs`：`World.CivSim` / `World.Gameplay` 从白名单与
  被扫消费者移除，原 `CivSim_DoesNotReferenceWorldGen`（方向守卫）改写为
  `CivSimAndGameplay_AreMovedOut`（结果 + 位置钉）；
- 三处仅剩 `using World.CivSim*` 的**死 using** 删除：`DeterministicRandomTests` / `BiomeTests` / `LogicGridTests`；
- `Logic/Archive/MapData.cs`：删去引用 `World.CivSim.CivSimResult` 的 `Civilization` 字段（本就零读写）;
- `scripts/verify.sh`：移除 `CivSimDiag` 回归组；
- `.github/workflows/ci.yml`：`perf-baseline` 作业（以 `CivSimDiag.tscn --only=T40` 为载体）随宿主移出而停用。

---

## 回归（恢复）操作

```bash
# 1) 目录移回
git mv scripts/_removed/Logic/CivSim   scripts/Logic/CivSim
git mv scripts/_removed/Logic/Gameplay scripts/Logic/Gameplay

# 2) 诊断脚本 / 场景 / 单测逐项移回原位（按上表）
git mv scripts/_removed/Test/Diagnostics/CivSimDiag.cs scripts/Test/Diagnostics/
#   （.Builders.cs / .Scenarios.cs / *.uid 同理）
git mv scripts/_removed/scenes/diag/CivSimDiag.tscn scenes/diag/
#   （9 个单测 .cs + .uid 同理）

# 3) 删掉 world.csproj 里 `scripts/_removed/**` 的两行排除

# 3.5) ★2026-10-09 新增依赖：`scripts/Logic/Services/` 必须**一并恢复**
#      （CivSimDiag.cs 用 LogService；Logic/CivSim/Tables/TechTable.cs 用 LogService）
#      —— 从 `git show 20e9d96^:scripts/Logic/Services/...` 取回，见 docs/裁决-Services删除.md §五。
#      `Test/Diagnostics/DiagSceneBase.cs` **未删**，无需恢复；`scenes/diag/CivSimDiag.tscn` 按上表移回即可。

# 4) 恢复 ArchitectureContractTests.cs：白名单行 `"World.CivSim"` / `"World.Gameplay"`、
#    消费者 `typeof(World.CivSim.CivSimContext)`，并把 CivSimAndGameplay_AreMovedOut
#    换回方向守卫（见 commit 0a1408e 的版本）

# 5) 验证
dotnet build world.csproj && dotnet test scripts/Test/World.Tests/World.Tests.csproj
```

> ⚠️ 移回后建议跑一次 `godot --headless --import`，让 Godot 校正 `.cs.uid` 与场景引用。
