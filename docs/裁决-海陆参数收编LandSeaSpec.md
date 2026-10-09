# 裁决：海陆参数收编 `LandSeaSpec`（`LandSeaParams` 删除）＋ 数据层分段 `Spec/` / `Carrier/`

日期：2026-10-10
状态：**已执行**（未提交）
触发：用户「Data 下一部分是 spec，另一部分应该放在什么目录名字下比较好」→
→「`LandSeaParams` 如果是 spec 为什么会有初始值，而不是和其他文件一样」→ **「一起做吧」**
关联：`docs/裁决-数据层World.Data.md`（数据层建层）｜`docs/审查报告-世界生成参数盘点.md`（§2.1 参数归属 / §四 下一步）｜
`docs/architecture.md` §9

---

## 〇 裁决表

| 项 | 改动 |
|---|---|
| **段划分** | `Data/Spec/` = 世界定义（装配层构造、按段切片下传）｜`Data/Carrier/` = 生成链内部流通的数据形状 |
| **段判据** | **"谁构造它"**：装配层构造 ⇒ Spec；生成链内部写读 ⇒ Carrier（子目录 = 自由分组，不进命名空间） |
| **收编** | `LandSeaParams` 的 **10 个世界参数**（域扭曲 3 + 三尺度 7）搬入 `LandSeaSpec`（12 字段 `record struct`） |
| **删除** | `LandSeaParams.cs`（含 `Seed = 42`——零引用，且与 `WorldSpec.Seed` 争夺"世界身份"的解释权） |
| **默认值** | 全部 10 个数值**逐字**搬入 `WorldSpecDefaults.Earth`（生产侧唯一构造点，**具名参数**） |
| **`LandSeaField`** | ctor 改 `(ContinentLayout layout, int seed, LandSeaSpec spec)`；`Params` 属性 → `Spec`；内部 `readonly LandSeaSpec` 直读字段 |
| **连带** | 33 处构造点 · 21 处 `using World.Data;` 成死引用已删 · 契约登记与 forbidden 表 · 注释路径 3 处 |
| **新增护栏** | `WorldSpecTests` 补 10 条退化档钉；`ContinentLayoutTests` 补 1 条**关断值**测试（4 个幅度全 0 ⇒ 逐点等于纯影响场） |
| **未做** | 10 个参数**不**暴露成 `[Export]` 旋钮（分级暴露属面板那批）｜不建 `WarpSpec`/`ScaleSpec` 子形状｜不改生成公式 |

**一句话**：`LandSeaParams` 此前**身兼两职**——既是"配置"（装配层构造、注释自述"接线面板/JSON 走后续批次"）
又是运行时字段持有者、还自带字段初值。它**不是**"spec 可以有初值"的例子，而是**没被规范化**的例子；
拆分后它整体归 spec 一侧，载体一侧只留真正由生成器产出/消费的形状。

---

## 一 为什么"带初始值"这件事本身就是判据

新规范（`WorldSpec` 收编时定）只允许**一个**默认值真相源：`WorldSpecDefaults.Earth`
（装配层 `[Export]` 初值全部参照它）。判据不是"spec 能不能有初始值"，而是：

> **默认值有几个真相源？**

`LandSeaParams` 的内联初值一旦进 spec，就立刻成为**第二个源**——改了 `WorldSpecDefaults` 而漏改形状初值，
两边**悄悄分叉**，且编译与单测都看不出来。三条实测证据：

| # | 证据 |
|---|---|
| 1 | **`Seed = 42` 是双重身份声明**：世界身份已归 `WorldSpec.Seed`（`WorldSpecDefaults.Earth` 首参**也是 42**）；全仓 **0 处**裸构造 `new LandSeaParams()` ⇒ **那个 42 从未生效**，只会误导人以为"Seed 归 ① 海陆阶段" |
| 2 | **10 个噪声参数的初值原先是唯一源**（`LandSeaPipeline` 只覆盖 `Seed`）⇒ 按 `审查报告` §四 扩 spec 时立刻冒出两个默认源 |
| 3 | **形态不合规范**：spec 是 `readonly record struct`（不可变值形状）；它是 `sealed class` + 可变 `public` 字段，且被 `LandSeaField.Params` 当运行时字段持有 |

★**三种"初始值"必须分开**（别一刀切）：

| 种类 | 例 | 判定 |
|---|---|---|
| **配置默认值** | `LandSeaParams.Seed = 42`、各噪声幅度 | ❌ 违"形状/内容分离"⇒ 上移 `WorldSpecDefaults`（本次已做） |
| **防 null 空值** | `MountainRidge.Points = Array.Empty<Vector3>()` | ✅ 合法，与 spec 无关，**保留** |
| **位置参数** | `WorldSpec` / `LandSeaSpec` / `TerrainSpec` | ✅ 天然无默认 |

---

## 二 `LandSeaSpec` 的形状：为什么扁平 12 字段

- **不建子形状类型**（`WarpSpec(wavelength,octaves,amplitude)` / `ScaleSpec(wavelength,amplitude)`）：
  各自只有 **1 个消费者**（`LandSeaSpec` 本身） ⇒ 撞防膨胀第③问「真实消费者？」。
  等做「面板分组 / Tier 分级暴露」时再抽（`审查报告` §3.3 对 `Range` 类型用的是同一条口径）。
