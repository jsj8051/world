# tests —— 测试与诊断代码

生产树（`scripts/`、`scenes/`）只放游戏运行所需的代码与资源；测试、诊断、基准一律放这里。

| 内容 | 约定 |
|---|---|
| 单元测试工程 | `tests/<ProjectName>/`，`world.csproj` 已排除 `tests/**` ⇒ 不进游戏程序集 |
| 回归 / 冒烟脚本 | `tests/verify.sh`（Rebuild、会话失败恢复、主世界 headless res4 单步与跳转） |
| 诊断场景 | `tests/scenes/`，不要放回 `scenes/`；脚本指 `res://tests/...` |

`tests/verify.sh` 用法：

```bash
GODOT_EXE=/path/to/godot_console.exe bash tests/verify.sh
```

当前状态：**已有 `World.Tests.Local`**（纯 C# 控制台跑器，不启引擎、不碰 H3 原生库）：

```bash
dotnet run --project tests/World.Tests.Local
```

- 默认用例使用合成网格覆盖气候、构造边界、地形、演化材料守恒及候选帧确定性。
- 全链冒烟（真 H3 网格，需原生库，不进默认表）：
  `dotnet run --project tests/World.Tests.Local -- --world 4`（打印各场读数与气候耗时）。
- 演化会话回归（需 H3 原生库）：`dotnet run --project tests/World.Tests.Local -- --evolve-session`。
  验证失败后保留快照、确认恢复、旧任务取消和播放提交。
- 山带输运诊断（res4，需 H3 原生库）：`dotnet run -c Release --project tests/World.Tests.Local -- --ridge-transport map_exports/ridge_transport_res4_audit`。
  导出单板刚体旋转 0、10、20 Ma 的测量数据与板块坐标高程栅格，附理想山带在同一网格上的采样对照。
- 启用 `WORLD_BUILD_PROFILE=1` 后，`tests/verify.sh` 会显示主场景的分段耗时、峰值工作集和材料槽容量。
- 测试工程带 `.gdignore`：Godot 不进这棵树，不生成 `.uid`。
- 退出码 0 = 全绿；`.githooks/pre-commit` 与 `.github/workflows/ci.yml` 都调同一个入口。
- 改 `tests/` 下的工程名时，同步改 `world.csproj` 的 `InternalsVisibleTo`（测试直测 internal 纯函数）。
