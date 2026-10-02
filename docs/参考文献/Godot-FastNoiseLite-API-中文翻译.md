# FastNoiseLite — 中文参考（全译）

> **原文**：[class_fastnoiselite.html](https://docs.godotengine.org/en/stable/classes/class_fastnoiselite.html)（Godot Engine 官方文档，stable 分支，据 2026-09-30 抓取版本）
> **许可与署名**：Godot 在线文档除另有说明外以 **CC BY 3.0（Creative Commons Attribution 3.0 Unported）** 发布，版权归 Juan Lini 及 Godot 社区所有。本中文全译在其许可下进行，译文与原文如有出入以原文为准。

---

**继承关系**：`Noise < Resource < RefCounted < Object`

使用 FastNoiseLite 库生成噪声。

## 描述

此类使用 FastNoiseLite 库生成噪声。该库是一系列噪声算法的集合，包括 Cellular、Perlin、Value 等。

大多数生成的噪声值位于 [-1, 1] 区间内，但并非总是如此。部分 cellular 噪声算法会返回大于 1 的结果。

## 属性

| 类型 | 属性 | 默认值 |
|---|---|---|
| CellularDistanceFunction | cellular_distance_function | 0 |
| float | cellular_jitter | 1.0 |
| CellularReturnType | cellular_return_type | 1 |
| float | domain_warp_amplitude | 30.0 |
| bool | domain_warp_enabled | false |
| float | domain_warp_fractal_gain | 0.5 |
| float | domain_warp_fractal_lacunarity | 6.0 |
| int | domain_warp_fractal_octaves | 5 |
| DomainWarpFractalType | domain_warp_fractal_type | 1 |
| float | domain_warp_frequency | 0.05 |
| DomainWarpType | domain_warp_type | 0 |
| float | fractal_gain | 0.5 |
| float | fractal_lacunarity | 2.0 |
| int | fractal_octaves | 5 |
| float | fractal_ping_pong_strength | 2.0 |
| FractalType | fractal_type | 1 |
| float | fractal_weighted_strength | 0.0 |
| float | frequency | 0.01 |
| NoiseType | noise_type | 1 |
| Vector3 | offset | Vector3(0, 0, 0) |
| int | seed | 0 |

## 枚举

### enum NoiseType

- **NoiseType TYPE_VALUE = 5**：在格点上赋随机值，再依据相邻值进行插值。
- **NoiseType TYPE_VALUE_CUBIC = 4**：与值噪声（TYPE_VALUE）类似，但更慢。峰与谷的方差更大。当值噪声用于生成凹凸贴图（bumpmap）时，三次噪声可以避免某些伪影。一般而言，若值噪声用于高度图或凹凸贴图，应始终使用此模式。
- **NoiseType TYPE_PERLIN = 3**：格点上放置随机梯度，对梯度的点积做插值得到格点之间的值。
- **NoiseType TYPE_CELLULAR = 2**：Cellular 同时包含 Worley 噪声与 Voronoi 图，会生成大量同值区域。
- **NoiseType TYPE_SIMPLEX = 0**：与 TYPE_PERLIN 不同，梯度存在于单纯形（simplex）格而非方形格上，避免方向性伪影。内部使用 FastNoiseLite 的 OpenSimplex2 噪声类型。
- **NoiseType TYPE_SIMPLEX_SMOOTH = 1**：TYPE_SIMPLEX 的改进版，质量更高但更慢。内部使用 FastNoiseLite 的 OpenSimplex2S 噪声类型。

### enum FractalType

- **FractalType FRACTAL_NONE = 0**：不使用分形噪声。
- **FractalType FRACTAL_FBM = 1**：用分数布朗运动（Fractional Brownian Motion）把各倍频组合成分形的方法。
- **FractalType FRACTAL_RIDGED = 2**：把各倍频组合成分形、呈现"山脊"外观的方法。
- **FractalType FRACTAL_PING_PONG = 3**：以 ping-pong（乒乓）效果把各倍频组合成分形的方法。

### enum CellularDistanceFunction

- **CellularDistanceFunction DISTANCE_EUCLIDEAN = 0**：到最近点的欧氏距离。
- **CellularDistanceFunction DISTANCE_EUCLIDEAN_SQUARED = 1**：到最近点的欧氏距离的平方。
- **CellularDistanceFunction DISTANCE_MANHATTAN = 2**：到最近点的曼哈顿距离（出租车度量）。
- **CellularDistanceFunction DISTANCE_HYBRID = 3**：DISTANCE_EUCLIDEAN 与 DISTANCE_MANHATTAN 的混合，得到弯曲的单元边界。

### enum CellularReturnType

- **CellularReturnType RETURN_CELL_VALUE = 0**：cellular 距离函数对同一单元内的所有点返回相同的值。
- **CellularReturnType RETURN_DISTANCE = 1**：cellular 距离函数返回一个由到最近点距离决定的值。
- **CellularReturnType RETURN_DISTANCE2 = 2**：cellular 距离函数返回到第二近点的距离。
- **CellularReturnType RETURN_DISTANCE2_ADD = 3**：到最近点的距离加上到第二近点的距离。
- **CellularReturnType RETURN_DISTANCE2_SUB = 4**：到第二近点的距离减去到最近点的距离。
- **CellularReturnType RETURN_DISTANCE2_MUL = 5**：到最近点的距离乘以到第二近点的距离。
- **CellularReturnType RETURN_DISTANCE2_DIV = 6**：到最近点的距离除以到第二近点的距离。

### enum DomainWarpType

- **DomainWarpType DOMAIN_WARP_SIMPLEX = 0**：使用 simplex 噪声算法扭曲域。
- **DomainWarpType DOMAIN_WARP_SIMPLEX_REDUCED = 1**：使用 simplex 噪声算法的简化版扭曲域。
- **DomainWarpType DOMAIN_WARP_BASIC_GRID = 2**：使用简单的噪声网格扭曲域（不如其他方法平滑，但性能更好）。

### enum DomainWarpFractalType

- **DomainWarpFractalType DOMAIN_WARP_FRACTAL_NONE = 0**：扭曲空间时不使用分形噪声。
- **DomainWarpFractalType DOMAIN_WARP_FRACTAL_PROGRESSIVE = 1**：逐倍频渐进地扭曲空间，产生更"液化"的失真。
- **DomainWarpFractalType DOMAIN_WARP_FRACTAL_INDEPENDENT = 2**：对每个倍频独立地扭曲空间，产生更混乱的失真。

## 属性说明

### CellularDistanceFunction cellular_distance_function = 0

`void set_cellular_distance_function(value: CellularDistanceFunction)` / `CellularDistanceFunction get_cellular_distance_function()`

决定到最近点/第二近点的距离如何计算。

### float cellular_jitter = 1.0

`void set_cellular_jitter(value: float)` / `float get_cellular_jitter()`

点相对其网格位置可偏移的最大距离。设为 0 得到均匀网格。

### CellularReturnType cellular_return_type = 1

`void set_cellular_return_type(value: CellularReturnType)` / `CellularReturnType get_cellular_return_type()`

cellular 噪声计算的返回类型。

### float domain_warp_amplitude = 30.0

`void set_domain_warp_amplitude(value: float)` / `float get_domain_warp_amplitude()`

设置相对原点的最大扭曲距离。

### bool domain_warp_enabled = false

`void set_domain_warp_enabled(value: bool)` / `bool is_domain_warp_enabled()`

若启用，将使用另一个 FastNoiseLite 实例来扭曲空间，造成噪声的失真。

### float domain_warp_fractal_gain = 0.5

`void set_domain_warp_fractal_gain(value: float)` / `float get_domain_warp_fractal_gain()`

决定用于扭曲空间的噪声中，后续每一层的强度。低值更强调低频基础层，高值更强调高频层。

### float domain_warp_fractal_lacunarity = 6.0

`void set_domain_warp_fractal_lacunarity(value: float)` / `float get_domain_warp_fractal_lacunarity()`

用于扭曲空间的分形噪声在倍频之间的频率变化，也称"隙度"（lacunarity）。增大该值会提高倍频，产生细节更细、外观更粗糙的噪声。

### int domain_warp_fractal_octaves = 5

`void set_domain_warp_fractal_octaves(value: int)` / `int get_domain_warp_fractal_octaves()`

为得到用于扭曲空间的分形噪声的最终值而采样的噪声层数。

### DomainWarpFractalType domain_warp_fractal_type = 1

`void set_domain_warp_fractal_type(value: DomainWarpFractalType)` / `DomainWarpFractalType get_domain_warp_fractal_type()`

用于扭曲空间的分形噪声把各倍频组合起来的方式。

### float domain_warp_frequency = 0.05

`void set_domain_warp_frequency(value: float)` / `float get_domain_warp_frequency()`

用于扭曲空间的噪声的频率。低频产生平滑的噪声，高频产生更粗糙、更颗粒化的噪声。

### DomainWarpType domain_warp_type = 0

`void set_domain_warp_type(value: DomainWarpType)` / `DomainWarpType get_domain_warp_type()`

扭曲算法。

### float fractal_gain = 0.5

`void set_fractal_gain(value: float)` / `float get_fractal_gain()`

决定分形噪声中后续每一层噪声的强度。低值更强调低频基础层，高值更强调高频层。

### float fractal_lacunarity = 2.0

`void set_fractal_lacunarity(value: float)` / `float get_fractal_lacunarity()`

相邻倍频之间的频率乘数。增大该值会提高倍频，产生细节更细、外观更粗糙的噪声。

### int fractal_octaves = 5

`void set_fractal_octaves(value: int)` / `int get_fractal_octaves()`

为得到分形噪声类型的最终值而采样的噪声层数。

### float fractal_ping_pong_strength = 2.0

`void set_fractal_ping_pong_strength(value: float)` / `float get_fractal_ping_pong_strength()`

设置分形 ping-pong 类型的强度。

### FractalType fractal_type = 1

`void set_fractal_type(value: FractalType)` / `FractalType get_fractal_type()`

把各倍频组合成分形的方法。

### float fractal_weighted_strength = 0.0

`void set_fractal_weighted_strength(value: float)` / `float get_fractal_weighted_strength()`

权重越高，当低频层产生较大影响时，高频层的影响就越小。

### float frequency = 0.01

`void set_frequency(value: float)` / `float get_frequency()`

所有噪声类型共用的频率。低频产生平滑的噪声，高频产生更粗糙、更颗粒化的噪声。

### NoiseType noise_type = 1

`void set_noise_type(value: NoiseType)` / `NoiseType get_noise_type()`

所使用的噪声算法。

### Vector3 offset = Vector3(0, 0, 0)

`void set_offset(value: Vector3)` / `Vector3 get_offset()`

按给定的 Vector3 平移噪声输入坐标。

### int seed = 0

`void set_seed(value: int)` / `int get_seed()`

所有噪声类型共用的随机数种子。

---

## 附：项目内使用注意（本仓库自拟，非官方文档）

1. **纯 .NET 测试宿主禁用**：Godot 的 `FastNoiseLite` 是引擎类型，在纯 .NET 测试宿主中实例化会 access violation 崩溃进程（见《地壳运动-02》§11 教训表）。因此 `Utils/SphericalFbmNoise.cs` / `SphericalBlobNoise.cs` 为自研实现，**不得**替换为该类。本译文供参数语义参考与离线（编辑器/工具链）场景备查。
2. **参数对照**：`fractal_gain` 默认 0.5 = gain 2^(−H) 中 H=1 的档位，与 iquilez 实测山体轮廓 −9 dB/octave 的自然地形档一致（见本目录《IQ-噪声文章集-中文版》）；`SphericalFbmNoise` 的"基波长 km"口径与 `frequency` 互为倒数（波长 ≈ 1/frequency，需按单位球坐标换算）。
3. 基类 `Noise` 的通用方法（`get_noise_1d/2d/3d`、`get_image_2d/3d`、`get_seamless_image_2d/3d`）见 [class_noise.html](https://docs.godotengine.org/en/stable/classes/class_noise.html)。原库（MIT，含独立 C# 版本）：<https://github.com/Auburn/FastNoiseLite>。
