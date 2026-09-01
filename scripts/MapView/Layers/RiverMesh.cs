using Godot;
using System;
using System.Collections.Generic;
using World.MapGen;
using World.Utils;
using static World.MapView.MapLayerColors;

namespace World.MapView.Layers;

/// <summary>河流覆盖层几何构建（2026-09-01 重做：蜿蜒/渐变宽/防穿模/水系一根线）。
/// CurrentFlow 模式：画法独立组件，RiverLayer 策略只引用（BuildOverlay = 一行调用）。
///
/// 原内联画法问题（用户反馈）：折线直、半宽固定、河带互相穿模、多条彩条拼接不成河。
/// 画法（渲染层重做——不改存档/数据层，flow/riverLevel/RiverVolume 语义不变）：
///   1. 水系 = 一棵树：按入海口/盆地（终点）分组，同一终点的干流+支流 = 一个水系，
///      整棵水系一根连续的线画出来（2026-09-01 用户定法）——同色、汇合点连续、粗细随流量渐变；
///   2. 点列 → 平滑曲线：河道取点列（路径顶点 = flow 沿当地高度最低的格子序列），
///      Catmull-Rom 球面细分（MathUtils.CatmullRomFill 思路）归一化回球面，
///      圆角化 zigzag 路径 → 蜿蜒跟随地形（用户定法：点列 + 平滑曲线连接）；
///   3. 地形微调：细分中间点按横向（垂直流向）左右探针高度差微调位置（NearestVertex）——
///      弯向低侧（TerrainBias 幅度），低通平滑防抖，端点（源头/入海/汇合）固定；
///   4. 渐变宽：半宽逐路径顶点 = RiverVolume 分位数映射（上细下粗、汇流变宽），
///      移动平均防一格流量跳变，骨架子点按段线性插值；
///   5. 防穿模：逐点切线差分 → 侧向连续（带子不扭折自叠）；支流末段宽度收束到汇合点干流宽度
///      （painted 字典记 顶点→干流半宽）锥形融合。
/// 纯构建期一次性计算（非逐帧）。
/// </summary>
public static class RiverMesh
{
    // ── 画法参数（集中可调，2026-09-01）──
    /// <summary>每段 Catmull-Rom 子点数（5 = 每格 6 个骨架点；越大越平滑、开销同比增大）。</summary>
    public const int Subdivisions = 5;
    /// <summary>地形探针距离（格距倍数，左右各一针查当地格子高度）。</summary>
    public const float ProbeDist = 1.0f;
    /// <summary>地形偏向强度（格距倍数——朝低侧最大偏移；路径已沿最低邻居走，
    /// 此量只微调网格折线偏离谷底的量，不宜大）。</summary>
    public const float TerrainBias = 0.3f;
    /// <summary>最细半宽（格距倍数；源头小溪——水系一根线的最细端）。</summary>
    public const float HalfWMin = 0.05f;
    /// <summary>最宽半宽（格距倍数；大河入海口；原固定 0.13 为中值档 → 2026-09-01 用户嫌粗调低上限）。</summary>
    public const float HalfWMax = 0.15f;
    /// <summary>宽度标定分位：河格流量的此分位 → HalfWMax（现场自适应，不随 n/阈值漂移）。</summary>
    public const float WidthQuantile = 0.95f;
    /// <summary>宽度移动平均窗口（路径顶点级，防一格流量跳变）。</summary>
    public const int WidthSmooth = 3;
    /// <summary>汇合收束长度（格距；支流末段宽度锥形收束到干流宽的渐变范围）。</summary>
    public const float MergeGrowCells = 1.0f;

