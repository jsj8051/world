# 设计：UX 归航 HUD 改造（B 定制版实施）

> 2026-08-24 · 用户拍板：方案 B（归航 HUD）定制版——**观测左上 / 地图潜藏底部 / 图例右下 / 时间右上**。
> mockup 见 `docs/图解/UX重设计三方案.html`（方案 B tab，可 hover 试交互）。
> 架构红线：场景化优先、不改 CivSim 任何逻辑、现有按钮逻辑（分类/图层/月份）只搬家不重写。
>
> **2026-08-30 修订（单面板抽屉）**：用户拍板地图坞改为**单面板**——去掉抓手节点与钉住态；
> 面板常驻屏幕底部，平时只露顶部标题条「地图」，鼠标移上去 0.15s 平滑滑出全貌，移开**立即**滑回
> （2026-08-31 用户再拍板：不做 0.4s 防抖延迟）。
> 本修订把"抓手 + 独立弹出面板"的两节点结构合并为同一面板的位移（offset）控制。
>
> **2026-08-31 修订（场景制作为核心）**：样式全部收敛进 `MapViewer.tscn` sub_resource——坞面板
> `theme_override_styles/panel = sb_hud_panel`（与图例面板同款羊皮纸）；删代码侧样式工厂
> （`CukDockStyle`/`HudBtnStyle`/`HudBtnHoverStyle`/`HudBtnPressedStyle`）；动态图层按钮与保存按钮
> 经场景分类按钮 `GetThemeStylebox` 取样式（单一来源）——此前代码 pressed=金色 与场景 pressed=暗红
> 同坞分裂，现统一暗红。场景调试残留 `MapPath=regress_v9_n64.cmp` 已删（回归代码默认 map1.mpa）。

---

## 〇、目标布局（四角分工）

```
┌──────────────────────────────────────────────┐
│ 文明观测(左上,可折叠)        时间(右上,胶囊)   │
│                                              │
│              星 球 最 大 化                    │
│                                              │
│ 图例(右下,随图层)          [地图坞——单面板抽屉] │
│                          常驻只露标题条「地图」 │
└──────────────────────────────────────────────┘
```

| 角/位 | 元素 | 现状 → 目标 |
|---|---|---|
| 左上 | 文明观测面板 | CivPanel body 右上 → **左上**（宽 244 收起条也在左上） |
| 右上 | 时间胶囊 | MonthRow 右下 → **右上**；新增纪元/演化年标签 |
| 底部中 | 地图坞（单面板抽屉） | CatRow+LayerRow 常驻底部 → **同一个坞面板**，平时只露标题条「地图」，hover 滑出 |
| 右下 | 图例 | LegendPanel 保持右下（MonthRow 搬走后更宽裕，尺寸不变） |

---

## 一、场景结构改造（MapViewer.tscn）

### Before（UiLayer 直接子）

```
UiLayer (CanvasLayer=100)
├── ProgressPanel       （右下，加载进度——不动）
├── CatRow              （底部 分类三按钮——搬）
├── LayerRow            （底部 17 图层按钮——搬）
├── MonthRow            （右下 月份滑块——搬右上）
├── LegendPanel         （右下 图例——不动）
└── CivPanel            （右上 观测——改锚左上）
```

### After（2026-08-30 单面板：无独立抓手节点）

```
UiLayer
├── ProgressPanel         （不动）
├── EpochPanel (右上胶囊)  （纪元徽记 · 演化年 · 月份滑块）
│   └── MonthRow 移入     （原右下 → 右上；滑块缩小 200→140 宽）
├── CukDock  (底部中央 PanelContainer，宽 660，常驻可见)   （单面板抽屉）
│   └── DockBox (VBox)
│       ├── HeadRow  (深墨标题条「地图」30px——收起时唯一露出部分；样式 sb_cuk_head)
│       ├── CatRow   （原节点 reparent 至此）
│       └── LayerRow （原节点 reparent 至此）
├── LegendPanel         （不动）
└── CivPanel            （改锚点 → 左上）
```

