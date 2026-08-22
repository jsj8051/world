using Godot;
using System.Collections.Generic;
using World.HexPlanet;
using World.MapGen;
using World.Services;
using World.Utils;

namespace World.MapView.Layers;

/// <summary>
/// 洋流图层渲染组件（2026-08-21 v4 用户拍板：cambecc/earth 式粒子动画——自平流粒子 + 短拖尾）。
/// ⚠️ v3 箭头流图被用户否决（"我承认他的那种更好"——粒子流动感是静态箭头给不了的），
///    本版参数吸取 v1 教训：无底图流线、拖尾 9 点 ~0.15s（短促干净）、纯粒子、自平流。
///
/// 方案（cambecc/earth 同款粒子范式）：
///   1. 出生表：海洋格 + 平滑场有效顶点（弱场格不投种子）；粒子随机撒放。
///   2. 自平流：每帧 pos += 平滑方向 × (基础速度 × 局部流速) × dt（cos⁴ 1 环插值 + 切平面投影）。
///   3. 停滞/上岸 → 重生（弱场从出生表随机重放，拖尾淡入）——cambecc 同款 minSpeed 重置。
///   4. 拖尾：9 点几何 ribbon（头亮尾淡，加法混合发光）；切到其他图层动画停跑（Visible 门控）。
///
/// 数据兜底（EnsureLiveField）：存档场可用方向过少 / 离面（旧档损坏）→ OceanCurrent 现场重算。
/// 性能：纯 C# 预分配缓冲（粒子/拖尾/网格数组零每帧分配；mesh 每帧重建，数组复用——
///       如性能不足可改 ArrayMesh.SurfaceUpdate* 区域更新）。
/// </summary>
public partial class CurrentFlow : Node3D
{
    /// <summary>粒子总数（2026-08-21 对齐 earth 画法：2400 个——高密度靠重叠产生丝绸感，
    /// 单粒子的折角被淹没在点云里，画法对场噪声宽容）。</summary>
    private const int ParticleCount = 2400;

    /// <summary>拖尾点数（6 点 ≈ 0.1s@60fps——earth 式"亮点+短残影"：粒子只是带个短影，
    /// 不再是清晰流带；短拖尾天然掩盖场噪声/折角）。</summary>
    private const int TrailLen = 6;

    /// <summary>基础速度（rad/s；× 局部流速乘子）——恒定角速度，拖尾视觉长度与分辨率无关。</summary>
    private const float BaseSpeedRad = 0.26f;

    /// <summary>局部流速乘子下限（0.35~1.0：速度差异收窄——快慢粒子拖尾长度接近，
    /// 避免"不规则长度线段"观感；cambecc 场均匀所以拖尾整齐）。</summary>
    private const float MinSpeedMul = 0.35f;

    /// <summary>重生后淡入时间（秒）：新粒子拖尾快速出现，避免闪烁。</summary>
    private const float FadeInTime = 0.06f;

    /// <summary>弱场惯性滑行上限（秒）：方向场失效后沿最后方向减速漂移，超时才重生。
    /// ⚠️ 2026-08-21 模式 A（持续流动）：0.15s → 2.5s——粒子跟随洋流持续存在（tracer 物理），
    ///   漂进归零区（开阔大洋/环流边界）沿惯性慢速漂过（~19°，足够穿过弱区），
    ///   漂回强区恢复跟随 / 上岸重生 / 漂 2.5s 仍无方向才重生（兜底防大圆绕球）。
    ///   弱区轨迹由 FillMesh 的惯性弱化（alpha×0.4）淡色显示，不误导流向。
    ///   现象：粒子路径连续完整（湾流→跨洋→岸边），弱区有淡色慢速粒子——earth 同款。</summary>
    private const float InertiaMaxTime = 2.5f;

    /// <summary>拖尾头宽 = 格距 × 此系数（随分辨率缩放）。
    /// 2026-08-21 对齐 earth：宽度略收（0.04 格距）——短拖尾+细线更像"光痕"，而非粗条带。</summary>
    private const float HeadWFactor = 0.04f;

