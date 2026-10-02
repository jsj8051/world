# 参考文献（外部教程中文版）

> 本目录收录与"**噪声生成球面地图 / 程序化世界**"直接相关的五份外部资料，按来源许可分两档提供中文版：
> - **全译**（来源允许演绎/翻译）：Godot 官方文档（CC BY 3.0）、Azgaar FMG Wiki（MIT）；
> - **逐节完整编译**（原文文字保留版权、未开放翻译许可，故按原文小节结构完整覆盖其内容、以中文重述而非逐字复制）：Red Blob Games 两篇、Inigo Quilez 四篇。每篇头部均注明原文链接与版权状态，语义以原文为准；原文的交互演示与图例请回原网页看。
> 整理日期：2026-09-30（原文内容以当日抓取为准）。

## 文档清单

| 文件 | 原文 | 档位 |
|---|---|---|
| [RedBlob-球面程序化地图生成-中文版.md](RedBlob-球面程序化地图生成-中文版.md) | [redblobgames.com/x/1843-planet-generation](https://www.redblobgames.com/x/1843-planet-generation/) | 逐节完整编译 |
| [RedBlob-噪声地形-中文版.md](RedBlob-噪声地形-中文版.md) | [redblobgames.com/maps/terrain-from-noise](https://www.redblobgames.com/maps/terrain-from-noise/) | 逐节完整编译 |
| [Godot-FastNoiseLite-API-中文翻译.md](Godot-FastNoiseLite-API-中文翻译.md) | [docs.godotengine.org … class_fastnoiselite](https://docs.godotengine.org/en/stable/classes/class_fastnoiselite.html)（CC BY 3.0） | 全译（附署名） |
| [IQ-噪声文章集-中文版.md](IQ-噪声文章集-中文版.md) | [iquilezles.org/articles](https://iquilezles.org/articles/)（fbm / warp / voronoise / morenoise 四篇） | 逐节完整编译（代码片段 MIT） |
| [Azgaar-FMG-Wiki-中文翻译.md](Azgaar-FMG-Wiki-中文翻译.md) | [azgaar.org](https://azgaar.org/) · [Wiki](https://github.com/Azgaar/Fantasy-Map-Generator/wiki)（MIT） | 首页全译 + 35 页索引；其余页面点名即译 |

## 阅读顺序建议

1. 新人入门：RedBlob-噪声地形 → RedBlob-球面 → Azgaar-FMG（先看产品形态）；
2. 做噪声相关工作：IQ-文章集（数学）↔ Godot-FastNoiseLite（参数语义）互查。

## 相关内部文档

- `docs/地壳运动/`（new_HexWorld 设计系列，噪声在 -01 初值、教训表 -02 §11）
- `docs/tectonics-ref/`（tectonics.js 原版源码，CC-BY-4.0，已整包 vendor）
- `docs/架构设计.md` §生成管线、`docs/索引.md` §5.11
