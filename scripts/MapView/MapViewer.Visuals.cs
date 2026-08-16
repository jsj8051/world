// Slice: MapViewer.Visuals.cs - verbatim member extraction from MapViewer.cs (pure refactor, 2026-08-19).
using Godot;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using World.Biome;
using World.Camera;
using World.HexPlanet;
using World.MapGen;
using World.PlanetLOD;
using World.Surface;
using World.UI;

namespace World.MapView;

public partial class MapViewer
{
    private void EnsureMonthWind()
    {
        if (_monthWind != null || _monthWindStarted || _map == null || _map.Verts == null) return;
        _monthWindStarted = true;
        var map = _map;   // 快照引用（后台线程只读字段，主线程不再改 _map）
        System.Threading.Tasks.Task.Run(() =>
        {
            var nb = map.BuildNeighbors();
            if (nb == null) return;
            int n = map.Verts.Length;
            float span = Mathf.Max(-map.MinElev, map.MaxElev);
            var eNorm = new float[n];
            for (int i = 0; i < n; i++)
                eNorm[i] = span > 1e-6f ? map.Elev[i] / span : 0f;
            MonsoonSystem.Compute(map.Verts, nb, eNorm, map.Elev, map.Temp, map.Precip, map.AxialTilt, map.RotationSpeed,
                new ClimateGenerator(map.Seed, map.AxialTilt, 1f),
                out var mons, out _, out _, out _, out _, out _, out var mw, out var mt, out _,
                radiusKm: map.RadiusKm);
            _monthWindPending = mw;   // 后台线程写字段，主线程 CallDeferred 后读
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
                GD.PrintErr($"[MapViewer] 季风月风场计算失败: {t.Exception?.GetBaseException().Message}");
            CallDeferred(nameof(ApplyMonthWind));   // 回主线程应用（含失败路径清 pending）
        });
    }


    private void ApplyMonthWind()
    {
        var mw = _monthWindPending;
        _monthWindPending = null;
        if (mw == null) return;
        _monthWind = mw;
        GD.Print($"[MapViewer] 季风月风场重算完成（{_map?.Verts.Length} 顶点，倾角 {_map?.AxialTilt}°）");
        // 若当前已是风场/月降水/月温度图层，补建箭头（异步完成前可能已跳过）
        if (Layer == 4 || Layer == 10 || Layer == 11)
            BuildMonsoonArrows();
    }


