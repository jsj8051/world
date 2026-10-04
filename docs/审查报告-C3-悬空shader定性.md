# C-3 定性报告 · 悬空 shader（`noise_map_material` / `planet_detail`）

> **性质**：**只做定性取证，未删任何文件**。
> 目的：把依赖扫描的**第 6 类介质**（类自带的专属外部资源）真正落到 **B 线遗留资源**上。
> 判据：清退三层的第 ③ 层 + **第 ④ 层（整个依赖单元是否一起退出）**。

- 日期：2026-10-05
- 基线：`d6d6050`
- 对象：`shaders/noise_map_material.gdshader`（308 B）+ `shaders/planet_detail.gdshader`（1405 B）

---

## 0 ★结论速览

| 项 | 结论 |
|---|---|
| **定性** | **两者都是「生产删除 + 封存」**，但**归属线不同、封存理由不同** |
| `noise_map_material` | **B 线**（噪声地形 2026-09-29）遗留；⚠️ **它的语义仍在**，只是实现被内联 shader 取代 |
| `planet_detail` | **A 线**（`MapViewer` 2026-08-20）遗留；语义（cel shading + 势力边界线）**已完全消失** |
| 资源依赖闭环 | **六查全 0**（含Godot 特有的 `.godot/` 缓存与 uid 链） |
| ★本轮验证 | **第 ④ 条：删除一个资源时，它的整个依赖单元（`.gdshader` + `.uid`）一起退出** |

---

## 1 ★资源依赖闭环六查（防"文件名未被引用 ≠ 没人用"）

你特别提醒的误判：**"仓库里没有 `X.gdshader` 这个文件名引用" ≠ "没人通过材质/资源链使用它"**。
因此本轮不只搜文件名，按**资源链**逐查：

| 查 | 范围 | `noise_map_material` | `planet_detail` |
|---|---|---|---|
| **1** | `.tscn` / `.tres` / `.material` 的 `ext_resource` 路径 | 0 | 0 |
| **2** | shader 内部 `#include` / `#load` / `res://` 外部依赖 | 0（无 include） | 0（无 include） |
| **3** | `.cs` 中通过字符串/路径装载（`GD.Load` / 路径字面量） | 0 | 0 |
| **4** | editor / tool / diagnostic（`scripts/Diagnostics/`） | 0 | 0 |
| **5** | 其他 shader / material 引用它 | 0 | 0 |
| **6** | `.uid` 是否成对/ 是否入库 | ✅ 成对（20 B） | ✅ 成对（20 B） |

### 1.1 ★另查两条 Godot 特有的隐蔽链（本次新增）

| 链 | 查什么 | 结果 |
|---|---|---|
| **`.godot/` 缓存** | `filesystem_cache10` / `uid_cache.bin` 里是否登记 | ⚠️ **两者都有登记** |
| **`.godot/editor/editor_layout.cfg`** | 编辑器 UI 状态是否记录 | ⚠️ **`planet_detail` 被记为"最近打开的 shader 面板"** |

⇒ ★**必须区分这两条的性质**：

```bash
.godot/ 已被 .gitignore:2 排除，git 完全不跟踪
  ⇒ editor_layout.cfg 是**本地编辑器 UI 状态**（"上次我打开了哪个 shader 面板"），
     随编辑器关闭/切换而变，**不是资产依赖**；
  ⇒ filesystem_cache / uid_cache 是**导入索引缓存**（Godot 扫描产物），随文件存在自动生成，
     **不代表有人引用**。
```

⇒ ★**这正是你担心的那类误判的正面案例**：如果只查到"`.godot` 里有登记"就下结论，
会误判成"有人在用"。核实到"被.gitignore 排除 + 性质是 UI 状态/导入缓存"才能排除。

### 1.2 uid 交叉引用（最终确认）

| shader | uid | 仓库内引用 |
|---|---|---|
| `noise_map_material` | `uid://etnixawmhfga` | **0** |
| `planet_detail` | `uid://b7qslqsjyaw3q` | **0** |

★Godot 4.4+ 的 `.tscn` 可能用 `uid=` 而非 `path=` 引用 ⇒ 必须查 uid 值本身。
两个 uid 在仓库内（排除 `.git` / `.godot`）**零引用**。

⇒★**资源依赖闭环确认：生产0 / 测试 0 / 资产链 0 / uid 0**。

---

## 2 ③ 语义判断（两者定性不同，这是本轮核心）

### 2.1 `noise_map_material.gdshader` —— ⚠️ **语义仍在，实现已被取代**

原文（`:4-5`）：

> 噪声地形 · **平光地图材质**（2026-09-29）：颜色全部来自**顶点色**（海拔分档），
> 不受灯光影响（unshaded）——单半径/颜色表达拍板的配套材质。

