# 裁决：scripts/Test 第三分区（测试代码归位）

> 日期：2026-10-09（承接同日的 `docs/裁决-SceneLogic两分区.md`）
> 状态：**已落地并验证**（`dotnet build` / `dotnet test` 523 PASS / `--headless` 实机读数与基线逐字段一致）
> ⚠️ **基线更新（2026-10-09 20:48）**：本裁决落地时为 **523 PASS**；随后**存档清退**（独立变更边界）删除 17 条存档相关用例，当前基线 = **506 PASS**，本裁决所涉测试项**无一被删**，结论不受影响。
> 边界声明（原则 13）：本裁决改动边界 = **测试资产的位置**，与同日 Scene/Logic 分区（改动边界 = 生产代码的位置）**分属两次变更**，应分开 commit。

---

## 1. 请求与判据

用户请求：**「`scripts` 下再加入一个 test 目录，把所有测试的东西都移过来」**。

两处口径由用户拍板：

| 维度 | 拍板 |
|---|---|
| 搬哪些 | **两摊都搬** —— 既搬仓库根的 `tests/`（三个 dotnet 项目），也搬 `scripts/Scene/Diagnostics/`（诊断场景脚本） |
| 目录名 | **`scripts/Test`**（与既有 `Scene` / `Logic` 并列的第三个中立容器） |

「中立容器」定义沿用两分区裁决：`scripts/{Scene,Logic,Test}/` 三个顶层名**不消耗 namespace 段**（同 `src/`），其下路径**逐字对应** `World.<领域>`。故：

- `scripts/Test/Diagnostics/` → `World.Diagnostics`（不变；`Test` 被吃掉）
- `scripts/Test/World.Tests/` → `World.Tests`（不变）
- `scripts/Test/PerfBench/` → `World.PerfBench`（不变）

⇒ **零命名空间改动**，纯物理移动。

---

## 2. 落点结构

```
scripts/
├── Scene/      # 场景树驱动（Node / 挂场景的哑组件 / 装配层）
├── Logic/      # 纯逻辑（不依赖场景树）
└── Test/                       ← 本次新增的第三分区
    ├── Diagnostics/            # 20 .cs（+20 .uid）诊断场景脚本 —— 由 Godot 运行时加载
    ├── World.Tests/            # 50 .cs（+50 .uid）+ World.Tests.csproj —— NUnit 单测
    ├── World.Tests.Local/      # 1 .cs（+1 .uid）+ csproj —— 受限环境本地执行器
    └── PerfBench/              # 1 .cs（+1 .uid）+ csproj —— 性能基线工具
```

（仓库根的 `tests/` 目录**整体删除**，仅剩的 obj 构建残留一并清理。）

---

## 3. ★核心设计冲突：`world.csproj` 的排除范围**不能**是整个 `scripts/Test/`

直觉做法是沿用旧规则 `<Compile Remove="tests/**/*.cs" />` 改成 `<Compile Remove="scripts/Test/**/*.cs" />`。**这是错的**，会静默打瘫全部诊断场景：

- `scripts/Test/Diagnostics/*.cs` 是**挂在 `scenes/diag/*.tscn` 上、由 Godot 运行时加载**的脚本（`DiagSceneBase` 派生类）。
- Godot 加载挂载脚本时，要求该类型**属于游戏程序集**（`world.dll`）。
- 若把整个 `scripts/Test/` 从 `world.csproj` 排除，`Diagnostics` 就不进 `world.dll` ⇒ **`--headless` 一跑 11 个诊断场景全部 `Cannot load C# script`**（而 `dotnet build` / 523 单测仍全绿 —— 典型的"编译绿、运行炸"）。

**与单元测试的本质区别**：

| | 需要进游戏程序集？ | 由谁加载 |
|---|---|---|
| `World.Tests` / `World.Tests.Local` / `PerfBench` | ❌ 不进 | 独立 dotnet 进程 |
| `Diagnostics` | ✅ **必须进** | Godot 运行时（按 `.tscn` 挂载） |

