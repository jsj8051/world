# ADR-0002：服务层设计（EventBus / LogService / ArchiveService）

日期：2026-08-19
状态：已采纳（实施中）
关联：docs/architecture.md §2 L2 层；ADR-0001

## 背景

架构文档把"场景之间禁止直接互访"列为宪法条文（§3.3）。盘点结果：
- 跨场景耦合点很少：唯一明显的"酋长直令"是 `ViewerLauncher.PendingPath`（MapGenMenu → MapViewer 静态传值）。
- 真正散落的是 274 处 `GD.Print/GD.PrintErr`（带 `[标签]` 前缀），且已有"后台线程禁止 GD.Print"的纪律注释。
- 存档入口散落在 MapArchive / CivMapArchive / GameMapArchive 三个静态类，路径拼写在调用点。

## 决策

### 1. EventBus（跨场景事件）
静态类 `World.Services.EventBus`，C# `event`/`Action` 驱动（Godot 信号不适合非 Node 通信）：
- `MapViewRequested(string path)` —— 替代 `ViewerLauncher.PendingPath`（MapGenMenu 发布，MapViewer 订阅）。
- `GenerationProgress(float)` / `GenerationFinished(bool, string)` —— 后台线程写、主线程读的模式已有
  （volatile + _Process），事件只做解耦通知，不承担线程安全。
**迁移范围**：`ViewerLauncher` 删除，MapGenMenu/MapViewer 改用事件。

### 2. LogService（日志收编）
静态类 `World.Services.LogService`：
- `Log(tag, message)` / `LogErr(tag, message)` → 内部仍调 GD.Print/GD.PrintErr。
- 纪律不变：**后台线程禁止调用**（沿用现有 log:false 参数模式，不强制线程安全队列——
  为低价值高复杂度，暂不引入）。
**迁移范围**：只迁移"明显调用点"（菜单类/服务层新代码），不搞 274 处全量替换
（全量替换是纯机械噪声，留待有空时批量做；架构文档 §6 记为此项未完成状态）。

### 3. ArchiveService（存档统一入口）
静态类 `World.Services.ArchiveService`，封装 MapArchive/CivMapArchive/GameMapArchive：
- `MapPath(seed, n, radiusKm)` —— 统一 `user://maps/map_seed{n}_n{n}_r{r}.mpa` 命名（现散落在 MapGenMenu）。
- `LoadMap(path)` / `SaveMap(map, path, log)` / `LoadCiv(path)` / `SaveCiv(...)` —— 薄包装。
- 版本/损坏校验语义保持在各 Archive 类内部（不动）。
**迁移范围**：MapGenMenu 的路径拼写、菜单类的读档入口。

## 否决项

- **Autoload 单例（Godot Node）**：服务层无节点生命周期需求，静态类更轻、可测试；否决。
- **依赖注入容器**：项目规模不需要，静态定位器足够；否决。
- **日志线程安全队列**：Godot 的 GD.Print 后台调用问题是既有纪律已覆盖，队列化是过度设计；否决。
