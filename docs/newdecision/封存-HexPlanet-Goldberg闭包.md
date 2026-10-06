# 封存 · HexPlanet Goldberg 闭包设计史料

> **性质**：本文档是**知识封存**，不是设计文档，也不是待办清单。
>
> **背景**：`scripts/HexPlanet/` 的四个文件（`GoldbergBuilder` / `SubdividedMesh` / `HexTile`）
> 与 `scripts/PlanetLOD/ChunkMeshBuilder.cs` 已按 D 步决策（`9f2dc52` / `f4cc534`）
> 从生产树删除。本文在**删除代码之前**把其中不可再生的工程经验提取成文 ——
> **生产代码删除，设计知识保留**。
>
> **提取原则**：只保留"重建成本高、且不依赖旧线范式"的知识。
> 凡是"只有在该范式下才有意义"的结论，记录其**为何被取代**而非成功形态。
> **代码本体不进入任何目录** —— 封存的是经验，不是源码副本。

- 日期：2026-10-06
- 关联提交：`9f2dc52`（D-A 清退闭包）、`f4cc534`（D-B 清退 PlanetColors）
- 关联取证：`docs/审查报告-D-Legacy逐项决策.md`
- 源码取回：`git show 072d415:scripts/HexPlanet/GoldbergBuilder.cs`

---

## 1. 被封存的四个文件

| 文件 | 行数量级 | 角色 |
|---|---|---|
| `HexPlanet/GoldbergBuilder.cs` | ~150 | **核心**：顶点 → 六边形/五边形 tile 的对偶构造 |
| `HexPlanet/SubdividedMesh.cs` | ~200 | 三角网格去重 + 顶点↔三角形双向索引 + 三角形邻接 |
| `HexPlanet/HexTile.cs` | 17 | 数据类：tile 的中心/角点/邻居/五边形标志 |
| `PlanetLOD/ChunkMeshBuilder.cs` | ~257 | 从 tile 子集构建"每 tile 扁平着色"的渲染网格 |

---

## 2. GoldbergBuilder —— 核心设计（用户要求的六项）

### 2.1 原用途

**构造 Goldberg / `10n²+2` 顶点体系的六边形-五边形对偶网格。**

输入是 icosahedron 细分后的三角网格，输出是"每个顶点对应一个多边形格子（tile）"。
这是**对偶网格构造**（dual mesh / Voronoi 式）：三角网格的**顶点** → 对偶网格的**面**。

### 2.2 关键算法（重建成本最高的部分）

对每个顶点 `vertexIndex`：

```
1. 取该顶点的相邻三角形列表   mesh.VertToTris[vertexIndex]
2. 对每个相邻三角形算"外心"   ComputeTriangleCircumcenter(a,b,c,radius)
   ── 实际实现是质心投影到球面：(a+b+c)/3 归一化 × radius
3. 用该顶点的单位方向建立切平面局部坐标系：
     centerDirection = UniqueVerts[vertexIndex].Normalized()
     tangent    = centerDirection × Up   （退化时改用 × Right）
     bitangent  = centerDirection × tangent
4. 把各角点按切平面内的极角排序 ⇒ 得到有序 Corners
5. 邻居 = 相邻三角形中除自身外的顶点集合（去重）
6. IsPentagon = (邻居数 == 5)
```

⚠️ **退化处理**：`tangent` 在极点附近与 `Up` 平行时会退化，代码检测
`LengthSquared() < 0.001` 后改用 `Right` 作参考轴。这是球面局部坐标系的经典陷阱。

⚠️ **并行设计**：外层用 `Parallel.For` 遍历顶点，每个 tile 只读共享网格数据、
只写自己的数组槽位 ⇒ **无锁无竞态**。这个"每格独占槽位"的模式是本项目后来
多处并行实现的原型。

### 2.3 拓扑特征（可直接复用的结论）

| 量 | 值 | 说明 |
|---|---|---|
| tile 总数 | **`10n²+2`** | 等于唯一顶点数（与 `Icosahedron.VertexCountFor` 同源公式） |
| 五边形数 | **恒为 12** | 对应 icosahedron 的 12 个原始顶点 |
| 六边形数 | `10n²+2 − 12` | 其余全部 |
| 每 tile 邻居数 | **5 或 6**（仅此两种） | 模块级不变量，曾被 `ModuleTest_SubdividedSphere_AllDegrees5Or6` 钉住 |

**实测数据**（取自已删除的 `HexPlanetTests`）：

| n | tile 总数 | 五边形 | 六边形 |
|---|---|---|---|
| 1 | 12 | 12 | 0 |
| 2 | 42 | 12 | 30 |
| 4 | 162 | 12 | 150 |