- **CukGrip（底部抓手 Button）已于 2026-08-30 删除**——抓手功能并入 HeadRow：平时露出的标题条本身就是展开触发区。
- 位置控制：`CukDock` anchors preset 7（CenterBottom），代码用 **OffsetTop/OffsetBottom** 位移（不用 Position setter——CenterBottom 锚点下 Position 不可靠，同 RebuildLegend 备注）。收起 = `offset_top=-30`（标题条高）、`offset_bottom=+(高-30)`；展开 = `offset_top=-高`、`offset_bottom=0`。两 offset 同增减 → 面板高度不变，纯位移，内容布局零重算。
  ⚠️ **2026-08-31 符号勘误**：本行旧写 `offset_bottom=-(高-30)`（负号）是错误公式——CenterBottom 锚下 `rect.bottom = 屏高 + offset_bottom`，收起要埋入屏幕下方必须**正号** `+(高-30)`；负号使 rect 倒置、面板被最小尺寸撑开后浮在屏幕底部上方（用户报"坞浮在空中"）。代码与本文档同修。
  ⚠️ **场景初始态 = 收起态**（2026-08-31）：`MapViewer.tscn` 中 CukDock 直接写 `offset_top=-30 / offset_bottom=127`
  （= 场景所见即运行时收起样：只露标题条，坞主体埋在视口下方）。运行时代码仍会按真实内容高度精确对齐
  （SetupCukHud instant 收起 + CukOffsetAligned 补正）——场景值是初始近似，内容高度变化时自动修正，
  无需手动改场景。编辑器里想看坞全貌：临时把 offset_top 改回 -157（展开）查看，改完收回来。

---

## 二、地图坞交互规格（MapViewer.Ui.cs）

状态机（**位置驱动**——ProcessCukHud 每帧读鼠标位置判定，不用 hover 信号：坞内按钮覆盖时 Dock 收不到 MouseEntered，曾致"在地图上停一会就收回"）：

```
[兜底态] 面板沉底，只露标题条「地图」（30px）
   │ 鼠标移入面板矩形（GetGlobalRect().HasPoint）
   ▼
[展开] CukSlideDur=0.25s 平滑滑出（offset tween，Quad Out，Physics 节拍）——分类三按钮 + 17 图层按钮全见
   │ 鼠标移出面板矩形
   ▼
[收回] 0.25s 平滑滑回（立即触发，无防抖延迟——2026-08-31 用户拍板）
```

- **收回无延迟**（2026-08-31 修订）：原 0.4s 防抖 Timer（跨行移动不误收）已删——用户拍板"鼠标移出立即收回"。
- **动画平滑**（2026-08-31 修订）：0.15s Cubic Out → **0.25s Quad Out + `TweenProcessMode.Physics`（60Hz 物理帧节拍插值）**——原 Idle 模式按渲染帧采样，渲染帧率 ~30fps 时仅 4-5 个采样点且 Cubic 起步陡，每帧大跳成肉眼可见的"三段式收起"（用户报）；Quad 起步缓 + Physics 节拍后任意渲染帧率下位移曲线平滑递减。
- **收起态判定说明**：面板大部分在屏幕外（视口裁剪不绘制），`GetGlobalRect()` 虽含屏幕外部分，但鼠标坐标只在窗口内 → 等效"只露标题条"，鼠标无法触及屏幕外区域。
- **无钉住态**（2026-08-30 拍板：单面板抽屉里钉住没有意义，删除 `_cukPinned`/`_cukSuppressUntil`/抓手点击整套）。
- **补正去抖**（2026-08-31 修订）：补正前先 `CukOffsetAligned` 判定（offset 差 <1px 即跳过 setter）——旧实现每帧 set offset 即使值相同也触发布局 dirty，收起后静止态持续重排造成卡顿（用户报）。anchor 固定 1.0 → 窗口 resize 时 rect 自动跟随，本就不需每帧补正。
- 面板常驻 `Visible=true`——不再有"长出一个新窗口"的观感。

---

## 三、时间胶囊（EpochBar，右上）

