# 用噪声函数制作地图（中文版）

> **原文**：[Making maps with noise functions](https://www.redblobgames.com/maps/terrain-from-noise/) · Amit Patel（Red Blob Games），2015 年首发，2016–2022 年多次更新
> **版权**：原文文字与图片 © Red Blob Games (Amit Patel)，保留版权。本文件按原文小节**逐节完整编译**为中文（语义覆盖原文全部内容，但非逐字翻译）；原文的滑块交互、图表与图例请务必对照原网页阅读。
> 原文引用格式：`Patel, Amit J., "Making maps with noise functions", Red Blob Games, 2015`。

我网站上最受欢迎的页面之一是关于[多边形地图生成](http://www-cs-students.stanford.edu/~amitp/game-programming/polygon-map-generation/)的。做那种地图很费工。但我并不是从那里起步的——我起步的东西**简单得多**，就是本文要讲的这套。用不到 50 行代码，它能做出这样的地图：

（原文首页配图：三维渲染的噪声地形示例）

我不打算解释怎么**画**这些地图——那取决于你的语言、图形库、平台等。我只讲怎么**把一个数组填上**高度图和生物群系数据。

## 噪声（Noise）

生成 2D 地图的常见做法，是用一个**带限梯度噪声函数**（如 Simplex 或 Perlin 噪声）作为积木。噪声函数长这样：每个位置对应一个 0.0–1.0 的数（在原文的图里，0.0 是黑色，1.0 是白色）。用类 C 语法给每个格位上色：

```c
for (int y = 0; y < height; y++) {
  for (int x = 0; x < width; x++) {
    double nx = x/width - 0.5, ny = y/height - 0.5;
    value[y][x] = noise(nx, ny);
  }
}
```

这段循环在 JavaScript、Python、Haxe、C++、C#、Java 等几乎所有语言里都一样，所以下文都用类 C 语法，你换成自己用的语言即可。整个教程中，我会展示循环体（`value[y][x]=…` 那一行）如何随着功能增加而演进，[最后](#实现)给出完整示例。

取决于你用的库，可能需要平移或缩放返回值才能落进 0.0–1.0：有的库返回 0.0–1.0，有的返回 −1.0–+1.0，还有的返回 −0.7–+0.7 之类的区间；有的干脆不说明值域，那就得自己看返回值摸索。

## 海拔（Elevation）

噪声本身只是一堆数，我们需要赋予它**意义**。第一反应是让噪声对应海拔（也叫"高度图"）。把前面的噪声画成海拔，代码几乎不变，只改内层一行：

```c
elevation[y][x] = noise(nx, ny);
```

对，就这样。数据没变，只是现在它叫 `elevation` 了。

到处是丘陵，但没有别的东西。哪里不对？

### 频率（Frequency）

噪声可以用任意**频率**生成。到目前为止我只用了一个频率。移动滑块看不同频率的效果——其实就是在放大和缩小。乍一看没什么用，其实很有用：

```c
elevation[y][x] = noise(frequency * nx, frequency * ny);
```

有时用**波长**（频率的倒数）来思考更直观。频率 = 单位距离的振荡次数；波长 = 每次振荡的距离（像素/格子/米等）。频率加倍 → 一切缩小一半；波长加倍 → 一切放大一倍。两者关系：`wavelength = map_size / frequency`。

```c
elevation[y][x] = noise(x / wavelength, y / wavelength);
```

我有[另一篇教程](https://www.redblobgames.com/articles/noise/introduction.html)专门讲概念：频率、波长、振幅、倍频程、粉红/蓝色/白色噪声等。

### 倍频程（Octaves）

要让高度图更有趣，就**把不同频率的噪声加起来**：低频大丘 + 高频小丘混进同一张图：

```c
elevation[y][x] =    1 * noise(1 * nx, 1 * ny)
                 + 0.5 * noise(2 * nx, 2 * ny)
                 + 0.25* noise(4 * nx, 4 * ny);
```

（原文滑块：把更小的丘陵一点点加进来。）现在这看起来更像我们要的分形地形了！能得到丘陵和崎岖的山，但**还是没有平坦的谷地**——那需要别的手段。

有个潜在问题：`noise` 输出 0–1 时，`1*noise() + 0.5*noise() + 0.25*noise()` 的和可以达到 1.75。`[1, 0.5, 0.25]` 这些数叫**振幅**。最简单的处理是除以振幅总和：

```c
e =    1 * noise(1 * nx, 1 * ny)
   + 0.5 * noise(2 * nx, 2 * ny)
   + 0.25* noise(4 * nx, 4 * ny);
elevation[y][x] = e / (1 + 0.5 + 0.25);
```

实践中，除数值得动手实验——虽然除以振幅和能保证海拔落在 0–1，但值的**分布**未必是你想要的。

振幅通常取 `[1, 1/2, 1/4, 1/8, 1/16, …]`，每项是前一项的一半，这个比值叫 **gain** 或 **persistence**。但我们不必用固定比值：我在本页很多示例里用 `[1, 1/2, 1/3, 1/4, 1/5]`，比常规振幅能带出更多细节。振幅也可以动态计算——比如用上一层噪声值（第 1 层的噪声影响第 2 层的振幅），或用一个独立的噪声场，甚至用玩家/模拟数据。

另一个可能的问题：`noise(1*nx, 1*ny)`、`noise(2*nx, 2*ny)`、`noise(4*nx, 4*ny)` 在 nx、ny 接近 0 时是**相关**的。想要好结果，希望它们**独立**。如果噪声库支持种子，就给每个倍频用不同的种子；不支持的话，给每个倍频加个偏移，例如 `noise(1*nx, 1*ny)`、`noise(2*nx + 5.3, 2*ny + 9.1)`、`noise(4*nx + 17.8, 4*ny + 23.5)`——这样每个倍频采样的是噪声空间的不同区域，彼此独立而非相关。还有一个问题：这些噪声值可能沿相同方向对齐，有时会产生可见伪影（Perlin 噪声尤其明显）。缓解办法：旋转其中某些倍频的输出，或者改用 Simplex 噪声。

### 重分布（Redistribution）

噪声函数给我们 0–1 之间的值。想要平坦的谷地，可以把海拔**取幂**：

```c
e =    1 * noise(1 * nx, 1 * ny)
   + 0.5 * noise(2 * nx, 2 * ny)
   + 0.25* noise(4 * nx, 4 * ny);
e = e / (1 + 0.5 + 0.25);
elevation[y][x] = Math.pow(e, exponent);
```

指数取高值会把中间海拔**压进谷底**，取低值会把中间海拔抬向峰顶。我们想要前者。

实践中用 `Math.pow(e * fudge_factor, exponent)` 往往效果更好，fudge factor 取 1 附近的数（原文演示用的是 1.2）。多试几个值，看哪个合适。

`pow()` 只是重塑海拔的方式之一，还有一大堆函数可以试。你也不必局限于数学函数——可以像照片编辑器里的"曲线"工具那样自己画曲线。

到这里海拔图已经像样了，接下来加点生物群系！

## 生物群系（Biomes）

噪声给我们的是数，我们想要的是有森林、沙漠和海洋的地图。第一步，把低海拔变成水：

```js
function biome(e) {
  // 演示里 0.2–0.5 之间的阈值效果不错，但每个生成器都需要自己的调参
  if (e < waterlevel) return WATER;
  else return LAND;
}
```

嘿，开始像个程序生成的世界了！有水、草地和雪。想要更多东西？按 海水→沙滩→草地→森林→稀树草原→沙漠→雪 的序列切带：

```js
function biome(e) {
  // 这些阈值需要按你的生成器调参
  if (e < 0.1) return WATER;
  else if (e < 0.2) return BEACH;
  else if (e < 0.3) return FOREST;
  else if (e < 0.5) return JUNGLE;
  else if (e < 0.7) return SAVANNAH;
  else if (e < 0.9) return DESERT;
  else return SNOW;
}
```

看起来不错！数字和生物群系当然要按你的游戏改——《孤岛危机》要多丛林，《上古卷轴 5》要多冰雪。但不管怎么改，这个方案都有局限：地形类型只跟海拔对齐，所以会**成带状**。要更有趣，就得用海拔以外的东西来定生物群系。再造一张"土壤湿度"噪声图：

然后**同时**用海拔和湿度。下方左图中，y 轴是海拔，x 轴是湿度，得到一张挺像样的地图。代码：

```js
function biome(e, m) {
  // 这些阈值需要按你的生成器调参
  if (e < 0.1) return OCEAN;
  if (e < 0.12) return BEACH;

  if (e > 0.8) {
    if (m < 0.1) return SCORCHED;
    if (m < 0.2) return BARE;
    if (m < 0.5) return TUNDRA;
    return SNOW;
  }

  if (e > 0.6) {
    if (m < 0.33) return TEMPERATE_DESERT;
    if (m < 0.66) return SHRUBLAND;
    return TAIGA;
  }

  if (e > 0.3) {
    if (m < 0.16) return TEMPERATE_DESERT;
    if (m < 0.50) return GRASSLAND;
    if (m < 0.83) return TEMPERATE_DECIDUOUS_FOREST;
    return TEMPERATE_RAIN_FOREST;
  }

  if (m < 0.16) return SUBTROPICAL_DESERT;
  if (m < 0.33) return GRASSLAND;
  if (m < 0.66) return TROPICAL_SEASONAL_FOREST;
  return TROPICAL_RAIN_FOREST;
}
```

这些都只是示例阈值。我做的每个项目都得改它们——不光因为主导生物群系不同（达戈巴要多沼泽、霍斯要多冻原、塔图因要多沙漠），还因为结果取决于所用的噪声库和倍频的混合方式。**做好调参的心理准备！**

如果你不需要离散的生物群系，也可以用平滑渐变（见制图师 Tom Patterson 的[文章](http://www.shadedrelief.com/hypso/hypso.html)）来上色。

<details><summary>延伸阅读（原文折叠区，点击展开）</summary>

- 本文的生物群系基于 Robert Whittaker 的生物群系体系（[1966 年论文](https://scholar.google.com/scholar?hl=en&as_sdt=0%2C48&q=holdridge+1966)），用年平均温度和平均降雨量。
  - 本节里我用海拔代理温度；下一节会加入纬度。
  - Whittaker 原始分类图是**三角形**的（反映冷空气容纳的降雨更少）；我把它拉成了方形——用"相对最大值"的湿度代替绝对降雨量。方形在查表或 GPU 纹理查找时更好用。
- [Holdridge 生命地带系统](https://en.wikipedia.org/wiki/Holdridge_life_zones)用三根轴：降雨、湿度、"潜在蒸散比"（PET）。乍看没有考虑纬度和海拔，但这两者同时影响 PET 和降雨，所以三根轴并不独立。
- [Köppen 气候分类](https://en.wikipedia.org/wiki/K%C3%B6ppen_climate_classification)在程序化生成圈外很流行。我没用它，因为它更像"描述性"的（从生物群系出发描述特征），而程序化生成需要"规定性"的（手里有特征值，要查出生物群系）。
- 不少游戏项目自建了生物群系分类，比如 Minecraft 的表、[这张表](https://imgur.com/a/bh2iy)。考虑围绕你项目的具体需求设计自己的体系。
- 查表虽然好用，但地球上的真实生物群系复杂得多。可以考虑：降雨的季节波动（有的植物受不了干湿季）、寒冷天数（有的植物要靠它打破休眠）、最高温度（有的活不了）、最低温度（有的活不了）、积温 growing degree days（一年里高于某温度的天数）、土壤类型（壤土保水远强于沙土）、岩石类型（多孔岩能在不降雨时供泉水）、季节变化（有的慢生长植物受不了偶发干旱）等等。
- 科学家们的体系很多：1949 Allee、1961 Kendeigh、1974 Goodall、1976 Walter、1988 Schultz、1989 Bailey、1998 Olson & Dinerstein、1954 Köppen-Geiger / Köppen-Trewartha、1948 Thornthwaite、2005 Thornthwaite-Feddema。但做程序化生成，我推荐读 2025 年的 [Hersfeldt-Pasta](https://worldbuildingpasta.blogspot.com/2025/09/public-climate-data-re-explorations.html)。

</details>

## 气候（Climate）

上一节我用**海拔**代理**温度**——海拔越高温度越低。但纬度也影响温度。把海拔和纬度一起用来控制温度：极地附近（高纬度）更冷，山顶（高海拔）也更冷。从海拔 `e` 出发，把它换算成一个"等效海拔"。原文演示用的公式是：

```
equivalent_elevation = 10*e*e + poles + (equator - poles) * sin(PI * (y / height))
```

然后用 `equivalent_elevation` 代替 `e` 去算生物群系。*我不认为这是最佳方案*——你需要对公式和参数做实验、做调整，才能得到想要的效果。

<details><summary>延伸阅读（原文折叠区，点击展开）</summary>

纬度和海拔是温度的主因，但还有更多因素：

- 温度有**季节性**：夏冬两季南北半球交替变暖变冷，赤道变化不大。
- 太阳"辐射"（insolation）可以用三角函数估算，而不是用我上面给的公式（[Wikipedia: Solar irradiance](https://en.wikipedia.org/wiki/Solar_irradiance#Derivation)）。
- 降雨也有季节性。Wikipedia 上有一段[精彩的动画](https://en.wikipedia.org/wiki/Earth_rainfall_climatology#/media/File:MeanMonthlyP.gif)，展示降雨如何逐月变化。
- 温度还取决于：盛行风、洋流、反照率（取决于植被）、海洋对温度的调节作用、土壤湿度，等等。
- [降雨取决于海拔](https://en.wikipedia.org/wiki/Orographic_precipitation)（地形雨）。我在程序化生成项目里用过的简单模型：空气能容纳的水汽量取决于气温。风把空气推上山，海拔升高温度下降，空气能容纳的水汽变少，装不下的就变成雨。这个效应解释了安第斯山脉西侧为什么干旱、东侧为什么孕育亚马逊流域。

</details>

## 岛屿（Islands）

有些项目想让地图边界全是水。办法之一是按前文生成地图，然后**重塑**它。从侧面看，是什么让它不像岛？边缘的陆地要压进水下，中心的水要抬出水面成陆。

两个**配料**：

1. **距离函数**：给地图上每个位置赋一个距离，中心为 0，边缘为 1。
2. **塑形函数**（即重分布一节用过的那类函数）：输入海拔，输出新海拔。

在地图中心（距离 0），塑形函数永远输出**陆地**；在边缘（距离 1），永远输出**水**。中间地带则海陆皆可。为简单起见，假设水面为 0.5：≥0.5 是陆地，<0.5 是水。

计算距离 `d`：令 `nx = 2*x/width - 1`、`ny = 2*y/height - 1`（范围 −1 到 +1）。然后从 [u/KdotJPG 推荐的](https://old.reddit.com/r/proceduralgeneration/comments/kaen7h/new_video_on_procedural_island_noise_generation/gfjmgen/)距离函数里挑一个：

- **Square Bump**：`d = 1 - (1-nx²) * (1-ny²)`——方形地图、想让岛尽量撑满空间但不碰到边界时用。
- **Euclidean²**：`d = min(1, (nx² + ny²) / sqrt(2))`——想要圆形岛、并打算把它嵌进更大的世界时用。

给海拔 `e` 塑形的最简办法，是把 `1-d` 线性地混进来：中心处 `d`=0，希望海拔高（1）；边缘处 `d`=1，希望海拔低（0）。线性插值就能做到：`e = lerp(e, 1-d, mix)`，`mix` 取 0 到 1 之间。把 `mix` 滑到 0 看原图，滑到 1 看被强制成岛形的图。

（原文滑块：线性塑形 mix 参数、距离函数选择、"New map" 按钮重新随机。）

可以试的东西还有很多：给距离函数加减常数让它上下平移；乘常数改变坡度；指数从 2 改成 4 或 6；只对噪声的低频倍频应用重塑，让高频细节在全域均匀发挥；先选定目标陆地面积，再把所有海拔整体上推或下推到目标岛面积；用噪声造岛的**形状**、用别的方案赋海拔；用查找表实现任意（分段线性）重塑或距离函数；用非线性塑形函数混合 `e` 和 `1-d`。找到喜欢的组合需要实验。

## 山脊噪声（Ridged noise）

不取幂，改用绝对值，可以造出尖锐的山脊：

```js
function ridgenoise(nx, ny) {
  return 2 * (0.5 - abs(0.5 - noise(nx, ny)));
}
```

要加倍频，可以调整高频的振幅，让细节只加在山上：

```js
e0 =    1 * ridgenoise(1 * nx, 1 * ny);
e1 =  0.5 * ridgenoise(2 * nx, 2 * ny) * e0;
e2 = 0.25 * ridgenoise(4 * nx, 4 * ny) * (e0+e1);
e = (e0 + e1 + e2) / (1 + 0.5 + 0.25);
elevation[y][x] = Math.pow(e, exponent);
```

（原文滑块：倍频层数 1–8。）

这门技术我没什么经验，还得多玩才能用好。把低频山脊噪声和高频非山脊噪声混起来也许也有意思。

## 台地（Terraces）

把海拔四舍五入到最近的 n 级之一，就得到台地：

```js
e = Math.round(e * n) / n;
```

（原文滑块：台阶级数 4–32。）

这仍是 `e = f(e)` 式海拔重分布的应用——前面用 `Math.pow(e, exponent)` 让山峰更陡，这里用取整让台地出现。换用阶跃函数以外的[其他函数](https://gamedev.stackexchange.com/a/188513/2472)，台地可以更圆，或者只在某些海拔段出现。

## 树木摆放（Tree placement）

我们通常把分形噪声用于海拔和湿度，但它也能用来摆放树、石头这类不规则分布的物件。海拔要"红噪声"（低频高振幅），摆物件要"蓝噪声"（**高频**高振幅）。左边是蓝噪声图样，右边是噪声值大于邻近值的位置（即放树的地方）：

```js
for (int y = 0; y < height; y++) {
  for (int x = 0; x < width; x++) {
    double nx = x/width - 0.5, ny = y/height - 0.5;
    // 蓝噪声是高频；这个系数可以调
    bluenoise[y][x] = noise(50 * nx, 50 * ny);
  }
}

for (int yc = 0; yc < height; yc++) {
  for (int xc = 0; xc < width; xc++) {
    double max = 0;
    // 有比这更高效的算法
    for (int dy = -R; dy <= R; dy++) {
      for (int dx = -R; dx <= R; dx++) {
        int xn = dx + xc, yn = dy + yc;
        // 可选：加圆形判定 (dx*dx + dy*dy <= R * (R + 1))
        if (0 <= yn && yn < height && 0 <= xn && xn < width) {
          double e = bluenoise[yn][xn];
          if (e > max) { max = e; }
        }
      }
    }
    if (bluenoise[yc][xc] == max) {
      // 在 xc,yc 放一棵树
    }
  }
}
```

KDotJPG 建议用 `dx*dx + dy*dy <= R * (R + 1)` 把方形邻域判定换成[圆形判定](https://www.redblobgames.com/grids/circle-drawing/#distance-test)，同时支持非整数半径。另外半径不必是常数——给每种生物群系选不同的 R，就能得到疏密不同的树林。

虽然 Simplex/Perlin 噪声能摆树很酷，但**有更高效、分布更好的算法**。摆树这类物件，我推荐用 [Poisson Disc 采样](http://devmag.org.za/2009/05/03/poisson-disk-sampling/)或[抖动网格](https://www.redblobgames.com/x/1830-jittered-grid/)，而不是本文演示的高频噪声法。JavaScript 里我用 [poisson-disk-sampling](https://github.com/kchapelier/poisson-disk-sampling)、[fast-2d-poisson-disk-sampling](https://github.com/kchapelier/fast-2d-poisson-disk-sampling)、[jittered-hexagonal-grid-sampling](https://github.com/kchapelier/jittered-hexagonal-grid-sampling)。Wang tiles 和图形抖动（dithering）算法也值得一看。

## 环绕地图（Wraparound maps）

有时希望地图东边和西边接上。这对应 3D 空间里的一个**圆柱面**。只需小改：把平面地图上的 x 解释成圆柱世界里的**角度**，再把角度转成笛卡尔坐标。要让南北也接上，对 y 如法炮制，去 4D 噪声空间里查。看地图和它平铺 copies 相邻的效果（原文两图：只东西环绕；四向环绕）。代码：

```js
const TAU = 2 * M_PI;

function cylindernoise(nx, ny) {
  let angle_x = TAU * nx;
  /* 在"噪声参数空间"里，nx 和 ny 走过的距离必须相同。
     由 nx 画出的圆周长必须等于 ny 走过的线段长度 1，
     即圆半径 = 1/2π = 1/tau */
  return noise3D(cos(angle_x) / TAU, sin(angle_x) / TAU, ny);
}

function torusnoise(nx, ny) {
  let angle_x = TAU * nx,
      angle_y = TAU * ny;
  return noise4D(cos(angle_x) / TAU, sin(angle_x) / TAU,
                 cos(angle_y) / TAU, sin(angle_y) / TAU);
}
```

实际使用时大概率要放大这些噪声值：高维噪声的值域比低维窄，如果你的生物群系常数是按 2D 噪声调的，可以试着把 3D 结果乘 √1.5、4D 乘 √2，之后还得按需微调。见 Rudi Chen 的[《Perlin 噪声值域》](https://digitalfreepen.com/2017/06/20/range-perlin-noise.html)。可平铺噪声的更多内容见 Ron Valstar 的[指南](https://ronvalstar.nl/creating-tileable-noise-maps)。

另一件可能影响质量的事：倍频之间会互相"渗漏"频段，见 Cook & DeRose 的论文《Wavelet Noise》：

> 渲染时，常用 3D 噪声函数给 2D 表面贴图，但这样得到的 2D 纹理一般**不是带限的**——哪怕 3D 函数本身完美带限。

## 无限地图（To infinity and beyond）

位置 (x,y) 的生物群系计算与其他位置无关。这种**局部计算**带来两个好处：可以并行，也可以做无限地形。（原文演示：鼠标悬停/触摸左侧小地图，右侧即时生成对应区域。）我们可以生成地图的任意局部，而不必生成（或存储）整张图。

怎么改代码？小改动：把 `noise(…, …)` 换成 `noise(… - camera.x, … - camera.y)`。演示里我用鼠标位置设 `camera.x/y`；你也可以用 WASD 或其他控制键在无限地图上移动相机。

## 实现（Implementation）

用噪声生成地形是很流行的技术，各种语言、平台都能找到教程，代码也大同小异。最简单的循环，三种语言各一份：

**JavaScript：**

```js
import { createNoise2D } from 'simplex-noise';
let gen = createNoise2D();
function noise(nx, ny) {
  return gen(nx, ny) / 2 + 0.5;  // 从 -1.0:+1.0 缩放到 0.0:1.0
}

let value = [];
for (let y = 0; y < height; y++) {
  value[y] = [];
  for (let x = 0; x < width; x++) {
    let nx = x/width - 0.5, ny = y/height - 0.5;
    value[y][x] = noise(nx, ny);
  }
}
```

**C++**（libnoise 用 `module::Perlin gen;` + `gen.GetValue(nx, ny, 0)`；FastNoiseLite 用 `FastNoiseLite gen;` + `gen.GetNoise(nx, ny)`；两者都 `/2.0 + 0.5` 缩放，再走同样的双重循环）；

**Python**（opensimplex 用 `gen.noise2d(nx, ny) / 2.0 + 0.5`，同样循环）。

有了噪声库，代码都差不多。**当心：有些库会自动叠加多个倍频**——方便，但你就没法按自己的方式混合它们了。

各语言噪声库：Python：[opensimplex](https://pypi.org/project/opensimplex/)；C++：[SimplexNoise](https://github.com/SRombauts/SimplexNoise)、[FastNoiseLite](https://github.com/Auburn/FastNoiseLite)、[libnoise](http://libnoise.sourceforge.net/docs/)；JavaScript/TypeScript：[simplex-noise.js](https://github.com/jwagner/simplex-noise.js)、FastNoiseLite；Java/C#：[opensimplex2](https://github.com/KdotJPG/OpenSimplex2)、FastNoiseLite；Unity：[Unity.Mathematics.noise](https://docs.unity3d.com/Packages/com.unity.mathematics@0.0/api/Unity.Mathematics.noise.html)、[Mathf.PerlinNoise](https://docs.unity3d.com/ScriptReference/Mathf.PerlinNoise.html)。

主流语言的噪声库很多。或者，你也可以花时间研究 Simplex/Perlin/OpenSimplex 的原理，或者自己实现一个。*我没有*——我用的现成库。尤其 [FastNoiseLite](https://github.com/Auburn/FastNoiseLite)，有 C、C#、C++、Java、JavaScript、Rust、Go、GLSL、HLSL、Fortran、Zig、Odin、Haxe、Pascal、GML 甚至 PowerShell 版本。

真项目里你可能想把 `noise` 函数和 `gen` 对象包进一个类，但那些细节与本文无关，我就做成全局的了。对这个简单的项目来说，用 Simplex、OpenSimplex、Perlin、值噪声、中点位移、diamond-square 还是逆傅里叶变换，差别不大——各有优劣，但对这类地图生成器，输出都足够相似。地图的**绘制**与平台和游戏强相关，所以不提供；这份代码负责生成海拔和生物群系，画法随你的游戏风格自定。欢迎拷贝、移植、用到你自己的项目里。

## 演示场（Playground）

前面讲了倍频混合、取幂重分布、海拔×湿度查 biome。这里有个交互图，把这些参数全放开让你玩，并展示代码如何拼起来：

```js
import alea from 'alea';
import { createNoise2D } from 'simplex-noise';
const genE = createNoise2D(alea(seed1));
const genM = createNoise2D(alea(seed2));
function noiseE(nx, ny) { return genE(nx, ny)/2 + 0.5; }
function noiseM(nx, ny) { return genM(nx, ny)/2 + 0.5; }

for (var y = 0; y < height; y++) {
  for (var x = 0; x < width; x++) {
    var nx = x/width - 0.5, ny = y/height - 0.5;
    var e = (e1 * noiseE( 1 * nx,  1 * ny)
           + e2 * noiseE( 2 * nx,  2 * ny)
           + e3 * noiseE( 4 * nx,  4 * ny)
           + e4 * noiseE( 8 * nx,  8 * ny)
           + e5 * noiseE(16 * nx, 16 * ny)
           + e6 * noiseE(32 * nx, 32 * ny));
    e = e / (e1 + e2 + e3 + e4 + e5 + e6);
    e = Math.pow(e, exponent);
    var m = (m1 * noiseM( 1 * nx,  1 * ny)
           + m2 * noiseM( 2 * nx,  2 * ny)
           + m3 * noiseM( 4 * nx,  4 * ny)
           + m4 * noiseM( 8 * nx,  8 * ny)
           + m5 * noiseM(16 * nx, 16 * ny)
           + m6 * noiseM(32 * nx, 32 * ny));
    m = m / (m1 + m2 + m3 + m4 + m5 + m6);
    /* 在 x,y 处绘制 biome(e, m) */
  }
}
```

（原文可调参数：指数 0.5–15；海拔倍频 e1–e6 默认 1.0 / 0.5 / 0.25 / 0.125 / 0.0625 / 0.03125；湿度倍频 m1–m6 默认 1.0 / 0.75 / 0.33 / 0.33 / 0.33 / 0.5。）

**坑一**：海拔和湿度的噪声必须用**不同的种子**——否则两者产生相同的噪声值，地图会很无聊。simplex-noise v3、Python 的 opensimplex、C++ 的 FastNoiseLite 都支持种子；simplex-noise v4 允许你接入自己的可播种随机数发生器。库不支持种子的话，替代办法是从远处空间采样：海拔用 `noise2D(nx, ny)`，湿度用 `noise2D(nx + 1000, ny)`。

**坑二**：多倍频合成后，输出值域可能不符合预期，可能要加减乘除把值拉回目标区间（比如 0.0–1.0）。Scott Turner 写过[噪声常见问题的更多讨论](https://heredragonsabound.blogspot.com/2016/10/is-it-noisy-in-here.html)；Rudi Chen [分析过 Perlin 噪声的值域](https://digitalfreepen.com/2017/06/20/range-perlin-noise.html)；KDotJPG [研究过 Simplex/OpenSimplex/Perlin 的归一化问题](https://noiseposti.ng/posts/2021-03-22-Normalizing-Gradient-Noise.html)。我用的 [simplex-noise.js](https://github.com/jwagner/simplex-noise.js) 已考虑此事，输出归一到 −1.0–+1.0。

## 心得（Thoughts）

我喜欢这套做法的地方：**简单**。快。很少的代码就能得到体面的结果。

我不喜欢的地方：它有局限。局部计算意味着每个位置与其他位置互不依赖——地图的各个区域**互不关联**。每处都"感觉"一样。没有"应该有 3 到 5 个湖"这样的全局约束，也没有"河流从最高峰流向大海"这样的全局特征。另一个不满：要调很多参数才能调出喜欢的效果。

那我为什么还推荐它？因为它是好的**起点**，尤其适合独立游戏或 game jam。我的两个朋友曾在 30 天里写出《Realm of the Mad God》的初始版本参加比赛，他们请我帮做地图。我用这套技术（外加一些事后证明没多大用的附加功能）给他们做了地图。几个月后，在吸收玩家反馈、深入思考游戏设计之后，我们设计了更高级的、基于 Voronoi 多边形的地图生成器（见[这里](http://www-cs-students.stanford.edu/~amitp/game-programming/polygon-map-generation/)）。那个生成器不用本文的技术，但以完全不同的方式使用噪声。

噪声海拔好玩、易上手，但很快会撞到天花板。Scott Turner 有一篇[很有洞见的文章](https://heredragonsabound.blogspot.com/2019/02/perlin-noise-procedural-content.html)，讲为什么要换掉噪声。Artifexian 讲[海岸线](https://www.youtube.com/watch?v=ztemzsxso0U)的视频能让你直观感受噪声地形有多局限。一位[现实中的地球科学家](https://www.reddit.com/r/proceduralgeneration/comments/gi4hq4/comment/fqe9pt0/)的做法是：从噪声出发，再用专门算法处理山脉、海平面、冰川峡湾、海岸礁、内陆海。

## 更多（More）

噪声函数能玩的酷东西**非常多**，本页只是入门。搜一搜还能看到更高级的技术：turbulence、billow、ridged multifractal、amplitude damping、terraced、voronoi noise、解析导数、域扭曲（domain warping）等。去看看 [Inigo Quilez 的页面](https://iquilezles.org/articles/warp/)找灵感。Runevision 展示了[如何用指数、voronoi、正弦波、塑形函数、方向性噪声、梯度、渐隐、山脊、图层做出伪侵蚀](https://blog.runevision.com/2026/03/fast-and-gorgeous-erosion-filter.html)。

影响本文的几个旧项目：

- 我给[第一版 Realm of the Mad God 地图生成器](https://simblob.blogspot.com/2010/01/simple-map-generation.html)用的是普通 Perlin 噪声。alpha 测试的头六个月用的就是它，之后按玩法需求换成了定制设计的 [Voronoi 多边形生成器](http://www-cs-students.stanford.edu/~amitp/game-programming/polygon-map-generation/)。本文的生物群系和配色就来自那些项目。
- 学音频信号处理时，我写过一篇[噪声教程](https://www.redblobgames.com/articles/noise/)，覆盖频率、振幅、倍频程、噪声"颜色"等概念——音频里成立的这些概念同样适用于噪声地图生成。当时还做了一些未完工的[地形生成演示](https://www.redblobgames.com/articles/noise/2d/)。
- 有时我做实验找极限：想知道用最少的代码能做出多像样的地图。[这个迷你项目](https://www.redblobgames.com/x/1446-svg-filters/)里我做到了 **0 行代码**——全部用图像滤镜（turbulence、阈值、颜色渐变）。我既高兴又不安：地图生成有多少能靠图像滤镜完成？相当多。前文"平滑渐变配色"方案里的每一样都来自那次实验：噪声层是 turbulence 滤镜，倍频是叠在一起的图层，取幂就是 Photoshop 里的"曲线"工具。

有点困扰我的是：我们游戏开发者写的噪声地形代码（包括中点位移）其实和音频、图像滤镜是同一回事。反过来说，它能用极少的代码做出体面的结果——所以我才写了这篇：一个**快而简单的起点**。我通常不会长期用这类地图：等游戏做得更多、更清楚什么样的地图匹配这个游戏的设计之后，就换成定制的地图生成器。这是我的常见模式：从极简的东西起步，等更理解这个系统之后才替换它。

噪声能玩的花样太多，本文只是浅尝。试试 [Noise Studio](https://codepen.io/MittenedWatchmaker/full/aVeoRM/) 交互式探索。另外：

- [Inigo Quilez 的文章](https://iquilezles.org/articles/)——**必读**
- [Sharing everything I could understand about gradient noise](https://blog.pkh.me/p/42-sharing-everything-i-could-understand-about-gradient-noise.html)——对 Simplex/Perlin 原理的出色讲解
- [Building Worlds Using Maths](https://www.gdcvault.com/play/1024514/Building-Worlds-Using)——《无人深空》Sean Murray 的 GDC 演讲，跳到 20 分钟处
- [Continuous World Generation in No Man's Sky](https://www.gdcvault.com/play/1024265/Continuous_World_Generation_in__No_Man_s_Sky_)——Innes McKendrick
- [Factorio 地图用的噪声函数（FFF-390）](https://factorio.com/blog/post/fff-390)

---

*编译于 2026-09-30，据当日抓取的原文版本（Created 07 Jul 2015 / Last modified 26 Aug 2026）。原文其余脚注链接均已在正文对应位置内联。*
