# tests/（国家常备军）

NUnit 单元测试项目，引用 `world.csproj`（游戏程序集）。

## 测试范围（重要）

- ✅ 只测 **L0 纯模型/纯函数**：不触碰 Godot 原生调用（`Vector3` 原生方法、`FileAccess`、
  节点树等），因此可直接 `dotnet test` 运行，无需 Godot 引擎。
- ✅ 确定性不变量：同 seed 同序列、读档续跑无分叉（`DeterministicRandom` 状态往返）。
- ❌ 不做重集成测试：headless 回归（构造全流程、存档往返）由 `scripts/verify.sh` 承担——
  那是既有体系，测试项目不与它重复。

## 运行

CI / 正常本机环境：

```bash
dotnet test tests/World.Tests/World.Tests.csproj
```

受限/离线环境（无法启动 vstest testhost 或访问 nuget.org）——用本地执行器跑**同一套**测试：

```bash
dotnet build tests/World.Tests.Local/World.Tests.Local.csproj -m:1 -nodeReuse:false -p:NuGetAudit=false
dotnet run --project tests/World.Tests.Local/World.Tests.Local.csproj --no-build
```

> 两个坑（2026-08 实测）：
> 1. 本机只有 .NET 6/10 运行时，无 8.0 → 本地执行器 csproj 已设 `RollForward=LatestMajor`；
>    `dotnet test` 路径可临时 `$env:DOTNET_ROLL_FORWARD='LatestMajor'`。
> 2. 受限沙箱禁 MSBuild 工作进程管道 → 构建必须 `-m:1 -nodeReuse:false`。

> NuGet 包版本（NUnit 3.14.0 / NUnit3TestAdapter 4.5.0 / Test.Sdk 17.9.0）
> 与本机 `~/.nuget/packages` 缓存一致，离线可还原；联网环境从 nuget.org 还原同版本。

## 约定

- 新测试必须能回答"这个函数保证什么"，而不是"这个函数怎么算的"（避免测试复刻实现）。
- 改公式/查表（如 `InfluenceWeight`）时必须同步改本项目的断言——查表值即契约。
