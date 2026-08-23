# world 项目架构文档（"酋邦→国家"制度化 · v1.0）

> 本文档是项目的"宪法"：定义分层、依赖规则与工程纪律。
> 新代码违反本文件即视为架构债，review 时一票否决。
> 更新本文档需走 ADR（`docs/decisions/`）。

## 1. 项目是什么

Godot 4.7.1（C# / .NET 8）程序化行星生成 + 文明演化模拟（v0.5.0）。
流程：板块构造 → 气候/洋流/季风 → 生物群系 → 河流 → 文明模拟（部落→酋邦→国家），
全部在六边形球面（Goldberg 多面体，`scripts/HexPlanet/`）上**确定性**运行。

## 2. 四层架构

```
L3 视图/交互层（Godot Node）
    scripts/MapView · scripts/UI · scripts/Camera · scripts/Diagnostics
        │  只调用 L1 公开 API 与 L2 服务；UI 逻辑不得下沉
L2 服务层（全局基础设施，跨场景通信的唯一通道）
    EventBus · LogService · ArchiveService · SettingsService（GameServices）
        │  只依赖 L1/L0
L1 生成/模拟系统层（确定性、无 UI、可 headless）
    TectonicsSimulation · ClimateGenerator · RiverSystem · MonsoonSystem ·
    OceanCurrent · MineralSystem · SoilSystem · WildCropsSystem ·
    PlanetPipeline · MapGenerator · CivEngine · CivSimContext
        │  只依赖 L0
L0 纯模型/数学层（不依赖 Godot 节点；纯 C# 优先，可直接单元测试）
    HexPlanet（GoldbergBuilder/HexTile/Icosahedron/SubdividedMesh）
    LogicGrid（GameGrid/ArchiveLayout/GameMapArchive）
    CivSim 纯模型（CivModels/DeterministicRandom/CommodityTable/TechTable/
                   CapabilityTable/Habitation/Polity）
    MapGen/Model（ClimateFields/PipelineFields/ClimateLoops/ModelBase）
    Tectonics 纯数学（SphereGrid/FieldOps）
```

## 3. 依赖规则（宪法条文）

1. **依赖只向下**：L3→L2→L1→L0。反向引用（如模型里出现 UI、系统里访问场景树）禁止。
2. L0/L1 不得引用 Godot 节点、`SceneTree`、UI 类型；允许 Godot 数学类型（`Vector3` 等），
   纯 C# 更佳——可测试性是硬指标。
3. **场景之间禁止直接互访**（跨场景 `GetNode` 直调、单例互指）；一律经 L2 服务或 C# 事件。
4. 存档统一走 `ArchiveService`；`.mpa/.cmp` 格式与随机状态入档规则不许散落各处。
5. **单类单文件**；拆大类用 `partial` 分片，新文件放**同目录**、命名 `原类名.职责.cs`
   （.tscn 的脚本绑定路径不变，移动文件必须同步改场景引用并验证）。
6. 诊断场景统一继承 `DiagSceneBase`（行星搭建/截图/参数面板在基类，子类只写"测什么"）。
7. 一切随机性走 `DeterministicRandom`；禁止裸 `System.Random` 实例与时间种子。
8. **日志统一走 `LogService.Log/LogErr`**（ADR-0004）：L1 系统层允许调用（日志为横切关注点，
   L1→L2 的此项依赖是唯一豁免）；L0 纯模型禁止打印（调试残留一律删除，不依赖 Godot）；
   后台线程禁止调用（低频错误打印保留 GD.Print 直调 + 注释，见 ADR-0004 §决策 4）。

## 4. 确定性纪律（本项目的命根子）

- 生成/模拟全部由 `DeterministicRandom` 驱动，状态可序列化（SplitMix64）。
- **读档续跑 = 从存档随机状态继续消耗，与从头跑 N+M ticks 完全一致**（防分叉）。
- 回归双保险：`scripts/verify.sh` 四组 headless 回归 + `docs/screenshots/` 黄金截图对比。

## 5. 工程质量（"国家机器"）

- **提交门槛**：`.githooks/pre-commit`（build + 单元测试；安装 `git config core.hooksPath .githooks`）。
- **测试**：`tests/World.Tests`（NUnit，见 ADR-0001）。L0 纯模型与 L1 系统的不变量必须进测试，
  不变量包括：同 seed 同输出、边界合法、存档往返一致。
- **规范**：`.editorconfig` + `dotnet format`（CI 强制校验）。
- **CI**：GitHub Actions —— `dotnet build` → `dotnet test` → `dotnet format --verify` →
  Godot `--headless` 冒烟。
- **决策记录**：`docs/decisions/*.md`（ADR），记录"为什么"，不是"做了什么"。

## 6. 现状对照表（建国进度 v2）

| 条目 | 状态 | 说明 |
|---|---|---|
| 分层文档 | ✅ | 本文档 + ADR-0001~0004 |
| 单元测试项目 | ✅ | `tests/World.Tests`（NUnit 48 用例）+ 本地执行器 `World.Tests.Local` + pre-commit 门槛 |
| 服务层 | ✅ | EventBus / LogService / ArchiveService（ADR-0002） |
| CI | ✅ | GitHub Actions `build+test+format` + T40 性能基线作业（workflow_dispatch，自托管 runner）；headless 回归由本地 `scripts/verify.sh` 承担（本机已跑通） |
| 诊断场景统一 | ✅ | 18 个诊断场景全迁 DiagSceneBase（ADR-0003） |
| 重复场景 | ✅ | 已删重复 MainMenu |
| 超大文件 | ✅ | TectonicsSimulation（6 分片）/ CivModels（21 文件）/ MapViewer（3 分片）/ CivSimDiag（4 分片） |
| GD.Print 收编 | ✅ | 全量迁移 LogService（L3/L2/L1；断言输出与后台线程直调例外，ADR-0004） |

## 7. 命名与目录约定

- 命名空间：`World.<领域>`（CivSim / Tectonics / Biome / MapGen / HexPlanet /
  MapView / LogicGrid / Diagnostics / UI / Camera）。
- 文件名 = 类名；`partial` 分片用 `原类名.职责.cs` 后缀，放同目录。
- 场景放 `scenes/<层>/`，脚本放 `scripts/<领域>/`，一一对应。

## 8. 迁移路线（进度勾选）

- [x] ① 立宪：本文档
- [x] ② 建军：tests/ 项目 + 首批测试（48 用例全绿）
- [x] ③ 拆酋长：✅TectonicsSimulation（6 分片）→ ✅CivModels（21 模型文件）→ ✅MapViewer（3 分片）→ ✅CivSimDiag（4 分片）
- [x] ④ 国家机关：服务层 scripts/Services/（EventBus 替代 ViewerLauncher、LogService、ArchiveService）——ADR-0002
- [x] ⑤ 收税与官僚：GitHub Actions CI（build+test+format；headless 回归由本地 verify.sh 承担，见 ADR-0001）
- [x] ⑥ 裁并重复：✅ DiagSceneBase（全部 18 个诊断场景已迁移，ADR-0003）+ ✅ 删重复 MainMenu
- [x] ⑦ 日志收编：✅ GD.Print 全量迁移 LogService（ADR-0004；断言输出/后台线程直调例外）+ ✅ 本机 headless 回归跑通

> 红线：每次提交可编译可运行；重构期间不加新功能；一次只拆一个文件。