**最终排除规则**（`world.csproj`）：**只排除三个纯托管测试项目目录，`Diagnostics/` 放行**。

```xml
<Compile Remove="scripts/Test/World.Tests/**/*.cs" />
<Compile Remove="scripts/Test/World.Tests.Local/**/*.cs" />
<Compile Remove="scripts/Test/PerfBench/**/*.cs" />
<None    Remove="scripts/Test/World.Tests/**" />
<None    Remove="scripts/Test/World.Tests.Local/**" />
<None    Remove="scripts/Test/PerfBench/**" />
```

代价：新增测试项目时须**同步加一行**（已知并接受；`Diagnostics` 不需要动）。

---

## 4. 连带项清单（漏一处即静默失败）

| # | 项 | 失败症状 | 处理 |
|---|---|---|---|
| 1 | `world.csproj` 排除模式 | **不能**写 `scripts/Test/**`（见 §3） | 改三条目 |
| 2 | 三个 `.csproj` 的相对引用 | 编译期 `ProjectReference` / `None Include` 解析失败 | `..\..\` → `..\..\..\`（`World.Tests.Local` 的 `ProjectReference` 因同层仍为 `..\World.Tests\...`） |
| 3 | `world.sln` 项目路径 | 构建找不到测试项目 | `tests\World.Tests\...` → `scripts\Test\World.Tests\...` |
| 4 | **11 个 `scenes/diag/*.tscn`** | **C# 编译与 523 单测全绿，Godot 丢脚本** | `res://scripts/Scene/Diagnostics/` → `res://scripts/Test/Diagnostics/` |
| 5 | **`.cs` + `.cs.uid` 成对搬** | 断引用 | 全部成对移动；`.tscn` 只用 `path=`（无 `uid=`），路径已更新 |
| 6 | CI（`.github/workflows/ci.yml:22`） | CI 的 `dotnet test` 找不到项目 | 同步路径 |
| 7 | **测试内的"向上找仓库根"辅助函数** | 断言静默跳过 / 失败 | ↑ 见 §5 |
| 8 | 源码注释里的 `tests/` 路径 | 误导（非失败） | `H3Native.cs` / `PrecipitationModel.cs` / `ElevationMode.cs` / `PerfBench/Program.cs` / `World.Tests/README.md` |

`scripts/verify.sh`、`scripts/bench_sheet.sh` **无** `tests/` / `scripts/Diagnostics` 引用，无需改。

---

## 5. ★深度陷阱：搬深 2 层饿死了"向上找仓库根"的循环上界

三个测试辅助函数用「从程序集位置向上最多 N 层、逐层试拼相对路径」的方式定位仓库根：

- `ArchitectureContractTests.FindRepoDir`
- `CellHighlightRingTests.FindRenderSources`
- `RiverSymbolWidthTests.FindShaderFile`

它们上界都是 `i < 6`。原输出目录 `tests/World.Tests/bin/Debug/net8.0` 到仓库根**恰好 5 层** ⇒ 够用。迁到 `scripts/Test/World.Tests/bin/Debug/net8.0` 后变成**6 层** ⇒ `i < 6` 差一层，三个函数全部返回 `null`，触发 4 条断言失败（`FrameworkCompositionLayer_IsGone` / `NewWorldLine_NamespaceIsWorldGen` / `Highlight_MustNotLiftGeometryRadially` / `Width_MustUseUv2_NotColorAlpha`）。

**处理**：三处上界 `6 → 10`（留冗余），并加注释写明层数与原因。

> 教训：这类"向上试探"的路径辅助函数**上界是隐式契约**，目录搬迁若不重算层数就会静默失效。

---

## 6. ★迁移事故与修复：`World.Tests.Local.csproj` 丢了一行

执行期用了一个带 bug 的 shell 移动辅助（实参数量与函数形参不符），**误把 4 个 `.cs` 的源码写进了同名 `.cs.uid`**；虽已用 `git show HEAD:` 全部还原，但 `World.Tests.Local.csproj` 在这轮混乱中**丢失了** `<ProjectReference Include="..\World.Tests\World.Tests.csproj" />`。

发现方式：拿当前 csproj 与 `git show HEAD:tests/World.Tests.Local/World.Tests.Local.csproj` 逐行 `diff` —— 该行缺失（而 `World.Tests.csproj` / `PerfBench.csproj` 的 diff **只有**预期的 `..\..\` → `..\..\..\`，零内容丢失）。

**修复**：补回该行（`World.Tests.Local` 与 `World.Tests` 迁后仍在同层，相对路径 `..\World.Tests\World.Tests.csproj` **不变**）。补回后 `dotnet build scripts/Test/World.Tests.Local/...` 直接通过 ⇒ 反证该行必需。

> 教训：搬迁收尾必须**逐文件对 `git HEAD` 做内容 diff**，不能只看"文件在不在"。

---

## 7. 验证证据（全绿）

| 检查 | 命令 | 结果 |
|---|---|---|
| 源文件搬迁完整 | `find tests -name '*.cs' \| wc -l` | **0**（残留仅 obj 构建产物，已用原生 `rmdir /s /q` 清除） |
| 计数 | `scripts/Test/{Diagnostics,World.Tests,World.Tests.Local,PerfBench}` | 20/20 · 50/50 · 1/1 · 1/1（`.cs`/`.cs.uid` 全配对，各带 1 `.csproj`） |
| `world.csproj` 排除正确 | 手工构建 | `Diagnostics` 在 `world.dll` 内、三个测试项目不在 |
| solution 构建 | `dotnet build` | **0 警告 0 错误** |
| 单元测试 | `dotnet test scripts/Test/World.Tests/World.Tests.csproj` | **523 PASS / 0 失败** |
| PerfBench（不在 sln，手工） | `dotnet build scripts/Test/PerfBench/PerfBench.csproj` | 0 错误 |
| 本地执行器 | `dotnet build scripts/Test/World.Tests.Local/...` | 0 错误 |
| `.tscn` 脚本引用有效性 | 提取全部 `path="res://...cs"` 逐条 `-f` | **16/16 存在**，无 `uid=` 形式 |
| 诊断场景实机加载 | 11 个 `scenes/diag/*.tscn` 逐个 `--headless` | **全部 `load_errors=0`** |
| 主场景实机 | `--headless --quit-after 300 WorldGenWorld.tscn` | **零 ERROR**，`[WORLDGEN-TIMING]` 与基线**逐字段一致**（`n=288122 res=4 land=29.0% regions=95 meanP=1098 meanT=76.0C ridges=71 basins=6971 lakes=3439 thr=0.636`） |
| Godot 启动期告警 | `--headless` 全量输出过滤 `test/assembly/script/uid` | **零命中**（Godot 不为测试 `.cs` 报警） |
| CI | `.github/workflows/ci.yml` | 路径已同步 |

---

## 8. 未做 / 后续（独立变更）

- **`docs/索引.md` 等结构文档未同步**：其目录表仍停留在更早的裸目录形态（`scripts/CivSim/` / `scripts/MapView/` / `scripts/MapGen/` 等），且主场景仍写 `NoiseWorld.tscn` —— 属**更早的文档债**（先于 Scene/Logic 与 Test 两次分区），不在本轮范围。建议单列一次"文档结构同步"变更，连同 `docs/{开发规范,架构设计,功能清单与结构重规划,README}.md` 里的 `tests/` / `scripts/Diagnostics/` 引用一并处理（涉及行号已在会话记录中列出）。
- **`Logic → Scene` 禁令尚无契约测试**（当前实测 0 处，仍为口头约定）—— 见两分区裁决 §7。
- **不产生编译期隔离**：`scripts/Test/Diagnostics` 与 `scripts/Scene` 同在游戏程序集内，二者互不设防（与"Test 是资产位置、不是隔离墙"的定位一致）。
