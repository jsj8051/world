# ADR-0001：测试基建与拆分策略决策

日期：2026-08-19
状态：已采纳
关联：docs/architecture.md（"酋邦→国家"制度化）

## 背景

项目从"酋邦"（原型，无测试/CI、超大文件）向"国家"（制度化）迁移。本机环境有两个硬约束：
1. **无外网**：nuget.org SSL 握手失败（SEC_E_NO_CREDENTIALS），只能使用 `~/.nuget/packages` 缓存。
2. **沙箱限制**：MSBuild 工作进程（命名管道）与 vstest testhost 的父进程句柄访问均被禁。

## 决策

### 1. 测试框架用 NUnit 3.14.0，不用 xunit
缓存里有 nunit/nunit3testadapter/microsoft.net.test.sdk（17.9.0），无 xunit。
版本锁定与缓存一致，离线可还原；联网环境（CI）从 nuget.org 还原同版本。
**后果**：未来升级框架需先联网，或更新缓存。

### 2. 增加零依赖本地执行器 tests/World.Tests.Local
`dotnet test` 的 testhost 在沙箱内必然失败（OpenProcess 拒绝访问），
故加一个纯控制台反射执行器，跑**同一套** NUnit 测试（[Test]/[TestCase]），
进程内执行，退出码 0/1，输出 PASS/FAIL（兼容 verify.sh 的 grep 约定）。
**后果**：CI 与正常本机用 `dotnet test`，受限环境用本地执行器；两入口共用测试源码。

### 3. 本机构建必须 `-m:1 -nodeReuse:false`
沙箱禁 MSBuild 工作进程管道，多进程构建会"0 错误 0 警告却失败"。
**后果**：本地所有 dotnet 构建命令都带此旗标（已写进 tests README；CI 环境不需要）。

### 4. 拆分超大文件用"partial class + 同目录分片"
超大文件（TectonicsSimulation/CivModels/MapViewer/CivSimDiag）拆分为
`原类名.职责.cs` 放**同目录**：`.tscn` 的脚本绑定路径不变、公开 API 不变、
行为不变（纯重构）。原文件保留类声明/字段/构造函数/公开入口。
**后果**：场景无需改动；Godot 编辑器重新导入即识别新文件。

### 5. CI 范围 = build + test + format；headless 回归留本地
CI 不下载 Godot 引擎（版本 4.7.1 无官方发布渠道保证），
`scripts/verify.sh` 继续作为本地 headless 回归大门（GODOT_EXE 指向本地引擎）。
**后果**：CI 防"编译级/单元级"回归；集成回归靠 verify.sh 手动/定时跑。
**补充（2026-08-19）**：ci.yml 中已加"自托管 runner 的 headless 回归作业模板"
（GODOT_EXE_PATH 经 secrets 注入；fast 组入默认 push，全量组用 workflow_dispatch 手动触发），
有自托管 Windows 机器时取消注释即可启用。

## 备选方案与否决理由

- **xunit**：无缓存且无网络，否决。
- **纯自定义断言框架**：放弃 NUnit 会失去 CI 标准工具链，否决。
- **移动文件到新目录**：破坏 .tscn 绑定路径，风险高，否决。
- **CI 下载 Godot headless**：版本不可得（4.7.1 非官方 stable 线上可下载），否决。