    /// <summary>
    /// 构建河流覆盖层网格；无可见河道返回 null。
    /// </summary>
    /// <param name="map">存档数据（Verts/Elev/RiverVolume/NearestVertex 数据源）</param>
    /// <param name="paths">主河道路径（RiverSystem.RebuildPaths 产物：源头→入海/盆地）</param>
    /// <param name="radius">覆盖层球面半径（RadiusKm × OverlayLiftFactor，防 z-fighting）</param>
    /// <param name="gridArc">格距（弧度；宽度/蜿蜒等全部相对格距缩放，分辨率无关）</param>
    public static Node3D Build(
        MapData map,
        IReadOnlyList<int[]> paths,
        float radius,
        float gridArc)
    {
        var verts = map.Verts;
        var elev = map.Elev;
        var volume = map.RiverVolume;
        if (verts == null || elev == null || volume == null) return null;

        // 海拔跨度（探针高度差归一化用；Min/MaxElev 为存档头部统计）
        float elevSpan = Mathf.Max(-map.MinElev, map.MaxElev);
        if (elevSpan < 1e-6f) elevSpan = 1f;

        // ── 宽度标定：河格流量 95 分位 → HalfWMax（上游细 → 入海粗的参考锚点）──
        float volRef = Quantile(volume, paths, WidthQuantile);
        if (volRef < 1e-3f) volRef = 1f;

        // ── 水系分组：同一终点（入海口/盆地）的路径 = 一个水系（一棵树，一根线）──
        var byOutlet = new Dictionary<int, List<int[]>>();
        foreach (var p in paths)
        {
            int outlet = p[p.Length - 1];
            if (!byOutlet.TryGetValue(outlet, out var list))
            {
                list = new List<int[]>();
                byOutlet[outlet] = list;
            }
            list.Add(p);
        }
        var watersheds = new List<List<int[]>>(byOutlet.Values);
        // 大河水系先画（最长干流降序——长水系覆盖广，先画让短支流可以收束进来）
        watersheds.Sort((a, b) => LongestPath(b).CompareTo(LongestPath(a)));

        var vertList = new List<Vector3>();
        var colorList = new List<Color>();
        var indexList = new List<int>();
        // painted：顶点 id → 干流在该点的 (半宽, 横截面侧向)（支流汇合 join 的依据——
        // 支流末段横截面方向旋转对齐干流横截面，无缝并入不覆盖不鼓包）
        var painted = new Dictionary<int, (float W, Vector3 Side)>();

        int wsCount = 0;
        foreach (var ws in watersheds)
        {
            // 水系内绘制顺序：干流先画 → 支流后画 join（2026-09-01 用户定法：先画中心曲线，
            // 各点上下加点连线加宽——支流汇合处横截面平滑旋转到干流横截面，逐点共享无缝）。
            //   ⚠️ 干流必须先画：支流 join 需要干流在汇合点的横截面方向（painted 里登记）。
            ws.Sort((a, b) => b.Length.CompareTo(a.Length));
            // 整个水系一根线：同一颜色（不同水系 HSL 黄金角循环，相邻水系差异最大）
            float hue = GoldenHue(wsCount);
            var c = HslToRgb(hue, 0.9f, 0.55f);
            bool anyDrawn = false;
            foreach (var path in ws)
                anyDrawn |= DrawPath(path, c, map, painted,
                    vertList, colorList, indexList, volRef, elevSpan, radius, gridArc);
            if (anyDrawn) wsCount++;
        }

        if (vertList.Count == 0)
            return null;

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertList.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colorList.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indexList.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        return new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
    }

