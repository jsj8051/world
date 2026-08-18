using Godot;
using System.Collections.Generic;
using World.Biome;
using World.HexPlanet;
using World.MapGen;
using World.Services;

namespace World.MapView.Layers;

/// <summary>
/// 洋流图层渲染组件（2026-08-21 v3 用户拍板：整体流图 + 箭头——去掉底图流线与粒子拖尾）。
/// ⚠️ 2026-08-21 M4 收尾：归入 Layers/（图层实现目录）——图层专用画法组件与策略类同目录，
///    自定义图层画法约定：简单几何直接写进策略 BuildOverlay；复杂算法抽成 Layers/ 下组件由策略引用。
///
/// 方案（经典洋流图样式：暖流红橙 / 寒流蓝的弯曲箭头流图）：
///   1. 整体流图：对【每个海洋格】投放种子，沿平滑方向场追踪流线
///      （1 环 cos⁴ 插值 + 切平面投影 + 反向护栏 + 桶索引去重）——覆盖全部流动区域，
///      开阔弱流空白。流线数学上不相交（平滑场积分曲线定理），实测交叉对 ≈ 0。
///   2. 箭头表现：沿每条流线按固定弧长间隔放置三角箭头（短杆 + 头），方向 = 流线切向，
///      颜色 = 冷暖连续色带（暖流橙红 / 寒流蓝，同图例语义）。
///   3. 静态单网格：构建一次，无逐帧动画（性能最优；无粒子无拖尾）。
///
/// 数据兜底（EnsureLiveField）：存档场可用方向过少 / 离面（旧档损坏）→ OceanCurrent 现场重算。
/// </summary>
public partial class CurrentFlow : Node3D
{
    /// <summary>流线追踪步长 = 0.35 格距（弯曲处弦线更贴曲线）。</summary>
    private const float StepArcFactor = 0.35f;

    /// <summary>箭头弧长间隔（弧度 ≈ 0.18 rad ≈ 10°：每线多箭头表现流向）。</summary>
    private const float ArrowSpacing = 0.18f;

    /// <summary>最短有箭头流线（弧长；短于它不画箭头，避免零散箭头）。</summary>
    private const float MinArrowLineArc = 0.30f;

    /// <summary>箭头几何（弧度）：全长 ≈ 0.04 rad ≈ 2.3°。</summary>
    private const float ArrowLen = 0.040f;

    /// <summary>箭头尾部半宽（弧度）。</summary>
    private const float ArrowTailW = 0.016f;

    /// <summary>流线上限（防极端场构建过久）。</summary>
    private const int MaxLines = 900;

    /// <summary>暖流色（warmth=+1）。</summary>
    private static readonly Color Warm = new(1f, 0.45f, 0.15f);

    /// <summary>中性色（warmth=0）。</summary>
    private static readonly Color Neutral = new(0.90f, 0.92f, 1f);

    /// <summary>寒流色（warmth=-1）。</summary>
    private static readonly Color Cold = new(0.25f, 0.55f, 1f);

    /// <summary>流线条数（构建后只读）。</summary>
    public int LineCount => _lines.Count;

    /// <summary>箭头数（构建后只读）。</summary>
    public int ArrowCount => _arrowCount;

    /// <summary>导出用流线快照（诊断 PNG 用；不暴露内部 FlowLine 类型）。</summary>
    public struct ExportedLine
    {
        public Vector3[] Pts;        // 球面折线点
        public Vector3[] Dirs;       // 每点流线切向（箭头方向）
        public float[] Warmth;       // 每点冷暖（-1~+1）
    }

    /// <summary>构建后的流线只读快照（CurrentFlowDiag 导出等距柱状图用）。</summary>
    public ExportedLine[] ExportedLines()
    {
        var arr = new ExportedLine[_lines.Count];
        for (int i = 0; i < _lines.Count; i++)
        {
            arr[i].Pts = _lines[i].Pts;
            arr[i].Dirs = _lines[i].Dirs;
            arr[i].Warmth = _lines[i].Warmth;
        }
        return arr;
    }

    private sealed class FlowLine
    {
        public Vector3[] Pts;        // 单位球面折线点
        public Vector3[] Dirs;       // 每点流线切向（平滑场采样，单位）
        public float[] Warmth;       // 每点冷暖（-1 寒流 ~ +1 暖流；颜色）
    }