- **12 个位置参数的顺序陷阱已消除**：生产侧**只有一处**构造（`WorldSpecDefaults.Earth`，
  **全部用具名参数**）；测试侧一律 `WorldSpecDefaults.Earth.LandSea with { … }`。
- **`Medium`/`Small` 没有 Octaves**：`LandSeaField` 里是写死的 **3 / 2**（模型内部常数，不是世界参数）——
  本次只搬"盘点认定的 A 类世界参数"，不趁机扩大参数面。

## 三 数值去向：为什么进 `WorldSpecDefaults` 而不是 `World.Constants`

口径 = **消费者个数**：`Geology.TargetRegionAreaKm2` 有**两个**消费者（`WorldSpecDefaults` +
`GeologicalRegions.Generate` 的缺省参数），故成具名常量；这 10 个各自只有本档**一个**消费者
⇒ 直接字面量，由 `WorldSpecTests` **逐字钉住**（与同处该档的 `Seed=42` / `ContinentCount=7` /
`LandFraction=0.29f` 完全同待遇）。

## 四 装配层：10 个参数**不**暴露成 `[Export]`

`WorldGenPlanet.BuildSpec()` 用 `with` 只覆盖现有两个旋钮：

```csharp
WorldSpecDefaults.Earth.LandSea with { ContinentCount = ContinentCount, LandFraction = LandFraction }
```

理由：`审查报告` §四已定"**按阶段分组（不是扁平滑杆）+ 分级暴露（Tier 1/2/3）**"——
把 10 个原始旋钮一次性铺到 Inspector 上正是"扁平滑杆"。⇒ `[Export]` 面**本次不变**。

---

## 五 改动清单

| 类别 | 文件 |
|---|---|
| 数据层 | `Data/Spec/LandSeaSpec.cs`（+10 字段）· **删** `Data/LandSeaParams.cs` · `Spec`/`Carrier` 六文件搬家 |
| 逻辑层 | `WorldSpecDefaults.cs`（组装 12 字段）· `LandSeaFields.cs`（ctor + `Spec`）· `LandSeaPipeline.cs`（切片下传） |
| 装配层 | `WorldGenPlanet.cs`（`BuildSpec` 用 `with`） |
| 测试 | `WorldSpecTests`（+10 钉）· `ContinentLayoutTests`（+1 关断值；`field.Spec`）· `ArchitectureContractTests`（登记/forbidden）· 20 个测试文件 33 处构造点 |
| 注释路径 | `MountainSkeleton.cs` · `ContinentLayout.cs` · `ArchitectureContractTests.cs` |

★**搬迁纪律落实**：`.cs` 与 `.cs.uid` **成对搬**（6 对已核）；迁移后对 `using World.Data;` 做**死引用清扫**
（21 处删除——这是 `LandSeaParams` 删除后暴露的连带，编译器**不报**）；三处注释里的旧路径已同步
（注释不受编译检查，漏改即静默失效）。

---

## 六 验证（本次实测）

| 项 | 结果 |
|---|---|
| `dotnet build` | **0 警告 / 0 错误** |
| `dotnet test scripts/Test/World.Tests` | **285 PASS / 0 失败**（= 284 基线 **+1** 新增关断值测试，无既有测试增减） |
| `dotnet build scripts/Test/PerfBench` | 0/0（**不在 `world.sln`**，须手工构建） |
| `dotnet build scripts/Test/World.Tests.Local` | 0/0（同上；该项目的 3 处**全限定名**已同步） |
| headless 世界生成 | `n=288122 res=4 land=29.0% regions=95 meanP=1098mm/年 meanT=76.0C ridges=71 basins=6971(endorheic 0) lakes=3439 thr=0.636` —— **逐字段同基线**（仅 `total=` 为机器噪声） |
| `WORLDGEN-READY` / `Cannot load C# script` | 正常打印 `ViewCtor=… BuildChunks=… RiverLinesBuild=…`，**零 ERROR** |
| `.cs.uid` | 6 对搬家后完整；`--headless --import` 已刷新 |

> ★口径提醒：本次是"搬位置 + 换持有者"的重构，**数值一字未动** ⇒ **build 与单测全绿证明不了什么**
> （它们本来就该绿）。唯一真凭据是 **headless 读数逐字段比对**，本次已做且一致。

---

## 七 回滚

```bash
git checkout -- scripts/Logic/WorldGen/Placement/LandSeaFields.cs \
                scripts/Logic/WorldGen/Composition/LandSeaPipeline.cs \
                scripts/Logic/WorldGen/Composition/WorldSpecDefaults.cs \
                scripts/Scene/WorldGen/Composition/WorldGenPlanet.cs \
                scripts/Test/World.Tests/ scripts/Test/PerfBench/Program.cs \
                scripts/Test/World.Tests.Local/Program.cs
# 目录归位（Spec/Carrier 两段撤回平铺）：
mv scripts/Logic/Data/Spec/*   scripts/Logic/Data/ && rmdir scripts/Logic/Data/Spec
mv scripts/Logic/Data/Carrier/* scripts/Logic/Data/ && rmdir scripts/Logic/Data/Carrier
# 恢复 LandSeaParams.cs（其内容见本次 diff 的 deletion 侧）
```
