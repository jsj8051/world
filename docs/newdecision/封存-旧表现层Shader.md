# 封存 · 两个旧表现层 Shader（`noise_map_material` / `planet_detail`）

> **性质**：**知识封存**。生产资源已于 2026-10-05 整体退出（4 个文件成对删除）。
> ★**本文档最重要的目的**：防止把"零消费者资源退出"误读成"这个设计有问题"。
> 两个 shader 的退出性质**完全不同** —— 见 §0 的分类表。

---

## 0 ★先读这段：两种截然不同的退出性质

| | `noise_map_material` | `planet_detail` |
|---|---|---|
| 归属线 | **B 线**（噪声地形，2026-09-29） | **A 线**（`MapViewer`，2026-08-20） |
| 退出性质 | ★★**语义保留，实现迁移，旧载体退出** | ★★**需求消失，资源整体退出** |
| 一句话 | **删掉的是资源载体，不是平光语义** | **删掉的是整个已退役的图层语言** |
| 容易写错的地方 | ❌ 写成"该设计被淘汰" | ❌ 写成"设计有问题" |

★**两者都不是"设计有问题"，但原因完全不同** —— 这个区分是本封存的核心。

---

## 1 `noise_map_material.gdshader`（308 B · B 线）

### 1.1 它当时做什么

原文（`:4-5`）：

> 噪声地形 · **平光地图材质**（2026-09-29）：颜色全部来自**顶点色**（海拔分档），
> 不受灯光影响（unshaded）——单半径 / 颜色表达拍板的配套材质。

```glsl
shader_type spatial;
render_mode unshaded, cull_back;
void fragment() { ALBEDO = COLOR.rgb; }      // ← 颜色全部来自顶点色
```

它是"**单半径球 + 海拔分档色**"这套拍板的配套材质：
地形不做顶点位移（单半径），海拔靠颜色分档表达 ⇒ 材质只需"平光 + 直取顶点色"。

### 1.2 ★为什么它退出（不是被淘汰）

**旧实现 → 顶点色路线退出**：

```glsl
// 旧：unshaded + ALBEDO = COLOR.rgb（顶点色）
```

**现行实现**（`scripts/Render/BallView.cs:251-260`，**C# 内联 shader，不读文件**）：

```glsl
shader_type spatial;
render_mode unshaded, cull_disabled;   // 双面保险（背面经济性由块级剔除承担）
uniform sampler2D elev_tex : filter_nearest, repeat_disable;
void fragment() { ALBEDO = texture(elev_tex, UV).rgb; }   // ← 颜色来自数据纹理
```

### 1.3 ★★语义对照（本封存最重要的一张表）

| 维度 | 旧实现 | 现行实现 | 变了吗 |
|---|---|---|---|
| **平光（`unshaded`）** | ✅ | ✅ | ★**没变，语义继续存在** |
| 背面策略 | `cull_back` | `cull_disabled` | 改为块级剔除承担 |
| **颜色数据源** | ★**顶点色 `COLOR.rgb`** | ★**颜色数据纹理 `texture(elev_tex, UV).rgb`** | ★**变了，这是唯一的实质变化** |
| 表达什么 | 海拔分档色 | 海拔分档色 | 没变 |

⇒ ★★**结论：平光语义继续存在，只是颜色数据源从顶点色迁移到纹理。**

### 1.4 顶点色路线为什么必须退出（技术原因）

两条，都是**实测踩出来的**：

| 原因 | 说明 |
|---|---|
| **`COLOR.a` 会被 Godot 夹到 `[0,1]`** | 顶点色 alpha 通道不是"任意值"通道 ⇒ 无法用它传宽度/非颜色量 |
| **8 位量化误差会翻转色档** | 海拔色带是**分档**表达（硬阈值）⇒ 顶点色的8 位量化误差足以让相邻两格跳到不同档，表现为"色阶边界锯齿" |

⇒ 替代方案：**把颜色烘进纹理**（全星一张 RGBA8 数据纹理，每格 1 纹素）
⇒ 代价：材质必须是 `.gdshader` 文件或内联字符串（不能用顶点色了）⇒ **旧shader 文件失去存在意义**。

### 1.5 封存要点

- ★**平光拍板至今有效**（`BallView` 内联 shader 仍是 `unshaded`）
- ★**退出的只是"顶点色这个数据源"，不是平光语义**
- 若将来有人想恢复"顶点色路线"⇒ **先看 §1.4 的两条技术原因**
- 内联 shader 的选择本身也有理由：它与 C# 同处一个文件，不需要跨文件路径解析与 `.uid` 维护

---

## 2 `planet_detail.gdshader`（1405 B · A 线）

### 2.1 它当时做什么（两件事）

| # | 职责 | 实现要点 |
|---|---|---|
| **1** | **势力边界线** | `COLOR.a` 编码"到格边距离"（1=中心 / 0=边界），`smoothstep(0.98, 0.995, COLOR.a)` 暗化⇒ **线宽 ~2% 格子半径**。原文⚠️ 注明："`COLOR.a=0` 表示该角点紧邻**势力边界**" |
| **2** | **cel shading（分段色阶）** | `light()` 里按 `NdotL` 分档：亮面 0.9 / 中面 0.72 / 暗面 0.5 / 背光 0.5；过渡带用 smoothstep 软化（0.12~0.35、 0.35~0.55） |