- 结构：`HBox[ 纪元徽记 Label | 演化年 Label | 月份滑块(原 MonthRow) ]`，羊皮纸胶囊样式（SaveRowStyle）。
- 数据源（**只读派生，不碰 CivSim**）：
  - 演化年 = `_civCtx?.Tick ?? 0` × 100，格式 "17,100 年"；
  - 纪元 = 任意存活政体 `IsFarming` → "◆ 新石器" 否则 "◆ 旧石器"（读档后一次性）；纯自然图显示 "自然世界"；
  - 月份 = 原 MonthSlider（季风/月降水图层显示滑块，其余只显示 "3 月" 文本）。
- 更新时机：读档完成、演化完成回调处（与 RefreshCivPanel 同点）。

---

## 四、观测面板移位（CivPanel.tscn）

- `CivPanelBody`：锚点右上 → **左上**（`anchors_preset` 保持 1 但改 offset：left=14, top=14, bottom=~500；宽由 `CustomMinimumSize(244, 480)` 决定——去掉 right 偏移）。
- `CivRestoreBtn`：右上 → **左上**（`left=14, top=14`，收起后恢复入口同侧）。
- 其余（页签/列表/滚动/收起逻辑）零改动。

---

## 五、改造清单（文件级）

| # | 文件 | 改动 |
|---|---|---|
| 1 | `scenes/core/MapViewer.tscn` | 新增 CukDock/DockBox/HeadRow（标题条「地图」）节点 + sb_cuk_head 样式；CatRow/LayerRow reparent；**CukGrip 删除**；MonthRow 移入 EpochPanel 改锚；CivPanel anchor 改左上；load_steps 调整 |
| 2 | `scenes/ui/CivPanel.tscn` | body/restore 锚点左移（独立子场景自持） |
| 3 | `scripts/MapView/MapViewer.Ui.cs` | EnsureUi：SetupCukHud（样式/防抖 timer/初始收起）+ ProcessCukHud（位置驱动滑出滑入）+ ApplyCukDockPosition（offset tween 0.15s）+ EpochBar 数据填充；**删除抓手/钉住全套字段与函数** |
| 4 | `scripts/MapView/MapViewer.cs` | 读档/演化完成处调 `RefreshEpochBar()`（与 RefreshCivPanel 同点，1 行） |
| 5 | `scripts/Diagnostics/UiShotDiag.cs` | `--cuk-map=1`：平移到展开态（offset）；`--cuk-warp=1`：warp 到标题条中心 + offset 展开判定；`--cuk-click-icon`：点击前先展开；`--diag-rect`：名单去掉 CukGrip |

**验证**：窗口截图 ×2（兜底态：仅露标题条「地图」 / 展开态：dock 全见）+ 分类/图层切换回归截图 + 全量单测（无 CivSim 改动，应全绿）。移动端/小屏不特殊处理（同现状）。

---

## 六、明确不做（范围边界）

- ❌ 圣典语汇（纹章/纪年改词）——C 方案内容，未选；
- ✅ 地图坞滑出/滑入动画——**2026-08-30 已做**（0.15s offset tween，用户拍板"平滑滑出"）；
- ❌ 观测卡拖拽自由定位——固定在左上，可折叠够用；
- ❌ 图层分类重组/图例随图层淡入——功能不变，只搬家；
- ❌ 不改任何 CivSim / 存档 / 事件代码（本改造纯 UI 层）。

---

## 七、验收清单

1. 启动 MapViewer 加载 .cmp：左上观测、右上时间、右下图例、底部**仅一条深墨标题条「地图」**（无独立抓手）；
2. 鼠标移至标题条：面板 0.15s 平滑滑出（分类三按钮 + 17 图层按钮原样）；移出 0.4s 后平滑滑回；快速跨行移动不误收；
3. 无钉住行为（点标题条不再锁定常显——单面板抽屉语义）；
4. 切图层：分类/图层按钮行为与改造前完全一致（回归）；
5. 季风/月降水图层：右上出现月份滑块；
6. 窗口 1280×800 与 2560×1600 两档截图无重叠/错位（收起态标题条贴底居中）。