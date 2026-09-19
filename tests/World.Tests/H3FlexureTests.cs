using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using NUnit.Framework;
using World.NewHexWorld;
using World.NewHexWorld.Plate;
using World.Tectonics;

namespace World.Tests;

/// <summary>
/// 弹性挠曲（06 §3 批次 B）的回归测试——薄板弯曲刚度给"区域沉降"装上横向耦合。
///
/// 四条要钉住的物理性质：
///   ① **长波完全补偿**：均匀载荷下 `w = w_airy`（DC 增益恰为 1）⇒ 自由板锚/水量反解不受挠曲影响；
///   ② **短波被板强度撑住**：亚格尖峰被削低、载荷摊到邻域（纯 Airy 下不会发生）；
///   ③ **前隆 / 前陆盆地**：载荷边缘外出现 **反号环**（边缘下沉 → 环上抬）——区域响应的签名，
///      纯局部均衡（Airy）与任何正定平滑核都**不可能**产生它；
///   ④ **屈服限**：又窄又高的山带让板破裂（有效 Te 塌）⇒ 山保住，而远处仍是完整刚度。
///
/// ⚠️ **夹具纪律一：洋底热年龄必须 > 0**。`Te = 2.7·√age` ⇒ `Age = 0` 的洋格 `Te = 0`、无任何弯曲刚度，
/// 于是载荷"放在不存在的板上"——这样测出来的东西不是挠曲（2026-09-18 实测：那时解会退化成
/// 孤立 7 格子系统）。本类统一取 `OceanAgeMy = 100`（Te = 27 km、α ≈ 50 km ⇒ 真板）。
/// ⚠️ **夹具纪律二：载荷边界必须渐变**（余弦 taper）。硬边在网格尺度上产生高曲率 ⇒ 触发屈服限 ⇒
/// 板处处破裂 ⇒ 挠曲不生效（那是夹具的坑，不是模型的）。
/// ⚠️ **夹具纪律三：全场 Age 取同一个值**（本题=100）。若只让陆壳 Age=0，载荷边缘会同时出现
/// "热沉降阶跃"这个不该有的额外阶跃；统一取同值 ⇒ 热沉降为常数 ⇒ 曲率只来自我们铺的 felsic 渐变。
///
/// 分辨率纪律（06 §6）：α（Te 27–35 km）≈ 50–60 km，格宽 res3 = 111 km、res4 = 42 km。
/// res4 能把前隆环**分辨成 1–2 格宽**（本类用它）；res3 只能把整条前隆挤进**载荷外紧邻的一格**。
/// 纪律：只用 [Test]；不写文件；不触碰 GD.*/LogService。
/// </summary>
public class H3FlexureTests
{
    private const int Res = 4;                       // 格宽 42 km（α/h ≈ 1.2–1.4：前隆环可分辨）
    private const float OceanAgeMy = 100f;           // 老洋底 = 真板（27 km）；见纪律一
    static readonly Lazy<Ball> SharedBall = new(() => new Ball(Res, 1f));
    static Ball Ball => SharedBall.Value;
    static readonly MaterialDensity Material = new();

    /// <summary>参考海洋场：每格 7100 m 年轻洋壳质量、热年龄 <paramref name="ageMy"/>。
    /// 位移（Airy）在远场处处等于同一值 ⇒ 可作为"区域基准面"。</summary>
    static H3PlateFields OceanPlate(float ageMy = OceanAgeMy)
    {
        var fields = new H3PlateFields(Ball.CellIds.Length);
        for (int i = 0; i < fields.Count; i++)
        {
            fields.PlateId[i] = 0;
            fields.ClearCell(i);
            fields.MaficVolcanic[i] = Material.MaficVolcanicMin * H3Isostasy.OceanReferenceThicknessM;
            fields.Age[i] = ageMy;
        }
        return fields;
    }

    /// <summary>距离场（BFS 跳数，从 anchor 起）。</summary>
    static Dictionary<int, int> HopsFrom(Ball ball, int anchor)
    {
        var hops = new Dictionary<int, int>();
        var queue = new Queue<int>();
        hops[anchor] = 0;
        queue.Enqueue(anchor);
        while (queue.Count > 0)
        {
            int c = queue.Dequeue();
            foreach (int nb in ball.CellNeighbors[c])
            {
                if (hops.ContainsKey(nb)) continue;
                hops[nb] = hops[c] + 1;
                queue.Enqueue(nb);
            }
        }
        return hops;
    }

