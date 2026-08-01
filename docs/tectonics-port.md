# tectonics.js → C# 移植方案（world 项目）

> 状态：2026-08-02 启动。原板块方案（SotE 势能竞争）已全部删除，
> 改为移植 [tectonics.js](https://github.com/davidson16807/tectonics.js)
> （231★，CC-BY-4.0，球面板块构造模拟）。
> 源码参考：`docs/tectonics-ref/`（58 个 JS 文件，522KB，含原 LICENSE）。

## 为什么选它

- **真实球面板块运动**：每块板有局部网格 + 旋转矩阵（欧拉旋转），不是等距柱状伪板块
- **物理驱动**：Schellart 2010 板块速度模型（slab pull + 拖曳力平衡）、均衡补偿（isostasy）
- **完整岩石圈循环**：板块移动→合并→裂谷（rifting）→俯冲（subduction）→侵蚀/风化/变质/成岩
- 用户已否决 SotE 势能竞争方案（种子敏感、调参打地鼠、校准破坏形状）

## 总体架构

```
tectonics.js                              C# 移植
─────────────                             ─────────
Grid.js (球面网格)                 →      Tectonics/SphereGrid.cs
VoronoiSphere.js (最近邻)         →      并入 SphereGrid
RasterStackBuffer.js (临时缓冲)   →      简化：局部 float[]/Vector3[] 直接分配
Rasters.js (Uint8/Float32/Vector) →      Tectonics/Fields.cs (float[]/byte[]/Vector3[])
Fields/ (Scalar/Vector/Uint8/16)  →      并入 Fields.cs
Morphology/BinaryMorphology.js    →      并入 Fields.cs (Dilate/Erode/Closing)
Crust.js (8 物质场)               →      Tectonics/Crust.cs
RockColumn.js                     →      Tectonics/RockColumn.cs (常量表)
Plate.js                          →      Tectonics/Plate.cs
Lithosphere.js (主循环)           →      Tectonics/Lithosphere.cs
Tectonophysics.js (速度/旋转/分割) →      Tectonics/Tectonophysics.cs
FluidMechanics.js (均衡/流体)     →      Tectonics/FluidMechanics.cs
CrustGenerator.js (初始地壳)      →      Tectonics/CrustGenerator.cs
Simulation.js (时间驱动)          →      Tectonics/TectonicsSimulation.cs
```

**对接现有管线**：模拟产出球面网格每格的位移（elevation）→ 采样到
512×256 等距柱状海拔场 → MapGenerator 现有气候/biome/存档（不动）。

## 依赖关系（移植顺序）

```
1. SphereGrid ──┐
2. Fields ──────┤ (无内部依赖，可并行)
                ▼
3. Crust ──→ 4. RockColumn(常量) ──→ 5. CrustGenerator
                │
                ▼
6. Tectonophysics (速度/旋转/图像分割)
                │
                ▼
7. Plate ──→ 8. Lithosphere (主循环)
                │
                ▼
9. 对接采样 (球面→等距柱状)
```

## 关键算法笔记（来自源码研读）

### 1. 球面网格（Grid.js + VoronoiSphere.js）
- Icosahedron 细分网格，`grid.pos` = 顶点位置 VectorRaster
- 邻居预计算：`neighbor_lookup`（顶点→相邻顶点数组），O(1) 查邻居
- `getNearestIds(pos_field, result)`：Voronoi 最近邻——把任意位置映射到最近网格顶点
  - 用 `_voronoi = new VoronoiSphere(pos, min_dist/8, max_dist)` 预构建空间索引
  - 这是**板局部网格 ↔ 世界全局网格**互相映射的桥梁（`local_pos_of_global_cells` / `global_pos_of_local_cells`）
- **我们已有 Icosahedron.cs（n 细分，verts=10n²+2）**，可复用顶点/面/邻居，需补最近邻查找

### 2. Raster 场运算（核心数值层）
- SoA 结构：VectorRaster = {x: Float32Array, y, z, everything, grid}
- 常见运算（ScalarField/VectorField/Uint8Field）：
  - `mult_scalar/add_scalar/gt_scalar/lt_scalar/eq_scalar`
  - `gradient`（标量场→向量场）、`diffusion_by_constant`（拉普拉斯平滑，迭代 n 次）
  - `cross_vector_field`（叉积）、`dot_vector_field`、`normalize`
- Morphology（BinaryMorphology）：`erosion/dilation/closing/margin/padding/difference`
  - 用于 rifting 边界检测、subducted 区域检测
- 插值：`Float32RasterInterpolation.lerp(breaks, values, field)`——分段线性插值，
  CrustGenerator 用它把海拔→各物质厚度（关键！）

### 3. Crust 模型（8 种物质场，单位 km·密度 = kg/m²）
- 8 个场：sediment, sedimentary, metamorphic, felsic_plutonic, felsic_volcanic,
  mafic_volcanic, mafic_plutonic, age
- 守恒组（5 种 felsic 类）：sediment/sedimentary/metamorphic/felsic_plutonic/felsic_volcanic
  ——felsic 总量守恒，用于质量守恒校验
- 非守恒组：mafic_volcanic/mafic_plutonic/age（可新增/重置）
- 派生场（Memo 惰性缓存）：thickness → total_mass → density → buoyancy → displacement
  - `thickness = Σ 各物质厚度`
  - `total_mass = Σ 各物质质量`
  - `density = total_mass / thickness`
  - `displacement = thickness - thickness×density/mantle_density`（isostatic，均衡补偿）
  - `buoyancy = (mantle_density - density) × surface_gravity`（驱动板块运动）

### 4. 板块速度（Tectonophysics.guess_plate_velocity，Schellart 2010）
- 概念：板块像泡沫垫浮在水池，一侧挂铅块下沉（slab pull），拖曳力平衡→终端速度
- `v = S·F·(WLT)^(2/3) / (18·c·μ)`，其中 W/L/T=俯冲带宽/长/厚，S=形状参数
- 硬编码常量：width=300km, length=600km, thickness=100km, S=0.725, c=4.025, R=6367km
- `lateral_speed = buoyancy × (effective_area / (18·μ_mantle) × S/c / R)` → rad/My
- `velocity = boundary_normal × lateral_speed`
- **移植时保留常量，后续可参数化**

### 5. 旋转矩阵（get_plate_rotation_matrix3x3）
- 球面上刚体运动 = 绕世界中心旋转（线性运动） + 绕板块质心旋转（角运动）
- 角速度 = `cross(velocity, offset) / |offset|²`，加权平均后 × 时间步长
- `Matrix3x3.FromRotationVector` 构造旋转矩阵
- **坑**：旋转矩阵可能 NaN → 检测后回退 Identity

### 6. 初始板块分割（guess_plate_map）
- 输入：软流圈速度场（流体压力梯度，通过多分辨率扩散平滑）
- `VectorImageAnalysis.image_segmentation(vector_field, 7, 200)`——图像分割出初始板块
- 然后 dilation/closing 平滑边界
- **移植简化**：可以先用"球面噪声+分水岭/聚类"替代完整图像分割（最小原型阶段）

### 7. 主循环（Lithosphere.applyChanges）
```
每时间步：
1. calculate_deltas: 侵蚀/风化/成岩/变质 → 全局 crust_delta
2. integrate_deltas: 把 delta 应用到各板块局部 crust（按 top_plate_map 过滤）
3. move_plates: 每块板旋转（欧拉旋转矩阵 × 局部/全局坐标映射）
4. supercontinentCycle.update: 周期分裂板块
5. merge_plates_to_master: 各板块 crust 合并回全局（按密度决定谁在顶层）
6. update_rifting: 裂谷检测（边界+可裂谷区域）→ 新洋壳
7. update_subducted: 俯冲检测（被压在下层+密度>地幔）→ 消减+变质+增生
```
- 合并规则：`density < master_density` 的板在上面（浮力大的覆盖）
- 裂谷规则：板块内"count==0 或 (count==1 且自己是顶层)"的区域，侵蚀 1 层后边缘 → 裂谷
- 俯冲规则：非顶层 + density>mantle → 消减；被消减的 felsic 转 metamorphic

### 8. 超级大陆循环（SupercontinentCycle）
- 每 150 My 分裂一次板块（用当前软流圈速度场重新分割）

## 移植坑清单

1. **JS 无类型**：`Uint8Raster(grid)` 是构造函数（返回数组对象），不是类型声明——C# 直接 `byte[]`
2. **RasterStackBuffer.scratchpad**：全局临时缓冲池，函数内 allocate/deallocate——C# 直接局部分配更安全（性能可接受，网格 ~10k 顶点）
3. **Memo 惰性缓存**：invalidate 后下次访问重算——C# 简单字段 + 显式重算（原型阶段不做缓存）
4. **Matrix3x3 布局**：JS 是 9 元素数组（列主序？需核对 FromRotationVector 实现）——C# 用 Vector3 旋转向量 + 轴角矩阵构造
5. **NaN 防护**：旋转矩阵/角度可能 NaN，必须检测回退
6. **VoronoiSphere 最近邻**：空间索引，C# 可先线性扫描（网格 10k 顶点 × 每板 10k = 1e8，慢但原型可跑；后续加 KD-tree/球面 hash）
7. **Three.js 依赖**：Grid 里 `new THREE.IcosahedronGeometry(1, 4)` 构造网格——C# 用我们自己的 Icosahedron 细分，**注意顶点数不同**（tectonics 用 1 次细分≈42 顶点起步，我们 n 参数可调）
8. **性能**：JS 用 TypedArray SoA + 批量运算优化，C# 直接循环即可（JIT 后更快）

## 验证策略（每阶段 headless）

- 网格层：顶点数 = 10n²+2、邻居对称性、最近邻往返一致
- 数值层：diffusion 收敛、gradient 数值正确（与手算对照）
- Crust：守恒量不漂移（felsic 总量恒定）
- 板块层：板块数、每板面积、位移场范围
- 最终：位移场采样到 512×256 等距柱状 → 海拔图预览 + land% 统计

## 里程碑

- M1（最小原型）：SphereGrid + Fields + Crust + 简化速度 + 简化分割
  + 几轮迭代 + 位移场输出。验证：板块能移动、边界有山脉/海沟形态
- M2：完整 Tectonophysics（Schellart 速度 + 真旋转矩阵）+ 图像分割
- M3：完整 Lithosphere 循环（裂谷/俯冲/侵蚀/变质/成岩）+ 超级大陆循环
- M4：对接 MapGenerator（球面→等距柱状采样 + 存档 + 预览）

## 许可

CC-BY-4.0（可商用，需署名）。移植代码保留头部注释标注来源。
