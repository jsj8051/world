# ADR-0004：日志全量收编 LogService（分层豁免与例外清单）

日期：2026-08-19
状态：已采纳（实施完成）
关联：ADR-0002（服务层设计）；docs/architecture.md §3 条文 8、§6、§8

## 背景

ADR-0002 决定服务层 LogService 只迁移"明显调用点"，其余约 274 处 `GD.Print/GD.PrintErr`
（`[标签]` 前缀）留待批量收编（architecture.md §6 曾记为此项未完成）。
本 ADR 完成该遗留项，并解决批量收编暴露出的两个架构问题：

1. **L1 系统层依赖问题**：Tectonics/MapGen/CivSim 等 L1 层有大量日志打印。按宪法
   条文 1"依赖只向下"，L1 引用 L2 的 LogService 属反向引用——全量收编必须豁免。
2. **L0 纯模型依赖问题**：`Tribe.cs` 残留一处 `Godot.GD.Print`（"[临时调试]"），
   L0 禁止依赖 Godot 输出，该残留直接删除。

## 决策

### 1. 全量迁移范围

- **L3（MapView/UI/Diagnostics）+ L2（Services）+ L1（生成/模拟系统）**：
  带 `[标签]` 前缀的 `GD.Print/GD.PrintErr` 全部迁为 `LogService.Log/LogErr`（标签提为参数）。
- **L0（纯模型）**：不迁移、不打印。`Tribe.cs` 的 `[PopMerge调试]` 残留删除
  （该检测已由 S3 场景/ValidateInvariants 覆盖，打印无存在价值）。

### 2. 依赖规则修订（architecture.md §3 新增条文 8）

L1 系统层允许调用 `LogService.Log/LogErr`——日志是横切关注点，收编收益（统一入口、
格式一致、未来可换后端）大于形式上的分层纯度；此豁免**仅限日志**，
EventBus/ArchiveService 等仍按 L2 使用（L1 不依赖）。L0 保持禁止。

### 3. 断言输出例外（verify.sh 解析耦合）

`DiagSceneBase.cs` 的 PASS/FAIL 行与 `CivSimDiag.cs` 的 T 测试汇总行**保持 GD.Print 直调**：
输出格式 `  FAIL T…`（两空格缩进）被 `scripts/verify.sh` 与 CI 作业的
`grep -E "^  FAIL|FAIL T[0-9]"` 解析，加标签前缀会静默破坏失败检测。
这两处本质是测试断言输出而非日志，不迁移。

### 4. 后台线程例外（LogService 纪律）

后台线程（`Task.Run`/`ContinueWith` 回调、演化线程）内的低频错误打印**保持 GD.Print 直调
+ `// 后台线程` 注释**：LogService 纪律"后台线程禁止调用"不变（ADR-0002），
迁移不改变违规事实、只会在代码审查中混淆责任。高频路径的 `log:false` 参数模式照旧。

### 5. 无标签输出补标签

少量无 `[标签]` 前缀的输出（如 `GD.Print(sb.ToString())`、缩进续行）按文件归属补
对应标签迁移；CLICK 诊断的缩进续行保留 GD.Print 直调（非日志，是交互诊断的格式续行）。

## 验证

- `dotnet build`（pre-commit 门槛）+ `dotnet format`（CI 校验）通过；
- 本机 headless 回归（verify.sh 等价四组：TectonicsTest n16 / LogicGridDiag n64 往返 /
  MonsoonDiag n64 / CivSimDiag T 全套）全绿；
- 日志输出格式不变（`LogService` 内部即 `GD.Print($"[{tag}] {message}")`）。

## 否决项

- **LogService 下沉到 L0/L1**：其内部依赖 Godot 的 `GD.Print`，与 L0"纯 C# 可单测"冲突；否决。
- **L0 引入日志抽象接口**：单点日志不值得为 L0 造抽象层；L0 根本不应打印；否决。
- **后台线程安全队列**：沿用 ADR-0002 否决理由（低价值高复杂度）；否决。
