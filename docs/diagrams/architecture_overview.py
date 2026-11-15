"""Render the project architecture overview diagram.

Labels are curated from the source tree and the per-folder index; the diagram
itself reads no repository data.

Usage: python docs/diagrams/architecture_overview.py
Requires Pillow and a CJK font (Microsoft YaHei on Windows).

Output: docs/diagrams/architecture_overview.png
"""

import os
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "docs", "diagrams", "architecture_overview.png")

FONT_REG = r"C:\Windows\Fonts\msyh.ttc"
FONT_BOLD = r"C:\Windows\Fonts\msyhbd.ttc"

W, H = 2600, 1610
BG = (255, 255, 255)
INK = (31, 41, 55)
MUTED = (100, 116, 139)
ARROW = (107, 114, 128)
ALERT = (185, 28, 28)

img = Image.new("RGB", (W, H), BG)
draw = ImageDraw.Draw(img)

fonts = {
    "title": ImageFont.truetype(FONT_BOLD, 42),
    "subtitle": ImageFont.truetype(FONT_REG, 20),
    "panel": ImageFont.truetype(FONT_BOLD, 26),
    "name": ImageFont.truetype(FONT_BOLD, 23),
    "detail": ImageFont.truetype(FONT_REG, 16),
    "stage": ImageFont.truetype(FONT_BOLD, 19),
    "small": ImageFont.truetype(FONT_REG, 15),
    "tiny": ImageFont.truetype(FONT_REG, 14),
    "micro": ImageFont.truetype(FONT_REG, 13),
    "nano": ImageFont.truetype(FONT_REG, 12),
}


def text_width(text, font):
    return draw.textlength(text, font=font)


def wrap(text, font, max_width):
    lines, current = [], ""
    for char in text:
        candidate = current + char
        if text_width(candidate, font) <= max_width:
            current = candidate
        else:
            lines.append(current)
            current = char
    if current:
        lines.append(current)
    return lines


def band(x, y, w, h, fill, border, name, details, name_w=196):
    draw.rounded_rectangle([x, y, x + w, y + h], radius=12, fill=fill, outline=border, width=2)
    draw.text((x + 18, y + 14), name, font=fonts["name"], fill=INK)
    top = y + 15
    for line in details:
        draw.text((x + name_w, top), line, font=fonts["detail"], fill=INK)
        top += 25
    return (x, y, x + w, y + h)


def down_arrow(x, y1, y2, label=None, color=ARROW):
    draw.line([(x, y1), (x, y2 - 8)], fill=color, width=2)
    draw.polygon([(x - 6, y2 - 10), (x + 6, y2 - 10), (x, y2)], fill=color)
    if label:
        draw.text((x + 12, (y1 + y2) / 2 - 9), label, font=fonts["tiny"], fill=MUTED)


def right_arrow(x1, x2, y, color=ARROW):
    draw.line([(x1, y), (x2 - 8, y)], fill=color, width=2)
    draw.polygon([(x2 - 10, y - 6), (x2 - 10, y + 6), (x2, y)], fill=color)


def dashed_line(points, color=ALERT, width=2, dash=8, gap=6):
    for (x1, y1), (x2, y2) in zip(points, points[1:]):
        length = ((x2 - x1) ** 2 + (y2 - y1) ** 2) ** 0.5
        if length == 0:
            continue
        ux, uy = (x2 - x1) / length, (y2 - y1) / length
        pos = 0
        while pos < length:
            end = min(pos + dash, length)
            draw.line([(x1 + ux * pos, y1 + uy * pos), (x1 + ux * end, y1 + uy * end)],
                      fill=color, width=width)
            pos = end + gap


# ── Title ────────────────────────────────────────────────────────────────────
draw.text((60, 38), "world · 项目架构总览", font=fonts["title"], fill=INK)
draw.text((62, 94), "Godot 4.7 + C# · H3 六边形球面 · 程序化世界生成（板块 → 地形 → 气候 → 水文 → 索引）",
          font=fonts["subtitle"], fill=MUTED)
draw.text((62, 122), "依据 sprout-docs 索引与源码 using 关系绘制；实线箭头 = 编译期依赖方向",
          font=fonts["tiny"], fill=MUTED)

# ── Left panel: layers ──────────────────────────────────────────────────────
draw.text((46, 166), "一、分层架构（目录即边界，依赖自上而下）", font=fonts["panel"], fill=INK)

BX, BW = 64, 852
SAT_X, SAT_W = 936, 288