    /// <summary>季风月风箭头（图层 10 显示；方向 = 当月季风环流风，稀疏采样）。
    /// 复刻 BuildWindArrows 的箭头几何；无风（海洋/非季风区）不画。</summary>
    private void BuildMonsoonArrows()
    {
        if (_monsoonArrows != null)
        {
            _monsoonArrows.QueueFree();
            _monsoonArrows = null;
        }
        if (_tiles == null) return;
        EnsureMonthWind();
        if (_monthWind == null) return;

        const float arrowLen = 0.045f;    // 小箭头（0.07 原值；只标方向，不随强度缩放）
        const float tailW = 0.016f;
        float radius = RadiusKm * OverlayLiftFactor;   // 浮在球面上方防 z-fighting

        var verts = new System.Collections.Generic.List<Vector3>();
        var indices = new System.Collections.Generic.List<int>();

        // ⚠️ 2026-08-16：密集 3 倍（lat 步 12°→4°）；每环经度点数随 cos(lat) 递减（极区少点）
        for (float lat = -88f; lat <= 88f; lat += 4f)
        {
            float la = Mathf.DegToRad(lat);
            float cosLa = Mathf.Cos(la);
            int lonCount = Mathf.Max(8, Mathf.RoundToInt(36 * cosLa));
            for (int j = 0; j < lonCount; j++)
            {
                float lo = Mathf.Tau * j / lonCount;
                var dir = new Vector3(cosLa * Mathf.Cos(lo), Mathf.Sin(la), cosLa * Mathf.Sin(lo));
                int vid = _map.NearestVertex(dir);
                var wind = _monthWind[_month][vid];
                if (wind.LengthSquared() < 1e-9f) continue;   // 无风区不画
                var wDir = wind.Normalized();                 // 只标记方向
                var side = dir.Cross(wDir).Normalized();

                Vector3 tailC = dir - wDir * arrowLen * 0.35f;
                Vector3 tip = dir + wDir * arrowLen * 0.65f;
                Vector3 t1 = (tailC + side * tailW).Normalized() * radius;
                Vector3 t2 = (tailC - side * tailW).Normalized() * radius;
                Vector3 tipS = tip.Normalized() * radius;

                int baseIdx = verts.Count;
                verts.Add(t1); verts.Add(tipS); verts.Add(t2);
                indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // 青蓝色（海风色；与盛行风橙色区分）
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = new Color(0.25f, 0.78f, 0.92f),
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        _monsoonArrows = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = mat,
            Visible = (Layer == 4),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_monsoonArrows);
        GD.Print($"[MapViewer] monsoon arrows built: {verts.Count / 3} arrows (月={_month + 1})");
    }


    /// <summary>刷新当月温度缓存（月温度图层用；月份滑块变化时调用）。</summary>
    private void RefreshMonthTemp()
    {
        if (_tileMonthTemp == null || _map == null || _map.MonthTemp == null) return;
        int n = _tileMonthTemp.Length;
        var arr = _map.MonthTemp[_month];
        for (int i = 0; i < n; i++)
            _tileMonthTemp[i] = arr != null ? arr[_tileIndex.FaceToVertex(i)] : (byte)0;
    }


    /// <summary>刷新当月降水缓存（月降水图层用；月份滑块变化时调用）。</summary>
    private void RefreshMonthPrecip()
    {
        if (_tileMonthPrecip == null || _map == null || _map.MonthPrecip == null) return;
        int n = _tileMonthPrecip.Length;
        var arr = _map.MonthPrecip[_month];
        for (int i = 0; i < n; i++)
            _tileMonthPrecip[i] = arr != null ? arr[_tileIndex.FaceToVertex(i)] : (byte)0;
        // ⚠️ 2026-08-16：自适应色带——当月陆地月降水 min/max（用户拍板：最低到最高归一化）
        _monthPrecipMin = float.MaxValue;
        _monthPrecipMax = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (IsDisplaySea(i)) continue;   // ⚠️ 2026-08-17：统一海陆判定（只统计陆地格）
            float mm = FieldCodec.ByteMonthPrecipToMm(_tileMonthPrecip[i], _tilePrecip[i]) * 12f;   // 等效年尺度（比例×年降水×12）
            _monthPrecipMin = Mathf.Min(_monthPrecipMin, mm);
            _monthPrecipMax = Mathf.Max(_monthPrecipMax, mm);
        }
        if (_monthPrecipMax <= _monthPrecipMin) _monthPrecipMax = _monthPrecipMin + 1f;
    }


