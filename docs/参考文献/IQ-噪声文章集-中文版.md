# Inigo Quilez 噪声文章四篇（中文版）

> **原文**（iquilezles.org，© Inigo Quilez。站内说明：代码片段按 MIT 许可，其余内容保留版权）：
> [fBM](https://iquilezles.org/articles/fbm/) · [Domain warping](https://iquilezles.org/articles/warp/) · [Voronoise](https://iquilezles.org/articles/voronoise/) · [Value noise derivatives](https://iquilezles.org/articles/morenoise/)
> **说明**：四篇合订，按原文小节**完整编译**为中文（覆盖原文全部内容，非逐字翻译）。同站相关篇目：[Gradient noise derivatives](https://iquilezles.org/articles/gradientnoise/)、[Smooth voronoi](https://iquilezles.org/articles/smoothvoronoi/)、[Filterable procedurals](https://iquilezles.org/articles/filterableprocedurals/)。

---

## 一、fBM（Fractional Brownian Motion，分数布朗运动）

### 引言

BM（布朗运动）是白噪声的积分：位置增量随机。如果增量之间带"记忆"，就得到分数布朗运动——正相关增量让曲线更平滑，负相关更粗糙。**Hurst 指数 H**（0 到 1）同时控制记忆、分形维数与功率谱：H = 1/2 是标准 BM；H 越小越粗糙，H 越大越平滑。

### 基本思路

fBM 就是把频率倍增、振幅指数衰减的噪声叠加起来。规范形式：

```c
float fbm( in vecN x, in float H )
{
    float t = 0.0;
    for( int i=0; i<numOctaves; i++ )
    {
        float f = pow( 2.0, float(i) );
        float a = pow( f, -H );
        t += a*noise(f*x);
    }
    return t;
}
```

更常见（也更便宜）的等价写法用增益 G = 2⁻ᴴ：

```c
float fbm( in vecN x, in float H )
{
    float G = exp2(-H);
    float f = 1.0;
    float a = 1.0;
    float t = 0.0;
    for( int i=0; i<numOctaves; i++ )
    {
        t += a*noise(f*x);
        f *= 2.0;
        a *= G;
    }
    return t;
}
```

"Octave（倍频程）"一词来自音乐：频率翻倍。约 24 个倍频就能以 2 m 的细节覆盖整个地球。

两种防伪影技巧：**失谐**（倍频比 2.0 改成 2.01 或 1.99）或在 2D 中**旋转采样域**，都能避免不真实的波峰对齐。也可以用 IFFT（逆快速傅里叶）做 fBM，但代价高（正弦波的谱稀疏，而噪声的谱很宽）。

### 自相似性

横向放大 U 倍时，纵向需放大 V = U⁻ᴴ 才能自相似——所以 G = 2⁻ᴴ 正是自相似缩放因子。H = 1/2（G ≈ 0.707）对应各向异性缩放（像股票曲线；真实市场更接近 H ≈ 0.6）。H = 1（G = 0.5）对应各向同性缩放——**这正是山的形状**（山越高、基座按比例越宽），所以地形与云的生成里 G = 0.5 最流行。H = 0（G = 1）是最噪的情况。

### 信号处理视角

各类命名噪声服从功率律 f⁻ᴮ，B = 2H + 1：

| 名称 | H | G = 2⁻ᴴ | B = 2H+1 | 每 Octave | 听感 |
|------|---|---------|----------|-----------|------|
| 蓝噪声 | - | - | +1 | +3 dB | 喷洒的水 |
| 白噪声 | - | - | 0 | 0 dB | 风吹树叶 |
| 粉红噪声 | 0 | 1 | −1 | −3 dB | 雨 |
| 棕噪声 | 1/2 | √2 | −2 | −6 dB | 室内听雨 |
| "黄噪声" | 1 | 1/2 | −3 | −9 dB | 门后的引擎声 |

（"黄噪声"是 IQ 自造的名字。）

### 实测

IQ 拍摄了与成像平面平行的山脉照片，把天空-山脊轮廓提取成一维信号做 FFT。结果衰减约 **−9 dB/octave**，支持 H = 1（G = 0.5）是自然地形的正确模型——与图形界从业者的经验一致。

---

## 二、Domain Warping（域扭曲）

### 概述

域扭曲（域失真）是生成过程化纹理与几何的图形技术：对对象做捏、拉、拧、弯等变形。可追溯到 1984 年 Ken Perlin 的第一个程序化大理石纹理。

### 基础

几何或图像定义为空间函数 f(p)，p 是求值位置（体密度/表面颜色）。扭曲在求值前用函数 g(p) 畸变定义域：把 f(p) 换成 f(g(p))。只 distort 一点点时，用恒等映射加一个小畸变 h(p)：

```
g(p) = p + h(p)   ⇒   计算 f( p + h(p) )
```

本文把 f 与 h 都限制为基于 fBM 的图案，产生有机的抽象图像。

### 核心想法

用标准 fBM 分三步复合：

**第一级——普通 fBM：**

```c
float pattern( in vec2 p )
{
    return fbm( p );
}
```

**第二级——一次扭曲：**用两个带偏移的 1D fBM 拼出一个 2D 扭曲向量 q（偏移如 (0,0) 与 (5.2,1.3)）：

```c
vec2 q = vec2( fbm( p + vec2(0.0,0.0) ),
               fbm( p + vec2(5.2,1.3) ) );
return fbm( p + 4.0*q );
```

**第三级——二次扭曲：**再用 q 位移后重复：

```c
vec2 r = vec2( fbm( p + 4.0*q + vec2(1.7,9.2) ),
               fbm( p + 4.0*q + vec2(8.3,2.8) ) );
return fbm( p + 4.0*r );
```

概念上即：

- f(p) = fbm( p )
- f(p) = fbm( p + fbm( p ) )
- f(p) = fbm( p + fbm( p + fbm( p )) )

偏移量 (5.2,1.3)、(1.7,9.2)、(8.3,2.8) 是任意的——作用只是让每个单分量 fbm() 输出互不相同。系数 4.0 是扭曲强度。

### 实验

- **动画**：把时间作为参数（作者 2002 年的演示视频）。
- **配色**：把调色板映射到密度值；更好的做法是把内部扭曲向量暴露出来用于上色：

```c
float pattern( in vec2 p, out vec2 q, out vec2 r )
{
    q.x = fbm( p + vec2(0.0,0.0) );
    q.y = fbm( p + vec2(5.2,1.3) );
    r.x = fbm( p + 4.0*q + vec2(1.7,9.2) );
    r.y = fbm( p + 4.0*q + vec2(8.3,2.8) );
    return fbm( p + 4.0*r );
}
```

配色方案有无穷多种，作者演示的一种：先按 f 值走色带，再按 |q| 混入第三色，最后按 r 的竖直分量混入第四色。2012 年的图像/视频与 Shadertoy 在线示例：[4s23zz](https://shadertoy.com/view/4s23zz)、[lsl3RH](https://shadertoy.com/view/lsl3RH)。

---

## 三、Voronoise（噪声与 Voronoi 的统一）

### 引言

IQ 观察到：噪声（如 Perlin）与 Voronoi（cellular）图案共用同一套网格结构。噪声把特征"发起者"放在格点上（随机值或梯度），而 "Voronoi 把特征生成点抖动到网格的别处"。这暗示两者在实现层面并非无关。

区别在于：噪声对随机值/梯度做平滑插值，Voronoi 则对到最近特征点的最小距离求值。他提出问题：平滑双线性插值与最小值求值这两个看似迥异的操作，能否统一成一个度量，让两种经典图案成为同一网格图案生成器的特例？他还说明：这份广义代码的速度不会超过优化过的专用版本，目标是更深的理解与可能的新发现。

### 代码

两个参数完成泛化：

- **u** —— 网格控制：`u=0` 得到规整（噪声式）网格；`u=1` 得到完全抖动（Voronoi 式）网格；u 在其间控制抖动量。
- **v** —— 度量控制：在"噪声式的值插值"与"Voronoi 式的最小距离"之间混合。

由于 `min()` 不连续，他用受 Smooth Voronoi 启发的平滑替代：把到每个特征点的距离取幂，凸显最近的那个。幂为 1 时所有特征等权——恰是噪声插值需要的。首个尝试：

```glsl
float ww = pow( 1.0-smoothstep(0.0,1.414,sqrt(d)), 64.0 - 63.0*v );
```

实验后发现，把 v 再取个幂能获得感知上更线性的混合：

```glsl
float ww = pow( 1.0-smoothstep(0.0,1.414,sqrt(d)), 1.0 + 63.0*pow(1.0-v,4.0) );
```

假设单元 id 有确定性哈希（如 `vec3 hash3( in vec2 p )`），广义图案为：

```glsl
float noise( in vec2 x, float u, float v )
{
    vec2 p = floor(x);
    vec2 f = fract(x);

    float k = 1.0 + 63.0*pow(1.0-v,4.0);
    float va = 0.0;
    float wt = 0.0;
    for( int j=-2; j<=2; j++ )
    for( int i=-2; i<=2; i++ )
    {
        vec2 g = vec2( float(i), float(j) );
        vec3 o = hash3( p + g )*vec3(u,u,1.0);
        vec2 r = g - f + o.xy;
        float d = dot(r,r);
        float w = pow( 1.0-smoothstep(0.0,1.414,sqrt(d)), k );
        va += w*o.z;
        wt += w;
    }

    return va/wt;
}
```

结构与他的常规 Voronoi 实现几乎一致，区别只是对距离贡献做加权平均（`va` 累加、`wt` 归一）。

### 结果

该泛化复现了两种经典图案，外加两个特例：

| 参数 (u, v) | 图案 |
|---|---|
| u=0, v=1 | 噪声（规整网格 + 距离插值） |
| u=1, v=0 | Voronoi（抖动网格 + 最小度量） |
| u=0, v=0 | Cell noise（无抖动网格的最近距离，每单元一个常值） |
| u=1, v=1 | **Voronoise**（抖动网格 + 距离插值） |

Voronoise 在程序化生成里很有用：抖动能**掩盖噪声底层的网格结构**。（原文配图四象限：左下 cell noise、右下 noise、左上 Voronoi、右上 Voronoise；页面内有鼠标调 u/v 的实时演示。）

---

## 四、Value Noise Derivatives（值噪声的解析导数）

### 概述

本文讲值噪声（区别于梯度噪声/Perlin 噪声）及其**解析导数**——一个几乎没人写过的主题。作者用这些导数制作了 4k demo《Elevated》里的地形。梯度噪声版的姊妹篇另有其文；源码在 Shadertoy。

### 关键结论

- 解析导数"比中心差分法**快得多也精确得多**"——中心差分慢约 5 倍。
- 有了导数，可以构造比标准 fBM 更丰富的变体分形。
- 视分形求和函数而定（山脊噪声、turbulence 等），可以为高度图算出完整的解析法线。
- 对体积云光线步进，用噪声导数算解析法线可让算法**快达 6 倍**。
- 文中所有图像都直接从过程化函数光线步进渲染——没有法线贴图，没有材质，只有漫反射光照和雾。

### 数学（3D：n(x, y, z)）

噪声是 8 个格点随机值的三线性插值：

```
n = lerp(w, lerp(v, lerp(u,a,b), lerp(u,c,d)), lerp(v, lerp(u,e,f), lerp(u,g,h)))
```

插值函数 u(x)、v(y)、w(z) 是 smoothstep 型多项式：

- 三次式：`u(x) = 3x² − 2x³`，导数 `u'(x) = 6x(1−x)`
- 五次式：`u(x) = 6x⁵ − 15x⁴ + 10x³`，导数 `u'(x) = 30x²(x² − 2x + 1)`

把插值展开成多项式（a…h 为 8 个格点随机值）：

```
n(u,v,w) = k0 + k1·u + k2·v + k3·w + k4·uv + k5·vw + k6·wu + k7·uvw

k0 = a
k1 = b − a
k2 = c − a
k3 = e − a
k4 = a − b − c + d
k5 = a − c − e + g
k6 = a − b − e + f
k7 = −a + b + c − d + e − f − g + h
```

x 方向的偏导数：

```
∂n/∂x = (k1 + k4·v + k6·w + k7·vw)·u'(x)
```

（y、z 方向轮换 k 项同理。）

### 代码结构

**`noised(vec3 x)` → vec4**：一次返回噪声值 + 三个方向的导数。步骤：

1. 拆分 x：整数格点 `p = floor(x)`，小数部分 `w = fract(x)`；
2. 计算五次平滑函数 `u = w³(w(w·6−15)+10)` 及其导数 `du = 30w²(w(w−2)+1)`；
3. 在 p 的 8 个角取随机值 a…h；
4. 算出系数 k0–k7；
5. 返回 vec4：值 + 导数向量（按 2.0·du 缩放）。

**`fbm(vec3 x, int octaves)` → vec4**：noised() 的分形叠加。频率乘数 f = 1.98、振幅 s = 0.49（名义值 2.0 / 0.5）；累加值 `a += b·n.x` 与导数 `d += b·m·n.yzw`（m 为该倍频的旋转矩阵，配逆矩阵 m3i）；总导数是各倍频导数的加权和。

**地形变体**（2008）：2D、15 个倍频；把已积累的梯度喂回累加式 `a += b*n.x/(1.0+dot(d,d))`，产生类似侵蚀的效果——平缓区与粗糙区交替出现。

### 注意事项 / 伪影

- 三次式 u(x) 的二阶导不连续，会在 fBM 输出中造成可见接缝伪影；**五次式可避免**。
- 各层导数可以直接线性组合，除非整形函数不连续（如山脊噪声里的 `fabsf()`）。

### 用例清单

1. 免中心差分的解析法线（地形光线步进；例如按坡度摆树）。
2. 更快的体积云渲染（可能提速约 6 倍）。
3. 模拟侵蚀效果的变体 fBM 构造，让地形外观更多变。

### 文中链接

Shadertoy：主源码 [MdX3Rr](https://www.shadertoy.com/view/MdX3Rr) · noised() 示例 [XsXfRH](https://www.shadertoy.com/view/XsXfRH) · 地形演示 [4ttSWf](https://www.shadertoy.com/view/4ttSWf) · 解析法线岩石 [XttSz2](https://www.shadertoy.com/view/XttSz2)；姊妹篇 Gradient Noise derivatives。

---

*编译于 2026-09-30，据当日抓取的原文版本。*
