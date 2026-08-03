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
                path.Add(nxt);
                if (elevNorm[nxt] < 0f) break;
                cur = nxt;
            }
            if (path.Count >= 3)
                paths.Add(path.ToArray());
        }
        return paths;
    }
}
