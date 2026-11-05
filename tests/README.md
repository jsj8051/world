# tests —— 测试与诊断代码

生产树（`scripts/`、`scenes/`）只放游戏运行所需的代码与资源；测试、诊断、基准一律放这里。

| 内容 | 约定 |
|---|---|
| 单元测试工程 | `tests/<ProjectName>/`，`world.csproj` 已排除 `tests/**` ⇒ 不进游戏程序集 |
| 回归 / 冒烟脚本 | `tests/verify.sh`（Rebuild + 主世界 headless 冒烟） |
| 诊断场景 | `tests/scenes/`，不要放回 `scenes/`；脚本指 `res://tests/...` |

`tests/verify.sh` 用法：

```bash
GODOT_EXE=/path/to/godot_console.exe bash tests/verify.sh
```

当前状态：**已有 `World.Tests.Local`**（纯 C# 控制台跑器，不启引擎、不碰 H3 原生库）：

```bash
dotnet run --project tests/World.Tests.Local
```

- 12 条用例覆盖气候链内核（合成球面网格，纯托管）：年基温随纬度下降、零倾角无季节、
  南北半球季节反相、零倾角无季风、年降水 = Σ 月、ITCZ 随太阳北移、内陆摆幅大于海岸、
  纬度剖面没有台阶（防极圈硬边）、风场切向且有界、洋流只在海上且切向、湿润降温、
  降水估计向内陆衰减。
- 全链冒烟（真 H3 网格，需原生库，不进默认表）：
  `dotnet run --project tests/World.Tests.Local -- --world 4`（打印各场读数与气候耗时）。
- 测试工程带 `.gdignore`：Godot 不进这棵树，不生成 `.uid`。
- 退出码 0 = 全绿；`.githooks/pre-commit` 与 `.github/workflows/ci.yml` 都调同一个入口。
- 改 `tests/` 下的工程名时，同步改 `world.csproj` 的 `InternalsVisibleTo`（测试直测 internal 纯函数）。
