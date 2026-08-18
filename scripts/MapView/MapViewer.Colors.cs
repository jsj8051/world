// Slice: MapViewer.Colors.cs - verbatim member extraction from MapViewer.cs (pure refactor, 2026-08-19).
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
using World.Services;
using World.Surface;
using World.UI;
using static World.MapView.MapLayerColors;

namespace World.MapView;

public partial class MapViewer
{

    /// <summary>图层 → 颜色函数（查预计算缓存，零采样）。</summary>
    /// ⚠️ 2026-08-02 大改进：参数化 layer（不读共享字段）——原内部 switch(Layer) 在后台
    ///   线程每次调用读 _layer，主线程切图层写它 → 竞态 → 偶发颜色错图层/"未切换成功"。</summary>
    /// <summary>图层 → 颜色函数（2026-08-21 M3 策略化：原 20 分支 switch 删除——查策略注册表）。
    /// ⚠️ 2026-08-02 大改进保留：参数化 layer（不读共享字段）——原内部 switch(Layer) 在后台
    ///   线程每次调用读 _layer，主线程切图层写它 → 竞态 → 偶发颜色错图层/"未切换成功"。
    ///   策略 ColorOf 只读 ctx（构建后填充，只读共享），无竞态。</summary>
    private Func<HexTile, Color> MakeColorFn(int layer)
    {
        var strat = LayerRegistry.Of(layer);
        return t => strat.ColorOf(_ctx, t);
    }

    /// <summary>切图层：几何缓存命中 → 只重算颜色（查表，秒级）；无缓存（首次/GridN 刚变）→ 全量。
    /// ⚠️ 2026-08-02：几何未就绪时【禁止】调用 Generate()——构建中切图层会取消当前构建并重启，
    ///   快速连点=无限取消重启，几何永远构建不完 → 图层不切换。改为设置 _pendingRecolor，
    ///   等当前构建完成（FinishGenerate）后自动应用最新图层。</summary>
    private void RebuildColors()
    {
        if (!_geometryReady || _tiles == null)
        {
            _pendingRecolor = true;   // 构建完成后自动重算颜色（用最新 Layer）
            LogService.Log("MapViewer", $"RebuildColors: 几何未就绪 → pendingRecolor（Layer={_layer}）");
            return;
        }

        int version = ++_buildVersion;
        int layer = _layer;   // ⚠️ 主线程快照（后台只读快照，不碰共享字段）
        _cts?.Cancel();   // 取消旧重算任务
        _cts = new System.Threading.CancellationTokenSource();
        var token = _cts.Token;
        _progress = 0f;
        _phase = "重算颜色";
        ShowProgress();
        LogService.Log("MapViewer", $"RebuildColors: v{version} Layer={layer} 启动后台着色");
        _buildTask = Task.Run(() => BuildColorsTask(_map, version, token, layer), token);
        _buildTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
                // 后台线程回调：LogService 纪律禁止，保持 GD.Print 直调（ADR-0004 §决策4）
                GD.PrintErr($"[MapViewer] recolor failed: {t.Exception?.GetBaseException().Message}\n{t.Exception?.GetBaseException().StackTrace}");
            CallDeferred(nameof(FinishGenerate), version);
        });
    }


    /// <summary>后台线程：只重算颜色（查预计算缓存，零采样）。
    /// ️ 2026-08-02 大改进：layer 参数化快照——后台不读 _layer 字段（消除竞态）；
    ///   进度回调查 token，取消可中断（旧任务快速让位新图层）。</summary>
    private MeshData BuildColorsTask(MapData map, int version, System.Threading.CancellationToken token, int layer)
    {
        var geometry = _geometry; // 已就绪（_geometryReady 保证，不碰 Godot 对象）
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Color[] colors;
        // ⚠️ 2026-08-20：独立势力图层（14）用带边界 A 通道的颜色构建
        if (LayerRegistry.Of(layer).NeedsPowerBorders && _cache.TilePower != null)
        {
            colors = ChunkMeshBuilder.BuildColorsWithPowerBorders(_tiles, MakeColorFn(layer), geometry,
                _cache.TilePower,
                p =>
                {
                    if (token.IsCancellationRequested)
                        throw new OperationCanceledException(token);
                    _progress = 0.05f + p * 0.9f;
                });
        }
        else
        {
            colors = ChunkMeshBuilder.BuildColors(_tiles, MakeColorFn(layer), geometry,
                p =>
                {
                    if (token.IsCancellationRequested)
                        throw new OperationCanceledException(token);   // 取消中断（快速让位）
                    _progress = 0.05f + p * 0.9f;
                });
        }
        if (token.IsCancellationRequested) return default;
        _progress = 1f;
        return new MeshData
        {
            Verts = geometry.Verts,
            Normals = geometry.Normals,
            Colors = colors,
            Indices = geometry.Indices
        };
    }


    /// <summary>独立势力/族系取色（2026-08-21 M2：实现已迁移至 MapLayerColors.PowerColor / FamilyColor，
    /// using static 后本文件内的调用自动解析到新位置；原实现历史注释见 MapLayerColors.cs）。</summary>

    /// <summary>按钮图标（2026-08-21 M4：SVG 随策略走——IconSvg 属性；顺带修复聚落按钮越界）。</summary>
    private static Texture2D MakeLayerIcon(int idx)
    {
        try
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(LayerRegistry.All[idx].IconSvg);
            var img = new Image();   // LoadSvgFromBuffer 是实例方法（返回 Error）
            if (img.LoadSvgFromBuffer(bytes) != Error.Ok)
            {
                LogService.LogErr("MapViewer", $"SVG icon {idx} load failed");
                return null;
            }
            img.Resize(28, 28, Image.Interpolation.Bilinear);
            return ImageTexture.CreateFromImage(img);
        }
        catch (System.Exception e)
        {
            LogService.LogErr("MapViewer", $"SVG icon {idx} failed: {e.Message}");
            return null;
        }
    }

}