    /// <summary>跨缝判定（弧度）：相邻拖尾点距离超过 1.5 格距 = 瞬移（重生/上岸）→ 该段不画。</summary>
    private const float GapFactor = 1.5f;

    /// <summary>重合短段阈值 = 格距 × 此系数：拖尾点未拉开（重生后重合点）→ 该段不画。
    /// ⚠️ 2026-08-21 修复"尾部粗 3D 折线"：重生时拖尾 12 点全置同位置，后续帧头部点移动、
    ///   其余点未拉开 → d≈0 触发 side 兜底（固定 X 轴）→ 一串沿 X 轴的宽条。
    ///   短段隐藏后只有真正拉开的位置点才连线。
    /// ⚠️ 2026-08-21 Catmull-Rom 平滑后子段更短（帧距/3）——阈值按平滑后尺度收紧（0.005 格距）。</summary>
    private const float MinSegFactor = 0.005f;

    /// <summary>拖尾 Catmull-Rom 平滑细分数（每段插 2 子点：12 拖尾点 → 34 平滑点/33 段，
    /// 弯曲处无折角——通用实现见 World.Utils.MathUtils）。</summary>
    private const int SmoothSubdivisions = 2;

    /// <summary>暖流色（warmth=+1）。</summary>
    private static readonly Color Warm = new(1f, 0.45f, 0.15f);

    /// <summary>中性色（warmth=0）。</summary>
    private static readonly Color Neutral = new(0.90f, 0.92f, 1f);

    /// <summary>寒流色（warmth=-1）。</summary>
    private static readonly Color Cold = new(0.25f, 0.55f, 1f);

    /// <summary>粒子亮度（加法混合下防过曝：叠加 3-4 层即饱和）。</summary>
    private const float Brightness = 0.72f;

    private MapData _map;
    private float _radius = 1f;      // 显示半径 = RadiusKm × OverlayLiftFactor
    private int[][] _neighbors;      // 邻接表（Build 缓存）
    private Vector3[] _smoothDirs;   // 预平滑方向场（BuildSmoothDirs 产出）
    private Vector3[] _spawnTable;   // 出生顶点（海洋 + 平滑场有效）
    private float _maxStrength;      // 局部流速归一化基准（CurrentStrength 最大值）
    private float _gridArc;          // 格距（分辨率自适应）
    private float _gapArc;           // 跨缝阈值（太长 → 瞬移隐藏）
    private float _minSegArc;        // 重合短段阈值（太短 → 未拉开隐藏）
    private float _headW;            // 拖尾头宽
    private System.Random _rng = new(42);   // 固定种子：粒子分布可复现（诊断）

    // 粒子状态（预分配，零每帧分配）
    private struct Particle
    {
        public Vector3 Pos;      // 当前位置（单位球面）
        public Vector3 LastDir;  // 最近有效方向（弱场惯性滑行用）
        public float NoFieldTime;// 连续无有效方向时间（秒；超 InertiaMaxTime 重生）
        public float Age;        // 重生后年龄（秒；淡入用）
        public float Warmth;     // 冷暖 -1~+1（颜色）
        public float SpeedMul;   // 局部流速乘子 0.35~1.0
    }

    private Particle[] _particles;
    private Vector3[][] _trail;      // [粒子][TrailLen] 拖尾点（0=最新）
    private Vector3[][] _smoothTrail;// [粒子][1+(TrailLen-1)×(SmoothSubdivisions+1)] Catmull-Rom 平滑缓冲（零分配）
    private long _resetCount;        // 累计重生次数（诊断）

    // 最近顶点快速查询：球面桶索引（NearestVertex O(n) 线性扫描每帧 1800 次太慢）
    private const int BLat = 128, BLon = 256;
    private int[][] _vtxBuckets;     // [BLat*BLon] 顶点 id 列表

    // 渲染（预分配数组；索引全量固定，跨缝段 alpha=0 隐藏）
    private MeshInstance3D _meshInst;
    private Vector3[] _verts;
    private Color[] _colors;
    private int[] _indices;