### 2.4 与当前 `LogicGrid/GameGrid` 的功能重叠（**关键结论**）

**两者都建立在同一套 `10n²+2` 顶点体系上，但完备度不同：**

| 能力 | `GoldbergBuilder`（已清退） | `LogicGrid/GameGrid`（现行） |
|---|---|---|
| 顶点来源 | `Icosahedron.Subdivide` → `SubdividedMesh` | `Icosahedron.Subdivide` **直接**取顶点 |
| 格子 = | 顶点 + **对偶多边形**（Corners/Neighbors） | **顶点胞**（顶点本身即格） |
| 角点几何 | ✅ 显式算出 `Corners`（外心序列） | ❌ 不需要 |
| 邻居表 | ✅ 显式 `Neighbors` | ❌ 运行时按距离阈值推 |
| 五边形标志 | ✅ `IsPentagon` | ❌ 不需要 |
| 用途 | 渲染网格（tile 多边形填充） | 人文模拟网格（逐格数据容器） |

**结论**：`GoldbergBuilder` 提供的是**更完备的对偶构造**，而现行 `GameGrid` 只需要
"顶点即格"这一退化形式。**当渲染不再需要 tile 多边形时，完备对偶构造失去消费者。**

### 2.5 当前项目为何不再需要它

三层原因，按决定性排序：

1. **渲染路径换了范式**。现行表现层走 `Render/BallView`：H3 球壳 + 122 基块 × 2 档 LOD +
   全星一张 RGBA8 颜色纹理。**不经过任何 tile 多边形网格**。
   `ChunkMeshBuilder`（tile → 渲染网格）因此彻底失去调用者，`GoldbergBuilder`
   随之失去唯一的下游。
2. **逻辑格用退化形式就够**。`LogicGrid/GameGrid` 需要的是"逐格数据容器"，
   不需要角点几何与显式邻居表。
3. **空间底座已换成 H3**。现行世界生成的采样与邻接由 `Ball`（H3 六边形）承担，
   icosahedron 细分体系退居为 `Icosahedron` 单类（用于计数公式与测试夹具）。

### 2.6 替代来源在哪里

| 被清退能力 | 现行替代 | 位置 |
|---|---|---|
| 逐格邻居查询 | `Ball.CellNeighbors` / `CellNeighborDirs` | `Spatial/Ball/Ball.cs` |
| 格中心方向 | `Ball.CellDirs` | 同上 |
| 格顶点（角点）方向 | `Ball.VertexPositions` + `VertexIndexOf` | 同上 |
| 渲染网格构建 | `Render/BallView`（分块 LOD + 颜色纹理） | `Render/BallView.cs` |
| 逐格栅格化 | `WorldGen/Discretization/H3TerrainSampler`（格心 + 6 角点加权） | 同上 |
| **保留未删**：顶点计数公式 `10n²+2` | `Icosahedron`（**未清退**） | `HexPlanet/Icosahedron.cs` |

⚠️ 注意：**`Icosahedron` 没有被清退**。它是全项目唯一公式源，且被 9+ 个测试文件
当作"合成小网格"夹具（`Icosahedron.Subdivide(2, 6371f)` → 42 顶点）。
清退的只是建立在它之上的对偶构造层。

### 2.7 为什么不迁移、为什么直接清退

**不迁移的理由**：
- 迁移目标是 H3 六边形体系，而 `GoldbergBuilder` 是 icosahedron 顶点对偶 —— **两者拓扑不同源**，
  强行迁移等于用 H3 重写，等价于新实现（那应该在新架构下重新设计，而不是搬运旧代码）。
- 它的**完备对偶**（Corners/Neighbors/IsPentagon）在新架构里**没有消费者**：
  H3 的角点由 `H3.CellToVertexes` 提供，邻居由 `Ball` 提供。
- 项目永久原则第 1 条（防膨胀三问）第三问：「现在有真实消费者吗？」—— 答案是没有。

**直接清退的理由**：
- 生产消费者为 **0**（实测，非按目录名判定）。
- 整个子图**只依赖保留项 `Icosahedron`** ⇒ 可整体切除，不影响其它消费者。
- 项目惯例是"**资产封存代替代码迁移**"：知识进文档，代码不进代码树。

**❌ 明确不做的**：把 `GoldbergBuilder.cs` 挪到某个 `Legacy/` 目录继续留在树上。
那会制造"未来可能继续用"的假象，与"目录即架构边界"的治理目标相反。

---

## 3. 附带封存：另外三个文件的设计要点

### 3.1 `SubdividedMesh` —— 三角网格的邻接基础设施

