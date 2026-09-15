# docs 文档索引

> 中世纪 4X 国家策略 · Godot 4.7.1 mono · .NET 8 · C# 12
> 生成与游玩解耦（地图存档，参考 DF/Civ）；双层架构：纹理层与逻辑网格同口径（每格 5 km²，用户选星球半径，n 派生）
> 2026-08-23 整理：删除已完全取代的历史文档（文明演化v1/阶段2 重构/派生状态架构化）；
> 实体命名现状：**Polity**（政体）/ **Habitation**（聚集地）——旧文中的"部落/Tribe/Settlement 类"为历史名词。

## 必读（现状文档）

| 文档 | 内容 |
|---|---|
| [架构设计.md](架构设计.md) | 总体架构、目录结构、模块职责、生成管线、存档层（.mpa v9/.cmp v17/.gmp v3 段表）、技术决策记录 |
| [architecture.md](architecture.md) | 历史架构演进记录（2026-08 重构基线） |
| [开发规范.md](开发规范.md) | 命名/目录/git 约定、headless 验证流程、性能红线、陷阱清单 |
| [索引.md](索引.md) | **代码索引**：按目录列出脚本/场景/着色器/数据/插件的职责、关键类与交互 |
| [存档段表格式设计.md](存档段表格式设计.md) | 段表容器（ChunkWriter/ChunkReader）、布局、版本判定（.mpa v9 / .cmp v17 / .gmp v3）、验证配方 |

## 世界生成（new_HexWorld —— H3 六边形新世界）

| 文档 | 内容 |
|---|---|
| [设计-new_HexWorld-地壳运动.md](设计-new_HexWorld-地壳运动.md) | **入口（唯一权威，v12）**：Crust 六场与常量一次定义、架构分层（逻辑层 + UI 层 MVVM 地图模式体系）、地形目标清单、系列目录 |
| [设计-new_HexWorld-地壳运动-01-板块运动与属性平流.md](设计-new_HexWorld-地壳运动-01-板块运动与属性平流.md) | 01 初始地壳模板静态生成（v1.3）：抽种归并分板、海陆 = 板属性两级模板、海拔初值、边界几何派生口（**文件名遗留**，内容已重写为静态版） |
| [设计-new_HexWorld-地壳运动-02-板块运动与地形要素生成.md](设计-new_HexWorld-地壳运动-02-板块运动与地形要素生成.md) | 02 运动学与地形（v1.2）：欧拉极 Ω、逐边两遍分类（汇聚/离散/转换/惰性）、洋底热沉降（离脊距离→年龄→深度）、三类边界地形（造山 / 海沟+弧 / 洋脊·裂谷·被动陆缘）、写场机制纪律、参数表与测量项；**v1.2**：陆-洋汇聚改走造山带（岛弧只留洋-洋）、形态判读口径改 res4 起；**§11A** 登记「大陆架须水位」空洞（水量守恒另立项） |

## 人文层设计（阶段设计——现行机制）

| 文档 | 内容 |
|---|---|
| [阶段3设计-聚集地.md](阶段3设计-聚集地.md) | **Habitation 实体 + 功能定性**：村庄/集镇/城市 = 职能条件（HasAdmin/Market/Ritual），v17 存档；camp 形态占位 |
| [阶段3设计-贸易机制.md](阶段3设计-贸易机制.md) | 物物交换（Order 55）：比较优势出口、领接触、市场条件（商路节点→集镇） |
| [阶段3设计-存储衰变机制.md](阶段3设计-存储衰变机制.md) | 随身池/粮仓（Polity.Stocks + Habitation.Stocks）、存储容量、衰变 |
| [阶段4设计-国家涌现.md](阶段4设计-国家涌现.md) | 酋邦→国家：四条件涌现（都城 IsCity / 次级中心 IsMarketTown / 贡赋 / 存续）、机制差异、崩溃 |
| [阶段5设计-军事征服.md](阶段5设计-军事征服.md) | 战争=外交状态：宣战资格、会战、吞并/割地、断交；城市要塞（IsCity） |
| [阶段6设计-古典时代机制.md](阶段6设计-古典时代机制.md) | 古典时代 5 机制解锁器（writing/coinage/standing_army/law_code/sail）——设计稿 |

## 环境与反馈

| 文档 | 内容 |
|---|---|
| [气候反馈环.md](气候反馈环.md) | 气候子系统的反馈回路设计（有效） |
| [自然因素影响图谱.md](自然因素影响图谱.md) | 自然因素 → 人文影响的映射图谱 |

## 历史档案（保留参考——语义不再同步当前代码）

| 文档 | 内容 |
|---|---|
| [石器时代设计.md](石器时代设计.md) | ⚠️ 历史：石器时代（游群/采集/狩猎/科技）领域设计总览——"Tribe"为旧实体名，现为 Polity |
| [tectonics-port.md](tectonics-port.md) | ⚠️ 历史：tectonics.js → C# 移植方案（"等距柱状采样"段已过时，现为球面直通） |
| [tectonics-ref/](tectonics-ref/) | 原版 tectonics.js 源码参考（58 文件，CC-BY-4.0，含原 LICENSE） |
| [screenshots/](screenshots/) | 各阶段验证截图（侵蚀对照、海拔色带、洋流环、biome 等，40 张） |

> 已删除（2026-08-23，被完全取代）：`文明演化v1.md`、`阶段2设计-一格一实体重构.md`、`阶段3设计-派生状态架构化.md`。

## 快速入口

- 主场景：`res://scenes/core/NewBall.tscn`（project.godot main_scene；new_HexWorld 动态板块世界）
- 生成器：`res://scenes/core/MapGen.tscn`（headless：`--headless --quit-after 400`）
- 查看器：`res://scenes/core/MapViewer.tscn`（键盘切图层）
- 诊断场景：`res://scenes/diag/`（23 个，全部 headless 可跑）
- 单元测试：`dotnet test tests/World.Tests/World.Tests.csproj`（new_HexWorld/老树全量）

> 注：`docs/` 含 `.gdignore`（Godot 不导入本目录）；截图只作记录，非游戏资源。