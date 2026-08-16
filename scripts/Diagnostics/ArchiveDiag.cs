using Godot;
using System;
using System.Collections.Generic;
using World.HexPlanet;
using World.MapGen;
using World.Services;
using World.Tectonics;

namespace World.Diagnostics;

/// <summary>
/// 诊断依赖上下文（2026-08-04）：存档直读通用工具。
/// 模式：读存档 → 找缺少的依赖（存档有则直读，没有则从已有数据计算）→ 诊断脚本拿数据输出。
/// 依赖懒加载（首次访问计算并缓存），可灵活添加：内置依赖见下；自定义依赖用 Require(key, computer)。
/// 命令行：-- --arch=user://maps/xxx.mpa（--arch / arch= 亦可，省略值=默认 map1.mpa）。
/// 无 --arch 参数 → 诊断脚本保持原流程（向后兼容）。
/// </summary>
public sealed class DiagContext
{
    public readonly MapData Map;      // 原始存档（含全部尾部扩展字段）
    public readonly SphereGrid Grid;  // 与存档同拓扑重建网格（TectonicsSimulation 构造，秒级）
    private readonly Dictionary<string, object> _cache = new();

    public DiagContext(MapData map, SphereGrid grid) { Map = map; Grid = grid; }

    public int VertexCount => Grid.VertexCount;
    public Vector3[] Verts => Grid.Vertices;
    public int[][] Neighbors => Grid.Neighbors;

    /// <summary>通用依赖入口：已有缓存直接用，没有则 computer 计算后缓存——新依赖 = 一行 Require。</summary>
    public T Require<T>(string key, Func<T> computer)
    {
        if (_cache.TryGetValue(key, out var v)) return (T)v;
        var result = computer();
        _cache[key] = result;
        return result;
    }

    // ── 内置依赖（懒加载；全部按最近顶点从存档映射到网格，NaN→0）──

    /// <summary>归一化海拔（0=海平面，span=max|Elev|；与原模拟 (disp-sea)/span 同构）。</summary>
    public float[] ElevNorm => Require("elev_norm", MapElevNorm);

    /// <summary>绝对海拔（米，0=海平面）。</summary>
    public float[] ElevM => Require("elev_m", () => MapField((m, i) => m.Elev[i]));

    /// <summary>年降水 mm。</summary>
    public float[] Precip => Require("precip", () => MapField((m, i) => m.Precip[i]));

    /// <summary>年均温 °C。</summary>
    public float[] Temp => Require("temp", () => MapField((m, i) => m.Temp[i]));

    /// <summary>biome 类型。</summary>
    public byte[] Biome => Require("biome", () => MapFieldByte((m, i) => m.Biome[i]));

    /// <summary>存档海拔跨度（max|Elev|，NaN 过滤）。</summary>
    public float ElevSpan => Require("elev_span", () =>
    {
        float span = 0f;
        for (int i = 0; i < Map.Elev.Length; i++)
            if (!float.IsNaN(Map.Elev[i]))
                span = Mathf.Max(span, Mathf.Abs(Map.Elev[i]));
        return span > 1e-6f ? span : 1f;
    });

    // ── 计算器 ──

    private float[] MapElevNorm()
    {
        float span = ElevSpan;
        return MapField((m, i) => float.IsNaN(m.Elev[i]) ? 0f : m.Elev[i] / span);
    }

    private float[] MapField(Func<MapData, int, float> pick)
    {
        var result = new float[Grid.VertexCount];
        for (int i = 0; i < Grid.VertexCount; i++)
        {
            float v = pick(Map, Map.NearestVertex(Grid.Vertices[i]));
            result[i] = float.IsNaN(v) ? 0f : v;
        }
        return result;
    }

    private byte[] MapFieldByte(Func<MapData, int, byte> pick)
    {
        var result = new byte[Grid.VertexCount];
        for (int i = 0; i < Grid.VertexCount; i++)
            result[i] = pick(Map, Map.NearestVertex(Grid.Vertices[i]));
        return result;
    }
}

/// <summary>存档直读入口（静态工具）。</summary>
public static class ArchiveDiag
{
    /// <summary>解析 --arch 参数；返回存档路径（无 --arch 参数返回 null）。--arch 无值 = 默认 map1.mpa。
    /// 2026-08-19 迁移：统一走 DiagSceneBase.ParseUserArgs（兼容 --arch=X / --arch X / arch=X）。</summary>
    public static string ResolveArchPath()
    {
        var args = DiagSceneBase.ParseUserArgs();
        if (args.TryGetValue("arch", out var path))
            return path == "true" ? "user://maps/map1.mpa" : path;   // 裸 --arch → 默认档（旧语义）
        return null; // 未指定 → 调用方走原流程
    }

    /// <summary>读存档 + 重建同拓扑网格，包装为 DiagContext。失败打印并返回 false。</summary>
    public static bool TryLoad(string path, out DiagContext ctx)
    {
        ctx = null;
        if (!MapArchive.Read(path, out var map))
        {
            LogService.LogErr("ArchiveDiag", $"读取存档失败: {path}");
            return false;
        }
        // 存档 n 反推：顶点数 = 10n²+2（10242→32, 40962→64, 2562→16）
        int n = Icosahedron.GridNFromVertexCount(map.Verts.Length);
        var sim = new TectonicsSimulation(n);
        ctx = new DiagContext(map, sim.GlobalGrid);
        LogService.Log("ArchiveDiag", $"直读 {path} n={n} verts={map.Verts.Length}（跳板块模拟）");
        return true;
    }
}
