using Godot;
using System.Collections.Generic;

namespace World.MapGen;

/// <summary>
/// 河流系统（球面水文，经典算法适配）。
///
/// 算法：
///   1. 流向：每个陆地顶点 → 海拔最低的邻居（最陡下降方向）
///   2. 汇水面积：按海拔降序遍历，每顶点把 (1 + 自身面积) 累积给流向目标
///      → 每顶点得到"上游有多少格的水汇入"
///   3. 河流格：汇水面积 > 阈值（n=64 取 ~12）→ 标记为河，级别 = log2(面积) 分级
///   4. 河流路径：从河源（面积刚超阈值的最高点）沿流向追踪到入海/盆地
///   5. 盆地（所有邻居海拔更高）= 湖泊候选（返回 lakeIds，后续湖泊模块用）
///
/// ⚠️ 纯计算（无 Godot 对象依赖），生成时后台线程安全。
/// </summary>
public static class RiverSystem
{
    /// <summary>
    /// 计算河流。
    /// </summary>
    /// <param name="verts">球面顶点（单位方向）</param>
    /// <param name="neighbors">邻接表（每顶点邻居 id）</param>
    /// <param name="elevNorm">归一化海拔（&lt;0 = 海洋；0 = 海平面）</param>
    /// <param name="flow">输出：每顶点流向目标 id（陆地：最低邻居；无更低=自身=-1 盆地；海洋=自身）</param>
    /// <param name="area">输出：每顶点汇水面积（格数）</param>
    /// <param name="riverLevel">输出：0=无河，1=小河，2=中河，3=大河</param>
    /// <param name="riverPaths">输出：河流路径列表（每条 = 顶点 id 序列，源头→入海/盆地）</param>
    /// <param name="lakeIds">输出：盆地顶点 id（湖泊候选）</param>
    /// <param name="areaThreshold">河流阈值（格数，n=64 默认 12）</param>
    /// <param name="precip">可选：每格年降水 mm。提供时用水量版（累积降水-蒸发）</param>
    /// <param name="temp">可选：每格年均温 °C（计算蒸发，需与 precip 同时提供）</param>
    /// <param name="waterThreshold">水量版阈值（mm 净水量，默认 400）</param>
    public static void Compute(
        Vector3[] verts, int[][] neighbors, float[] elevNorm,
        out int[] flow, out float[] area, out byte[] riverLevel,
        out List<int[]> riverPaths, out List<int> lakeIds, out byte[] lakeLevel,
        float areaThreshold = 12f,
        float[] precip = null, float[] temp = null, float waterThreshold = 400f,
        float lakeThreshold = 200f)
    {
        int n = verts.Length;
        flow = new int[n];
        area = new float[n];
        riverLevel = new byte[n];
        riverPaths = new List<int[]>();
        lakeIds = new List<int>();
        lakeLevel = new byte[n];
        bool useWater = (precip != null && temp != null);   // 水量版（气候驱动）

        // ── 1. 流向：最低邻居（陆地顶点）；海洋顶点流向自身（终点）──
        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] < 0f) { flow[i] = i; continue; }   // 海洋 = 终点
            var nbs = neighbors[i];
            int best = -1;
            float bestE = elevNorm[i];
            foreach (var nb in nbs)
            {
                float e = elevNorm[nb];
                // 严格更低（避免平坦死循环）；海洋优先（只要比当前低就选最低）
                if (e < bestE)
                {
                    bestE = e;
                    best = nb;
                }
            }
            if (best >= 0) flow[i] = best;
            else flow[i] = i;   // 盆地（所有邻居更高）
        }

        // ── 2. 累积量（水量版：降水-蒸发；面积版：格数）按海拔降序 ──
        //    水量版：每格净水量 = 降水 - 蒸发；蒸发 ∝ 温度（0°C 下 20mm，30°C 下 600mm）
        //    干旱区蒸发 > 降水 → 净水量为负 → 累积水量递减 → 河流断流（内流/干河床）
        var water = new float[n];
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        System.Array.Sort(order, (a, b) => elevNorm[b].CompareTo(elevNorm[a]));
        for (int i = 0; i < n; i++)
        {
            int v = order[i];
            if (useWater)
            {
                float net = precip[v] - (20f + 20f * Mathf.Max(0f, temp[v]));   // 蒸发：20mm 底 + 20mm/°C
                water[v] += net;
            }
            else
            {
                water[v] += 1f;
            }
            if (flow[v] != v)
                water[flow[v]] += water[v];
        }
        // area 输出 = 水量（水量版）或面积（面积版），供调用方使用
        System.Array.Copy(water, area, n);

        // ── 3. 河流标记 + 湖泊判定 ──
        // ⚠️ 2026-08-02：湖泊 = 陆地盆地（flow==i 无出流）且汇入水量 ≥ 湖泊阈值
        //   （lakeThreshold 默认 200mm——湖泊阈值远低于河流阈值 5000：盆地是局部洼地
        //   汇水区小，水量达不到河流标准；只要净降水持续汇入（水量>0）就成湖。
        //   干涸盆地（水量≤阈值）= 盐湖/干湖不显示——用户确认：干湖不显示、单色、放河流图层）。
        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] < 0f) continue;       // 海洋无河无湖
            if (flow[i] == i && elevNorm[i] > 0f)
            {
                lakeIds.Add(i);                    // 陆地盆地 = 湖泊候选
                float lt = useWater ? lakeThreshold : areaThreshold;
                if (water[i] >= lt)
                    lakeLevel[i] = 1;              // 有水才成湖（干涸盆地排除）
            }
            float threshold = useWater ? waterThreshold : areaThreshold;
            if (water[i] >= threshold)
            {
                // 级别：水量/面积的超阈值倍数
                float ratio = water[i] / threshold;
                if (ratio >= 64f) riverLevel[i] = 3;
                else if (ratio >= 16f) riverLevel[i] = 2;
                else riverLevel[i] = 1;
            }
        }

        // ── 4. 河流路径：从源头正向追踪到入海/盆地 ──
        //    源头 = 无上游河格（incoming 为空，分水岭顶部）；流向唯一 → 每源头一条主河道。
        //    支流格仍在 riverLevel 标记里（网络完整），路径只画主河道。
        var incoming = new List<int>[n];
        for (int i = 0; i < n; i++) incoming[i] = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (riverLevel[i] > 0 && flow[i] != i)
                incoming[flow[i]].Add(i);
        }
        for (int i = 0; i < n; i++)
        {
            if (riverLevel[i] == 0) continue;
            if (incoming[i].Count > 0) continue;   // 有上游河格 → 不是源头
            // 正向追踪：沿 flow 到入海/盆地
            var path = new List<int> { i };
            int cur = i;
            int guard = 0;
            while (guard++ < n)
            {
                int nxt = flow[cur];
                if (nxt == cur) break;                    // 盆地/海洋终点
                if (riverLevel[nxt] == 0) break;          // 断流：遇非河格 → 河消失
                if (elevNorm[nxt] >= elevNorm[cur]) break; // 非单调（盆地溢流/异常）：防无限链
                path.Add(nxt);
                if (elevNorm[nxt] < 0f) break;            // 入海
                cur = nxt;
            }
            if (path.Count >= 3)
                riverPaths.Add(path.ToArray());
        }
    }

    /// <summary>
    /// 从存档数据重建河流路径（MapViewer 用——存档只存 riverLevel + flow，路径重建）。
    /// 与 Compute 第 4 步逻辑一致：源头 = 无上游河格，沿 flow 正向追踪到入海/盆地。
    /// </summary>
    public static List<int[]> RebuildPaths(int[] flow, byte[] riverLevel, float[] elevNorm)
    {
        int n = flow.Length;
        var paths = new List<int[]>();
        var incoming = new List<int>[n];
        for (int i = 0; i < n; i++) incoming[i] = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (riverLevel[i] > 0 && flow[i] != i)
                incoming[flow[i]].Add(i);
        }
        for (int i = 0; i < n; i++)
        {
            if (riverLevel[i] == 0) continue;
            if (incoming[i].Count > 0) continue;
            var path = new List<int> { i };
            int cur = i;
            int guard = 0;
            while (guard++ < n)
            {
                int nxt = flow[cur];
                if (nxt == cur) break;
                if (riverLevel[nxt] == 0) break;   // 断流（同 Compute）
                path.Add(nxt);
                if (elevNorm[nxt] < 0f) break;
                cur = nxt;
            }
            if (path.Count >= 3)
                paths.Add(path.ToArray());
        }
        return paths;
    }

    /// <summary>
    /// 河流侵蚀沉积（2026-08-02）：生成后处理，原地修正海拔（米）。
    /// 河流对地形的反馈，不迭代（小幅修正不改变流向格局）：
    ///   - 侵蚀：河格下切（河谷/峡谷）——流量越大挖得越深，上限 erodeMax，
    ///     且不挖穿海平面（留 5m 余量，防地形反转）
    ///   - 沉积：入海口格（流向海洋）堆积（三角洲/冲积扇）——贴海平面低平
    /// 幅度标定：n=64 每格 ~110km，河谷 ≤150m、三角洲 ≤80m（观感真实、不破坏流向）。
    /// </summary>
    public static void ApplyErosionDeposition(
        float[] elevM,       // 海拔（米，原地修改）
        byte[] riverLevel,   // 河级别（0=无河）
        float[] riverVolume, // 每格水量 mm（流量）
        int[] flow,          // 流向
        float[] elevNorm,    // 归一化海拔（&lt;0 = 海洋）
        float seaLevelM,     // 海平面海拔（米）
        float erodeMax = 150f, float depoMax = 80f, float fullFlow = 10000f)
    {
        int n = elevM.Length;
        // 侵蚀：河格下切
        for (int i = 0; i < n; i++)
        {
            if (elevM[i] <= seaLevelM || riverLevel[i] == 0) continue;
            float erode = erodeMax * Mathf.Clamp(riverVolume[i] / fullFlow, 0f, 1f);
            elevM[i] = Mathf.Max(seaLevelM + 5f, elevM[i] - erode);
        }
        // 沉积：入海口（流向海洋）堆积三角洲
        for (int i = 0; i < n; i++)
        {
            if (elevM[i] <= seaLevelM) continue;
            if (flow[i] != i && elevNorm[flow[i]] < 0f)
            {
                float depo = depoMax * Mathf.Clamp(riverVolume[i] / fullFlow, 0f, 1f);
                elevM[i] += depo;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // 迭代演化模块（2026-08-02 v2）：动态流向 + 输沙模型
    //   地貌演化的正确模型：侵蚀沉积改变海拔 → 流向实时更新（最低邻居）
    //   → 河流自然改道（黄河模式）——不需要显式触发事件。
    //   ⚠️ 2026-08-02：盆地溢流（SpillBasins）已移除——溢流把水量传给"最低邻居"
    //   （盆地邻居都 ≥ 自身 → 上坡链）→ 水量分布破坏 + 路径无限链（n=32 河格 83→7）。
    //   盆地 = 蓄水终点（湖），溢流增强（内流湖→外流）留作后续。
    // ═══════════════════════════════════════════════════════════════

    /// <summary>流向：陆地格 → 海拔最低邻居（无更低 = 盆地，flow=自身）；海洋 = 自身。</summary>
    public static void ComputeFlow(Vector3[] verts, int[][] neighbors, float[] elevNorm, int[] flow)
    {
        int n = verts.Length;
        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] < 0f) { flow[i] = i; continue; }   // 海洋 = 终点
            var nbs = neighbors[i];
            int best = i;
            float bestE = elevNorm[i];
            foreach (var nb in nbs)
            {
                float e = elevNorm[nb];
                if (e < bestE) { bestE = e; best = nb; }
            }
            flow[i] = best;   // 无更低邻居 → best=i（盆地）
        }
    }

    /// <summary>水量累积：海拔降序遍历，净水量（降水-蒸发）沿流向累积。</summary>
    public static void ComputeWater(
        Vector3[] verts, float[] elevNorm, int[] flow,
        float[] precip, float[] temp, float[] water)
    {
        int n = verts.Length;
        System.Array.Clear(water, 0, n);
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        System.Array.Sort(order, (a, b) => elevNorm[b].CompareTo(elevNorm[a]));
        for (int i = 0; i < n; i++)
        {
            int v = order[i];
            float net = precip != null && temp != null
                ? precip[v] - (20f + 20f * Mathf.Max(0f, temp[v]))
                : 1f;
            water[v] += net;
            if (flow[v] != v)
                water[flow[v]] += water[v];
        }
    }

    /// <summary>河流/湖泊标记：水量 ≥ 河流阈值 → 河（级别=超阈值倍数）；陆地盆地+水量≥湖阈值 → 湖。</summary>
    public static void MarkRiversLakes(
        float[] water, int[] flow, float[] elevNorm,
        float waterThreshold, float lakeThreshold,
        byte[] riverLevel, byte[] lakeLevel, List<int> lakeIds)
    {
        int n = water.Length;
        System.Array.Clear(riverLevel, 0, n);
        System.Array.Clear(lakeLevel, 0, n);
        lakeIds?.Clear();
        for (int i = 0; i < n; i++)
        {
            if (elevNorm[i] < 0f) continue;
            if (flow[i] == i)
            {
                lakeIds?.Add(i);
                if (water[i] >= lakeThreshold) lakeLevel[i] = 1;
            }
            float ratio = water[i] / waterThreshold;
            if (ratio >= 64f) riverLevel[i] = 3;
            else if (ratio >= 16f) riverLevel[i] = 2;
            else if (ratio >= 1f) riverLevel[i] = 1;
        }
    }

    /// <summary>
    /// 输沙侵蚀沉积 v2：携带能力 C = k × 坡度 × 流量，海拔降序单向传递泥沙。
    ///   C &gt; 来沙 → 侵蚀（挖河床，产沙传给下游）；C &lt; 来沙 → 沉积（落沙）。
    ///   侵蚀下限 = 下游海拔 + 最小坡降（河道单调下降，保流向）；
    ///   沉积上限 = 海平面 + depoCap（冲积平原不无限堆高，模拟改道转走）。
    /// ⚠️ seaLevelM 语义：elevM 已相对海平面（0=海平面）时传 0。
    /// 自限性：侵蚀降低坡度 → C 下降 → 侵蚀减弱 → 自然收敛。
    /// </summary>
    public static void ApplyErosionDepositionV2(
        float[] elevM, int[] flow, int[][] neighbors, float[] elevNorm,
        float[] water, float[] sedimentIn, float[] sedimentOut,
        float seaLevelM, float kCarry = 0.05f, float kErode = 0.4f, float kDepo = 0.3f,
        float minSlope = 0.001f, float maxErode = 40f, float depoCap = 300f)
    {
        int n = elevM.Length;
        System.Array.Clear(sedimentOut, 0, n);
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        System.Array.Sort(order, (a, b) => elevNorm[b].CompareTo(elevNorm[a]));

        for (int idx = 0; idx < n; idx++)
        {
            int i = order[idx];
            if (elevNorm[i] < 0f) continue;          // 海洋不侵蚀不沉积
            if (flow[i] == i) continue;              // 盆地：蓄水终点，不输沙
            int down = flow[i];
            if (down < 0 || down >= n || down == i) continue;

            float slope = Mathf.Max(0f, elevNorm[i] - elevNorm[down]);
            float carry = kCarry * slope * Mathf.Max(water[i], 0f);
            float sIn = sedimentIn[i];

            if (sIn > carry)
            {
                // 来沙超能力 → 沉积（冲积平原；上限 = 海平面 + depoCap）
                float dep = Mathf.Min((sIn - carry) * kDepo, Mathf.Max(0f, seaLevelM + depoCap - elevM[i]));
                elevM[i] += dep;
                sedimentOut[i] = carry;
            }
            else
            {
                // 能力过剩 → 侵蚀（下切河谷；下限 = 下游+minSlope 或海平面，保流向单调）
                float erode = Mathf.Min((carry - sIn) * kErode, maxErode);
                float downElev = elevM[down];
                float minElev = Mathf.Max(seaLevelM, downElev + minSlope * (elevM[i] - seaLevelM));
                elevM[i] = Mathf.Max(minElev, elevM[i] - erode);
                sedimentOut[i] = carry;
            }
            sedimentIn[down] += sedimentOut[i];
        }
    }

    /// <summary>
    /// 迭代演化主入口：rounds 轮上限（收敛检测自适应提前停，不依赖网格 n）。
    /// 每轮：流向 → 水量 → 河流/湖标记 → 输沙侵蚀沉积（改 elevM+elevNorm）。
    /// 输出最终 flow/water/riverLevel/lakeLevel + paths（含断流/非单调截断）。
    /// ⚠️ 2026-08-02：seaLevelM 传 0（elevM 已相对海平面）——传 sea 会导致 eNorm
    ///   整体平移 → 海陆判定错位 → 海洋被侵蚀（最低点 -1200m）→ 水系崩溃。
    /// </summary>
    public static void ComputeIterative(
        Vector3[] verts, int[][] neighbors, float[] elevNorm, float[] elevM,
        float[] precip, float[] temp,
        float waterThreshold, float lakeThreshold,
        float seaLevelM, float elevSpan, int rounds,
        out int[] flow, out float[] water, out byte[] riverLevel,
        out byte[] lakeLevel, out List<int[]> paths)
    {
        int n = verts.Length;
        flow = new int[n];
        water = new float[n];
        riverLevel = new byte[n];
        lakeLevel = new byte[n];
        var lakeIds = new List<int>();
        var sedIn = new float[n];
        var sedOut = new float[n];
        var prevFlow = new int[n];

        for (int r = 0; r < rounds; r++)
        {
            ComputeFlow(verts, neighbors, elevNorm, flow);
            ComputeWater(verts, elevNorm, flow, precip, temp, water);
            MarkRiversLakes(water, flow, elevNorm, waterThreshold, lakeThreshold, riverLevel, lakeLevel, lakeIds);

            // 收敛检测：流向变化 <0.5% 即停（自适应，不依赖手动轮数；n=32 粗网格防振荡过度）
            if (r > 0)
            {
                int changed = 0;
                for (int i = 0; i < n; i++)
                    if (flow[i] != prevFlow[i]) changed++;
                if (changed <= n / 200)
                    break;
            }
            System.Array.Copy(flow, prevFlow, n);

            if (r < rounds - 1)
            {
                System.Array.Clear(sedIn, 0, n);
                ApplyErosionDepositionV2(elevM, flow, neighbors, elevNorm, water, sedIn, sedOut, seaLevelM);
                for (int i = 0; i < n; i++)
                    elevNorm[i] = elevSpan > 1e-6f ? (elevM[i] - seaLevelM) / elevSpan : 0f;
            }
        }
        paths = RebuildPaths(flow, riverLevel, elevNorm);
    }
}
