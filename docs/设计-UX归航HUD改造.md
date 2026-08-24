# 设计：UX 归航 HUD 改造（B 定制版实施）

> 2026-08-24 · 用户拍板：方案 B（归航 HUD）定制版——**观测左上 / 地图潜藏底部 / 图例右下 / 时间右上**。
> mockup 见 `docs/图解/UX重设计三方案.html`（方案 B tab，可 hover 试交互）。
> 架构红线：场景化优先、不改 CivSim 任何逻辑、现有按钮逻辑（分类/图层/月份）只搬家不重写。

---

## 〇、目标布局（四角分工）

```
┌──────────────────────────────────────────────┐
│ 文明观测(左上,可折叠)        时间(右上,胶囊)   │
│                                              │
│              星 球 最 大 化                    │
│                                              │
│ 图例(右下,随图层)          [潜藏地图坞]        │
│                          ▲仅露抓手，hover展开 │
└──────────────────────────────────────────────┘
```

| 角/位 | 元素 | 现状 → 目标 |
|---|---|---|
| 左上 | 文明观测面板 | CivPanel body 右上 → **左上**（宽 244 收起条也在左上） |
| 右上 | 时间胶囊 | MonthRow 右下 → **右上**；新增纪元/演化年标签 |
| 底部中 | 地图坞（潜藏） | CatRow+LayerRow 常驻底部 → 收进坞，**只露抓手，hover 展开** |
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

### After

```
UiLayer
├── ProgressPanel         （不动）
├── EpochBar  (右上 HBox)  （新：纪元徽记 · 演化年 · 月份）
│   └── MonthRow 移入     （原右下 → 右上；滑块缩小 200→140 宽）
├── CukMapBar  (底部中央 PanelContainer，宽 660)      （新：潜藏坞）
│   ├── Grip    (金边抓手 Label "▲ 地 图 ▲")          （新）
│   └── Dock    (PanelContainer——默认隐藏)
│       └── DockBox (VBox)
│           ├── CatRow   （原节点 reparent 至此）
│           └── LayerRow （原节点 reparent 至此）
├── LegendPanel         （不动）
└── CivPanel            （改锚点 → 左上）
```

- **reparent 只改 tscn 的 parent 路径**（`parent="UiLayer/CukMapBar/Dock/DockBox"`）——按钮/信号/样式全部原样，逻辑零改动。
- Grip 文本随状态变：`▲ 地 图 ▲` ↔ `▾ 收 回 ▾`。

---

## 二、潜藏坞交互规格（MapViewer.Ui.cs 新增）

状态机（Control 信号——CanvasLayer 上有效）：

```
[兜底态] Dock 隐藏，仅露 Grip（9px 金边细条）
   │ mouse_entered（Grip 或 Dock 任意部位）
   ▼
[Hover 展开] Dock 显示（0 延迟——鼠标已在坞上，立刻可用）
   │ mouse_exited
   ├─ Timer { 0.4s 内再次进入 → 取消收回 }  ← 防抖：跨行移动不误收
   └─ Timer 到 → 收起回兜底态
   │ （Grip 点击任意时刻切换）
   ▼
[Pin 锁定] Dock 常显（防频繁 hover 误触；Grip 文字变"▾ 收 回 ▾"）
   │ 再点 Grip / 或点 Dock 外任意处
   ▼
[兜底态]
```

- **实现**：`CukMapBar`（Grip+Dock 的容器 PanelContainer）挂 `MouseEntered/MouseExited` 信号 + `SceneTreeTimer`（0.4s）延迟；Grip `Pressed` 切 `_pinned`；点击地图区（MapViewer 自身 `_Input` 检查非 UI 区域）解除 pin——用 `Timer`（Godot.Timer 单发）比 SceneTreeTimer 好管理取消。
- 展开/收回无动画（首版简洁；如需 Tween 高度动画后续加——不阻塞）。

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
| 1 | `scenes/core/MapViewer.tscn` | 新增 CukMapBar/Grip/Dock/DockBox/EpochBar 节点；CatRow/LayerRow reparent；MonthRow 移入 EpochBar 改锚；CivPanel body/restore 锚改左上；load_steps 调整 |
| 2 | `scenes/ui/CivPanel.tscn` | body/restore 锚点左移（独立子场景自持） |
| 3 | `scripts/MapView/MapViewer.Ui.cs` | EnsureUi：CukMapBar hover/pin 状态机 + EpochBar 组装 + 数据填充（纪元/年） |
| 4 | `scripts/MapView/MapViewer.cs` | 读档/演化完成处调 `RefreshEpochBar()`（与 RefreshCivPanel 同点，1 行） |
| 5 | `scripts/Diagnostics/UiShotDiag.cs` | 新增 `--cuk-map=1`：截图前模拟 hover（显示 Dock）——验证展开态；默认截图=兜底态 |

**验证**：窗口截图 ×2（兜底态：仅抓手 / 展开态：dock 全见）+ 分类/图层切换回归截图 + 全量单测（无 CivSim 改动，应全绿）。移动端/小屏不特殊处理（同现状）。

---

## 六、明确不做（范围边界）

- ❌ 圣典语汇（纹章/纪年改词）——C 方案内容，未选；
- ❌ Dock 展开动画/Tween——首版纯显隐，动画可后加；
- ❌ 观测卡拖拽自由定位——固定在左上，可折叠够用；
- ❌ 图层分类重组/图例随图层淡入——功能不变，只搬家；
- ❌ 不改任何 CivSim / 存档 / 事件代码（本改造纯 UI 层）。

---

## 七、验收清单

1. 启动 MapViewer 加载 .cmp：左上观测、右上时间、右下图例、底部仅一条金色抓手；
2. 鼠标移至抓手：Dock 展开（分类三按钮 + 17 图层按钮原样）；移出 0.4s 收回；快速跨行移动不误收；
3. 点抓手：Pin 锁定常显；再点/点地图解除；
4. 切图层：分类/图层按钮行为与改造前完全一致（回归）；
5. 季风/月降水图层：右上出现月份滑块；
6. 窗口 1280×800 与 2560×1600 两档截图无重叠/错位。