原文的设计意图注释：

> The outline is a **DARKENED version of the tile color** (not near-black),
> so cell borders read as **crisp grid lines** instead of looking like cracks between tiles.

### 2.2 ★为什么它退出（需求消失，不是被淘汰）

| 判据 | 状态 |
|---|---|
| **需求是否还在？** | ❌ **消失**。"势力边界线"是**A 线 `MapViewer` 的图层语言**（政治实体可视化），随 22 个 `MapView/Layers/*` 整体清退 |
| **现行架构有无对应语义？** | ❌ **无**。`WorldGenMapModes` 四个模式全是**逐格填色**，`cel`/`band`/`LIGHT`/`NdotL` 匹配数 = **0**；`BallView` 是 `unshaded`（不受光）⇒ cel shading 与新架构的**平光风冲突** |
| **现行 `RegionTypeMode` 是同一件事吗？** | ❌ **不是**。它的七类是**地质区域**（Plain/Highland/Basin/Mountain/Plateau/Rift/Coastal），**不是政治势力** ⇒ 势力边界线的需求**从未在现行架构立项** |

### 2.3 封存要点

- ★**它属于 A 线图层语言**，随 A 线整体清退而失去依据
- **cel shading 与现行平光风是两种互斥的视觉语言**，不是"新旧好坏"问题
- ⚠️ 记录时**不要写成"cel shading 不好"** —— 它服务于 A 线的"策略地图色块风"，
  在那个语境下是合适的选择；只是新架构选了平光风

---

## 3★一条必须保留的边界：`.godot/` 里的记录**不是**依赖边

这轮取证差点在这里误判，值得单独记录。

### 3.1 实测现象

`planet_detail.gdshader` 在 `.godot/` 里**有三处登记**：

| 文件 | 内容 |
|---|---|
| `.godot/editor/filesystem_cache10:564` | `planet_detail.gdshader::Shader::4666607142398943040::...` |
| `.godot/uid_cache.bin` | 二进制登记 |
| ★`.godot/editor/editor_layout.cfg:78,80` | `open_shaders=["res://shaders/planet_detail.gdshader"]` / `selected_shader=...` |

⇒ ★**如果只查到"有登记"就下结论，会误判成"有人在用"。**

### 3.2 为什么它们不是依赖边

```bash
$ git check-ignore -v .godot/editor/editor_layout.cfg
.gitignore:2:.godot/    .godot/editor/editor_layout.cfg

$ git ls-files .godot/          # ← 输出为空
```

| 类别 | 文件 | 性质 |
|---|---|---|
| **导入索引缓存** | `filesystem_cache10` / `uid_cache.bin` | Godot **扫描产物**，随文件存在自动生成，**不代表有人引用** |
| **本地编辑器 UI 状态** | `editor/editor_layout.cfg` | 记录"上次打开了哪些 shader 面板"，**随编辑器关闭/切换而变** |

⇒★**两者都被 `.gitignore` 排除、git 完全不跟踪** ⇒ **不属于项目资产**。

### 3.3 资源清退时的判据（沉淀为永久规则）

> ★**"编辑器曾经打开过" ≠ "项目仍然引用"。**
>
> 资源依赖边只认：
> - `.tscn` / `.tres` / `.material`的 `ext_resource`（**`path=` 与 `uid=` 都要查**）
> - `.cs` 里的字符串 / 路径装载（`GD.Load<Shader>("res://...")`）
> - shader 内部的 `#include` / `#load`
> - 其他 shader / material 对它的引用
>
> **不认**：`.godot/` 下的任何登记（导入缓存 + 编辑器 UI 状态）。

★**另**：Godot 4.4+ 的 `.tscn` 可能用 `uid=` 而非 `path=` 引用资源
⇒ ★**只搜文件路径会漏，必须查 uid 值本身**。
本轮实测两个 uid（`uid://etnixawmhfga` / `uid://b7qslqsjyaw3q`）在仓库内均零引用。

### 3.4 成对退出

资源对象的完整依赖单元 = **`.gdshader` + 其 `.uid`**，必须一起删。
只删 `.gdshader` 会留下**孤儿 `.uid`**（Godot 下次导入会报孤儿警告）。

---

## 4 取回方式

```bash
git show fb83da8:shaders/noise_map_material.gdshader   # 本轮删除前的版本
git show fb83da8:shaders/planet_detail.gdshader
```

---

## 5 现状

`shaders/` 现在**只剩一个 shader**：

| 文件 | 用途 |
|---|---|
| `river_surface.gdshader` | 河面（River v2.0）+ 选中环带（`CellQuery`）—— **唯一在用** |

⇒顺带观察：删除后"独立 `shaders/` 目录"这个约定是否还需要（只有一个文件了）？
**不必现在决定** —— 目录约定不是本轮范围。

---

**相关**：`docs/审查报告-C3-悬空shader定性.md`（本轮完整取证六查 + 语义判断）·
`docs/architecture.md` §3.0.1（依赖扫描第 6 类介质 + 两条防误判）·
`docs/newdecision/封存-SphereLines球面线带.md`（C-1 同类封存）·
`docs/newdecision/封存-ParallelLoops确定性并行.md`（C-2 同类封存）