    /// <summary>粒子数（构建后只读）。</summary>
    public int ParticleCountConst => ParticleCount;

    /// <summary>是否已构建（场异常跳过渲染时 false——_particles 为 null）。</summary>
    public bool IsBuilt => _particles != null;

    /// <summary>地图快照（诊断 PNG 陆地轮廓定位用）。</summary>
    public MapData MapSnapshot => _map;

    /// <summary>累计重生次数（诊断：弱场/上岸重放计数）。</summary>
    public long ResetCount => _resetCount;

    /// <summary>导出用粒子快照（诊断 PNG 用；不暴露内部结构）。</summary>
    public struct ExportedParticle
    {
        public Vector3 Pos;
        public Color Col;
    }

    /// <summary>当前粒子快照（等距柱状 PNG 导出用）。</summary>
    public ExportedParticle[] ExportParticles()
    {
        var outArr = new ExportedParticle[ParticleCount];
        for (int i = 0; i < ParticleCount; i++)
        {
            outArr[i].Pos = _particles[i].Pos;
            outArr[i].Col = CurrentColor(_particles[i].Warmth) * Brightness;
        }
        return outArr;
    }

    /// <summary>构建粒子动画（主线程；MapViewer.FinishGenerate 经策略 BuildOverlay 调用）。</summary>
    public void Build(MapData map, float radiusKm)
    {
        _map = map;
        _radius = radiusKm * MapViewer.OverlayLiftFactor;

        // ⚠️ 2026-08-21 用户拍板：去掉现场重算兜底——渲染只反映存档数据本身。
        // 旧档（v3/v4/v5 重构期）洋流场损坏（近乎全零/离面）→ 直接不渲染（日志警告原因），
        // 不再用 OceanCurrent 近似场替换存档数据（避免新旧档画面不一致）。
        var (healthy, usable, meanRadial) = FieldHealth();
        if (!healthy)
        {
            LogService.Log("CurrentFlow", $"存档洋流场异常（可用 {usable} 个，平均离面 |d·r|={meanRadial:F2}）→ 洋流图层不渲染");
            return;   // _particles == null → _Process 直接跳过
        }

        _neighbors = map.BuildNeighbors();
        BuildSmoothDirs(_neighbors, map.CurrentDirs);

        int simN = Icosahedron.GridNFromVertexCount(map.Verts.Length);
        _gridArc = Mathf.Tau / (Mathf.Sqrt(10f) * Mathf.Max(8, simN));
        _gapArc = _gridArc * GapFactor;
        _minSegArc = _gridArc * MinSegFactor;
        _headW = _gridArc * HeadWFactor;

        // 局部流速归一化基准（CurrentStrength 最大值；无强度段 → 全速 1.0）
        _maxStrength = 1f;
        if (map.CurrentStrength != null)
        {
            float mx = 0f;
            for (int i = 0; i < map.CurrentStrength.Length; i++)
                if (map.CurrentStrength[i] > mx) mx = map.CurrentStrength[i];
            _maxStrength = mx > 1e-9f ? mx : 1f;
        }

        BuildSpawnTable();
        BuildVertexBuckets();
        InitParticles();
        BuildMesh();
        LogService.Log("CurrentFlow", $"粒子动画构建完成：粒子 {ParticleCount} / 出生表 {_spawnTable.Length} / " +
            $"格距 {Mathf.RadToDeg(_gridArc):F2}° / 拖尾 {TrailLen} 点");
    }

    public override void _Process(double delta)
    {
        if (!Visible || _particles == null) return;   // ⚠️ 切图层停跑（MapViewer 切 Visible）
        float dt = Mathf.Clamp((float)delta, 0f, 0.05f);   // 防卡顿瞬移
        UpdateParticles(dt);
        FillMesh();
        PublishMesh();
    }

    // ── 0. 存档场健康检查（只检测不重算——2026-08-21 用户拍板去掉兜底）──

