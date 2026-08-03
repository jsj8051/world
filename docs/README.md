# docs 文档索引

> 中世纪 4X 国家策略 · Godot 4.7.1 mono · .NET 8 · C# 12
> 生成与游玩解耦（地图存档，参考 DF/Civ）；双层架构：纹理层 25km² 观感 + 逻辑网格 n=64~128 模拟。

## 必读

| 文档 | 内容 |
|---|---|
| [架构设计.md](架构设计.md) | 总体架构、目录结构、模块职责、生成管线 5 阶段、存档 v3、10 图层、物理模型、性能基线、技术决策记录 |
| [开发规范.md](开发规范.md) | 命名/目录/git 约定、headless 验证流程、性能红线、陷阱清单 |

## 参考与历史

| 文档 | 内容 |
|---|---|
| [tectonics-port.md](tectonics-port.md) | ⚠️ 历史：tectonics.js → C# 移植方案（2026-08-02；"等距柱状采样"段已过时，现为 v3 球面直通） |
| [tectonics-ref/](tectonics-ref/) | 原版 tectonics.js 源码参考（58 文件，CC-BY-4.0，含原 LICENSE） |
| [screenshots/](screenshots/) | 各阶段验证截图（侵蚀前后对照、海拔色带、洋流环、biome 等，40 张） |

## 快速入口

- 主场景：`res://scenes/core/MainMenu.tscn`（project.godot main_scene）
- 生成器：`res://scenes/core/MapGen.tscn`（headless：`--headless --quit-after 400`）
- 查看器：`res://scenes/core/MapViewer.tscn`（键盘 1-0 切 10 图层）
- 诊断场景：`res://scenes/diag/`（13 个，全部 headless 可跑）

> 注：`docs/` 含 `.gdignore`（Godot 不导入本目录）；截图只作记录，非游戏资源。
