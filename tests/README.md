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

当前状态：单测工程暂缺（原 `scripts/Test/` 已清理）；恢复时按上表落位，并同步
`.githooks/pre-commit` 与 `.github/workflows/ci.yml` 的测试路径。