rows = [
    dict(name="scenes/", h=88, fill=(238, 242, 247), border=(124, 140, 161),
         details=["AppRoot.tscn · pages/(5) · components/(10，与 pages 并列)", "render/(2) · camera/ —— 只挂节点不写逻辑"]),
    dict(name="Scene", h=104, fill=(232, 240, 254), border=(59, 106, 177),
         details=["组合根：AppRoot · AppShell · AppPageHost", "AppPageFactory · AppLoadCoordinator · AppNoticeBoard"]),
    dict(name="UI", h=104, fill=(255, 244, 229), border=(217, 164, 65),
         details=["Pages/(5 页) · Components/(11，与 Pages 并列)", "只做装配与重绘，交互逻辑不在此层"]),
    dict(name="Application", h=128, fill=(233, 247, 239), border=(76, 154, 106),
         details=["Pages(按页分目录) · Components(8，并列)", "Generation(会话/生产档) · Loading(作业/进度)",
                  "Storage(存储端口) · Layers(图层门禁)"]),
    dict(name="Runtime", h=104, fill=(243, 232, 255), border=(142, 106, 184),
         details=["WorldManager(世界入口) · WorldEvolutionSession", "Client/ = 相机与输入；不实现算法"]),
    dict(name="Render", h=128, fill=(231, 241, 251), border=(63, 127, 191),
         details=["BallView · WorldView · RenderManager", "Modes(取色策略) · Overlays(流线/河/板块/村庄)",
                  "WorldPicker(拾取) · MapExporter(2D 导出)"]),
    dict(name="Logic", h=128, fill=(253, 236, 236), border=(192, 80, 77),
         details=["0 板块 · 1 地形 · 2 气候 · 3 水文 · 4 索引", "Evolution(地壳演化) · Discretization(采样)",
                  "Composition(编排/WorldFacts) —— 零引擎调用"]),
    dict(name="Data", h=104, fill=(255, 249, 219), border=(201, 162, 39),
         details=["Spec(旋钮) · Constants(词表) · Carrier(载体)", "Maps(.wmap 线格式) · Generation · Work(信号)"]),
    dict(name="Utils", h=104, fill=(236, 239, 241), border=(96, 125, 139),
         details=["Geometry · H3/H3Grid(图算子) · Math · Noise", "Color · Random · Scene(装配辅助) · ITickable"]),
]

y = 206
positions = {}
for row in rows:
    positions[row["name"]] = band(BX, y, BW, row["h"], row["fill"], row["border"], row["name"], row["details"])
    y += row["h"] + 28

# satellite: Infrastructure (implements Application ports)
infra_y = positions["Application"][1] + 20
draw.rounded_rectangle([SAT_X, infra_y, SAT_X + SAT_W, infra_y + 96], radius=12,
                       fill=(245, 245, 245), outline=(156, 163, 175), width=2)
draw.text((SAT_X + 14, infra_y + 10), "Infrastructure", font=fonts["small"], fill=INK)
draw.text((SAT_X + 14, infra_y + 34), "Maps/ · Generation/", font=fonts["tiny"], fill=INK)
draw.text((SAT_X + 14, infra_y + 54), "实现 Application 的存储端口", font=fonts["tiny"], fill=INK)
draw.text((SAT_X + 14, infra_y + 74), "文件系统 / 地图包 → 端口", font=fonts["tiny"], fill=MUTED)
dashed_line([(SAT_X, infra_y + 22), (BX + BW + 4, infra_y + 22)], color=(120, 120, 120))

# satellite: Params (feeds Data/Spec)
params_y = positions["Data"][1] + 16
draw.rounded_rectangle([SAT_X, params_y, SAT_X + SAT_W, params_y + 74], radius=12,
                       fill=(245, 245, 245), outline=(156, 163, 175), width=2)
draw.text((SAT_X + 14, params_y + 10), "Params", font=fonts["small"], fill=INK)
draw.text((SAT_X + 14, params_y + 34), "WorldParams · WorldParamStore", font=fonts["tiny"], fill=INK)
draw.text((SAT_X + 14, params_y + 54), "res/params/*.json → Spec 实例", font=fonts["tiny"], fill=MUTED)
dashed_line([(SAT_X, params_y + 22), (BX + BW + 4, params_y + 22)], color=(120, 120, 120))

# dependency arrows between consecutive bands
labels = {
    "Scene": "实例化 .tscn",
    "UI": "装配页面 / 端口",
    "Application": "调用用例",
    "Runtime": "驱动 / 订阅",
    "Render": "只读世界事实",
    "Logic": "Spec / Carrier",
    "Data": "几何 / 网格 / 随机",
    "Utils": "工具函数",
}
order = [r["name"] for r in rows]
for upper, lower in zip(order, order[1:]):
    bottom = positions[upper][3]
    top = positions[lower][1]
    down_arrow(BX + 300, bottom + 4, top - 4, labels[lower])

# layering rules (left panel footer)
rules_y = positions["Utils"][3] + 22
draw.rounded_rectangle([BX, rules_y, BX + BW, rules_y + 132], radius=12,
                       fill=(250, 250, 250), outline=(203, 213, 225), width=2)