    /// <summary>
    /// 绘制一条路径（水系内干流或支流）：点列 → Catmull-Rom 平滑曲线 → 地形微调 →
    /// 汇合收束（支流末段锥形融入已画干流宽度）→ 带子四边形。
    /// </summary>
    /// <returns>是否有实际绘制的段（全被遮/退化 → false，不计入水系颜色计数）</returns>
    private static bool DrawPath(
        int[] path, Color c,
        MapData map,
        Dictionary<int, (float W, Vector3 Side)> painted,
        List<Vector3> vertList, List<Color> colorList, List<int> indexList,
        float volRef, float elevSpan, float radius, float gridArc)
    {
        var verts = map.Verts;
        var elev = map.Elev;
        var volume = map.RiverVolume;
        int k = path.Length;
        if (k < 2) return false;

        // ── 路径顶点 → 球面控制点 + 逐顶点半宽（移动平均平滑）──
        var ctrl = new Vector3[k];
        var halfWs = new float[k];
        for (int i = 0; i < k; i++)
        {
            ctrl[i] = verts[path[i]];
            float q = Mathf.Clamp(volume[path[i]] / volRef, 0f, 1f);
            halfWs[i] = (HalfWMin + (HalfWMax - HalfWMin) * q) * gridArc;
        }
        SmoothWidth(halfWs);

        // ── 汇合判定：路径上第一个已被同水系干流（更早路径）画过的顶点 = 汇合点 ──
        int firstPainted = -1;
        for (int i = 0; i < k; i++)
            if (painted.ContainsKey(path[i])) { firstPainted = i; break; }
        if (firstPainted == 0) return false;   // 源头即汇合 → 纯支流无独有段

        // ── 骨架细分（点列 → 平滑曲线：Catmull-Rom 球面插值）──
        int sub = Subdivisions;
        int segs = k - 1;
        int skelCount = 1 + segs * (sub + 1);
        var skeleton = new Vector3[skelCount];
        var widths = new float[skelCount];
        {
            int o = 1;
            skeleton[0] = ctrl[0].Normalized();
            widths[0] = halfWs[0];
            for (int i = 0; i < segs; i++)
            {
                Vector3 p0 = ctrl[Mathf.Max(0, i - 1)];
                Vector3 p1 = ctrl[i];
                Vector3 p2 = ctrl[i + 1];
                Vector3 p3 = ctrl[Mathf.Min(k - 1, i + 2)];
                for (int s = 1; s <= sub; s++)
                {
                    float t = s / (float)(sub + 1);
                    Vector3 pt = MathUtils.CatmullRom(p0, p1, p2, p3, t);
                    skeleton[o] = pt.Normalized();
                    widths[o] = Mathf.Lerp(halfWs[i], halfWs[i + 1], t);
                    o++;
                }
                skeleton[o] = p2.Normalized();
                widths[o] = halfWs[i + 1];
                o++;
            }
        }

        // ── 地形微调（点列位置按当地格子四周高度微调：弯向低侧；源头/绘制终点固定）──
        int drawSegs;   // 绘制段数（支流汇合点前的骨架段；干流 = 全段）
        int drawEnd;    // 最后一个绘制的骨架点序号（meander 终点固定 + 岸点生成的边界）
        if (firstPainted > 0)
        {
            int mergeIdx = firstPainted * (sub + 1);   // 汇合骨架点序号（支流绘制终点）
            drawSegs = mergeIdx;
            drawEnd = mergeIdx;
        }
        else
        {
            drawSegs = skelCount - 1;
            drawEnd = skelCount - 1;
        }
        ApplyMeander(skeleton, map, elevSpan, gridArc, sub, drawEnd);

        // ── 汇合收束：支流末段（汇合前 1 格距）宽度锥形收束到干流在该点的半宽 ──
        float wMerge = 0f;
        Vector3 sideMerge = default;
        int growPts = 0, mergeGrowStart = 0;
        if (firstPainted > 0)
        {
            (wMerge, sideMerge) = painted[path[firstPainted]];
            growPts = (int)Math.Round(MergeGrowCells * (sub + 1));
            mergeGrowStart = Mathf.Max(0, drawEnd - growPts);
            for (int i = mergeGrowStart; i <= drawEnd; i++)
            {
                float t = (i - mergeGrowStart) / (float)Mathf.Max(1, growPts);
                widths[i] = Mathf.Lerp(widths[i], wMerge, t);   // 起点保原宽 → 汇合点=干流宽
            }
        }

        // ── 带子：先画中心曲线，再对曲线上下加点连线加宽（2026-09-01 用户定法）──
        //   每个骨架点只算一次切线→侧向→左/右岸点；相邻段共享岸点（ribbon，物理无缝——
        //   不再每段重算 side 导致岸点漂移缝隙）。支流收束段横截面方向平滑旋转到干流
        //   横截面（join：逐点 Lerp 侧向），汇合点完全对齐干流 → 无楔形空隙、无覆盖鼓包。
        int m1 = drawSegs + 1;                       // 需要岸点的骨架点数（段数+1）
        var left = new Vector3[m1];
        var right = new Vector3[m1];
        var sideArr = new Vector3[m1];               // 每点横截面侧向（登记 painted 用）
        for (int i = 0; i < m1; i++)
        {
            Vector3 pPrev = i > 0 ? skeleton[i - 1] : skeleton[i];
            Vector3 pNext = i < m1 - 1 ? skeleton[i + 1] : skeleton[i];
            Vector3 tangent = (pNext - pPrev).Normalized();
            Vector3 side = tangent.Cross(skeleton[i]).Normalized();   // 垂直流向、切于球面
            if (side.LengthSquared() < 1e-9f && i > 0) side = sideArr[i - 1];   // 退化兜底
            // 支流收束段：横截面方向向干流旋转（join）
            if (firstPainted > 0 && i >= mergeGrowStart)
            {
                float t = (i - mergeGrowStart) / (float)Mathf.Max(1, growPts);
                side = side.Lerp(sideMerge, t).Normalized();
            }
            sideArr[i] = side;
            float w = widths[i];
            left[i] = (skeleton[i] + side * w).Normalized() * radius;
            right[i] = (skeleton[i] - side * w).Normalized() * radius;
        }
        // 绘制段（邻段共享岸点：段 i 终点边 = 段 i+1 起点边）
        for (int i = 0; i < drawSegs; i++)
        {
            int bi = vertList.Count;
            vertList.Add(left[i]);   vertList.Add(right[i]);
            vertList.Add(left[i + 1]); vertList.Add(right[i + 1]);
            colorList.Add(c); colorList.Add(c); colorList.Add(c); colorList.Add(c);
            indexList.Add(bi); indexList.Add(bi + 1); indexList.Add(bi + 2);
            indexList.Add(bi + 1); indexList.Add(bi + 3); indexList.Add(bi + 2);
        }
        int drawn = drawSegs;
        if (drawn == 0) return false;

        // ── 登记本次独有段（供支流汇合 join）：骨架点 → 归属路径顶点（半宽 + 横截面侧向）──
        for (int i = 0; i <= drawSegs; i++)
            painted[path[i / (sub + 1)]] = (widths[i], sideArr[i]);
        return true;
    }