    // ── 洋流箭头网格（图层 5 显示）──
    // 用存档洋流场（生成时流函数法算好存 v3.1 尾部）：方向 + 冷暖。
    // ⚠️ 2026-08-02 v2：用户纠正——真实洋流图（网上）是【特定流线】不铺满海洋。
    //   从均匀种子沿洋流方向场追踪流线（streamline），只保留长度足够的流线，
    //   沿流线画箭头 → 湾流/黑潮式清晰流线束，开阔大洋空白。
    //   暖流（warmth>0.05）→ 红橙，寒流（< -0.05）→ 蓝，中性 → 灰白。
    private void BuildCurrentArrows()
    {
        if (_currentArrows != null)
        {
            _currentArrows.QueueFree();
            _currentArrows = null;
        }
        if (_map == null || _map.CurrentDirs == null || _map.CurrentWarmth == null)
        {
            GD.Print("[MapViewer] current arrows skipped: 存档无洋流段（旧版）");
            return;
        }

        // v4 档：流函数 psi 在 → 水位法提取"每环最外圈"（用户拍板形态：环状洋流每环一条外圈，
        // 弱流也显示，不按强度筛选）；旧档（无 psi）回退下方稀疏箭头。
        if (_map.Psi != null)
        {
            BuildCurrentRingsFromPsi();
            return;
        }

        float radius = RadiusKm * OverlayLiftFactor;

        var verts = new System.Collections.Generic.List<Vector3>();
        var colors = new System.Collections.Generic.List<Color>();
        var indices = new System.Collections.Generic.List<int>();

        // ── 用户拍板(2026-08-06)：格点稀疏箭头——放弃闭合环追踪（n=128 追踪全失败）。
        //    稀疏采样（lat 10°≈隔 10 格）+ 强度筛选（只画主要洋流带，不铺满——网上洋流图式），
        //    箭头大小固定为星球比例（不随分辨率变——n=64/n=128 观感一致）。
        const float arrowLen = 0.045f;    // 箭头长（球面弧比例，固定）
        const float arrowTailW = 0.016f;
        int drawn = 0;
        for (float lat = -85f; lat <= 85f; lat += 10f)
        {
            float la = Mathf.DegToRad(lat);
            float cosLa = Mathf.Cos(la);
            int lonCount = Mathf.Max(8, Mathf.RoundToInt(36 * cosLa));
            for (int j = 0; j < lonCount; j++)
            {
                float lo = Mathf.Tau * j / lonCount;
                var pos = new Vector3(cosLa * Mathf.Cos(lo), Mathf.Sin(la), cosLa * Mathf.Sin(lo));
                int vid = _map.NearestVertex(pos);
                if (_map.SampleSpherical(pos, _map.Elev) >= 0f) continue;   // 陆地不画
                var cur = _map.CurrentDirs[vid];
                if (cur.LengthSquared() < 1e-9f) continue;                   // 无洋流不画
                if (_map.CurrentStrength != null && _map.CurrentStrength[vid] < 0.35f) continue;   // 只画主要洋流带
                var wDir = cur.Normalized();
                var side = pos.Cross(wDir).Normalized();
                Vector3 tailC = pos - wDir * arrowLen * 0.35f;
                Vector3 tip = pos + wDir * arrowLen * 0.65f;
                Vector3 t1 = (tailC + side * arrowTailW).Normalized() * radius;
                Vector3 t2 = (tailC - side * arrowTailW).Normalized() * radius;
                Vector3 tipS = tip.Normalized() * radius;
                // 每箭头独立冷暖色（湾流暖 / 加那利寒是同一环两侧）
                float w = _map.CurrentWarmth[vid];
                Color c = w > 0.05f ? new Color(1f, 0.45f, 0.2f)
                    : w < -0.05f ? new Color(0.25f, 0.55f, 1f)
                    : new Color(0.85f, 0.85f, 0.85f);
                int ai = verts.Count;
                verts.Add(t1); verts.Add(tipS); verts.Add(t2);
                colors.Add(c); colors.Add(c); colors.Add(c);
                indices.Add(ai); indices.Add(ai + 1); indices.Add(ai + 2);
                drawn++;
            }
        }
        GD.Print($"[MapViewer] current arrows built: {drawn} 箭头（格点稀疏采样，固定大小）");

        if (verts.Count == 0)
        {
            GD.Print("[MapViewer] current arrows: 无洋流数据");
            return;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        // unshaded + 顶点色
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };

        _currentArrows = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = mat,
            Visible = (Layer == 5),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_currentArrows);
        GD.Print($"[MapViewer] current arrows built: {drawn} 箭头（稀疏采样，固定大小） (from archive)");
    }


    // ── 洋流"每环最外圈"（水位法；v4 存档 psi；用户拍板形态——环状洋流每环一条外圈，
    //    弱流也显示，不按强度筛选）──
    //    原理：ψ 局部极值 = 环流中心；从极值逐层扩张（水位下降/上升），区域边界 = ψ 等值线；
    //    区域扩张到贴大陆前最后一层边界 = 该环流圈最外圈。画边界格箭头（方向=CurrentDirs）。
    private void BuildCurrentRingsFromPsi()
    {
        float radius = RadiusKm * OverlayLiftFactor;
        var verts = new System.Collections.Generic.List<Vector3>();
        var colors = new System.Collections.Generic.List<Color>();
        var indices = new System.Collections.Generic.List<int>();

        var psi = _map.Psi;
        int n = psi.Length;
        var eNorm = new float[n];
        float range = Mathf.Max(-_map.MinElev, _map.MaxElev);
        for (int i = 0; i < n; i++) eNorm[i] = range > 1e-6f ? _map.Elev[i] / range : 0f;
        var dirs = _map.CurrentDirs;
        var nbsAll = _map.BuildNeighbors();   // 现场重建邻接（存档不存拓扑）

        // 1. ψ 局部极值点（海洋格 = 环流中心）
        var seeds = new System.Collections.Generic.List<int>();
        for (int i = 0; i < n; i++)
        {
            if (eNorm[i] >= 0f) continue;
            var nbs = nbsAll[i];
            if (nbs == null || nbs.Length < 3) continue;
            bool isMax = true, isMin = true;
            foreach (var nb in nbs)
            {
                if (psi[nb] > psi[i]) isMax = false;
                if (psi[nb] < psi[i]) isMin = false;
            }
            if (isMax || isMin) seeds.Add(i);
        }

        // 2. 水位法：极值 → 逐层扩张 → 贴大陆前最后一层边界 = 最外圈
        // ⚠️ 2026-08-16 性能修复：n=128 时 seeds 数千、每层 Array.Clear(seen,0,n)+全扫 consumed
        //   = O(seeds×30×n) 千亿次 → 卡 361 秒。改为：
        //   · seen 用 int stamp（每层 stamp++ 即"清空"，O(1) 而非 O(n)）
        //   · consumed 只标记本层 BFS 访问过的格（O(区域) 而非 O(n)）
        //   · seeds 按 |psi| 降序（强环流先占区域，弱极值快速被跳过）
        //   行为不变（环数/箭头数与 02:47 版本一致）。
        var consumed = new bool[n];
        int ringCount = 0, arrowTotal = 0;
        var queue = new System.Collections.Generic.Queue<int>();
        var seenStamp = new int[n];
        int stamp = 0;
        var regionCells = new System.Collections.Generic.List<int>();
        const int layers = 30;
        // seeds 按 |psi| 从大到小：强环流中心先处理 → 弱极值通常已被 consumed 跳过
        seeds.Sort((a, b) => Mathf.Abs(psi[b]).CompareTo(Mathf.Abs(psi[a])));
        foreach (var seed in seeds)
        {
            if (consumed[seed]) continue;
            bool isMax = true;
            foreach (var nb in nbsAll[seed]) if (psi[nb] > psi[seed]) { isMax = false; break; }
            float level0 = psi[seed];
            float step = (level0 - 0f) / layers;   // 极大值降向 0 / 极小值升向 0

            var boundary = new System.Collections.Generic.List<int>();
            var lastBoundary = new System.Collections.Generic.List<int>();
            for (int l = 1; l <= layers; l++)
            {
                float level = isMax ? level0 - step * l : level0 + step * l;
                // BFS 连通区：ψ 满足（极大 ≥ level / 极小 ≤ level）的海洋格
                queue.Clear();
                stamp++;
                regionCells.Clear();
                queue.Enqueue(seed); seenStamp[seed] = stamp;
                int regionCount = 0;
                bool touchesLand = false;
                boundary.Clear();
                while (queue.Count > 0)
                {
                    int c = queue.Dequeue();
                    regionCount++;
                    regionCells.Add(c);
                    bool onBoundary = false;
                    foreach (var nb in nbsAll[c])
                    {
                        if (eNorm[nb] >= 0f) { touchesLand = true; continue; }   // 邻接陆地
                        bool inR = isMax ? psi[nb] >= level : psi[nb] <= level;
                        if (inR)
                        {
                            if (seenStamp[nb] != stamp) { seenStamp[nb] = stamp; queue.Enqueue(nb); }
                        }
                        else onBoundary = true;   // 邻接区域外海洋 = 等值线边界
                    }
                    if (onBoundary) boundary.Add(c);
                }
                if (regionCount == 0) break;
                if (touchesLand) { boundary = lastBoundary; break; }   // 贴岸 → 最外圈 = 上一层
                lastBoundary.Clear();
                lastBoundary.AddRange(boundary);
                foreach (var ci in regionCells) consumed[ci] = true;   // 标记本环区域（只扫访问过的格）
                if (boundary.Count == 0) break;   // 区域填满整个海洋盆（无闭合等值线）
            }
            if (boundary.Count >= 8)
            {
                int drawn = 0;
                const float arrowLen = 0.028f, arrowTailW = 0.012f;
                foreach (var c in boundary)
                {
                    var d = dirs[c];
                    if (d.LengthSquared() < 1e-9f) continue;
                    var pos = _map.Verts[c];
                    var wDir = d.Normalized();
                    var side = pos.Cross(wDir).Normalized();
                    Vector3 tailC = pos - wDir * arrowLen * 0.35f;
                    Vector3 tip = pos + wDir * arrowLen * 0.65f;
                    Vector3 t1 = (tailC + side * arrowTailW).Normalized() * radius;
                    Vector3 t2 = (tailC - side * arrowTailW).Normalized() * radius;
                    Vector3 tipS = tip.Normalized() * radius;
                    float w = _map.CurrentWarmth[c];
                    Color col = w > 0.05f ? new Color(1f, 0.45f, 0.2f)
                        : w < -0.05f ? new Color(0.25f, 0.55f, 1f)
                        : new Color(0.85f, 0.85f, 0.85f);
                    int ai = verts.Count;
                    verts.Add(t1); verts.Add(tipS); verts.Add(t2);
                    colors.Add(col); colors.Add(col); colors.Add(col);
                    indices.Add(ai); indices.Add(ai + 1); indices.Add(ai + 2);
                    drawn++;
                }
                if (drawn > 0) { ringCount++; arrowTotal += drawn; }
            }
        }

        if (verts.Count == 0)
        {
            GD.Print("[MapViewer] current rings: 无闭合环流圈（psi 水位法）");
            return;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            AlbedoColor = Colors.White,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _currentArrows = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = mat,
            Visible = (Layer == 5),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_currentArrows);
        GD.Print($"[MapViewer] 洋流环：最外圈 {ringCount} 个，箭头 {arrowTotal}（水位法，v4 psi）");
    }


    // ── 河流网格（图层 6 显示）──
    // 用存档河流（riverLevel + flow）→ RebuildPaths 重建主河道 → 每条河独立颜色
    // （HSL 黄金角），支流在汇合点截断（painted 集合），主河先画（长→短）。
    private void BuildRivers()
    {
        if (_riverMesh != null)
        {
            _riverMesh.QueueFree();
            _riverMesh = null;
        }
        if (_map == null || _map.RiverLevel == null || _map.RiverFlow == null)
        {
            GD.Print("[MapViewer] rivers skipped: 存档无河流段（旧版）");
            return;
        }

        // 归一化海拔（读档 Elev 是米 → 归一化，<0 = 海洋）
        var verts = _map.Verts;
        int n = verts.Length;
        var eNorm = new float[n];
        float range = Mathf.Max(-_map.MinElev, _map.MaxElev);
        for (int i = 0; i < n; i++) eNorm[i] = range > 1e-6f ? _map.Elev[i] / range : 0f;

        // 重建主河道（源头 → 入海/盆地）
        var paths = World.MapGen.RiverSystem.RebuildPaths(_map.RiverFlow, _map.RiverLevel, eNorm);
        if (paths.Count == 0)
        {
            GD.Print("[MapViewer] rivers: 无主河道");
            return;
        }

        float radius = RadiusKm * OverlayLiftFactor;   // 略高于球面，避免 z-fighting
        var vertList = new System.Collections.Generic.List<Vector3>();
        var colorList = new System.Collections.Generic.List<Color>();
        var indexList = new System.Collections.Generic.List<int>();

        // 主河先画（长→短），支流遇已画顶点截断（汇合点）
        var painted = new System.Collections.Generic.HashSet<int>();
        paths.Sort((a, b) => b.Length.CompareTo(a.Length));
        // ⚠️ 2026-08-06：河宽按分辨率缩放——固定 halfW 在 n=128 格距减半时相对粗 2 倍。
        //   统一按格距比例：halfW = 格距 × 0.13（n=64 时即原 0.004）
        int simN = Icosahedron.GridNFromVertexCount(n);
        float gridArc = Mathf.Tau / (Mathf.Sqrt(10f) * Mathf.Max(8, simN));
        float halfW = gridArc * 0.13f;   // 河宽 ≈ 0.26 格距（观感统一，随分辨率缩放）
        int riverCount = 0;
        foreach (var path in paths)
        {
            // 每条河独立颜色：HSL 色相黄金角循环（相邻河差异最大）
            float hue = GoldenHue(riverCount);
            var c = HslToRgb(hue, 0.9f, 0.55f);
            riverCount++;
            bool drawn = false;
            for (int i = 0; i < path.Length - 1; i++)
            {
                int va = path[i], vb = path[i + 1];
                if (painted.Contains(va)) break;   // 遇汇合点 → 支流段结束
                painted.Add(va);
                Vector3 a = verts[va], b = verts[vb];
                Vector3 seg = b - a;
                if (seg.LengthSquared() < 1e-12f) continue;
                Vector3 side = seg.Cross(a).Normalized();
                Vector3 l0 = (a + side * halfW).Normalized() * radius;
                Vector3 r0 = (a - side * halfW).Normalized() * radius;
                Vector3 l1 = (b + side * halfW).Normalized() * radius;
                Vector3 r1 = (b - side * halfW).Normalized() * radius;
                int bi = vertList.Count;
                vertList.Add(l0); vertList.Add(r0); vertList.Add(l1); vertList.Add(r1);
                colorList.Add(c); colorList.Add(c); colorList.Add(c); colorList.Add(c);
                indexList.Add(bi); indexList.Add(bi + 1); indexList.Add(bi + 2);
                indexList.Add(bi + 1); indexList.Add(bi + 3); indexList.Add(bi + 2);
                drawn = true;
            }
            if (!drawn) riverCount--;   // 全被截断（纯支流无独有段）→ 不计
        }

        if (vertList.Count == 0)
        {
            GD.Print("[MapViewer] rivers: 无可见河道");
            return;
        }

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

        _riverMesh = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = mat,
            Visible = (Layer == 6),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_riverMesh);
        GD.Print($"[MapViewer] rivers built: {riverCount} 条主河道 / {paths.Count} 源 (from archive)");
        // ⚠️ 2026-08-03：headless 验证构建完成即退——取消 --quit-after 800 帧空转
        //   （构建完不再等帧数；验证循环 n=16 从 ~51s 减到 ~15s）
        if (OS.HasFeature("headless"))
            GetTree().Quit();
    }


    /// <summary>显示海陆判定（2026-08-17）：视觉海（byte 量化 elev<hSea）且逻辑非陆地
    /// （R≤0 或无 civ）才判海；近海格（elev<hSea 但 R>0 逻辑可居）显示陆地/数据色——
    /// 人口点不落在"视觉海水"上（byte 量化误差——R>0 是模拟权威）。
    /// ⚠️ 2026-08-18：R 是逻辑格（顶点）数组——id 是显示格——按 _tileVerts[id] 查。</summary>
    private bool IsDisplaySea(int id)
        => _tileElev[id] < _hSea && (_civCtx?.R == null || _civCtx.R[_tileIndex.FaceToVertex(id)] <= 0f);

}