draw.text((BX + 16, rules_y + 10), "分层规则（改动前先对照）", font=fonts["small"], fill=INK)
rules = [
    "依赖只向下：上层改动不要求下层改；同层之间不互相引用",
    "Logic 纯逻辑：零引擎调用（无 GD.* / Node* / Mathf），可在无 Godot 的测试里整链跑",
    "跨层只走契约：IWorldFactsSource（事实）/ IMapLayer（图层）/ IPageHost（页面）/ LoadOperation（加载）",
    "纯工具进 Utils：只看有无世界语义，不看复用次数（AGENTS 3.1）；虚线 = 端口实现/契约引用",
]
rule_y = rules_y + 36
for rule in rules:
    for wrapped in wrap(rule, fonts["nano"], BW - 30):
        draw.text((BX + 16, rule_y), "• " + wrapped, font=fonts["nano"], fill=MUTED)
        rule_y += 18

# ── Right panel: pipeline & runtime ─────────────────────────────────────────
RX, RW = 1288, 1272
draw.text((RX + 4, 166), "二、生成管线（Logic 五阶段，因果序不可换）", font=fonts["panel"], fill=INK)
draw.text((RX + 6, 204), "输入：WorldSpec（Params/UI 生成配置下传）→ 输出：一份 WorldFacts 只读快照",
          font=fonts["small"], fill=MUTED)

stages = [
    ("0 · 板块", "PlatePipeline", ["PlateManager · PlateCrustOps", "PlateBoundaryBuilder", "产出：板块/壳型/边界集"]),
    ("1 · 地形", "TerrainPipeline", ["TerrainManager · LandFieldOps", "HighlandOps · OceanDepthOps", "ErosionOps · RemapOps"]),
    ("2 · 气候", "ClimatePipeline", ["MonthlyClimate：月温→气压→风→", "水汽→月降水", "OceanCurrentModel：表层洋流"]),
    ("3 · 水文", "HydrologyPipeline", ["RiverNetwork · RiverGraph", "BasinGraph · LakeState", "WaterTopology"]),
    ("4 · 索引", "IndexPipeline", ["FinalSpatialIndex", "nearest / distance / within"]),
]
stage_y, stage_h, gap = 244, 196, 12
stage_w = (RW - 4 * gap) / 5
stage_boxes = []
for index, (name, pipeline, detail) in enumerate(stages):
    x = RX + index * (stage_w + gap)
    draw.rounded_rectangle([x, stage_y, x + stage_w, stage_y + stage_h], radius=10,
                           fill=(253, 236, 236), outline=(192, 80, 77), width=2)
    draw.text((x + 12, stage_y + 10), name, font=fonts["stage"], fill=INK)
    draw.text((x + 12, stage_y + 36), pipeline, font=fonts["micro"], fill=MUTED)
    line_y = stage_y + 58
    for line in detail:
        for wrapped in wrap(line, fonts["micro"], stage_w - 20):
            draw.text((x + 12, line_y), wrapped, font=fonts["micro"], fill=INK)
            line_y += 18
    stage_boxes.append((x, stage_y, x + stage_w, stage_y + stage_h))
    if index:
        right_arrow(stage_boxes[index - 1][2] + 1, x - 1, stage_y + 22, (192, 80, 77))

# WorldFacts snapshot
facts_y = stage_y + stage_h + 26
draw.rounded_rectangle([RX, facts_y, RX + RW, facts_y + 96], radius=12,
                       fill=(255, 249, 219), outline=(201, 162, 39), width=2)
draw.text((RX + 16, facts_y + 12), "WorldFacts —— 一份只读快照，全工程唯一的取数口",
          font=fonts["small"], fill=INK)
draw.text((RX + 16, facts_y + 40),
          "网格 Ball · 地形/地表 · 构造/边界 · 气候(温度/气压/风/降水/洋流) · 水文(河网/流域/湖泊/水系) · 空间索引",
          font=fonts["micro"], fill=MUTED)
draw.text((RX + 16, facts_y + 64),
          "下游（Render / UI / Application / Final 查询）只读它，不顺着阶段句柄翻中间产物",
          font=fonts["micro"], fill=MUTED)
down_arrow(RX + RW / 2, stage_y + stage_h + 3, facts_y - 3, None, (201, 162, 39))
draw.text((RX + RW / 2 + 12, stage_y + stage_h + 6), "五阶段产物 → 一份快照",
          font=fonts["micro"], fill=MUTED)

# Runtime chains
chain_top = facts_y + 126
draw.text((RX + 4, chain_top), "三、三条运行链路", font=fonts["panel"], fill=INK)

STEP_W, STEP_GAP, STEP_H = 188, 22, 68


