# 设计 · new_HexWorld 地壳运动 15 —— 外部替代算法调研（求解器侧 + 物理侧）

> 状态：**调研记录 v0.1（未拍板）**。缘起：用户 2026-09-27「替换掉这个算法，从网上找替代品」。
> 目的：把**真实存在**的替代方案摆出来，并按本项目六条硬约束筛一遍——**本文只做筛选与建议，不落代码**。
> 六条硬约束（下文简称 H1–H6）：**H1 物质守恒**（Crust 七池 + owner 随物质）｜**H2 海拔可追溯机制**（禁"只写海拔"）｜**H3 事件涌现**（缝合/裂解由应变局部化触发）｜**H4 逐位确定性**（13 支指纹）｜**H5 无补丁自洽**（拆限幅/钳制/伺服仍自洽）｜**H6 每步固定成本**（无迭代项，面向 res5）。

---

## 0 一句话结论

**没有可以"直接插进来"的现成品。** 外部方案分两族，代价性质完全不同：

| 族 | 换什么 | 接口影响 | 主要风险 | 可行性 |
|---|---|---|---|---|
| **A 求解器侧** | 不动物理，换**解算算法**（多重网格/预条件子） | **零**（`Solve()` 内部） | 只是收敛速度 | ★**低风险、有文献处方、可立即验证** |
| **B 物理侧** | 换**模型**（程序化/块体/松弛动力学） | **大**（物质/事件/均衡接口要重写） | 丢掉 H1-H3 中任一条即不可接受 | 中高风险，须先论证 |

**我的判断**：B 族里没有一条能"直接实现"我们要的东西（因为它们都不带我们的物质池与机制纪律）；而 **A 族存在一份**针对"我们正好遇到的那个症状"的**现成处方**（见 §2）。

---

## 1 B 族：物理侧替代方案（换模型）

> ★**2026-09-27 用户拍板**：**A 族立即执行；B 族留作备用（实在不行再考虑）；违反 H1/H2/H3 的 B1/B2/B6 直接去掉**（下表保留它们只为**留档追溯**——已**不再列为候选**）。
> **保留为备用的只有三条**：**B3（块体+弱带）｜B4（局部松弛/伪时间）｜B5（Clustered Convection，带质量）**。