**现行实现**（`scripts/Render/BallView.cs:251-260`，**内联 shader，不读文件**）：

```csharp
_shader = new Shader { Code = """
    shader_type spatial;
    render_mode unshaded, cull_disabled;   // 双面保险
    uniform sampler2D elev_tex : filter_nearest, repeat_disable;
    void fragment() { ALBEDO = texture(elev_tex, UV).rgb; }   // 直采格分档色
""" };
```

**语义对比**：

| 维度 | `noise_map_material` | 现行 `BallView` 内联 |
|---|---|---|
| 平光（`unshaded`） | ✅ | ✅ **完全继承** |
| 背面 | `cull_back` | `cull_disabled`（改为块级剔除承担） |
| **颜色数据源** | ★**顶点色 `COLOR.rgb`** | ★**颜色数据纹理 `texture(elev_tex, UV).rgb`** |

⇒ ★★**这不是"语义被淘汰"，而是"同一语义换了数据源"**：
平光地图风（不受灯光影响）**至今仍是现役方案**；
变的是颜色从哪来—— 顶点色路线因
**`COLOR.a` 会被 Godot 夹到 `[0,1]`**（以及 8 位量化误差翻转色档）而退出，
改为"烘色进纹理"。

⇒ **定性**：`noise_map_material` 本身**零价值**（它的独特贡献"顶点色取色"已被证明不可行），
但它记录的是**平光拍板的原始出处** ⇒ **删除 + 封存（记录拍板来源 + 顶点色路线为何退出）**。

### 2.2 `planet_detail.gdshader` —— 语义**已完全消失**

原文记载两件事：

| # | 内容 | 原文 |
|---|---|---|
| 1 | **势力边界线** | `COLOR.a` 编码"到格边距离"，`smoothstep(0.98, 0.995, COLOR.a)` 暗化 ⇒ 线宽 ~2% 格半径；⚠️ 2026-08-20 注释：**势力边界线** |
| 2 | **cel shading（分段色阶）** | `light()` 里按 `NdotL` 分3 档（0.9 / 0.72 / 0.5），边界用 smoothstep 软化 |

**现行核实**：

- `WorldGenMapModes.cs` 中 `cel` / `band` / `LIGHT` / `NdotL` 匹配数 = **0**
  ⇒ **地图模式全部是逐格填色，无光照、无cel shading**
- 现行 `BallView` 材质是 `unshaded`（不受光）
- 现行4 个地图模式：海拔分档 / 海陆场 / **七类区域填色** / 离岸距离
  ⇒ 区域靠**色块相邻**区分，**不画势力边界线**

⇒ ★**"势力边界线"是A 线 `MapViewer` 的图层语言**（政治实体可视化），
随 A 线清退（`MapView/Layers/PolityLayer.cs` 等22 个 Layer 类）一起消失。
新架构的 `RegionTypeMode` 表达的是**地质区域**（七类），**不是政治势力**。

⇒ **定性**：语义已完全消失，**无现行替代也不需要**（政治势力可视化未在现行架构中立项）
⇒ **删除 + 封存（记录它属于 A 线图层语言）**。

---

## 3 ③ 现行替代核查

| 需求 | 现行承担者 | 状态 |
|---|---|---|
| 平光地图风（不受灯光影响） | `BallView` 内联 shader（`unshaded`） | ✅ **语义仍在** |
| 颜色数据源 | 全星一张 RGBA8 颜色数据纹理 | ✅ 已取代顶点色 |
| 格子边界线 / 网格线 | 无 | ➖ 不需要（色块足够） |
| cel shading（分段色阶） | 无 | ➖ **已随 A 线退役**（新架构是平光风） |
| 势力边界线 | 无 | ➖ **未在现行架构立项**（`RegionTypeMode` 是地质区域非政治势力） |

---

## 4 定性与建议

### 4.1 两者定性对照

| | `noise_map_material` | `planet_detail` |
|---|---|---|
| 归属线 | **B 线**（噪声地形 2026-09-29） | **A 线**（`MapViewer` 2026-08-20） |
| 当时视觉职责 | 平光地图 + 顶点色取色 | 势力边界线 + cel shading 色阶 |
| 为什么退出 | ★**顶点色路线不可行**（`COLOR.a` 被夹到 `[0,1]`）⇒ 改为"烘色进纹理"，材质内联进 C# | ★**A 线图层语言整体退役**（cel shading 与新架构的平光风冲突） |
| 语义是否仍在 | ⚠️ **平光语义仍在**（继承），顶点色部分已废 | ❌ **完全消失** |
| 现行替代 | `BallView` 内联 shader | 无（也不需要） |
| 定性 | 生产删除 + 封存 | 生产删除 + 封存 |

