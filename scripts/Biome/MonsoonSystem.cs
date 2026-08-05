using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

namespace World.Biome;

/// <summary>
/// 季风环流诊断场（2026-08-16，用户拍板方案 B'：与洋流流函数法同方法论——诊断法，无时间步进）。
///
/// 物理：海陆热力差异 → 季节性风系 → 水汽输送 → 月降水分布。
///   1. BFS 距离场 distCoast：每格到最近海岸的跳数（海陆分布是板块构造产物，此处直接消费）。
///   2. 12 个月循环（太阳赤纬 δₘ = 倾角×sin(2π(m−3)/12)）：
///      · 月辐射 Sₘ = cos(lat − δₘ)（极夜截断 0）
///      · 月温度 Tₘ = 年均温 + 季节半幅×sin(2π(m−3)/12)×(1 + 大陆性)——大陆性 ∝ 距海距离，
///        海洋蓄热滞后季节温差小、内陆季节温差大（冬冷夏热）。
///      · 热力异常 P_anom = −(Tₘ − 同纬度海洋参考温) → 热低压/冷高压
///      · 月风场 vₘ = +∇P_anom（气压梯度风：冷高压 → 热低压，从海吹向陆 = 夏季风）
///      · 水汽追踪：陆地格沿 −vₘ（风上游方向）走 ≤6 步，遇海洋 → 获得海洋水汽（季风雨）
///      · 月降水权重 = ITCZ 摆动基线 + 季风增强（水汽 × 辐射）
///   3. 输出：季风强度场（新图层）、最热/最冷月温、最干月降水 + 月份（柯本分类用真实月数据，
///      不再用干季系数近似）、12 个月降水分布（存档）。
///
/// ⚠️ 纯计算（Vector3 是 struct，不碰引擎 API），生成时后台线程安全。
/// </summary>
public static class MonsoonSystem
{
    public const int MonthCount = 12;