| 方案 | 做法 | 成本 | 与 H1–H6 的差距 | 处置 |
|---|---|---|---|---|
| **B1 程序化板块**<br>Cortial et al. 2019《Procedural Tectonic Planets》(CGF/Eurographics)<br>`perso.liris.cnrs.fr/eric.galin/Articles/2019-planets.pdf` | 板块 = 球面 Voronoi 胞；板块漂移；四类交互（俯冲/陆-陆碰撞/洋壳生成/裂解）按**几何互穿透距离**更新地壳参数（褶皱方向/海拔/壳龄）+ 侵蚀 + 洋壳老化下沉。**δt = 2 My**，地壳分辨率 **50–500 km**，交互速率、几分钟出一颗行星 | 极低（几遍扫描） | **H1 ✗**（无七池/无 owner 随物质）｜**H2 部分**（海拔由规则+侵蚀更新，非"厚度经均衡"）｜**H3 部分**（有裂解，但非应变局部化）｜H4 ✓ H6 ✓ | **否决**（违反 H1；H2 不成立）|
| **B2 抬升+河流功率（含解析解）**<br>Cordonnier et al. 2016 / 2018《Sculpting Mountains》/ 2024《Analytical terrains》<br>`cs.purdue.edu/cgvlab/www/resources/papers/…` | 抬升图（uplift map）驱动 + **河流功率定律**侵蚀；2018 版把地壳当**不可压黏性体**做**体积守恒折叠 + 均衡**；2024 版给出**解析解 ⇒ 完全不需要迭代仿真**（时间变成滑动条） | 极低（解析/几次扫描）；2018 版交互级 | **H1 部分**（2018 版有体积守恒，但非七池）｜**H2 ✓**（均衡导出海拔）｜**H3 ✗**（抬升图是输入，不是涌现）｜H4 ✓ H6 ✓ | **否决**（违反 H3）|
| **B3 块体 + 弱带（block-and-fault）**<br>Gabrielov/Keilis-Borok/Soloviev《block structure dynamics》(npg 2008)；Rundle/Burridge–Knopoff 元胞滑块模型；Meade 2005 块体模型 | 岩石圈 = **刚性块体 + 薄而弱的断层带**；变形只发生在断层带与底接触处；块体被边界块/底介质的给定运动拖走 ⇒ 事件（地震）由块体运动累积应力而**涌现** | 低（块体级 O(#块)，断层处局部） | **H1 部分**（质量/应变在断层带记账即可实现）｜H2 需自建（均衡可后接）｜**H3 ✓**（事件天然涌现，且统计性质是这类模型的长项）｜H4 ✓ H6 ✓ | **备用**（需先写"不复现刚体拟合"论证）|
| **B4 局部松弛 / 伪时间双曲松弛**<br>SORh：`escholarship.org/uc/item/5gc906gb`；冰流模型《Beyond the Stokes approximation》(J. Glaciology) | 用**双曲伪时间**局部松弛去逼近椭圆解；冰流界用 shallow visco-elastic relaxation **收敛到黏性 Stokes 极限** | 每步固定遍数 | ★**H1–H6 全部可满足**（因为它不改本构/物质，只改"怎么逼近平衡"）——**这正是 docs/14 候选 B 的学术对应物** | **备用（= 候选 B 的理论先例）** |
| **B5 点云 / Clustered Convection**<br>Nick McDonald 2020 `nickmcd.me/2020/12/03/clustered-convection-for-simulating-plate-tectonics/` | 每个"段"带**质量/厚度/密度**，浮在热图上按浮力算均衡高度；板块=段的集合，按质心做**刚体运动**；俯冲=密度大者下潜并把质量转移；热图用 shader 扩散 | 低（GPU，全并行） | **H1 ✓**（带质量）；H2 ✓（浮力/密度导出）；H3 部分（碰撞半径扫描生成事件）；H4 ✓（固定序）；H6 ✓。★**与我们的状态模型最接近的一条** | **备用** |
| **B6 元胞自动机库**<br>Mindwerks `plate-tectonics`（C++/Python，WorldEngine 在用）；`tectonics.js`（浏览器 3D，CC-BY） | 网格 CA + 板块重叠消解 + 折叠/侵蚀；输出高度图（platec 可另出板块图） | 极低 | **H1 ✗ H2 ✗**（纯高度图，无物质/无机制）⇒ 与我们 §2.1 纪律直接冲突 | **否决**（违反 H1/H2）|

**B 族的共同问题**：它们**不携带我们的状态**（七池/owner/热/拓扑），也不遵守"海拔必须可追溯机制"。采用 = **重写与运输/均衡/海平面/事件的所有接口**，而不是"替换一个算法"。**且旧02 静态路线（2026-09-22 整篇删除）正是 B1/B6 这一族的前身**——当时被否的理由（地形与机制脱钩、规则跟不上演化、无法表达历史、连大陆架都生成不了）**今天依然成立**。

**唯一值得单列的是 B3（块体+弱带）**：它与我们**已退役的"刚体拟合"**必须分清——
> 退役的是**把刚体拟合当补丁**贴在连续体解算器上（用来平均化、掩盖失控）⇒ 那是补丁；
> B3 是**把"块体+弱带"当模型本身**（学界成熟架构，事件统计是它的长项）。
⇒ 若要走 B3，判据是 **H5：拆掉一切限幅/钳制/伺服后仍自洽**，外加 H1 的物质账。

---

## 2 A 族：求解器侧替代（不动物理，只换解算算法）★ 推荐先做

**文献对我们的症状有明确结论**：几何多重网格（GMG）在**局部大黏度对比**下会**崩**（Gee et al. 2009）；我们的"几何档 R·A·P 误差 0.67 vs 聚合档 1.29e-4"、以及"单轮 22 次 CG（正常应 10-20）"正是同一现象。下面是**可直接验证的七条处方**：

| # | 处方 | 出处 | 对我们的意义 |
|---|---|---|---|
| **A1** | **AMG（按"强连接"建粗层）替代几何粗化** | Gee et al. 2009 `homepage.tudelft.nl/d2b4e/papers/Gee09RMSVBS.pdf`：AMG 用算子强连接粗化 ⇒ 对**局部大对比鲁棒、O(n)**；GMG 会崩 | 直接对症我们"几何档误差 0.67"——粗层该由**算子**决定，不是由网格几何决定 |
| **A2** | **F-cycle 替代 V-cycle** | Tackley GJI 1999 `academic.oup.com/gji/article/137/3/793/614917`：**F-cycle 的收敛率对局部黏度对比近乎无关**，V-cycle 随对比恶化；F-cycle 贵 28–36% | 我们用的正是 V-cycle ⇒ 换 F-cycle 可能直接砍掉迭代数 |
| **A3** | **黏度粗化用算术平均（VR arithmetic）** | 同上：注入/调和/温度限制都**不合适**，算术平均最优 | 一条参数级检查（我们现在是哪种？） |
| **A4** | **在粗层多加平滑步** | 同上：粗层多平滑 ⇒ **用更少的细层 pre-smoother 达到同收敛率** | ★直击我们最大的一笔：**层0 平滑占总成本 75.4%** |
| **A5** | **对比度渐进（continuation）+ 热启动** | Liang et al. 2013（GPU MG for variable-viscosity Stokes）：V-cycle 开头把黏度对比压小、逐轮恢复 ⇒ 拿初值 | 配合我先前发现的"**现在是冷启动**"（`Solve()` 清零）⇒ 两条一起用 |
| **A6** | **RBGS（红黑 GS）smoother 替代 Jacobi** | Liang 2013；Tackley 1999（SCGS：对称耦合 GS 更优） | 我们默认档是**无阻尼 Jacobi**（"不构成光滑"，本项目自己的结论）⇒ 换成真正会"光滑"的 smoother |
| **A7** | **对比度/网格双鲁棒预条件子（AGKS / 增广拉格朗日 + 鲁棒 MG）** | Aksoylu & Ünlü `burak.wayne.edu/pubsNew/b04_aksoyluUnlu2013_preprint.pdf`；arXiv 2107.00820（对比度 1e10、16 亿未知量） | 上限方案，重，但证明"大对比不必然要很多迭代" |

### 2.1 A 族实验协议（**跑前写死**，2026-09-27 用户拍板启动）

**原则**：**一次只改一个变量**；每条 A 项 = **一个显式可选档**（默认档不动）+ 一次**同实例同窗** A/B；判据先写；**长任务不并行**（与判别腿 / res4 排队，不抢机器）。

| 序 | 项 | 改哪 | 预期 | 成本 | 判据（**只认计数**） |
|---|---|---|---|---|---|
| **A0** | **先读清现状**（不是改动） | — | — | 分钟级 | 现档实际配置：ν1/ν2 平滑步数分布、**黏度粗化方式**（算术/调和？）、smoother 类型（自陈）⇒ **先知道自己现在是什么** |
| **A4** | **粗层多加平滑步**（细层 pre-smoother 可减） | MG 配置 | 直击**层0 占总成本 75.4%** | 小 | 迭代数**不升** 且 层0 当量下降 |
| **A2** | **F-cycle 替代 V-cycle** | MG 循环类型 | F-cycle 收敛率对局部对比近乎无关 | 小 | 迭代数 / 当量**双降** |
| **A6** | **换真正的 smoother**（现默认=无阻尼 Jacobi，本项目自判"不构成光滑"） | 解算核心 | 迭代数下降 | 中 | 同 A2 + 新档 13 支指纹 |
| **A3** | **黏度粗化 = 算术平均** | 粗层构造 | 收敛改善（Tackley：注入/调和/温度限制均不合适） | 小 | 同 A2 |
| **A5** | **对比度渐进（continuation）+ 热启动** | 解算入口 | 初值更好 ⇒ 迭代少（现为**冷启动**：`Solve()` 清零） | 中 | 同 A2；★**会改位级指纹** ⇒ 新档重标定 |
| **A1** | **AMG（算子"强连接"粗化）替代几何粗化** | 粗化策略（**结构性**） | 大对比鲁棒（对症"几何档 R·A·P 误差 0.67"） | 大 | 同 A2；**先出设计再动手** |

**共同口径（沿用本项目纪律）**：判据**只认计数**（CG 迭代数 / V-cycle 当量 / ns per slot），**墙钟不作判据**；每份报告贴 **`[SW]` 行原文 + DLL sha**；**同统计量**（均值对均值，禁均值÷峰值）；**禁跨分辨率外推**；**位级无损项免 A/B**，改数值的项必须跑 13 支指纹（新档锚）。

**已知坑（先查再动手）**：
1. **红黑着色未必可行**：RBGS 需要图**二分**，而 H3 网格含**五边形** ⇒ 存在**奇环** ⇒ 可能无法两色。若不可行，退到"**固定序 GS 扫**"（串行但确定性）或"**带阻尼 Jacobi（最优 ω）**"——**不许**用非确定性着色兜。
2. **F-cycle 必须保对称**：V-cycle 是**对称构造**（CG 预条件子要求对称正定）；F-cycle 的对称版本要先确认，否则 CG 的前提被破坏。
3. **别破坏 T2-1 的对角缓存**：层0 加了平滑步数后，脏标（`_mgDiag[0]` 复用）不能失配。
4. **一次只改一个**：A2/A3/A4/A6 都能"降迭代数"，**同时上就再也说不清是谁的功劳**。

**A 族的价值**：**接口零改动、默认档不动、可逐条 A/B**，且每条都有文献支持。若 A1+A2+A4+A6 落地后迭代数能降一个量级，那么 **res5 的算量账要重算**（在"换方程"之前先吃掉这一笔更划算）。

---

## 3 排序（★2026-09-27 已拍板）

1. **A 族 = 当前唯一执行路线**，按 **§2.1 协议**跑（A0 → A4 → A2 → A6 → A3 → A5 → A1），**一次只改一个变量**。
2. **B 族 = 备用留档**，**只有"实在不行"才启用**；启用时的指定两条是 **B4（局部松弛/伪时间，= 候选 B 的理论先例）** 与 **B3（块体+弱带，需先写"不复现刚体拟合"论证）**；**B5** 因带质量也被保留但排在 B4/B3 之后。
3. **B1 / B2 / B6 = 已否决并删除**（违反 H1/H2/H3；采用等于重走 09-22 已删除的静态路线）。

---

## 4 拍板记录

**已拍定（2026-09-27，用户原话）**：「**先搞 A 族，B 族留着作为备用，如果实在不行再考虑，把违反的 B 族直接去掉**」
⇒ A 族执行；B 族备用（B1/B2/B6 删除，B3/B4/B5 留档）；A 族协议 = §2.1。

**遗留（A 族报告出来后再拍）**：
1. A 族若某个开关**确实降了一个量级**，**是否进默认档**（会改位级指纹 ⇒ 需新档重标定 + 13 支指纹）？
2. A 族收益吃完后 **res5 的账怎么重算**（是否已够，还是要回去动 docs/14 的物理侧）？
3. 是否要出一份**逐条对照表**（外部方案 × H1–H6 × 改动面）存档？

## 5 来源清单（可点开核）

- Procedural Tectonic Planets (Cortial et al. 2019)：https://perso.liris.cnrs.fr/eric.galin/Articles/2019-planets.pdf
- Sculpting Mountains (Cordonnier et al. 2018)：https://cs.purdue.edu/cgvlab/www/resources/papers/Cordonnier-IEEE_Transactions_on_Visualization_and_Computer_Graphics-2018-Sculpting_Mountains_Interactive_Terrain_Modeling_Based_on_Subsur.pdf
- Analytical terrains (Tzathas et al. 2024)：http://www-sop.inria.fr/reves/Basilic/2024/TGSC24/Analytical_Terrains_EG.pdf
- plate-tectonics (Mindwerks)：https://github.com/Mindwerks/plate-tectonics/
- tectonics.js：https://github.com/davidson16807/tectonics.js
- Clustered Convection：https://nickmcd.me/2020/12/03/clustered-convection-for-simulating-plate-tectonics/
- Block structure dynamics：https://npg.copernicus.org/articles/15/209/2008/npg-15-209-2008.pdf
- Multigrid convection with strongly variable viscosity (Tackley)：https://academic.oup.com/gji/article/137/3/793/614917
- Scalable robust solvers for FE geodynamics (Gee et al.)：https://homepage.tudelft.nl/d2b4e/papers/Gee09RMSVBS.pdf
- GPU multigrid for variable-viscosity Stokes (Liang et al. 2013)：http://jupiter.ethz.ch/~tgerya/reprints/2013_HPCA_Liang.pdf
- Robust preconditioning for high-contrast Stokes (Aksoylu & Ünlü)：http://www.burak.wayne.edu/pubsNew/b04_aksoyluUnlu2013_preprint.pdf
- AL Schur + robust multigrid，对比度 1e10：https://arxiv.org/pdf/2107.00820
- SORh hyperbolic relaxation for elliptic problems：https://escholarship.org/uc/item/5gc906gb
- Beyond the Stokes approximation (shallow visco-elastic ice-sheet)：https://www.cambridge.org/core/journals/journal-of-glaciology/article/beyond-the-stokes-approximation-shallow-viscoelastic-icesheet-models/9FB2F9C588EB9084DD0731DAD308831C