def chain(y, title, steps, note, color):
    draw.text((RX + 6, y), title, font=fonts["name"], fill=color)
    x = RX + 6
    top = y + 32
    for index, (box_title, box_note) in enumerate(steps):
        draw.rounded_rectangle([x, top, x + STEP_W, top + STEP_H], radius=9,
                               fill=(255, 255, 255), outline=color, width=2)
        draw.text((x + 9, top + 7), box_title, font=fonts["nano"], fill=INK)
        note_y = top + 26
        for line in wrap(box_note, fonts["nano"], STEP_W - 16)[:2]:
            draw.text((x + 9, note_y), line, font=fonts["nano"], fill=MUTED)
            note_y += 15
        if index < len(steps) - 1:
            right_arrow(x + STEP_W + 1, x + STEP_W + STEP_GAP - 1, top + 18, color)
        x += STEP_W + STEP_GAP
    draw.text((RX + 8, top + STEP_H + 4), note, font=fonts["micro"], fill=MUTED)


chain(chain_top + 40, "生成链（新地图）", [
    ("配置页动作端口", "Application/Pages"),
    ("WorldGenerationJob", "Application/Loading"),
    ("WorldManager", "Runtime 运行时宿主"),
    ("WorldGenSimulation", "Logic.Composition.Run(spec)"),
    ("五阶段", "0→1→2→3→4"),
    ("WorldView", "Render 重烘球面"),
], "跨页长任务由 AppLoadCoordinator 承接，进度经 LoadOperation 上报；失败回退配置页", (76, 154, 106))

chain(chain_top + 178, "演化链（地壳运动）", [
    ("WorldEvolutionSession", "Runtime 播放/暂停/跳步"),
    ("EvolutionSimulation", "Logic.Evolution 候选帧"),
    ("EvolutionFrame", "Data.Carrier 冻结帧"),
    ("CrustProjection", "Surface 逐格投影"),
    ("WorldSurfaceInput", "Composition 输入"),
    ("1→2→3→4", "复用地形/气候/水文"),
], "候选帧校验失败不覆盖上一已提交帧；播放提交在场景线程完成", (142, 106, 184))

chain(chain_top + 316, "存档链（.wmap）", [
    ("GeographySnapshotCapture", "从 WorldFacts 只读捕获"),
    ("MapPackageWriter", "Data.Maps 分块+校验"),
    (".wmap", "原子写出到地图库"),
    ("MapPackageReader", "严格校验后读取"),
    ("ImportedWorldFactsFactory", "零生成重建事实"),
    ("同一 WorldFacts", "浏览/演化共用契约"),
], "Application/Storage 定义端口，Infrastructure/Maps 用文件系统实现", (63, 127, 191))

# Contracts
contract_top = chain_top + 476
draw.text((RX + 4, contract_top), "四、关键契约与边界", font=fonts["panel"], fill=INK)
contracts = [
    ("IWorldFactsSource", "生成与导入共用的只读世界事实来源；Render 经它取数"),
    ("IMapLayer / IMapLayerDataSource", "图层契约：SetContext 唯一数据入口，Changed 驱动重绘"),
    ("LoadOperation / ILoadProgress", "加载状态机与进度；迟到提交必须被拒绝"),
    ("IPageHost / 页面动作端口", "页面 ↔ 组合根：Scene 只认端口，不认具体页面类型"),
    ("IMapOpener / IWorldGenerator", "组合根注入的长任务入口：打开地图 / 生成世界"),
    ("INoticeSink", "提示通道：首条消息时创建状态条，之后复用并只隐藏"),
    ("Spec / Constants / Carrier", "跨阶段只读契约；Logic 之间只通过它们耦合"),
]
line_y = contract_top + 38
for name, desc in contracts:
    draw.text((RX + 10, line_y), "• " + name, font=fonts["small"], fill=INK)
    draw.text((RX + 340, line_y + 2), desc, font=fonts["micro"], fill=MUTED)
    line_y += 26

line_y += 8
draw.text((RX + 10, line_y), "边界例外（现状，非目标态）", font=fonts["small"], fill=ALERT)
line_y += 24
for item in [
    "• Data/Carrier/Evolution 引用 Logic 的 EvolutionRelief：载体反向依赖算法，建议改为纯数据投影",
    "• Application/Layers 引用 Render 的图层契约：应用层编排渲染实现，层级略越界",
    "• Logic 必须零引擎调用（无 GD.* / Node* / Mathf），由测试与 code review 保障",
]:
    for wrapped in wrap(item, fonts["micro"], RW - 16):
        draw.text((RX + 12, line_y), wrapped, font=fonts["micro"], fill=MUTED)
        line_y += 19

draw.text((60, H - 30), "生成日期：2026-11-15 · 图源：sprout-docs 索引 + 源码 using 统计 · 结构变更后请同步更新本图",
          font=fonts["micro"], fill=MUTED)

os.makedirs(os.path.dirname(OUT), exist_ok=True)
img.save(OUT)
print(OUT)