    /// <summary>铺"大陆/山体"载荷：半径 <paramref name="hops"/> 内为满厚，外缘 <paramref name="taper"/> 跳
    /// **余弦渐变**到零。陆壳按比例顶替洋壳质量（`mafic × (1−f)`）——不凭空增删柱子，
    /// 于是"载荷"就是长英质厚度那一个自变量。`Age` 全场不动（见纪律三）。</summary>
    static void StampPlateau(Ball ball, H3PlateFields fields, int anchor, int hops, float felsicThicknessM, int taper)
    {
        foreach (var kv in HopsFrom(ball, anchor))
        {
            int d = kv.Value;
            if (d > hops + taper) continue;
            float f = d <= hops ? 1f : 0.5f * (1f + MathF.Cos(MathF.PI * (d - hops) / taper));
            fields.FelsicPlutonic[kv.Key] = Material.FelsicPlutonic * felsicThicknessM * f;
            fields.MaficVolcanic[kv.Key] = Material.MaficVolcanicMin
                * H3Isostasy.OceanReferenceThicknessM * (1f - f);
        }
    }

    static float AiryAt(H3PlateFields fields, int cell)
        => H3Isostasy.ComputeCellDisplacement(fields, cell, Material);

    /// <summary>取一个离 anchor 至少 <paramref name="minHops"/> 跳的格子（当"远场/区域基准面"用；
    /// 不能用固定编号猜——球面 H3 编号不保证空间邻近）。</summary>
    static int FarCell(int anchor, int minHops)
    {
        foreach (var kv in HopsFrom(Ball, anchor))
            if (kv.Value >= minHops) return kv.Key;
        throw new InvalidOperationException($"网格上找不到离 {anchor} 至少 {minHops} 跳的格子");
    }

    /// <summary>逐跳统计 `w − w_airy` 的环均值（挠曲贡献本身；Airy 基线被减掉 ⇒ 读数与 datum 无关）。</summary>
    static double[] RingDeviation(Ball ball, H3PlateFields fields, H3Isostasy isostasy, int anchor,
        int maxRing, out int[] ringCount)
    {
        var sum = new double[maxRing + 1];
        ringCount = new int[maxRing + 1];
        var dist = HopsFrom(ball, anchor);
        for (int i = 0; i < fields.Count; i++)
        {
            if (!dist.TryGetValue(i, out int d) || d > maxRing) continue;
            sum[d] += isostasy.Displacement[i] - AiryAt(fields, i);
            ringCount[d]++;
        }
        for (int d = 0; d <= maxRing; d++)
            if (ringCount[d] > 0) sum[d] /= ringCount[d];
        return sum;
    }

    static string Profile(double[] dev, int[] count)
    {
        var sb = new System.Text.StringBuilder();
        for (int d = 0; d < dev.Length; d++)
            if (count[d] > 0) sb.Append($"r{d}={dev[d]:F0} ");
        return sb.ToString();
    }

    static H3Isostasy Solve(Ball ball, H3PlateFields fields)
    {
        var isostasy = new H3Isostasy(ball.CellIds.Length);
        isostasy.ComputeDisplacement(ball, fields, Material);
        return isostasy;
    }

    // ═══════════════════════════════════════════════════════════════
    // ① 长波完全补偿（DC 增益 = 1）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Flexure_UniformLoad_LeavesAiryUnchanged()
    {
        // 全球均匀陆壳（波长 = 行星尺度 ⇒ α⁴k⁴ → 0）⇒ 挠曲必须**不动**它
        //（否则海平面反解/自由板锚会被挠曲整体平移 —— 这是选"直接解双调和"而非卷积分核的原因）。
        var fields = OceanPlate();
        for (int i = 0; i < fields.Count; i++)
        {
            fields.FelsicPlutonic[i] = Material.FelsicPlutonic * H3Isostasy.LandReferenceThicknessM;
            fields.MaficVolcanic[i] = 0f;
        }
        var isostasy = Solve(Ball, fields);
        float maxDiff = 0f;
        for (int i = 0; i < fields.Count; i++)
            maxDiff = MathF.Max(maxDiff, MathF.Abs(isostasy.Displacement[i] - AiryAt(fields, i)));
        Assert.Less(maxDiff, 1f, $"均匀载荷下挠曲改动了 {maxDiff:F3} m —— DC 增益不为 1（长波必须完全补偿）");
        Assert.Less(isostasy.FlexurePeakReductionM, 1f, "均匀载荷没有任何峰被削低");
    }

