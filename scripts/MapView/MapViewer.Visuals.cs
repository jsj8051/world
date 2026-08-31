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
using World.Services;
using World.Surface;
using World.UI;
using static World.MapView.MapLayerColors;

namespace World.MapView;

public partial class MapViewer
{
	/// <summary>懒算季风月风场（读档后第一次进风场/月降水/月温度图层时算一次；不存档）。
	/// 2026-08-21 M3：internal——WindLayer.BuildOverlay 经 host 触发。</summary>
	internal void EnsureMonthWind()
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
				// 后台线程回调：LogService 纪律禁止，保持 GD.Print 直调（ADR-0004 §决策4）
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
		// ⚠️ 2026-08-21 M3：同步策略上下文引用（构建时填充 _ctx.MonthWind 为 null——可变引用需手动同步）
		if (_ctx != null) _ctx.MonthWind = mw;
		LogService.Log("MapViewer", $"季风月风场重算完成（{_map?.Verts.Length} 顶点，倾角 {_map?.AxialTilt}°）");
		// 若当前层是覆盖层（风场异步完成前可能已跳过）→ 补建（M3：EnsureOverlayFor 幂等）
		if (LayerRegistry.Of(Layer).HasOverlay)
			EnsureOverlayFor(Layer);
	}


	// ═══════════════════════ 选国选中叠加（2026-08-31；NationSelect 金网格——跨图层常显）═══════════════════════

	private MeshInstance3D _selectionOverlay;   // 选国金网格（跨图层可见；SetSelection 重建）

	/// <summary>重建选国选中叠加：选中政权领地格金色描面（Corners 多边形，浮高 OverlayLiftFactor）。
	/// 与政体层压暗互补：覆盖网格任意图层可见（方案 P3-A），政体层下额外整层压暗由 PolityLayer 读 SelectionId 负责。</summary>
	private void RebuildSelectionOverlay()
	{
		if (_selectionOverlay != null) { _selectionOverlay.QueueFree(); _selectionOverlay = null; }
		if (_ctx == null || _ctx.SelectionId < 0 || _tiles == null || _cache == null) return;
		// 收集选中领地格（TilePower==SelectionId——SelectionId 已由 SetSelection 转势力域 Id）
		var st = new Color(0.98f, 0.82f, 0.30f, 0.85f);
		var verts = new System.Collections.Generic.List<Vector3>();
		var indices = new System.Collections.Generic.List<int>();
		int tileCount = 0;
		for (int i = 0; i < _tiles.Count; i++)
		{
			if (_cache.TilePower[i] != _ctx.SelectionId) continue;
			var c = _tiles[i].Corners;
			if (c == null || c.Length < 3) continue;
			int baseIdx = verts.Count;
			for (int k = 0; k < c.Length; k++) verts.Add(c[k] * OverlayLiftFactor);
			for (int k = 1; k < c.Length - 1; k++) { indices.Add(baseIdx); indices.Add(baseIdx + k); indices.Add(baseIdx + k + 1); }
			tileCount++;
		}
		if (verts.Count == 0) return;
		var arr = new Godot.Collections.Array();
		arr.Resize((int)Mesh.ArrayType.Max);
		arr[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
		arr[(int)Mesh.ArrayType.Index] = indices.ToArray();
		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
		var mat = new StandardMaterial3D
		{
			AlbedoColor = st,
			Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			CullMode = BaseMaterial3D.CullModeEnum.Disabled,   // 双面——背面观测也不漏
		};
		_selectionOverlay = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat };
		AddChild(_selectionOverlay);
		LogService.Log("MapViewer", $"selection overlay: {tileCount} 格 / {verts.Count} 顶点（政权 #{_ctx.SelectionId}）");
	}


}