    /// <summary>水系的最长路径长度（排序用）。</summary>
    private static int LongestPath(List<int[]> ws)
    {
        int maxLen = 0;
        foreach (var p in ws)
            if (p.Length > maxLen) maxLen = p.Length;
        return maxLen;
    }

    /// <summary>
    /// 地形微调：骨架点横向探两侧高度 → 弯向低侧（点列位置贴合当地地形）；两端固定。
    /// 原地修改 skeleton（偏移后重归一化回球面）。
    /// ⚠️ 终点固定用「绘制终点 drawEnd」而非骨架末端：支流汇合点（= 绘制终点）必须
    /// meander 偏移为零，否则带子末端偏离干流中心线 → join 错位。
    /// </summary>
    /// <param name="skeleton">球面骨架点（单位方向，原地偏移）</param>
    /// <param name="map">存档（NearestVertex 桶索引探针，O(~270)/次）</param>
    /// <param name="elevSpan">海拔跨度（steer 归一化）</param>
    /// <param name="gridArc">格距（弧度）</param>
    /// <param name="sub">细分密度（弧长换算：1 格距 = sub+1 个骨架点）</param>
    /// <param name="drawEnd">最后一个需偏移的骨架点序号（源头=0；终点固定区向后取 1 格距）</param>
    private static void ApplyMeander(Vector3[] skeleton, MapData map,
        float elevSpan, float gridArc, int sub, int drawEnd)
    {
        var elev = map.Elev;
        int m = Mathf.Min(skeleton.Length, drawEnd + 1);   // 只处理绘制范围内的骨架点
        if (m < 3) return;

        // 1) 每点横向探针高度差 → steer（>0 = 右侧高 → 应向左偏）
        //    探针：骨架点沿横向 ±ProbeDist 格距处的球面点 → NearestVertex 查归属顶点海拔
        var steer = new float[m];
        for (int i = 0; i < m; i++)
        {
            Vector3 p = skeleton[i];
            Vector3 tPrev = i > 0 ? skeleton[i - 1] : p;
            Vector3 tNext = i < m - 1 ? skeleton[i + 1] : p;
            Vector3 tangent = (tNext - tPrev).Normalized();
            Vector3 side = tangent.Cross(p).Normalized();   // 垂直流向、切于球面
            if (side.LengthSquared() < 1e-9f) { steer[i] = 0f; continue; }
            Vector3 pl = (p + side * (ProbeDist * gridArc)).Normalized();
            Vector3 pr = (p - side * (ProbeDist * gridArc)).Normalized();
            float hL = elev[map.NearestVertex(pl)];
            float hR = elev[map.NearestVertex(pr)];
            steer[i] = Mathf.Clamp((hR - hL) / elevSpan, -1f, 1f);
        }
        // 2) 低通（窗口 3；格点噪声会让 steer 高频抖动 → 河道左右甩）
        var steerSm = new float[m];
        for (int i = 0; i < m; i++)
        {
            int a = Mathf.Max(0, i - 1), b = Mathf.Min(m - 1, i + 1);
            float s = 0f;
            for (int j = a; j <= b; j++) s += steer[j];
            steerSm[i] = s / (b - a + 1);
        }
        // 3) 偏移 = 地形偏向；两端 1 格距内渐变到 0（固定源头/入海/汇合——终点用绘制终点）
        for (int i = 0; i < m; i++)
        {
            float offset = steerSm[i] * TerrainBias * gridArc;
            float arcCells = i / (float)(sub + 1); // 弧长（格距）
            float growStart = Mathf.Clamp(arcCells, 0f, 1f);
            float growEnd = Mathf.Clamp((m - 1 - i) / (float)(sub + 1), 0f, 1f);
            offset *= Mathf.Min(growStart, growEnd);
            if (Mathf.Abs(offset) < 1e-9f) continue;
            Vector3 p = skeleton[i];
            Vector3 tPrev = i > 0 ? skeleton[i - 1] : p;
            Vector3 tNext = i < m - 1 ? skeleton[i + 1] : p;
            Vector3 tangent = (tNext - tPrev).Normalized();
            Vector3 side = tangent.Cross(p).Normalized();
            skeleton[i] = (p + side * offset).Normalized();
        }
    }