    // ═══════════════════════════════════════════════════════════════
    // ② 短波被板强度撑住：尖峰被削低、载荷摊到邻域
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Flexure_SpikeNarrowerThanAlpha_IsCrushed_AndLoadSpreadToNeighbours()
    {
        // 单格陆壳尖峰（宽 42 km < α ≈ 50 km）⇒ 板撑不住这么窄的载荷：峰被**砍掉近一半**、
        // 载荷摊到邻域。这正是"单格假山"的来源之一：纯 Airy 下这一格独享全部浮力。
        // 定量对照（连续理论，点载荷 w(0) = P/(8Δρgα²) ÷ Airy P/(Δρg h²) = h²/(8α²) ≈ 9%）：
        // 实测保留 ~40% 的 Airy 高差 —— 比连续点载荷估计更"硬"，因为离散网格在**亚格尺度**上
        // 对最高频模的衰减比连续算子弱（网格尺度上 `|λ_L|` 封顶在 `2c`，而连续 ∇² 无上界）。
        // ⚠️ α 平滑（见 `H3Isostasy.ApplyFlexure`）会让**孤立硬格**（陆壳 35 km 嵌在 27 km 老洋板里）
        // 的有效刚度降到邻域均值 ⇒ 亚格尖峰的削低量比不平滑时小（实测削低 7623 → 4036 m）。
        // 这是有意取舍：换来的是"两个成分相同的相邻格差 7.6 km"这类**网格假起伏**被消掉。
        var fields = OceanPlate();
        StampPlateau(Ball, fields, 0, hops: 0, felsicThicknessM: 60000f, taper: 0);
        var isostasy = Solve(Ball, fields);
        int farCell = FarCell(0, minHops: 8);

        float regional = AiryAt(fields, farCell);                // 远场洋底（区域基准面）
        float loadHeight = AiryAt(fields, 0) - regional;         // 载荷的 Airy 高差
        float flexHeight = isostasy.Displacement[0] - regional;
        Assert.Greater(loadHeight, 3000f, "合成的单格尖峰在纯 Airy 下应显著高于洋底");

        Assert.Greater(isostasy.FlexurePeakReductionM, loadHeight * 0.25f,
            $"亚格尖峰应被板削掉可观的一块：Airy 高差 {loadHeight:F0} m，实测削低 {isostasy.FlexurePeakReductionM:F0} m");
        Assert.Less(flexHeight, loadHeight * 0.6f,
            $"尖峰在挠曲下应被明显压低：Airy {loadHeight:F0} m → 挠曲 {flexHeight:F0} m"
            + $"（削低 {isostasy.FlexurePeakReductionM:F0} m；纯 Airy 下这里恒等于高差本身）");

        // 邻域被抬起（削掉的载荷摊到周围）：紧邻格明显高于区域基准面
        float nbSum = 0;
        foreach (int nb in Ball.CellNeighbors[0]) nbSum += isostasy.Displacement[nb] - regional;
        float nbMean = nbSum / Ball.CellNeighbors[0].Length;
        Assert.Greater(nbMean, loadHeight * 0.05f,
            $"尖峰邻域应被抬起（区域支撑）：实测邻格平均高出区域基准 {nbMean:F0} m"
            + $"（载荷高差 {loadHeight:F0} m；纯 Airy 下恒 0）");
        Assert.Less(isostasy.FlexureResidualM, 10f,
            $"CG 应已收敛（残差 {isostasy.FlexureResidualM:F2} m）");
    }