    private MapData _map;
    private float _radius = 1f;      // 显示半径 = RadiusKm × OverlayLiftFactor

    private readonly List<FlowLine> _lines = new();
    private int _arrowCount;
    private MeshInstance3D _arrowMesh;   // 箭头静态网格

    private int[][] _neighbors;      // 邻接表（BuildLines 缓存）
    private float _stepArc;          // 追踪步长（按格距自适应）
    private float _closureArc;       // 闭合判定半径（1.3 格距）
    private int _minPts;             // 最短有效线点数（≈ 0.45 rad 弧长）
    private Vector3[] _smoothDirs;   // 预平滑方向场（BuildSmoothDirs 产出）

    // 急弯指标（诊断；每线最急弯，>90° 已被反向护栏拦截）
    private float _maxTurn;
    private double _turnSum;
    private int _turnCount;

    /// <summary>构建洋流箭头流图（主线程；MapViewer.FinishGenerate 调用）。</summary>
    public void Build(MapData map, float radiusKm)
    {
        _map = map;
        _radius = radiusKm * MapViewer.OverlayLiftFactor;

        EnsureLiveField();
        BuildLines();
        if (_lines.Count == 0)
        {
            LogService.Log("CurrentFlow", "无有效流线（洋流场全弱）→ 不渲染");
            return;
        }
        BuildArrowMesh();
        LogService.Log("CurrentFlow", $"构建完成：流线 {_lines.Count} 条 / 箭头 {_arrowCount} / " +
            $"最急弯 max={Mathf.RadToDeg(_maxTurn):F0}° 每线均值={Mathf.RadToDeg((float)(_turnSum / Mathf.Max(1, _turnCount))):F0}°");
    }

    // ── 0. 数据兜底 ──

    /// <summary>存档洋流场"有效方向"过少或离面 → 现场重算（旧档兼容；同流域/月风场"现场算"约定）。
    /// v4/v5 中途态存档（2026-08-16 重构期生成）CurrentDirs 段近乎全零（实测仅 17/10242 个
    /// 零散向量，无法连成流线）；v3 旧档存过带径向分量的场（实测平均离面 |d·r|=0.70）——
    /// 重算用 OceanCurrent 解析风 + 存档温度热成风（同 OceanCurrentDiag 口径），
    /// 结果写回 _map（仅洋流渲染消费该字段；重算一次后续复用）。</summary>
    private void EnsureLiveField()
    {
        var dirs = _map.CurrentDirs;
        int usable = 0;
        double radialSum = 0;
        int radialCnt = 0;
        if (dirs != null)
        {
            for (int i = 0; i < dirs.Length; i++)
            {
                var d = dirs[i];
                if (d.LengthSquared() > 1e-9f)
                {
                    // 切向性检测：dir·pos 应为 ~0（洋流切向流动）
                    if (++usable < 4096)
                    {
                        radialSum += Mathf.Abs(d.Normalized().Dot(_map.Verts[i]));
                        radialCnt++;
                    }
                    if (usable >= 64 && radialCnt >= 256) break;
                }
            }
        }
        float meanRadial = radialCnt > 0 ? (float)(radialSum / radialCnt) : 0f;
        // 64 个可用方向且平均径向分量 < 0.25（<14.5° 离面）→ 场正常
        if (usable >= 64 && meanRadial < 0.25f) return;

        LogService.Log("CurrentFlow", $"存档洋流场异常（可用 {usable} 个，平均离面 |d·r|={meanRadial:F2}）→ 现场重算（OceanCurrent 解析风 + 存档温度）");
        var nbs = _map.BuildNeighbors();
        var eNorm = new float[dirs?.Length ?? _map.Verts.Length];
        float range = Mathf.Max(-_map.MinElev, _map.MaxElev);
        for (int i = 0; i < eNorm.Length; i++) eNorm[i] = range > 1e-6f ? _map.Elev[i] / range : 0f;
        WindField.Prograde = _map.ProgradeRotation;
        WindField.RotationSpeed = _map.RotationSpeed;
        OceanCurrent.Compute(_map.Verts, nbs, eNorm,
            out _map.CurrentDirs, out _map.CurrentWarmth, out _map.CurrentStrength, out _map.Psi,
            windField: null, oceanTemp: _map.Temp);
    }