    /// <summary>
    /// 计算季风环流诊断场。
    /// </summary>
    /// <param name="verts">球面顶点（单位方向）</param>
    /// <param name="neighbors">邻接表</param>
    /// <param name="elevNorm">归一化海拔（&lt;0.02 = 海洋）</param>
    /// <param name="elevM">海拔（米，含海洋负值；热压场用海平面等效温度需海拔补偿）</param>
    /// <param name="tempC">年均温（°C，现有气候模型产物）</param>
    /// <param name="precipAnn">年降水（mm，现有气候模型产物）</param>
    /// <param name="axialTilt">轴向倾角（°）</param>
    /// <param name="rotationSpeed">自转速度（科里奥利强度；1.0=地球）</param>
    /// <param name="monsoon">输出：季风强度 0~1（新图层用）</param>
    /// <param name="tHotMonth">输出：最热月均温（柯本 A/E/D/C 分界）</param>
    /// <param name="tColdMonth">输出：最冷月均温</param>
    /// <param name="dryMonthPrecip">输出：最干月降水（mm）</param>
    /// <param name="dryMonthIndex">输出：最干月所在月份 0-11（判定干季季节 → B 带阈值修正）</param>
    /// <param name="monthPrecip">输出：[12][n] 各月降水（mm，Σ = 年降水）</param>
    /// <param name="monthWind">输出：[12][n] 各月统一风场（热成风：+∇Tₘ + 科里奥利；切向量，长度=强度 0~1）</param>
    /// <param name="monthTemp">输出：[12][n] 各月均温（°C；温度系统月度化的正式产物）</param>
    public static void Compute(
        Vector3[] verts, int[][] neighbors, float[] elevNorm, float[] elevM,
        float[] tempC, float[] precipAnn, float axialTilt, float rotationSpeed,
        out float[] monsoon, out float[] tHotMonth, out float[] tColdMonth,
        out float[] dryMonthPrecip, out int[] dryMonthIndex, out float[][] monthPrecip,
        out Vector3[][] monthWind, out float[][] monthTemp)
    {
        int n = verts.Length;
        monsoon = new float[n];
        tHotMonth = new float[n];
        tColdMonth = new float[n];
        dryMonthPrecip = new float[n];
        dryMonthIndex = new int[n];
        monthPrecip = new float[MonthCount][];
        monthWind = new Vector3[MonthCount][];
        monthTemp = new float[MonthCount][];
        for (int m = 0; m < MonthCount; m++)
        {
            monthPrecip[m] = new float[n];
            monthWind[m] = new Vector3[n];
            monthTemp[m] = new float[n];
        }

        // ── 1. 海陆标记 + BFS 距离场（多源：所有海洋格 dist=0）→ 大陆性指数 ──
        var distCoast = new int[n];
        var q = new Queue<int>();
        bool anyOcean = false;
        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] < 0.02f) { distCoast[i] = 0; anyOcean = true; q.Enqueue(i); }
            else distCoast[i] = int.MaxValue;
        }
        while (q.Count > 0)
        {
            int i = q.Dequeue();
            foreach (var nb in neighbors[i])
                if (distCoast[nb] == int.MaxValue)
                {
                    distCoast[nb] = distCoast[i] + 1;
                    q.Enqueue(nb);
                }
        }
        // 大陆性因子（0 海洋 ~ 1 深内陆；季节温差放大）
        // ⚠️ 2026-08-16 v6：回退"到海岸距离"（用户拍板要真实物理——碎大陆就是弱季风，
        //   不能为视觉效果改成"区域陆地比例"）。顶点间距 ≈ 20000km/320（n=32）≈ 125km；
        //   Dc=8 步 ≈ 1000km 达内陆饱和。
        const int Dc = 8;
        var continent = new float[n];
        for (int i = 0; i < n; i++)
        {
            if (!anyOcean) { continent[i] = 1f; continue; }
            int d = distCoast[i];
            continent[i] = d >= int.MaxValue - 1 ? 1f : Mathf.Min(d, Dc) / (float)Dc;
        }

        // ── 2. 12 个月循环 ──
        // 月温度：Tₘ = tempC + seasonal_eff × sin(2π(m−3)/12)
        // seasonal_eff = (tilt/23.4)×25×sin|lat|^1.5 × (1 + 0.6×大陆性)
        // 月辐射：Sₘ = cos(lat − δₘ)，极夜截断 0
        // ⚠️ 2026-08-16：海拔梯度场 ∇h（静态预计算一次，地形雨影 w = V·∇h 用）
        var gradElev = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 grad = Vector3.Zero;
            foreach (var nb in neighbors[i])
            {
                Vector3 e = verts[nb] - verts[i];
                float len = e.Length();
                if (len < 1e-9f) continue;
                grad += (elevNorm[nb] - elevNorm[i]) * (e / len);
            }
            gradElev[i] = grad;
        }
        var monthT = new float[MonthCount][];
        var monthRad = new float[MonthCount][];
        for (int m = 0; m < MonthCount; m++)
        {
            monthT[m] = new float[n];
            monthRad[m] = new float[n];
            float deltaDeg = axialTilt * Mathf.Sin(2f * Mathf.Pi * (m - 3f) / 12f);
            float delta = deltaDeg * Mathf.Pi / 180f;
            for (int i = 0; i < n; i++)
            {
                float lat = Mathf.Asin(Mathf.Clamp(verts[i].Y, -1f, 1f));
                // ⚠️ 2026-08-16 Plan C（用户拍板）：把海洋也囊括进温度计算 + 海陆温差 ∝ 月辐射×大陆性
                //   Tₘ = tempC + seasonal_base × sin(相位) × (1 + Kc×大陆性×辐射)
                //   海洋格（c=0）：正常海洋季节（海洋热容大 → 平缓）——海洋是温度计算的一等公民
                //   大陆格：辐射强（夏季 rad≈0.9）→ 大陆热力响应大（热 8-13°C）；辐射弱（冬季）→ 接近海洋
                //   Kc=2.5 标定：lat30 内陆（c=1）夏季热力 +20°C > 海拔递减 6.6°C + 反照率差
                //   → 像素图（实际温度）上夏季大陆 > 同纬度海洋（用户验收标准）
                float seasonalBase = (axialTilt / 23.4f) * 25f
                    * Mathf.Pow(Mathf.Abs(Mathf.Sin(lat)), 1.5f);   // 海洋季节半幅（热容大 → 平缓）
                float rad = Mathf.Cos(lat - delta);   // 正午太阳高度
                rad = Mathf.Max(rad, 0f);
                monthRad[m][i] = rad;
                float phase = Mathf.Sin(2f * Mathf.Pi * (m - 3f) / 12f);
                const float Kc = 3.5f;   // 大陆热力响应系数（辐射×大陆性 → 海陆温差）
                float continentResp = Kc * continent[i] * rad;
                float t = tempC[i] + seasonalBase * phase * (1f + continentResp);
                // 反照率（Plan C 补充因素）：冰/雪反射强 → 额外降温；海洋低反射（吸收多）
                //   反射损失 = 反照率 × 月辐射 × 标定 10°C（全反射 −10°C；真实沙漠 0.3 vs
                //   海洋 0.06 → 表面温差 ~2.4°C）
                float albedo = elevNorm[i] < 0.02f ? 0.06f : 0.15f;   // 海洋 6%、陆地默认 15%
                float latAbsDeg = Mathf.Abs(Mathf.RadToDeg(lat));
                if (latAbsDeg > 60f) albedo = 0.65f;                  // 极地冰盖
                else if (elevNorm[i] > 0.5f) albedo = 0.45f;          // 高海拔雪
                t -= albedo * rad * 10f;
                monthT[m][i] = t;
                monthTemp[m][i] = t;   // 月温度正式输出（温度系统月度化）
            }
        }

        // ── 3. 每月：统一风场（热成风）→ 水汽追踪 → 月降水权重 ──
        // ⚠️ 2026-08-16 v3（用户拍板"不区分盛行风/季风"）：一个温度场 → 一个风场。
        //   风 = +∇Tₘ（指向热处=低压）旋转科里奥利角（北半球右偏，∝ sin(lat)×自转速度）。
        //   温度梯度同时含纬度梯度（→信风/西风带）与海陆差异（→季风），自然涌现全部风系。
        var monthMoist = new float[MonthCount][];
        var monthW = new float[MonthCount][];
        for (int m = 0; m < MonthCount; m++)
        {
            monthMoist[m] = new float[n];
            monthW[m] = new float[n];
            monthWind[m] = new Vector3[n];

            // ⚠️ 2026-08-16 v5：热压场（用户拍板架构——温度是局部量，气压是全局量）。
            //   大陆热低压通过气压场的连续性传播到整个海洋（流体静力平衡的远程效应），
            //   风 = 每格与邻居的压差。实现：
            //   1. 每格气压异常 p(i) = −β×(Tₘ(i) − 同纬度气候基准)：热 → 低压（hPa）
            //   2. 气压场平滑传播（热低压水平扩散，~1000km 环流尺度）
            //   3. 风 = −∇p（邻居压差，高压→低压）+ 科里奥利偏转
            //   纬度气候气压（信风/西风）由原始温度场梯度保留（不平滑——行星风系是局地尺度）。
            // 1. 热压异常场（β = 1 hPa/°C：大陆夏季热 5-10°C → 低压异常 5-10 hPa，量级合理）
            // ⚠️ 2026-08-16 v5：海平面等效温度——大陆平均海拔高，实际温度比海洋低（海拔递减
            //   6.0°C/km 用户标定），全年"恒冷" → 恒高压 → 无季风反转。气象学标准：海平面气压
            //   比较用海平面等效温度——青藏高原海平面气压夏季低压冬季高压。海拔是地形效应不是海陆热力。
            const int LatBuckets = 36;
            const float Beta = 1f;   // hPa/°C
            var latSum = new double[LatBuckets];
            var latCnt = new int[LatBuckets];
            for (int i = 0; i < n; i++)
            {
                float lat = Mathf.Asin(Mathf.Clamp(verts[i].Y, -1f, 1f));
                int b = Mathf.Clamp((int)((lat * 180f / Mathf.Pi + 90f) / 5f), 0, LatBuckets - 1);
                float tSl = monthT[m][i]
                    + (elevNorm[i] >= 0.02f && elevM != null && elevM[i] > 0f ? 0.006f * elevM[i] : 0f);
                latSum[b] += tSl;
                latCnt[b]++;
            }
            var pAnom = new float[n];
            for (int i = 0; i < n; i++)
            {
                float lat = Mathf.Asin(Mathf.Clamp(verts[i].Y, -1f, 1f));
                int b = Mathf.Clamp((int)((lat * 180f / Mathf.Pi + 90f) / 5f), 0, LatBuckets - 1);
                float latBase = latCnt[b] > 0 ? (float)(latSum[b] / latCnt[b]) : monthT[m][i];
                float tSl = monthT[m][i]
                    + (elevNorm[i] >= 0.02f && elevM != null && elevM[i] > 0f ? 0.006f * elevM[i] : 0f);
                pAnom[i] = -Beta * (tSl - latBase);   // 热 → 负异常（低压）
            }
            // 2. 气压场平滑传播（热低压水平扩散入海洋；仅异常部分——纬度气候气压不平滑）
            for (int s = 0; s < 8; s++)
            {
                var next = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float sum = pAnom[i];
                    int cnt = 1;
                    foreach (var nb in neighbors[i]) { sum += pAnom[nb]; cnt++; }
                    next[i] = sum / cnt;
                }
                pAnom = next;
            }

            // 3. 邻居压差（∇p）：纬度气候梯度（信风）+ 传播后的异常气压梯度（季风环流）
            var gradArr = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 grad = Vector3.Zero;
                foreach (var nb in neighbors[i])
                {
                    Vector3 e = verts[nb] - verts[i];
                    float len = e.Length();
                    if (len < 1e-9f) continue;
                    // 纬度气候项：T 梯度（+∇T ≈ 指向热低压）；异常项：−∇p_anom（高压→低压）
                    // ⚠️ 2026-08-16 v5：异常项权重 0.02→0.5——原被纬度项淹没 30 倍（风基本是
                    //   信风方向，向陆分量微弱 → 用户看不到季风气流）。真实海陆气压差与纬度
                    //   气压差单位距离梯度同级，两系数应可比。
                    grad += ((monthT[m][nb] - monthT[m][i]) * 0.012f
                        + (pAnom[i] - pAnom[nb]) * 0.5f) * (e / len);
                }
                gradArr[i] = grad;
            }
            for (int i = 0; i < n; i++)
            {
                Vector3 g = gradArr[i];
                float gMag = g.Length();
                if (gMag < 1e-9f) continue;
                Vector3 r = verts[i].Normalized();
                float lat = Mathf.Asin(Mathf.Clamp(verts[i].Y, -1f, 1f));
                // 科里奥利偏转（Rodrigues，绕径向旋转）：北半球右偏，南半球左偏
                //   θ = +60°×sin(lat)×自转速度：北半球正（右偏 → 东北信风 ✓），南半球负（左偏 → 东南信风 ✓）
                // ⚠️ 2026-08-16 v3 修：θ 符号——原 -60° 给左偏（西北风）；右手定则验证 +θ 才是右偏
                float theta = 60f * Mathf.Sin(lat) * rotationSpeed * Mathf.Pi / 180f;
                Vector3 v = g / gMag;
                v = v * Mathf.Cos(theta) + r.Cross(v) * Mathf.Sin(theta);
                float wMag = Mathf.Clamp(gMag * 60f, 0f, 1f);   // 强度标定（压差 → 风强）
                monthWind[m][i] = v.Normalized() * wMag;
            }

            // 水汽追踪：陆地格沿 −wind（风上游）走 ≤25 步，遇海洋 → 季风雨水汽
            // ⚠️ 2026-08-16 v2：6→25 步（×125km ≈ 3100km，够深内陆——印度季风深入 2000km+），
            //   衰减放缓；强度 = 风强度 × 距离衰减（大板块热低压强 → 水汽输送更强）。
            for (int i = 0; i < n; i++)
            {
                if (elevNorm[i] < 0.02f) continue;   // 只追踪陆地格
                float wMag = monthWind[m][i].Length();
                if (wMag < 1e-6f) continue;
                int steps = TraceUpstream(i, verts, neighbors, elevNorm, monthWind[m][i]);
                if (steps > 0)
                {
                    // ⚠️ 2026-08-16 v2：弱风也有季风存在性（0.35 底），强风（大板块热低压）加成；
                    //   纯乘法衰减会全压到阈值以下 → 季风区消失
                    monthMoist[m][i] = (0.35f + 0.65f * wMag) * Mathf.Exp(-steps * 0.08f);
                }
            }

            // 月降水权重 = ITCZ 摆动基线 + 季风增强（水汽 × 辐射）
            float deltaDeg = axialTilt * Mathf.Sin(2f * Mathf.Pi * (m - 3f) / 12f);
            for (int i = 0; i < n; i++)
            {
                if (elevNorm[i] < 0.02f) continue;   // 海洋格不分配降水（只陆地格）
                float latDeg = Mathf.Asin(Mathf.Clamp(verts[i].Y, -1f, 1f)) * 180f / Mathf.Pi;
                float itczLat = deltaDeg * 1.2f;
                float wItcz = Mathf.Exp(-(latDeg - itczLat) * (latDeg - itczLat) / (2f * 8f * 8f));
                // 副高带压制（ITCZ 远离时副高控制）
                float sub = Mathf.Abs(latDeg - 30f) < 8f ? 0.35f : 1f;
                float wBase = 0.25f + wItcz * 0.75f * sub;
                float wMonsoon = monthMoist[m][i] * monthRad[m][i];
                float w = wBase + 1.2f * wMonsoon;
                // ⚠️ 2026-08-16：风的影响——地形垂直速度公式 w = V·∇h（用户拍板全局场方案）：
                //   海拔梯度场 ∇h（每格坡度）预计算一次；爬坡分量 = 当月风向 · ∇h。
                //   正=风爬坡（迎风坡增雨）、负=风下坡（背风坡减雨）、垂直坡面=0。
                //   无步长参数（替代原"沿风向 ±0.12rad 取海拔差"的有限差分 hack）。
                Vector3 wind = monthWind[m][i];
                if (wind.LengthSquared() > 1e-9f)
                {
                    float windComp = wind.Normalized().Dot(gradElev[i]);
                    w *= 1f + Mathf.Clamp(windComp * 0.5f, -0.65f, 0.65f);
                }
                monthW[m][i] = w;
            }
        }

        // ── 4. 归一化月降水 + 输出极值/季风强度 ──
        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] < 0.02f) continue;   // 海洋格降水 0（保持）
            float sumW = 0f;
            for (int m = 0; m < MonthCount; m++) sumW += monthW[m][i];
            if (sumW <= 1e-9f) { for (int m = 0; m < MonthCount; m++) monthPrecip[m][i] = 0f; continue; }
            for (int m = 0; m < MonthCount; m++)
                monthPrecip[m][i] = precipAnn[i] * monthW[m][i] / sumW;

            // 极值
            float tHot = float.MinValue, tCold = float.MaxValue;
            float dMin = float.MaxValue; int dIdx = 0;
            for (int m = 0; m < MonthCount; m++)
            {
                float t = monthT[m][i];
                if (t > tHot) tHot = t;
                if (t < tCold) tCold = t;
                float p = monthPrecip[m][i];
                if (p < dMin) { dMin = p; dIdx = m; }
            }
            tHotMonth[i] = tHot;
            tColdMonth[i] = tCold;
            dryMonthPrecip[i] = dMin;
            dryMonthIndex[i] = dIdx;

            // 季风强度 = 冬夏季节反差（季风 = 季节反转的海陆风；无季节星球反差=0）
            //   max(水汽×辐射) − min(水汽×辐射)：夏季风强（海→陆）+ 冬季风弱（陆→海，水汽=0）
            float latAbs = Mathf.Abs(Mathf.Asin(Mathf.Clamp(verts[i].Y, -1f, 1f)) * 180f / Mathf.Pi);
            float latWin = latAbs < 40f ? 1f : Mathf.Max(0f, 1f - (latAbs - 40f) / 15f);
            float best = 0f, worst = float.MaxValue;
            for (int m = 0; m < MonthCount; m++)
            {
                float x = monthMoist[m][i] * monthRad[m][i];
                best = Mathf.Max(best, x);
                worst = Mathf.Min(worst, x);
            }
            monsoon[i] = Mathf.Clamp((best - worst) * latWin, 0f, 1f);
        }
    }

    /// <summary>沿风上游方向（−wind）追踪：返回遇海洋前的步数（1..25），无海洋返回 -1。
    /// 每步选"与上游方向点积最大"的邻居（球面最速下降）。</summary>
    private static int TraceUpstream(int start, Vector3[] verts, int[][] neighbors,
        float[] elevNorm, Vector3 windDir)
    {
        Vector3 up = -windDir.Normalized();
        int cur = start;
        for (int k = 0; k < 25; k++)
        {
            int best = -1;
            float bestDot = -2f;
            foreach (var nb in neighbors[cur])
            {
                float d = verts[nb].Dot(up);
                if (d > bestDot) { bestDot = d; best = nb; }
            }
            if (best < 0) return -1;
            cur = best;
            if (elevNorm[cur] < 0.02f) return k + 1;   // 到达海洋
        }
        return -1;   // 25 步内无海洋 → 非季风区
    }
}