输入 `verts + indices`，产出四个索引结构：

| 输出 | 含义 | 构造方式 |
|---|---|---|
| `UniqueVerts` | 去重后顶点 | 按坐标**量化去重**（`VertexKey` 按 1km 量化，`round(v)`） |
| `Tris` | 三角形 `(v0,v1,v2)` 索引三元组 | 遍历 indices 步长 3 |
| `VertToTris` | 顶点 → 所属三角形列表 | 反向索引 |
| `TriNeighbors` | 三角形 → 相邻三角形（共享边即邻） | `BuildTriangleNeighbors()` |

**可复用知识**：
- ⚠️ **去重必须按 `(顶点, 半径)` 同口径量化**。代码注释明确警告：`VertexKey` 按 1km 量化，
  所以调用方必须传**真实半径（km）** 再归一化；半径太小会导致不同顶点量化后误合并。
  这是"几何量化 + 球面尺度"一起才成立的约定。
- `VertToTris` / `TriNeighbors` 是**双向索引**（顶点↔三角形、三角形↔三角形）。
  现行 `Ball` 提供的是格↔格与格↔顶点，**没有三角形层级** —— 因为 H3 是六边形体系，无三角形。

### 3.2 `HexTile` —— 17 行数据类

```csharp
class HexTile {
    int       Id;                 // = 顶点索引（Tiles 按索引定序，无空槽）
    Vector3   Center;             // 球面位置（单位方向 × radius）
    Vector3[] Corners;            // 有序角点（切平面极角排序后）
    int[]     CornerFaceIndices;  // 每个角点来自哪个三角形
    int[]     Neighbors;          // 邻居 tile 索引
    bool      IsPentagon;         // 邻居数 == 5
    float     Elevation;          // 逐 tile 高度（渲染用）
}
```

**可复用知识**：`Id == 索引` 的不变量（测试 `HexTile_FieldsConsistent` 钉过）——
"列表下标即身份"能省掉一层映射表，代价是删除时不留空槽。
这与现行 `Ball` 的 `CellIds` ↔ 索引对齐是同一设计取向。

### 3.3 `ChunkMeshBuilder` —— 几何/颜色分离的渲染网格构建

**核心设计（值得记住）**：

> **`BuildGeometry` 产出顶点/法线/索引（与颜色无关，可缓存）；
> `BuildColors` 按图层/色板重算颜色。几何不变时切换图层只需重算颜色 ⇒ 秒级。**

- 顶点**按 tile 分割不共享** ⇒ 所有并行路径无锁无竞态（代价是顶点冗余，换来并行安全）。
- 每 tile 一个**扁平色**（flat per-tile color），fan 三角化，外向绕序。
- 法线取自**位移后位置**（flat facet 外观，非平滑着色）。

**与现行做法的关系**：现行 `Render/BallView` 也做"颜色纹理烘焙"，但走的是
**全星一张 RGBA8 纹理**（`CellColorAt` → 纹理像素），而不是"几何/颜色分离 + 逐 tile 顶点色"。
两者解决同一问题（切图层不重建几何），现行方案的纹理通路更适合 H3 的逐格索引。

---

## 4. 复活条件（将来若需要，从这里开始）

若未来出现以下任一需求，本文档是对应的起点：

| 需求 | 应先读 | 注意 |
|---|---|---|
| 需要**球面六边形-五边形对偶网格**（非 H3） | §2.2 算法 + §2.3 拓扑 | 五边形恒 12 个是硬约束 |
| 需要**三角形层级**的邻接（H3 无三角形） | §3.1 `SubdividedMesh` | 注意量化口径与半径的耦合 |
| 需要**几何/颜色分离**的渲染缓存策略 | §3.3 `ChunkMeshBuilder` | 现行 `BallView` 已有等价能力，先判断是否重复 |
| 需要 `10n²+2` 体系的**角点/邻居**完备形式 | §2.4 重叠分析 | 先问：H3 的 `CellToVertexes`/`Ball.CellNeighbors` 是否已够用 |

⚠️ **前置检查**：复活前先过项目永久原则第 1 条（防膨胀三问）。
本闭包被清退的根因是"**完备度超出消费者需要**"—— 复活时应先确认新消费者
**真的需要完备形式**，而不是"顺手用旧的"。

---

## 5. 一句话结论

`GoldbergBuilder` 系一套**正确、可并行、拓扑自洽**的球面对偶网格构造，
其失效不是质量问题，而是**范式更替**：项目从"icosahedron 顶点对偶 + tile 多边形渲染"
转向"H3 六边形 + 分块 LOD + 颜色纹理"。**代码删除，经验存此。**