    // ── 1. 整体流图：每个海洋格投放种子 → 平滑场追踪流线 ──

    /// <summary>对【每个海洋格（顶点）】投放种子追踪流线（2026-08-21 v3 用户拍板"整体流图"）。
    /// 去重用桶索引（~2.8° 桶、查 3×3 邻桶）：相邻种子追出的重合路径只保留一条；
    /// 平滑场/切平面投影/反向护栏保证流线光滑且不相交。</summary>
    private void BuildLines()
    {
        var map = _map;
        var nbs = _neighbors = map.BuildNeighbors();   // 邻接缓存（存档拓扑现场建一次）
        var dirs = map.CurrentDirs;

        // 分辨率自适应（格距 gridArc；步长 = 0.35 格距）
        int simN = Icosahedron.GridNFromVertexCount(map.Verts.Length);
        float gridArc = Mathf.Tau / (Mathf.Sqrt(10f) * Mathf.Max(8, simN));
        _stepArc = Mathf.Clamp(gridArc * StepArcFactor, 0.006f, 0.04f);
        _closureArc = gridArc * 1.3f;                  // 闭合判定：绕回种子 1.3 格距内
        float dedupeArc = gridArc * 0.6f;              // 去重半径：相邻流线 ≥0.6 格距
        _minPts = Mathf.Max(10, Mathf.RoundToInt(0.45f / _stepArc));
        int maxSteps = Mathf.RoundToInt(5.5f / _stepArc);

        // 方向场预平滑（1 环均值；等价 cambecc 双线性插值消噪——n=32 原始场相邻格
        // 方向差可达 60-90°，直接追踪必然锯齿交叉）
        BuildSmoothDirs(nbs, dirs);

        // 去重桶索引（已接受流线点；桶 ~2.8°，查 3×3 邻桶 → O(1) 去重）
        const int bLat = 64, bLon = 128;
        var buckets = new List<List<Vector3>>(bLat * bLon);
        for (int i = 0; i < bLat * bLon; i++) buckets.Add(new List<Vector3>(16));

        int vn = map.Verts.Length;
        for (int seedId = 0; seedId < vn && _lines.Count < MaxLines; seedId++)
        {
            if (map.Elev[seedId] >= 0f) continue;               // 陆地格跳过
            if (dirs[seedId].LengthSquared() < 1e-9f) continue;  // 弱场格跳过
            var seed = map.Verts[seedId];
            if (NearAnyBucket(seed, buckets, dedupeArc, bLat, bLon)) continue;   // 已有流线经过
            TraceLine(seed, nbs, dirs, maxSteps, buckets, bLat, bLon);
        }
    }

    private void TraceLine(Vector3 seed, int[][] nbs, Vector3[] dirs, int maxSteps,
        List<List<Vector3>> buckets, int bLat, int bLon)
    {
        var map = _map;
        var pts = new List<Vector3> { seed };
        var dlist = new List<Vector3> { SmoothDir(seed, nbs, dirs) };
        var warms = new List<float> { SampleWarmth(seed) };
        Vector3 pos = seed;
        Vector3 lastDir = Vector3.Zero;
        bool hasLastDir = false;
        for (int s = 0; s < maxSteps; s++)
        {
            int id = map.NearestVertex(pos);
            if (map.Elev[id] >= 0f) break;                          // 上岸
            Vector3 dir = SmoothDir(pos, nbs, dirs);
            if (dir.LengthSquared() < 1e-9f) break;                 // 弱场边缘（开阔大洋/环流中心）
            // 方向突变护栏：与上一步采样方向夹角 >90°（dot<0.1）= 跨界到反向流区
            // （副热带/副极地环流边界：两侧流向相反）。真实流线不穿越流线分隔线；
            // 无此护栏追踪会在此来回振荡 → 锯齿交叉。
            if (hasLastDir && lastDir.Dot(dir) < 0.1f) break;
            lastDir = dir;
            hasLastDir = true;
            Vector3 next = (pos + dir * _stepArc).Normalized();
            if (pts.Count >= 12 && (next - seed).Length() < _closureArc) break;   // 闭合环流
            pts.Add(next);
            dlist.Add(dir);
            warms.Add(map.CurrentWarmth != null ? map.CurrentWarmth[id] : 0f);
            pos = next;
        }
        if (pts.Count < _minPts) return;

        var line = new FlowLine
        {
            Pts = pts.ToArray(),
            Dirs = dlist.ToArray(),
            Warmth = warms.ToArray(),
        };

        // 急弯指标（诊断）：相邻段最大转向角——每线最急弯；>90° 已被反向护栏拦截
        float maxTurn = 0f;
        for (int i = 2; i < line.Pts.Length; i++)
        {
            Vector3 a = (line.Pts[i - 1] - line.Pts[i - 2]).Normalized();
            Vector3 b = (line.Pts[i] - line.Pts[i - 1]).Normalized();
            float turn = Mathf.Acos(Mathf.Clamp(a.Dot(b), -1f, 1f));
            if (turn > maxTurn) maxTurn = turn;
        }
        _maxTurn = Mathf.Max(_maxTurn, maxTurn);
        _turnSum += maxTurn;
        _turnCount++;
        _lines.Add(line);

        // 流线点入桶（供后续种子去重）
        foreach (var p in line.Pts)
        {
            (int by, int bx) = BucketOf(p, bLat, bLon);
            buckets[by * bLon + bx].Add(p);
        }
    }