    /// <summary>河格流量分位数（拼所有路径顶点；无数据 → 1f 兜底）。</summary>
    private static float Quantile(float[] volume, IReadOnlyList<int[]> paths, float q)
    {
        var vals = new List<float>(paths.Count * 8);
        foreach (var p in paths)
            for (int i = 0; i < p.Length; i++)
            {
                float v = volume[p[i]];
                if (v > 0f) vals.Add(v);
            }
        if (vals.Count == 0) return 1f;
        vals.Sort();
        int idx = Mathf.Clamp((int)(vals.Count * q), 0, vals.Count - 1);
        return vals[idx];
    }

    /// <summary>宽度移动平均（窗口 WidthSmooth，边界收窄；防一格流量跳变 → 宽度突变）。</summary>
    private static void SmoothWidth(float[] w)
    {
        if (w.Length < 3) return;
        var tmp = new float[w.Length];
        for (int i = 0; i < w.Length; i++)
        {
            int a = Mathf.Max(0, i - WidthSmooth / 2), b = Mathf.Min(w.Length - 1, i + WidthSmooth / 2);
            float s = 0f;
            for (int j = a; j <= b; j++) s += w[j];
            tmp[i] = s / (b - a + 1);
        }
        Array.Copy(tmp, w, w.Length);
    }
}