### 4.2 建议：一次收掉（4 个文件）

```
shaders/noise_map_material.gdshader      （308 B）
shaders/noise_map_material.gdshader.uid  （20 B）
shaders/planet_detail.gdshader           （1405 B）
shaders/planet_detail.gdshader.uid       （20 B）
```

★**第 ④ 条验证点**：`.gdshader` 与 `.uid` **必须成对退出** ——
只删 `.gdshader` 会留下孤儿 `.uid`（Godot 下次导入会重新生成或报孤儿警告）。

删完后 `shaders/` 仅剩 `river_surface.gdshader`（+ `.uid`）——
**唯一在用的 shader**（河面 + 选中环带）。

### 4.3 封存重点（★按你的要求：不是记录"设计有问题"）

> **必须避免的错误记录**："零消费者的资源删除" ≠ "这个 shader 设计本身有问题"。

| shader | 封存要记什么 |
|---|---|
| `noise_map_material` | ① 属**B 线**平光地图材质配套<br>② 视觉职责：颜色全来自顶点色（海拔分档），不受灯光影响<br>③ **为什么随 B 线退出**：★**顶点色取色路线被证不可行** —— Godot 会把 `COLOR.a` 夹到 `[0,1]`，且 8 位量化误差会翻转色档 ⇒ 改为"烘色进纹理"<br>④ **平光语义仍在**（继承进 `BallView` 内联 shader），退出的是**顶点色数据源**，不是平光拍板 |
| `planet_detail` | ① 属**A 线** `MapViewer` 图层语言<br>② 视觉职责：势力边界线（`COLOR.a` 编码边距+ 2% 半径暗化）+ cel shading 三档色阶<br>③ **为什么退出**：★**A 线图层体系整体清退**（22 个 `MapView/Layers/*`）；且 cel shading 与新架构的**平光风**冲突<br>④ 现行`RegionTypeMode` 的"七类区域"是**地质区域**，**不是政治势力** ⇒ 势力边界线的需求从未在现行架构立项 |

---

## 5 ★本轮验证的演进（第 ④ 条）

你指出的清退判据演进，本轮完成**第 ④ 条**的验证：

```
① 引用存在吗？      .cs / .tscn / 静态调用 / 继承链 / ★外部资源
② 引用是什么？      生产语义 / 测试夹具 / 工具 / 外部资产链
③ 它还代表现行语义吗？
④ ★它的整个依赖单元是否一起退出？   ← 本轮对纯资源对象验证
```

★**第 ④ 条对资源对象的具体含义**（与 C-1 `SphereLines` 的 6 文件单元同源）：

| 对象 | 依赖单元 | 本轮验证 |
|---|---|---|
| C-1 `SphereLines` | 类 + `.uid` + 2 shader + 2 `.uid`（6 文件） | ✅ 已随 C-1 一起退出 |
| **C-3 两个 shader** | 各 `.gdshader` + `.uid`（4 文件） | ⏸ **待本轮收掉** |
| C-2 `ParallelLoops` | 类 + `.uid`（2 文件，**无外部资源**） | ✅ 已退出（规则按对象适用，未无脑套用） |

★**新增的两条防误判**（本轮实测得出）：
1. **Godot 的 `.godot/` 缓存登记 ≠ 有人引用** ——
   `filesystem_cache` / `uid_cache.bin` 是导入索引产物；
   `editor_layout.cfg` 是**本地编辑器 UI 状态**（"上次打开了哪个 shader"）。
   ⚠️ `planet_detail` 就被后者记了一笔 —— 若只查到它就下结论，**会误判成"有人在用"**。
2. **必须查 uid 值本身** —— Godot 4.4+ 的 `.tscn` 可能用 `uid=` 而非 `path=` 引用，
   只搜文件路径会漏。

---

## 6 遗留

1. **本轮只做定性，未删文件** ⇒ 4 个文件仍在树里（等确认后一次性收掉）
2. 删完后 `shaders/` 只剩 1 个 shader ⇒ **"shaders/ 独立目录"这个约定是否还需要**
   可以顺带看一眼（不必现在决定）
3. **本轮两个新发现已沉淀进 `architecture.md`**（第 6 类介质 + 两条防误判）

---

**纪律**：本报告是**定性**，**不构成删除执行**。§4.2 的处置需你确认后再执行。

**相关**：`docs/审查报告-测试夹具依赖审计.md` §3（3 份C 档原始清单）·
`docs/审查报告-C1-SphereLines定性.md`（第 ④ 条的前一轮验证）·
`docs/审查报告-C2-ParallelLoops取证.md`（C-2 判据对照）·
`docs/architecture.md` §3.0（清退判定的三层）