    // ═══════════════════════════════════════════════════════════════
    // ③ 前隆 / 前陆盆地：载荷边缘外的反号环（区域响应的签名）
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Flexure_WidePlateauEdge_HasForebulgeRing()
    {
        // 宽台地的**边缘**是挠曲最该出签名的地方：板在载荷下被压弯，弯到外面必然上翘（前隆），
        // 再往外是衰减的符号交替（前陆盆地/海沟外侧隆起同源）。
        // 纯 Airy（局部均衡）与任何正定平滑核都做不出**反号**环 ⇒ 这条是"横向刚度真的接上了"的判据。
        var fields = OceanPlate();
        StampPlateau(Ball, fields, 0, hops: 6, felsicThicknessM: 60000f, taper: 3);
        var isostasy = Solve(Ball, fields);
        var dev = RingDeviation(Ball, fields, isostasy, 0, maxRing: 14, out var count);
        string profile = Profile(dev, count);

        // 载荷边缘（本夹具在 6+3=9 跳内含载荷）⇒ 边缘外紧邻环先被压低
        double minEdge = 0;
        int minRing = -1;
        for (int d = 7; d <= 10; d++)
            if (count[d] > 0 && dev[d] < minEdge) { minEdge = dev[d]; minRing = d; }
        Assert.Less(minEdge, -100.0,
            $"载荷边缘外应被压低（板被弯下去）—— 实测边缘最低环 r{minRing}={minEdge:F0} m，剖面 {profile}");

        // 反号环：边缘之外必须出现**正值**（上翘 = 前隆）
        double maxOuter = 0;
        int maxRing = -1;
        for (int d = 1; d < dev.Length; d++)
            if (count[d] > 0 && dev[d] > maxOuter) { maxOuter = dev[d]; maxRing = d; }
        Assert.Greater(maxOuter, 100.0,
            $"载荷外应出现**上弯环**（前隆/前陆盆地反号环）：实测最高环 r{maxRing}={maxOuter:F0} m，剖面 {profile}");
        Assert.Greater(maxRing, minRing,
            $"反号环应在压低环之外：压低 r{minRing} → 上弯 r{maxRing}（剖面 {profile}）"
            + "—— 若上弯环出现在载荷内侧说明符号错了");

        // 远场必须衰减回区域基准面（挠曲是局部响应，不是整体平移）
        int far = dev.Length - 1;
        Assert.Less(Math.Abs(dev[far]), maxOuter / 5.0,
            $"远场（r{far}）应衰减到前隆幅度的 1/5 以下：实测 {dev[far]:F0} m vs 前隆 {maxOuter:F0} m，剖面 {profile}");
    }

    // ═══════════════════════════════════════════════════════════════
    // ④ 屈服限：窄而高的山带让板破裂（山保住），远处仍完整
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Flexure_YieldLimit_BreaksNarrowBelt_ButFarFieldStaysElastic()
    {
        // 06 §5.1：126 km 宽、5 km 高的山带若按完整弹性板（Te 35 km）响应会被支撑掉大半；
        // 但该处弯曲应力 σ = E·Te·κ/(2(1−ν²)) ≈ 1.8 GPa ≫ 250 MPa ⇒ 板**屈服破裂** ⇒ 有效 Te 塌
        // ⇒ 该带几乎完全补偿（山保住）而**外围仍弹性**（前陆盆地）。这条让"刚性克拉通内部 +
        // 破碎边缘"自然涌现，不需要任何分支。
        var fields = OceanPlate();
        StampPlateau(Ball, fields, 0, hops: 1, felsicThicknessM: 50000f, taper: 2);
        var isostasy = Solve(Ball, fields);
        var dist = HopsFrom(Ball, 0);

        int farCell = -1;
        foreach (var kv in dist) if (kv.Value == 6) { farCell = kv.Key; break; }
        Assert.Greater(isostasy.MaxSmoothedCurvaturePerM, 0f, "应看到非零的平滑载荷曲率（屈服判据的输入）");

        float coreTe = isostasy.EffectiveElasticThicknessM[0];
        float thermalTe = H3Isostasy.TeContinentalKm * 1000f;          // 陆壳热年龄 Te = 35 km
        float farTe = isostasy.EffectiveElasticThicknessM[farCell];
        float farThermalTe = isostasy.ElasticThicknessM(fields, farCell);
        Assert.Less(coreTe, thermalTe / 3f,
            $"窄高带的核心应屈服破裂（有效 Te 塌）：实测 {coreTe / 1000f:F1} km vs 热年龄 Te {thermalTe / 1000f:F0} km");
        Assert.AreEqual(farThermalTe, farTe, farThermalTe * 0.01f,
            $"载荷 6 跳外应仍是**完整弹性板**（无屈服）：实测 {farTe / 1000f:F1} km vs 热年龄 Te {farThermalTe / 1000f:F1} km");

        // 山保住：破板处几乎完全补偿 ⇒ 峰只被削掉一小部分（与②的亚格尖峰形成对照）
        float regional = AiryAt(fields, farCell);
        float loadHeight = AiryAt(fields, 0) - regional;
        Assert.Less(isostasy.FlexurePeakReductionM, loadHeight * 0.3f,
            $"窄山带应基本保住高度（屈服限让板在此处失去支撑能力）：削低 {isostasy.FlexurePeakReductionM:F0} m"
            + $" vs Airy 高差 {loadHeight:F0} m（无屈服限时会被压掉 83%）");
    }

