# 球面程序化地图生成（中文版）

> **原文**：[Procedural map generation on a sphere](https://www.redblobgames.com/x/1843-planet-generation/) · Amit Patel（Red Blob Games），2018-10-22
> **版权**：原文文字与图片 © Red Blob Games (Amit Patel)，保留版权。本文件按原文小节**逐节完整编译**为中文（语义覆盖原文全部内容，但非逐字翻译）；原文含大量交互演示与图例，请对照原网页阅读。源码：<https://github.com/redblobgames/1843-planet-generation>
> 文末附原文全部参考链接。

ProcJam 2018 促使我去学习如何在球面上生成地图。ProcJam 开幕的那个周末，我花了 11 个小时做几何，并写成一篇博客：在球面上布点、处理球面几何、在球面上构建 Delaunay 三角剖分、再构建 Voronoi 区域。周末结束后，剩下的 8 天里我只挤出 12 个小时，做了这些事：在球面上赋海拔、赋湿度/降雨、赋温度、生成河流，以及把山脉、山谷、海洋、河流渲染到球面上。

在我的项目里，我要么限定范围，要么限定时间。那些大长文（A*、六边形网格等）通常是限定范围的结果；而这篇是一次为期一周的实验（URL 里的 /x/ 就是这个意思）——我想在一周里尽可能多地学到东西。学到了很多，也剩下大量没做的事，以后也许会回来继续。

## 1. 抖动（Jitter）

第一部分（博客 part 1）花了不少时间在球面上找均匀分布的点。可一旦开始做地图，我发现的第一件事是：**当初花在均匀布点上的时间全白费了——均匀分布的点做出来的地图很无聊！**（[图：均匀点](https://www.redblobgames.com/x/1843-planet-generation/blog/voronoi.png)）

最后我给这些点加了随机抖动，让它们看起来有趣。（[图：抖动后](https://www.redblobgames.com/x/1843-planet-generation/blog/jitter.png)）

## 2. 噪声高度图（Noise heightmap）

我通常从噪声高度图起步。这次我用 **3D Simplex 噪声**给球面上的每个点赋一个海拔值。结果是合理的，但在我看来并不有趣。（[图：纯噪声行星](https://www.redblobgames.com/x/1843-planet-generation/blog/planet-500k-voronoi.png)）

于是我决定寻找别的方案。

## 3. 构造板块（Tectonic plates）

我决定跟随 Andy Gainey 等人的路线（引用见文末），构造一些构造板块。作为快速实验，我只想找"能跑通的最简做法"。

首先随机挑选板块位置。我有 numRegions 个 Voronoi 区域，从中随机选，直到凑够 N 个。实验下来 N 取 **10 到 50** 之间比较合理：

```js
let chosen_r = new Set();
while (chosen_r.size < N) {
  chosen_r.add(randInt(numRegions));
}
```

有了板块种子后，我从它们出发做洪泛（广度优先搜索），把每个 Voronoi 区域归属到某个板块。但洪泛出来的边界太光滑，于是我改成了**随机填充（random fill）**算法：它和 BFS 很像，只是扩张时不是取第一个元素（BFS）或最后一个元素（DFS），而是**随机取一个元素**来扩张。这段代码脱离上下文未必好读，但能看出用随机填充分配板块需要多少代码：

```js
// plate_r 是作为板块种子的区域 id 集合
let r_plate = new Int32Array(mesh.numRegions); // 区域 → 板块
r_plate.fill(-1);
let queue = Array.from(plate_r);
for (let r of queue) { r_plate[r] = r; }
let out_r = [];
const randInt = makeRandInt(SEED);
for (let queue_out = 0; queue_out < mesh.numRegions; queue_out++) {
  let pos = queue_out + randInt(queue.length - queue_out);
  let current_r = queue[pos];
  queue[pos] = queue[queue_out]; // 随机交换队列元素
  mesh.r_circulate_r(out_r, current_r); // 相邻区域
  for (let neighbor_r of out_r) {
    if (r_plate[neighbor_r] === -1) {
      r_plate[neighbor_r] = r_plate[current_r];
      queue.push(neighbor_r);
    }
  }
}
```

随机填充让边界效果好了些。（[图：BFS 边界](https://www.redblobgames.com/x/1843-planet-generation/blog/continent-boundaries.png) → [图：随机填充边界](https://www.redblobgames.com/x/1843-planet-generation/blog/continent-boundaries-random.png)）

有了板块之后，我给每个板块随机分配海拔和湿度。（[图](https://www.redblobgames.com/x/1843-planet-generation/blog/continent-boundaries-2.png)）

把区域数和板块数调大，效果依然成立。（[图](https://www.redblobgames.com/x/1843-planet-generation/blog/continent-boundaries-3.png)）

## 4. 板块运动（Plate movement）

下一步是给板块内部赋海拔。我给每个板块分配一个**随机的方向向量**——这比其他人做的都要简化，但我认为它是能产出有用结果的最简方案。（[图：运动向量 1](https://www.redblobgames.com/x/1843-planet-generation/blog/continent-movement-vectors-1.png) · [图 2](https://www.redblobgames.com/x/1843-planet-generation/blog/continent-movement-vectors-2.png)）

然后沿板块边界比较两边的方向向量：如果板块运动会使相邻区域互相靠近，就采用与"静止或远离"不同的规则：

| 边界类型 | 距离减小 | 距离增大 |
|---|---|---|
| 陆地 + 陆地 | 山脉 | 海岸线 |
| 陆地 + 海洋 | 山脉 | 海洋 |
| 海洋 + 海洋 | 海岸线 | 海洋 |

这套规则只给边界上的区域赋海拔；其余区域用三个距离场插值，方法写在我 2017 年的[这篇博客](https://www.redblobgames.com/x/1728-elevation-control/)里。

**效果并不好。**我想问题在于：任何一点轻微的相对运动都会触发完整的造山，结果是每块板块的四周都长满了山。（[图：满缘山脉](https://www.redblobgames.com/x/1843-planet-generation/blog/continent-boundaries-4.png)）

我调整了规则，给"区域互相挤压的程度"加了一个**阈值**，达到阈值才触发造山：

| 边界类型 | Δdistance < −0.75 | 其他情况 |
|---|---|---|
| 陆地 + 陆地 | 山脉 | 不处理 |
| 陆地 + 海洋 | 山脉 | 海岸线 |
| 海洋 + 海洋 | 海岸线 | 海洋 |

（注：0.75 是拍脑袋定的，我也想知道最优值是多少。）效果好了很多——山脉开始在板块边界处成形了。（[图](https://www.redblobgames.com/x/1843-planet-generation/blog/continent-boundaries-5.png)）

还有一个进一步的调整有所帮助：在计算距离场之前，先把每个板块的中心设为海洋或海岸线。

这部分还需要更多打磨，我对结果仍不太满意。既然这是一次限时一周的实验，我决定先去做别的事，有时间再回来。

**2022 年更新**：后来发现我这段代码有 bug，这大概就是效果差的原因。不幸的是，生成器里的其他参数都是围绕带 bug 的代码调出来的，所以修复它不像"改掉 bug"那么简单。

## 5. 板块大小（Plate size）

改变板块数量，就能得到更大或更小的陆地。（[图 1](https://www.redblobgames.com/x/1843-planet-generation/blog/planet-1.png) · [图 2](https://www.redblobgames.com/x/1843-planet-generation/blog/planet-2.png) · [图 3](https://www.redblobgames.com/x/1843-planet-generation/blog/planet-3.png) · [图 4](https://www.redblobgames.com/x/1843-planet-generation/blog/planet-4.png)）

## 6. 生物群系（Biomes）

给行星配置湿度/降雨/气压的好办法是做大气模拟，但我在海拔上花的时间远超预期，其他部分时间不够了，所以采取了"能跑通的最简做法"：给每个大陆板块随机分配湿度，再拿去查生物群系。

## 7. 渲染（Rendering）

mapgen4 那套带描边的渲染器和自定义投影在球面上全都用不了，我只好回到基础，还得跟 bug 搏斗。（[图：金属死球](https://www.redblobgames.com/x/1843-planet-generation/blog/sphere-metallic-deathball.png)）

我实现的东西[类似我在这篇博客里描述的方案](https://www.redblobgames.com/x/1725-procedural-elevation/#rendering)，但我不喜欢它渲染山脉的方式。这是需要回头重做的地方。（[图](https://www.redblobgames.com/x/1843-planet-generation/blog/planet-5.png)）

如果没有 [regl.js](http://regl.party/) 我大概不会尝试这个项目——它把 WebGL 里所有烦人的部分都包掉了，同时把真正重要的底层细节（内存管理、着色器、纹理等）留给我。

## 8. 河流（Rivers）

河流通常是最难的部分，但我已经有 mapgen4 的河流代码，而且它建立在**图**而不是网格上。球面地图就是一个图，所以河流代码**一行都没改**就能在球面上跑。河流的渲染代码则完全不能用，我用 GL_LINES 重写了一套。遗憾的是 WebGL 里改不了线宽，所以第一版长这样：（[图 6](https://www.redblobgames.com/x/1843-planet-generation/blog/planet-6.png) · [图 7](https://www.redblobgames.com/x/1843-planet-generation/blog/planet-7.png)）

有效的部分：湿润地区的河流确实比干旱地区多。

线宽做不了，我就用 **alpha 透明度**来模拟更细的河流。（[图 8](https://www.redblobgames.com/x/1843-planet-generation/blog/planet-8.png)）

有个视觉小毛病：河流入海处会穿帮。本想认真解决，但这是限时项目，最省事的绕法是把河流画成与浅水相同的颜色——河流流入海洋的地方就看不见 overdraw 了。我觉得最终效果相当不错。这里只画了较大的河流：（[图 9](https://www.redblobgames.com/x/1843-planet-generation/blog/planet-9.png) · [图 10](https://www.redblobgames.com/x/1843-planet-generation/blog/planet-10.png)）

这张画了多得多的河流：（[图 11](https://www.redblobgames.com/x/1843-planet-generation/blog/planet-11.png) · [图 12](https://www.redblobgames.com/x/1843-planet-generation/blog/planet-12.png)）

我对河流的效果很满意！

还有个小问题：极小的"海洋"区域可能会有河流流入。本来想过滤掉这类情况，但限时项目里没排上。

另一个补偿 GL_LINES 行为的实用技巧：在星球侧缘（不会以俯视角度看到河流的地方）降低透明度。

## 9. 交互演示（Demo）

原文此处有一个可交互演示，参数包括：绘制方式（四边形 / 平面着色）、是否显示板块运动向量、是否显示板块边界、区域数量、板块数量、海洋板块百分比、抖动量、球体旋转。区域数调大时会变慢。

## 10. 还有很多没做的（More）

没来得及实现的东西太多了：山脉看着不对；温度应该随极地降低；着色太平；没有树；河流应该用可变宽度画；板块海拔插值有时不对；板块数量增多时海洋似乎偏多。要改进的地方很多很多，但我现在不想继续做了——学到了很多，玩得很开心，该回到主项目 mapgen4 了。

杂乱的代码放在 [github](https://github.com/redblobgames/1843-planet-generation) 上。

## 参考链接（原文文末清单，按原顺序）

**行星生成 / 板块构造：**
- [Sebastian Lague 的行星生成 YouTube 系列](https://www.youtube.com/playlist?list=PLFt_AvWsXl0cONs3T0By4puYy6GM22ko8)
- [McKenzie & Parker (1967) 数学建模论文导读](https://blogs.egu.eu/divisions/ts/2020/09/30/ts-must-read-papers-mckenzie-and-parker-1967/)（EGU 博客）
- [Worldbuilding Pasta: An Apple Pie from Scratch V-A](https://worldbuildingpasta.blogspot.com/2020/01/an-apple-pie-from-scratch-part-va.html)
- [Crafting Plausible Maps](https://mythcreants.com/blog/crafting-plausible-maps/)（综述，不专讲板块）
- [Experilous: Procedural Planet Generation](https://web.archive.org/web/20220617041817/http://experilous.com/1/blog/post/procedural-planet-generation)（存档）
- [Procedural Map Generation With Voronoi Diagrams](https://squeakyspacebar.github.io/2017/07/12/Procedural-Map-Generation-With-Voronoi-Diagrams.html)
- [Evelios Dev: Plate Tectonics](https://eveliosdev.blogspot.com/2016/06/plate-tectonics.html)
- [Outerra 论坛：行星生成讨论](https://forum.outerra.com/index.php?topic=980.0)
- [tectonics.js](https://davidson16807.github.io/tectonics.js/)（库与演示）；其 research 目录的论文合集链接已失效，[博客](https://davidson16807.github.io/tectonics.js/blog/news.html)仍有好内容
- [板块运动几何：三联点（Triple Junctions）PDF](http://earthweb.ess.washington.edu/brown/downloads/ESS403/Triple_Junctions.pdf)
- [幻想地图的板块构造（视频）](https://www.reddit.com/r/proceduralgeneration/comments/85o9zt/video_guide_to_plate_tectonics/)
- [用"撕碎的纸片"做大陆（Reddit 回帖）](https://www.reddit.com/r/worldbuilding/comments/4dicue/way_to_randomly_generate_continentsterrain/d1rasn0/)
- [Leatherbee: 板块构造科学](https://leatherbee.org/index.php/2018/10/06/terrain-generation-2-tectonic-plate-science/) · [Leatherbee: 板块/大陆/海岸](https://leatherbee.org/index.php/2018/10/28/terrain-generation-4-plates-continents-coasts/)
- [板块构造模拟动画（Reddit，附说明）](https://old.reddit.com/r/proceduralgeneration/comments/bstqbe/simulating_plate_tectonics_for_map_generation/)
- [Clustered Convection for Simulating Plate Tectonics](https://nickmcd.me/2020/12/03/clustered-convection-for-simulating-plate-tectonics/)（Voronoi + 板块下沉漂浮 + 碰撞 + GPU 实现）
- [构造抬升地图（Reddit）](https://old.reddit.com/r/proceduralgeneration/comments/ysa518/quite_pleased_with_how_my_tectonic_uplift_map_is/)
- [Second-System: Tectonics 1](https://second-system.de/2022/03/01/tectonics_1)——列出各板块边界类型及其行为；Delaunay/Voronoi 2500 三角形 / 1300 区域规模；移动种子点、合并/拆分三角形
- [For In Hexes: 板块构造入门](https://forhinhexes.blogspot.com/2018/04/tectonics-primer.html)
- [YouTube：Voronoi 上的板块构造](https://www.youtube.com/watch?v=7xL0udlhnqI)（球面、噪声、板块）
- [球面上的构造板块（Reddit，附细节）](https://old.reddit.com/r/proceduralgeneration/comments/dudauc/simulating_tectonic_plates_on_a_sphere_details_in/)
- [带超大陆的实时行星构造模拟（Reddit）](https://old.reddit.com/r/proceduralgeneration/comments/10as9mo/realtime_planetary_tectonics_simulation/)
- [板块生成更新（Reddit）](https://old.reddit.com/r/proceduralgeneration/comments/1pzs6ef/update_tectonic_plate_generation/)
- [Imgur 图集](https://imgur.com/a/Cb5ri)
- [Worlds and their geography](https://web.archive.org/web/20220707094343/http://blog.particracy.com/worlds-and-their-geography/)（存档）
- [断层线的生成](http://entropicparticles.com/generation-of-fault-lines/)

**行星气候：**
- [Climate Cookbook（存档）](https://web.archive.org/web/20130619132254/http://jc.tech-galaxy.com/bricka/climate_cookbook.html)
- [Worldbuilding Workshop: Climate](https://worldbuildingworkshop.com/2015/11/27/climate/)
- [For In Hexes：潜在蒸散](https://forhinhexes.blogspot.com/2018/06/i-couldnt-think-of-pun-about-potential.html) · [For In Hexes：降雨](https://forhinhexes.blogspot.com/2018/06/rain-rain-go-away.html)
- [风带（视频）](https://www.youtube.com/watch?v=LifRswfCxFU&feature=youtu.be)

---

*编译于 2026-09-30，据当日抓取的原文版本（Created 22 Oct 2018 / Last modified 26 Sep 2026）。*
