# ADR-0003：诊断场景统一基类 DiagSceneBase

日期：2026-08-19
状态：已采纳（增量迁移中）
关联：docs/architecture.md §3.6（宪法条文 6）

## 背景

15+ 个诊断场景各自复制"命令行参数解析（--key=value 与 --key value 两种写法）→ 运行 →
PASS/FAIL 打印 → Quit"样板（如 TectonicsTest.cs 的 40+ 行解析器）。ADR-0001 的拆分已把
`CivSimDiag` 拆成 4 分片，其中 `Check/Want/ParseSet` 仍是场景内私有实现。

## 决策

### 1. 新增 `scripts/Diagnostics/DiagSceneBase.cs`（Node 抽象基类）
- `ParseUserArgs()`：统一参数解析，同时兼容 `--key=value`（CivSimDiag 风格）与
  `--key value`/`--flag`（TectonicsTest 风格）；`--` 前缀剥离、大小写不敏感。
- `Report(name, ok, data)`：PASS/FAIL 打印（与 verify.sh 的 grep 约定兼容）。
- `Quit(code)`：headless 统一退出出口。
- 新诊断场景一律继承本类；旧场景**增量迁移**（每次迁移后跑 verify.sh 对应组回归）。

### 2. 语义差异（相对旧 TectonicsTest 解析器）
`--seed --init` 这类"值缺失 + 下一个是开关"的畸形输入：旧解析器会误吞 `--init` 当 seed 的值
并跳过它（InitOnly 不生效）；新实现把 `--seed` 记为开关值 "true"（TryParse 失败 → 无效果），
`--init` 正常生效。正常输入（verify.sh 的 `-- --n=16 --plates=6` 等）行为完全一致。

## 迁移进度

- [x] TectonicsTest（2026-08-19，首个迁移：解析块 40+ 行 → 12 行）
- [x] 12 个无参数解析场景（WindTest/TiltDiag/TempDiagnosis/CurrentDiag/SpeedDiag/RiverDiag/
      RiverCheck/RidgeDiag/PolarDiagnosis/PolarPreview/StreamlineDiag/OceanCurrentDiag）——仅换基类
- [x] 6 个带参数解析场景（CivSimDiag/EvolveCmp/MonsoonDiag/HumanLayersProbe/LogicGridDiag/
      ArchiveDiag.ResolveArchPath）——解析块统一走 ParseUserArgs（ArchiveDiag 为静态工具，
      ParseUserArgs 相应改为 public）
- ✅ 全部 18 个诊断场景已迁移；headless 冒烟验证通过（TectonicsTest/LogicGridDiag/MonsoonDiag/
      EvolveCmp 及 8 个快场景，2026-08-19）

## 行为验证（2026-08-19，git worktree 基线对照）

对重构版本与 HEAD 基线（557e3f0）分别跑 CivSimDiag 全量套件：
- regress_v9_n64.mpa：两者均为 66 PASS / 2 FAIL（T64 国家涌现、T66 国家崩溃）
- map_seed42_n120_r239.mpa：两者均为 66 PASS / 4 FAIL（T64/T66/T14 农业涌现/T21 人口梯度）
**失败集逐项一致 → 拆分与迁移零行为回归**。

### T64/T66 根因与修复（2026-08-19，commit c90ca42）

T64/T66 是构造式测试，断言写死"贡赋阈值 = 总人口×0.1"；`StateTributePerCap` 常量已于
2026-08-16 校准为 **0.01**（CivSimContext 注释有校准记录），测试未同步：反例"池 50 < 100"
实际 50 ≥ 1000×0.01=10 → 国家合法涌现 → 反例断言失败。修复：反例贡赋池降到 5（2/2/1），
注释同步校准说明。**结论：既有失败 = 过时测试常量，与模拟逻辑和重构均无关。**
T14/T21 为 n120 旧档特有问题（该图 500 tick 无农业，地图早于参数校准），非测试 bug。

## 备选方案与否决理由

- **一次性迁移全部 15 个场景**：无法在本沙箱跑 headless 验证（Godot 引擎启动被环境拦截，
  与 testhost 同类限制），大批量改动无验证风险高；改为增量迁移，否决。
- **把解析器做成实例方法**：解析是纯函数，静态更合适；Quit/Report 用实例/静态混合，
  见代码注释。