    // ═══════════════════════════════════════════════════════════════
    // Te 场：洋侧随热年龄增厚、陆侧克拉通
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ElasticThickness_GrowsWithThermalAge_AndContinentIsCraton()
    {
        var fields = OceanPlate(ageMy: 0f);
        fields.Age[0] = 0f;
        fields.Age[1] = 25f;
        fields.Age[2] = 100f;
        fields.Age[3] = 300f;                                  // 超过 Te 封顶（2.7√age = 40 km 对应 age ≈ 219 My）
        var isostasy = new H3Isostasy(Ball.CellIds.Length);
        Assert.AreEqual(0f, isostasy.ElasticThicknessM(fields, 0), 1f, "脊轴 Te = 0（刚生成的岩石圈无强度）");
        Assert.AreEqual(2.7f * 5f * 1000f, isostasy.ElasticThicknessM(fields, 1), 10f, "Te(25 My) = 2.7·√25 km");
        Assert.AreEqual(2.7f * 10f * 1000f, isostasy.ElasticThicknessM(fields, 2), 10f, "Te(100 My) = 2.7·√100 km");
        Assert.AreEqual(H3Isostasy.TeMaxKm * 1000f, isostasy.ElasticThicknessM(fields, 3), 10f, "Te 封顶 40 km");

        var continental = OceanPlate(ageMy: 0f);
        continental.ClearCell(0);
        continental.FelsicPlutonic[0] = Material.FelsicPlutonic * 35000f;
        Assert.AreEqual(H3Isostasy.TeContinentalKm * 1000f, isostasy.ElasticThicknessM(continental, 0), 10f,
            "陆壳 = 克拉通 35 km（热稳态 ⇒ 与热年龄无关）");
    }

    // ═══════════════════════════════════════════════════════════════
    // 退化板（Age = 0 洋底 ⇒ Te = 0）：耦合随距离衰减，且求解器必须收敛
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Flexure_ZeroRigidityPlate_SolverConverges_AndCouplingDecaysWithDistance()
    {
        // 本测试的**主要目的**是求解器回归：算子的对称性曾被打破过——图 Laplacian 原写成按局部度归一
        // `c(sum/deg − w)`，于是 12 个二十面体顶点（deg=5 邻 deg=6）处 `L ≠ Lᵀ` ⇒ `A = I + L·D·L`
        // 不再对称 ⇒ CG 失去收敛保证（实测残差卡在 7.5 km 不降、解只走出一步）。
        // 改成固定参考度的对称形 `(c/6)(Σw − deg·w)` 之后收敛（残差 < 0.01）。本测试把这条钉住。
        // 物理读数：Age = 0 的洋底热年龄为 0 ⇒ 热年龄 Te = 0（无强度）——但 ⚠️ α 平滑会把邻域的刚度
        // 摊过来（尖峰格陆壳 35 km ⇒ 邻格拿到 ~1/7 的 α），故**不再**是"逐格恒等于 Airy"；
        // 能钉的是"耦合随距离快速衰减"（离散 4 阶算子在 ~2 格内衰减）。
        var fields = OceanPlate(ageMy: 0f);
        StampPlateau(Ball, fields, 0, hops: 0, felsicThicknessM: 60000f, taper: 0);
        var isostasy = Solve(Ball, fields);
        var dist = HopsFrom(Ball, 0);

        Assert.Less(isostasy.FlexureResidualM, 1f,
            $"退化板（热年龄 Te 几乎处处为 0）也必须收敛：残差 {isostasy.FlexureResidualM:F3} m");

        var devByHop = new Dictionary<int, float>();
        var countByHop = new Dictionary<int, int>();
        foreach (var kv in dist)
        {
            if (kv.Value < 1 || kv.Value > 6) continue;
            devByHop[kv.Value] = devByHop.GetValueOrDefault(kv.Value)
                + MathF.Abs(isostasy.Displacement[kv.Key] - AiryAt(fields, kv.Key));
            countByHop[kv.Value] = countByHop.GetValueOrDefault(kv.Value) + 1;
        }
        float Hop(int d) => countByHop.GetValueOrDefault(d) > 0 ? devByHop[d] / countByHop[d] : 0f;
        Assert.Less(Hop(6), 20f,
            $"热年龄 Te = 0 的板几乎没有刚度 ⇒ 6 跳外应基本回到 Airy：实测平均偏离 {Hop(6):F1} m"
            + $"（剖面 {string.Join(" ", System.Linq.Enumerable.Range(1, 6).Select(d => $"r{d}={Hop(d):F1}"))}）");
        Assert.Less(Hop(6), Hop(1),
            $"耦合应随距离衰减：r1={Hop(1):F1} m → r6={Hop(6):F1} m");
    }