    /// <summary>存档洋流场健康检查：可用方向 ≥64 且平均离面 |d·r| &lt; 0.25（&lt;14.5°）→ 正常。
    /// v4/v5 中途态存档（2026-08-16 重构期生成）CurrentDirs 段近乎全零（实测仅 17/10242 个
    /// 零散向量）；v3 旧档存过带径向分量的场（实测平均离面 |d·r|=0.70）。
    /// ⚠️ 不再现场重算（旧行为：OceanCurrent 解析风 + 存档温度近似场写回 _map）——
    /// 渲染如实反映存档数据，异常场直接跳过渲染。</summary>
    private (bool ok, int usable, float meanRadial) FieldHealth()
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
        return (usable >= 64 && meanRadial < 0.25f, usable, meanRadial);
    }

    // ── 1. 方向场预平滑（cos⁴ 1 环插值 + 切平面投影；等价 cambecc 双线性插值消噪）──

    /// <summary>方向场预平滑（1 环均值；等价 cambecc 双线性插值消噪）。
    /// 逐格：自身 + 海洋邻居方向取均值；均值相干性过低（分歧/鞍点附近）→ 0 → 追踪停止。
    /// 陆地格与无方向格保持 0（平滑不向外扩散——开阔大洋仍空白）。
    /// ⚠️ 2026-08-21 增强（用户"不该截断"）：第一遍平滑后，无方向的海洋顶点从已平滑邻居
    ///   迭代扩散出方向（弱流边缘/大洋中央——数值截断成 0 但物理有慢速方向）：
    ///   场处处有方向 → 粒子自然慢速流动，无需靠惯性漂过弱区（earth 同款）。
    ///   涡旋中心（邻居方向抵消）相干护栏仍归 0（物理零——涡眼无流）。</summary>
    private void BuildSmoothDirs(int[][] nbs, Vector3[] dirs)
    {
        int n = dirs.Length;
        var smooth = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            if (_map.Elev[i] >= 0f) continue;               // 陆地保持 0
            if (dirs[i].LengthSquared() < 1e-9f) continue;  // 本身弱场保持 0（第一遍）
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

        // ── 弱区方向扩散（迭代：无方向海洋顶点 ← 已平滑邻居均值；直到收敛）──
        // 每次扩散把方向向内推一层；长度随扩散轮次弱化（越往大洋中央越慢——物理弱流）。
        // 相干护栏：邻居方向互相抵消（均值过短）→ 保持 0（环流边界/涡旋中心——物理无流向）。
        for (int pass = 1; pass <= 32; pass++)
        {
            bool any = false;
            for (int i = 0; i < n; i++)
            {
                if (_map.Elev[i] >= 0f) continue;                 // 陆地不扩散
                if (smooth[i].LengthSquared() > 1e-9f) continue;  // 已有方向
                var nb = nbs[i];
                if (nb == null) continue;
                Vector3 sum = Vector3.Zero;
                int cnt = 0;
                foreach (var v in nb)
                    if (smooth[v].LengthSquared() > 1e-9f) { sum += smooth[v]; cnt++; }
                if (cnt < 2) continue;                            // <2 个有方向邻居：不扩散（防单邻居噪声外推）
                Vector3 mean = sum / cnt;
                mean -= _map.Verts[i] * mean.Dot(_map.Verts[i]);  // 切平面投影
                // 相干护栏：均值长度相对衰减太多 = 邻居方向互相抵消（边界）→ 保持 0
                if (mean.LengthSquared() < 0.04f * cnt * cnt) continue;
                smooth[i] = mean.Normalized() * (1f / pass);      // 越内层越弱（速度衰减）
                any = true;
            }
            if (!any) break;
        }
        _smoothDirs = smooth;
    }

    /// <summary>方向场平滑采样：最近顶点 + 1 环图邻居 cos⁴ 加权（同 SampleSpherical 口径）。
    /// 相邻格共享顶点 → 插值场连续 → 粒子路径平滑。切平面投影 + 相干护栏
    /// （分歧/鞍点 → zero → 停滞重生）。</summary>
    private Vector3 SmoothDir(Vector3 p, int[][] nbs, Vector3[] dirs)
    {
        int id = NearestVertexFast(p);
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

    // ── 2. 出生表 / 顶点桶（粒子的家）──

    /// <summary>出生表：海洋格 + 平滑场有效（弱场格不投——开阔大洋保持空白，粒子集中洋流区）。</summary>
    private void BuildSpawnTable()
    {
        var list = new List<Vector3>(4096);
        for (int i = 0; i < _map.Verts.Length; i++)
            if (_map.Elev[i] < 0f && _smoothDirs[i].LengthSquared() > 1e-9f)
                list.Add(_map.Verts[i]);
        _spawnTable = list.ToArray();
        if (_spawnTable.Length == 0)
            LogService.LogErr("CurrentFlow", "出生表为空（无海洋强场格）——粒子全部无法出生");
    }

    /// <summary>顶点桶索引：球面分 BLat×BLon 桶，每桶存顶点 id；最近顶点查询 O(1)（3×3 邻桶候选）。
    /// ⚠️ 粒子每帧 1800 次最近顶点——NearestVertex 是 O(n) 线性扫描（10242 顶点）会卡死。</summary>
    private void BuildVertexBuckets()
    {
        _vtxBuckets = new int[BLat * BLon][];
        var lists = new List<int>[BLat * BLon];
        for (int i = 0; i < lists.Length; i++) lists[i] = new List<int>(4);
        for (int v = 0; v < _map.Verts.Length; v++)
        {
            (int by, int bx) = BucketOf(_map.Verts[v], BLat, BLon);
            lists[by * BLon + bx].Add(v);
        }
        for (int i = 0; i < lists.Length; i++) _vtxBuckets[i] = lists[i].ToArray();
    }

    /// <summary>球面点 → 桶坐标（lat:lon = 1:2 分桶）。</summary>
    private static (int by, int bx) BucketOf(Vector3 p, int bLat, int bLon)
    {
        float lat = Mathf.Asin(Mathf.Clamp(p.Y, -1f, 1f));
        float lon = Mathf.Atan2(p.Z, p.X);
        int by = Mathf.Clamp((int)((lat + Mathf.Pi / 2f) / Mathf.Pi * bLat), 0, bLat - 1);
        int bx = Mathf.Clamp((int)((lon + Mathf.Pi) / Mathf.Tau * bLon), 0, bLon - 1);
        return (by, bx);
    }

    /// <summary>最近顶点快速查询：自身桶 + 8 邻桶候选 → 最近（桶 2.8°×1.4° 细于格距，候选 <50）。</summary>
    private int NearestVertexFast(Vector3 p)
    {
        (int by, int bx) = BucketOf(p, BLat, BLon);
        int best = -1;
        float bestD = float.MaxValue;
        for (int dy = -1; dy <= 1; dy++)
        {
            int yy = (by + dy + BLat) % BLat;
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = (bx + dx + BLon) % BLon;
                var list = _vtxBuckets[yy * BLon + xx];
                for (int i = 0; i < list.Length; i++)
                {
                    int vid = list[i];
                    float d = (_map.Verts[vid] - p).LengthSquared();
                    if (d < bestD) { bestD = d; best = vid; }
                }
            }
        }
        return best >= 0 ? best : _map.NearestVertex(p);   // 兜底（理论不触发）
    }

    // ── 3. 粒子初始化与更新（自平流 + 重生）──

    private void InitParticles()
    {
        _particles = new Particle[ParticleCount];
        _trail = new Vector3[ParticleCount][];
        int smoothLen = 1 + (TrailLen - 1) * (SmoothSubdivisions + 1);   // 12 点 → 34
        _smoothTrail = new Vector3[ParticleCount][];
        for (int i = 0; i < ParticleCount; i++)
        {
            _trail[i] = new Vector3[TrailLen];
            _smoothTrail[i] = new Vector3[smoothLen];
            SpawnParticle(i, random: true);
        }
    }

    /// <summary>投放/重生：出生表随机选点（微扰 ±0.5 格距防整排同位），拖尾全置当前位置，年龄归零。</summary>
    private void SpawnParticle(int idx, bool random)
    {
        var p = _particles[idx];
        Vector3 pos;
        if (_spawnTable.Length > 0)
        {
            Vector3 basePos = _spawnTable[_rng.Next(_spawnTable.Length)];
            pos = random
                ? (basePos + new Vector3(_rng.NextSingle() - 0.5f, _rng.NextSingle() - 0.5f, _rng.NextSingle() - 0.5f) * _gridArc).Normalized()
                : basePos;
        }
        else pos = Vector3.Up;
        int vid = NearestVertexFast(pos);
        p.Pos = pos;
        p.LastDir = Vector3.Zero;   // 无惯性（首次出生即遇弱场 → 立即重生）
        p.NoFieldTime = 0f;
        p.Age = 0f;
        p.Warmth = _map.CurrentWarmth != null ? _map.CurrentWarmth[vid] : 0f;
        p.SpeedMul = SpeedMulOf(vid);
        for (int k = 0; k < TrailLen; k++) _trail[idx][k] = pos;
        _particles[idx] = p;
    }

    /// <summary>局部流速乘子：CurrentStrength 归一化 → MinSpeedMul~1.0
    /// （速度差异收窄——拖尾长度整齐；弱流区也有基本速度，全图不空）。</summary>
    private float SpeedMulOf(int vid)
    {
        float s = _map.CurrentStrength != null && _maxStrength > 1e-9f
            ? Mathf.Clamp(_map.CurrentStrength[vid] / _maxStrength, 0f, 1f) : 1f;
        return MinSpeedMul + (1f - MinSpeedMul) * s;
    }

    /// <summary>单帧更新：自平流（pos += dir × speed × dt）+ 惯性滑行 + 上岸/超时重生 + 拖尾前移。
    /// ⚠️ 2026-08-21 系统修复：采样源改为预平滑场 _smoothDirs（原为原始场 CurrentDirs——
    ///   预平滑红利没吃到；预平滑场顶点值已 1 环降噪，跳变大幅减少，
    ///   下方突变护栏/方向低通降级为兜底而非主力）。</summary>
    private void UpdateParticles(double dt)
    {
        var dirs = _smoothDirs;   // ⚠️ 预平滑场采样（系统修复：数据源已降噪）
        var nbs = _neighbors;
        float speed = BaseSpeedRad * (float)dt;
        for (int i = 0; i < ParticleCount; i++)
        {
            var p = _particles[i];
            bool respawn = false;
            int vid = NearestVertexFast(p.Pos);
            // 上岸 → 重生（tracer 到岸旅程结束；拖尾跨缝段 alpha=0 兜底）
            if (_map.Elev[vid] >= 0f) respawn = true;
            else
            {
                // ══ 统一 tracer 速度方程（2026-08-21 重构）：dx/dt = 驱动方向 × 驱动尺度 × 场速 ══
                //   驱动方向：洋流场方向（有场，已低通/护栏处理）∨ 残余动量方向 LastDir（无场）
                //   驱动尺度：有场 1.0（洋流全速驱动）∨ 0.5（动量滑行——无场时残余动量衰减）
                //   动量计时 NoFieldTime：有场清零，无场累计——超 InertiaMaxTime 动量耗尽 → 重生
                Vector3 dir = SmoothDir(p.Pos, nbs, dirs);
                bool hasField = dir.LengthSquared() >= 1e-9f;
                // 有场时的方向低通（0.55 新 + 0.45 旧）：平滑转向——
                //   • 采样噪声抖动 → 低通吸收，不产生大转向（防抽搐）
                //   • 真实反向流区 → 多帧累积转向跟随（被流场捕获，不穿越）
                // ⚠️ 2026-08-21 去掉硬护栏（原 dot<0 → 保持动量）：护栏让粒子惯性方向
                //   与流区方向 >90° 时"无视"流区穿过去——穿过好几个流区不变方向的根源。
                //   低通对反向信号同样平滑过渡（0.55 新方向主导 → 逐渐转向），无瞬间折返。
                if (hasField && p.LastDir.LengthSquared() > 1e-12f)
                    dir = (dir * 0.55f + p.LastDir * 0.45f).Normalized();
                Vector3 drive = hasField ? dir : p.LastDir;            // 驱动方向
                float scale = hasField ? 1f : 0.5f;                    // 驱动尺度
                p.NoFieldTime = hasField ? 0f : p.NoFieldTime + (float)dt;
                bool alive = drive.LengthSquared() > 1e-12f && p.NoFieldTime <= InertiaMaxTime;
                if (alive)
                {
                    p.Pos = (p.Pos + drive.Normalized() * (speed * p.SpeedMul * scale)).Normalized();
                    p.Age += (float)dt;
                    if (hasField) p.LastDir = dir;   // 动量基准仅在场驱动时更新（滑行期间保持旧基准）
                    // ⚠️ 2026-08-21 每帧更新冷暖（颜色跟随位置）：原只在 SpawnParticle 采样一次
                    //   → 粒子从暖流流到寒流颜色不变（出生色固定）。0.3 Lerp 平滑防跨格跳变。
                    float w = _map.CurrentWarmth != null ? _map.CurrentWarmth[vid] : 0f;
                    p.Warmth = Mathf.Lerp(p.Warmth, w, 0.3f);
                }
                else respawn = true;
            }
            if (respawn)
            {
                SpawnParticle(i, random: true);
                _resetCount++;
                continue;   // 拖尾已在 Spawn 重置
            }
            // 拖尾前移（0=最新；12 点拷贝 ≈ 65K float/帧，微不足道）
            var trail = _trail[i];
            for (int k = TrailLen - 1; k >= 1; k--) trail[k] = trail[k - 1];
            trail[0] = p.Pos;
            _particles[i] = p;
        }
    }

    // ── 4. 渲染：拖尾 ribbon（头亮尾淡，加法混合发光）──

    private void BuildMesh()
    {
        // ⚠️ 段数按 Catmull-Rom 平滑后的点数：12 拖尾点 → 34 平滑点 → 33 段
        int segs = (TrailLen - 1) * (SmoothSubdivisions + 1);
        _verts = new Vector3[ParticleCount * segs * 2];
        _colors = new Color[ParticleCount * segs * 2];
        _indices = new int[ParticleCount * segs * 6];
        for (int i = 0; i < ParticleCount; i++)
        {
            int baseV = i * segs * 2;
            for (int s = 0; s < segs; s++)
            {
                int bi = (baseV + s * 2);
                _indices[baseV + s * 6] = bi;
                _indices[baseV + s * 6 + 1] = bi + 1;
                _indices[baseV + s * 6 + 2] = bi + 2;
                _indices[baseV + s * 6 + 3] = bi + 1;
                _indices[baseV + s * 6 + 4] = bi + 3;
                _indices[baseV + s * 6 + 5] = bi + 2;
            }
        }
        _meshInst = new MeshInstance3D
        {
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        // 加法混合发光（深色星球底上粒子叠加成丝绸光带——cambecc 观感）
        _meshInst.MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = Colors.White,
        };
        AddChild(_meshInst);
        PublishMesh();   // 首帧
    }

    /// <summary>填网格缓冲（数组复用，零分配）：每粒子 Catmull-Rom 平滑后 33 段 quad 带。
    /// 弯曲处无折角（原 11 段折线 → 平滑曲线，通用工具 World.Utils.MathUtils.CatmullRomFill）。</summary>
    private void FillMesh()
    {
        int vi = 0;
        for (int i = 0; i < ParticleCount; i++)
        {
            var trail = _trail[i];
            var smooth = _smoothTrail[i];
            int smoothN = MathUtils.CatmullRomFill(trail, SmoothSubdivisions, smooth);
            var p = _particles[i];
            // ⚠️ 2026-08-21 颜色饱和度 ∝ 流速：弱流区方向是 ψ 平缓区的数值噪声，经向分量随机
            //   → 红蓝乱跳失去暖寒语义。强度混合：弱流粒子趋近中性（白），强流带保持暖红寒蓝
            //   （真实洋流图同款：流速越强颜色越饱和）。
            float strength = Mathf.Clamp((p.SpeedMul - MinSpeedMul) / (1f - MinSpeedMul), 0f, 1f);
            float sat = 0.25f + 0.75f * strength;
            Color baseC = Neutral.Lerp(CurrentColor(p.Warmth), sat) * Brightness;
            float fadeIn = Mathf.Clamp(p.Age / FadeInTime, 0f, 1f);   // 重生淡入（防闪烁）
            for (int s = 0; s < smoothN - 1; s++)
            {
                // Catmull-Rom 插值点在 3D 弦上 → 归一化投影回球面
                Vector3 a = smooth[s].Normalized(), b = smooth[s + 1].Normalized();
                float segLen = (a - b).Length();
                // 无效段：太长（重生/上岸瞬移）或太短（重生后重合点未拉开——d≈0 会让 side
                // 兜底成固定方向 → "尾部粗 3D 折线" bug）→ alpha=0 隐藏（索引固定，靠 alpha 隐形）
                bool gap = segLen > _gapArc || segLen < _minSegArc;
                float x = s / (float)(smoothN - 2);            // 0=头，1=尾
                float w = _headW * (1f - x);                   // ⚠️ 2026-08-21 锥形拖尾：头粗 → 尾收尖到 0
                // 惯性轨迹弱化（NoFieldTime>0 = 弱场惯性滑行）：alpha×0.4——惯性方向不是
                // 该处真实洋流，淡色显示不误导流向判读（2026-08-21 用户反馈交叉看不清楚）
                float inertiaDim = p.NoFieldTime > 0f ? 0.4f : 1f;
                // alpha 衰减 (1-x)^1.4：头亮尾淡但全段可见（v4 首版 (1-x)² 尾段≈0 → 拖尾视觉只剩一半
                // → "短线段"观感；放宽后丝带完整呈现）
                float alpha = gap ? 0f : Mathf.Pow(1f - x, 1.4f) * fadeIn * inertiaDim;
                Vector3 d = a - b;
                // ⚠️ 2026-08-21：兜底方向改"垂直于 a 的稳定方向"（原固定 X 轴会在 d≈0 时
                // 产生沿 X 轴的粗条——"尾部粗 3D 折线" bug 的根源之一；短段隐藏后此处极少触发）
                Vector3 side = d.LengthSquared() > 1e-12f
                    ? a.Cross(d.Normalized()).Normalized() * w
                    : a.Cross(Mathf.Abs(a.Y) > 0.9f ? Vector3.Right : Vector3.Up).Normalized() * w;
                Vector3 v1 = (a + side).Normalized() * _radius;
                Vector3 v2 = (a - side).Normalized() * _radius;
                _verts[vi] = v1;
                _colors[vi] = new Color(baseC.R, baseC.G, baseC.B, alpha);
                vi++;
                _verts[vi] = v2;
                _colors[vi] = new Color(baseC.R, baseC.G, baseC.B, alpha);
                vi++;
            }
        }
    }

    /// <summary>发布网格（每帧重建 ArrayMesh——数组复用；如性能不足改 SurfaceUpdate* 区域更新）。</summary>
    private void PublishMesh()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _verts;
        arrays[(int)Mesh.ArrayType.Color] = _colors;
        arrays[(int)Mesh.ArrayType.Index] = _indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _meshInst.Mesh = mesh;
    }

    // ── 工具 ──

    /// <summary>冷暖连续色带：寒流蓝 → 中性白 → 暖流橙红（同图例语义，连续化）。</summary>
    private static Color CurrentColor(float warmth)
    {
        float t = Mathf.Clamp(warmth, -1f, 1f);
        return t < 0f ? Cold.Lerp(Neutral, -t) : Neutral.Lerp(Warm, t);
    }
}