    /// <summary>方向场预平滑（1 环均值；等价 cambecc 双线性插值消噪）。
    /// 逐格：自身 + 海洋邻居方向取均值；均值相干性过低（分歧/鞍点附近）→ 0 → 追踪停止。
    /// 陆地格与无方向格保持 0（平滑不向外扩散——开阔大洋仍空白）。</summary>
    private void BuildSmoothDirs(int[][] nbs, Vector3[] dirs)
    {
        int n = dirs.Length;
        var smooth = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            if (_map.Elev[i] >= 0f) continue;               // 陆地保持 0
            if (dirs[i].LengthSquared() < 1e-9f) continue;  // 本身弱场保持 0
            Vector3 sum = dirs[i];
            float wSum = 1f;
            var nb = nbs[i];
            if (nb != null)
            {
                foreach (var v in nb)
                {
                    var dv = dirs[v];
                    if (dv.LengthSquared() < 1e-9f) continue;
                    sum += dv;
                    wSum += 1f;
                }
            }
            Vector3 mean = sum / wSum;
            // 切平面投影：不同顶点切向量的均值带径向分量（实测 dir·p=0.62~0.87，步长被吞
            // 一半 → 织网振荡）。洋流必须切向流动：mean -= r·(mean·r) 再归一。
            mean -= _map.Verts[i] * mean.Dot(_map.Verts[i]);
            if (mean.LengthSquared() > 0.0036f)   // 相干门槛 0.06：均值长度 ≥0.06 才保留
                smooth[i] = mean.Normalized();
        }
        _smoothDirs = smooth;
    }

    /// <summary>方向场平滑采样：最近顶点 + 1 环图邻居 cos⁴ 加权（同 SampleSpherical 口径）。
    /// 相邻格共享顶点 → 插值场连续 → 流线沿 ψ 等值线走，不再因最近顶点跳变而交叉。
    /// 切平面投影（同 BuildSmoothDirs）+ 相干护栏（分歧/鞍点 → zero → 追踪停止）。</summary>
    private Vector3 SmoothDir(Vector3 p, int[][] nbs, Vector3[] dirs)
    {
        int id = _map.NearestVertex(p);
        Vector3 sum = Vector3.Zero;
        float wSum = 0f;
        void Blend(int vid)
        {
            var d = dirs[vid];
            if (d.LengthSquared() < 1e-9f) return;
            float cosAng = Mathf.Clamp(_map.Verts[vid].Dot(p), -1f, 1f);
            float w = cosAng * cosAng;
            w *= w;                        // cos⁴（近邻权重高，远邻衰减）
            sum += d * w;
            wSum += w;
        }
        Blend(id);
        var nb = nbs[id];
        if (nb != null)
            foreach (var vid in nb) Blend(vid);
        if (wSum < 1e-6f) return Vector3.Zero;
        // 切平面投影（跨顶点切向量混合 → 径向分量剔除）
        sum -= p * sum.Dot(p);
        // 相干护栏：|Σ| < 0.25×wSum → 邻居方向严重分歧 → 归一化放大噪声 → 停止
        return sum.LengthSquared() < 0.0625f * wSum * wSum ? Vector3.Zero : sum.Normalized();
    }

    private float SampleWarmth(Vector3 p)
        => _map.CurrentWarmth != null ? _map.CurrentWarmth[_map.NearestVertex(p)] : 0f;

    // ── 2. 箭头表现：沿流线按固定弧长间隔放三角箭头 ──

    private void BuildArrowMesh()
    {
        int arrowStep = Mathf.Max(3, Mathf.RoundToInt(ArrowSpacing / _stepArc));   // 点数间隔
        int startIdx = arrowStep;
        int endMargin = Mathf.Max(3, arrowStep / 2);
        var verts = new List<Vector3>(8192);
        var colors = new List<Color>(8192);
        var indices = new List<int>(16384);

        foreach (var line in _lines)
        {
            int n = line.Pts.Length;
            if ((n - 1) * _stepArc < MinArrowLineArc) continue;   // 短线不画（零散箭头）
            for (int i = startIdx; i < n - endMargin; i += arrowStep)
            {
                var dir = line.Dirs[i];
                if (dir.LengthSquared() < 1e-9f) continue;
                AddArrow(line.Pts[i], dir, CurrentColor(line.Warmth[i]), verts, colors, indices);
                _arrowCount++;
            }
        }
        if (verts.Count == 0)
        {
            LogService.Log("CurrentFlow", "箭头构建：无可用箭头（流线均过短）");
            return;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // 实色无光照（箭头不发光——区别于粒子/底图的加法混合）
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            AlbedoColor = Colors.White,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _arrowMesh = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_arrowMesh);
    }

    /// <summary>三角箭头（短杆 + 头；方向 = 流线切向）。</summary>
    private void AddArrow(Vector3 pos, Vector3 dir, Color col,
        List<Vector3> verts, List<Color> colors, List<int> indices)
    {
        Vector3 tip = (pos + dir * (ArrowLen * 0.62f)).Normalized() * _radius;
        Vector3 tailC = (pos - dir * (ArrowLen * 0.38f)).Normalized() * _radius;
        Vector3 side = pos.Cross(dir).Normalized() * ArrowTailW;
        Vector3 t1 = (tailC + side).Normalized() * _radius;
        Vector3 t2 = (tailC - side).Normalized() * _radius;
        int i0 = verts.Count;
        verts.Add(t1); verts.Add(tip); verts.Add(t2);
        colors.Add(col); colors.Add(col); colors.Add(col);
        indices.Add(i0); indices.Add(i0 + 1); indices.Add(i0 + 2);
    }

    // ── 几何/工具 ──

    /// <summary>球面点 → 去重桶坐标（lat:lon = 1:2 分桶）。</summary>
    private static (int by, int bx) BucketOf(Vector3 p, int bLat, int bLon)
    {
        float lat = Mathf.Asin(Mathf.Clamp(p.Y, -1f, 1f));
        float lon = Mathf.Atan2(p.Z, p.X);
        int by = Mathf.Clamp((int)((lat + Mathf.Pi / 2f) / Mathf.Pi * bLat), 0, bLat - 1);
        int bx = Mathf.Clamp((int)((lon + Mathf.Pi) / Mathf.Tau * bLon), 0, bLon - 1);
        return (by, bx);
    }

    /// <summary>桶去重：3×3 邻桶内存在距 p < radius 的已接受流线点 → 重合路径。</summary>
    private static bool NearAnyBucket(Vector3 p, List<List<Vector3>> buckets, float radius, int bLat, int bLon)
    {
        (int by, int bx) = BucketOf(p, bLat, bLon);
        float r2 = radius * radius;
        for (int dy = -1; dy <= 1; dy++)
        {
            int yy = (by + dy + bLat) % bLat;
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = (bx + dx + bLon) % bLon;
                var list = buckets[yy * bLon + xx];
                for (int i = 0; i < list.Count; i++)
                    if ((list[i] - p).LengthSquared() < r2) return true;
            }
        }
        return false;
    }

    /// <summary>冷暖连续色带：寒流蓝 → 中性白 → 暖流橙红（同图例语义，连续化）。</summary>
    private static Color CurrentColor(float warmth)
    {
        float t = Mathf.Clamp(warmth, -1f, 1f);
        return t < 0f ? Cold.Lerp(Neutral, -t) : Neutral.Lerp(Warm, t);
    }
}