    // ═══════════════════════════════════════════════════════════════
    // 分辨率纪律：res3（格宽 111 km）只把前隆挤进载荷外的**一格**
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void Flexure_AtRes3_ForebulgeSqueezedIntoOneRing()
    {
        // 06 §6 的分辨率纪律：res3 格宽 111 km > α（50–60 km）⇒ 前隆/前陆盆地这类**跨格**结构表达不出来，
        // 能表达的只有"载荷边缘被压下去 + 紧邻载荷外的一格被抬起来"（前隆挤成 1 格）。
        // 本测试把这条纪律钉住（防有人在 res3 上调挠曲旋钮看不到盆地而误判"挠曲没生效"）。
        // ⚠️ 这条断言原来是反的（写成"res3 下载荷外的环必须非正"）——那是把"挤成一格"误判成了"不存在"。
        var ball = new Ball(3, 1f);
        var fields = new H3PlateFields(ball.CellIds.Length);
        for (int i = 0; i < fields.Count; i++)
        {
            fields.PlateId[i] = 0;
            fields.ClearCell(i);
            fields.MaficVolcanic[i] = Material.MaficVolcanicMin * H3Isostasy.OceanReferenceThicknessM;
            fields.Age[i] = OceanAgeMy;
        }
        StampPlateau(ball, fields, 0, hops: 3, felsicThicknessM: 60000f, taper: 2);
        var isostasy = new H3Isostasy(fields.Count);
        isostasy.ComputeDisplacement(ball, fields, Material);
        var dev = RingDeviation(ball, fields, isostasy, 0, maxRing: 8, out var count);
        string profile = Profile(dev, count);

        // ⚠️ 阈值按 ρ_c 2600 时代标定（−50）；v1.19 物理化 2700 后板屈服减少、挠曲环带内移一格，
        // 实测 r4 = −46 —— 纪律本身（"边缘被压低 + 前隆挤进一格"）不变，阈值放宽到 −40。
        Assert.Less(dev[4], -40.0, $"载荷边缘格应被压低：实测 r4={dev[4]:F0} m，剖面 {profile}");
        Assert.Greater(dev[5], 100.0, $"载荷外紧邻的一格应是前隆（挤进一格）：实测 r5={dev[5]:F0} m，剖面 {profile}");
        int positiveRings = 0;
        for (int d = 5; d <= 8; d++) if (count[d] > 0 && dev[d] > 50.0) positiveRings++;
        Assert.AreEqual(1, positiveRings,
            $"res3 上前隆只应占**一格**（α < 格宽 ⇒ 环宽不可分辨）：实测 {positiveRings} 格为正，剖面 {profile}");
        Assert.Less(isostasy.FlexureResidualM, 10f,
            $"CG 应已收敛（残差 {isostasy.FlexureResidualM:F2} m）");
    }
}
