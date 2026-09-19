using Godot;
using System;
using System.Collections.Generic;
using World.NewHexWorld.Plate;               // H3Rivers（河流走廊，R3）
using World.NewHexWorld.UI.Modes;            // Elevation/TemperatureMapMode（色带烘焙源）
using World.NewHexWorld.UI.ViewModels;
using World.Render;                          // SphereLines（球面画线工具）
using World.Utils;
using static World.Utils.ColorRamp;          // RampSampleSmooth（画面与信息条同一色带源）
using World.Utils.H3;

namespace World.NewHexWorld
{
	// 球视图（View）：Ball 数据 + BallMesh 几何 + 单张全域材质渲染 + 板块边界线（World.Render.
	// SphereLines 球面画线工具）。MVVM 接线（设计入口 §2.2/§2.4）：Bind(HexWorldViewModel) 时
	// 一次性提交静态表面（顶点/索引 + UV=格纹素中心）与边界线几何（异板共享边换算成世界坐标点串
	// 喂 SphereLines，线带/端帽与两遍深度预写渲染都在工具内——自重叠每像素只混色一次，半透明线
	// 也不叠加变深），并把 VM 派生的逐格区域数据烘成 region_data 纹理；此后海拔/板块取色在片元侧
	// 派生，模式切换换 uniform、描边开关切线可见性——零几何重交。
	// View 只显示，不读/改 Model 的地壳场数组（区域数据/边界边集都经 VM 派生口）；Ball 自身的静态
	// 网格几何（建面/拾取）属渲染基础设施，直读只读 Ball。拾取 PickCell（屏幕点 → 球面 cell）。
	// 视角运动全交 OrbitalCamera（拖转/缩放）——星球本身不自转。
	public partial class BallView : Node3D
	{
		Ball _ball;                     // 数据层（组装器 Init 注入）
		BallMesh _mesh;                 // 几何层
		MeshInstance3D _meshInstance;   // 格面渲染产物（单表面一次提交，之后只换 uniform）
		ShaderMaterial _material;       // 全域材质（sphere_region_material.gdshader）
		SphereLines _lines;             // 板块边界线（球面画线工具，建一次常驻，开关切可见性）
		Color _outlineColor = Colors.Black;   // 线色（Init 注入；透明度任意——深度预写不叠加变深）
		float _lineWidthFrac;           // 线宽系数（Init 注入；线半宽 = × ρ × R，Bind 时换算）
		HexWorldViewModel _vm;          // 球视图 VM（Bind 注入；变更信号驱动 uniform 刷新）

		// 组装器下行：注入 Model 与线宽/线色旋钮并建几何/渲染节点。分辨率/半径以 Ball 为单一事实源
		// （_ball.Res / _ball.Radius），本类不再持参数导出。
		public void Init(Ball ball, float lineWidthFrac = 0.18f, Color? outlineColor = null)
		{
			_ball = ball;
			_lineWidthFrac = lineWidthFrac;
			_mesh = new BallMesh();
			if (outlineColor.HasValue) _outlineColor = outlineColor.Value;
			_mesh.ComputeCellMetrics(ball);           // 度量先行（线半宽 = _lineWidthFrac × ρ × R）
			_mesh.BuildTileMeshData(ball, _ball.Radius);
			CreateMeshNode();
		}

		// ── MVVM 绑定：一次性提交静态表面 + 边界线带 + 烘区域数据纹理；此后订阅变更信号 ──

		public void Bind(HexWorldViewModel vm)
		{
			_vm = vm;
			RebuildMeshWithRiverCorridors(vm.RiverCorridors);   // 河流走廊细分（生成后一次；无河则保持基网格）
			SubmitSurface();
			SubmitBoundaryLines(vm.BoundaryVertexEdges);   // 边界线几何（点串带 + 端帽，建一次常驻）
			_material.SetShaderParameter("region_data", BuildRegionDataTexture());
			_material.SetShaderParameter("region_data2", BuildRegionData2Texture());
			_material.SetShaderParameter("elevation_ramp", BakeRampTexture(ElevationMapMode.ElevationStops));
			_material.SetShaderParameter("temperature_ramp", BakeRampTexture(TemperatureMapMode.TemperatureStops));
			_material.SetShaderParameter("precipitation_ramp", BakeRampTexture(PrecipitationMapMode.PrecipStops));
			_vm.Changed += Refresh;
			Refresh();
		}

		// 河流走廊细分：走廊格的显示面替换为 res+1 子格面、河格走河流材质——
		// Init 期网格是基形态（生成未跑），Bind 时账本已定局，有走廊才重建重提交（静态世界一次）。
		void RebuildMeshWithRiverCorridors(H3Rivers.Corridors corridors)
		{
			if (corridors == null || corridors.ChildCount == 0) return;
			_mesh.BuildTileMeshData(_ball, _ball.Radius, corridors);
			((ArrayMesh)_meshInstance.Mesh).ClearSurfaces();
		}

		// 拉取 VM 显示状态 → uniform / 可见性（模式切换/描边开关只走这里；静态世界无重交）。
		void Refresh()
		{
			if (_vm == null) return;
			_material.SetShaderParameter("display_mode", _vm.DisplayMode);
			_lines.Visible = _vm.ShowBoundaries;
		}

		// ── 渲染提交（一次性）──

		// 建格面 + 边界线两节点（只建一次）：格面 = 全域材质（sphere_region_material + 逐格数据
		// 纹理区域查找）；边界线 = World.Render.SphereLines（线带/端帽几何与两遍深度预写渲染、
		// render_priority 钉序都在工具内，见该类）。抗锯齿走视口 MSAA（project.godot msaa_3d = 4x）。
		void CreateMeshNode()
		{
			_material = new ShaderMaterial
			{
				Shader = GD.Load<Shader>("res://shaders/sphere_region_material.gdshader"),
			};
			_meshInstance = new MeshInstance3D { Mesh = new ArrayMesh(), MaterialOverride = _material };
			AddChild(_meshInstance);

			_lines = new SphereLines { LineColor = _outlineColor };
			AddChild(_lines);
		}

		// 静态表面一次提交：顶点/索引 + UV（格纹素中心 = region_data 查找地址）。
		// Bind 可能带走廊重建过几何 → 先清旧面再加（幂等）。
		void SubmitSurface()
		{
			var am = (ArrayMesh)_meshInstance.Mesh;
			am.ClearSurfaces();
			var arr = new Godot.Collections.Array();
			arr.Resize((int)Mesh.ArrayType.Max);
			arr[(int)Mesh.ArrayType.Vertex] = _mesh.DisplayVerts;
			arr[(int)Mesh.ArrayType.TexUV] = _mesh.DisplayUv;
			arr[(int)Mesh.ArrayType.Index] = _mesh.DisplayIndices;
			am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
		}

		// 边界线一次提交：异板共享边顶点对 → 世界坐标两点串喂 SphereLines（线带/端帽几何由工具
		// 生成，建一次常驻）。换算口径：线半宽 = _lineWidthFrac × ρ × R、细分步长 =
		// 0.4 × ρ × R（格度量 ρ 角量 × 球半径 → 世界单位）。
		void SubmitBoundaryLines(IReadOnlyList<(ulong va, ulong vb)> edges)
		{
			float rho = _mesh.MeanInradius;
			float halfWidth = _lineWidthFrac * rho * _ball.Radius;
			var strips = new Vector3[edges.Count][];
			for (int i = 0; i < edges.Count; i++)
			{
				(ulong va, ulong vb) = edges[i];
				strips[i] = new[] { _ball.VertexPositions[_ball.VertexIndexOf(va)],
					_ball.VertexPositions[_ball.VertexIndexOf(vb)] };
			}
			_lines.SetLines(strips, _ball.Radius, halfWidth, 0.4f * rho * _ball.Radius);
		}

		// 逐格区域数据纹理（R=海拔归一 / G=板号色相 / B=陆1洋0 / A=温度归一；布局 =
		// 基格纹素 + 走廊子格纹素，尺寸由 _mesh 提供）。子格纹素场值从父格复制（子格是父格的
		// 细分显示，数据同源——统一判陆口不动）。
		Texture2D BuildRegionDataTexture()
		{
			int n = _vm.CellCount, w = _mesh.DataTexW, h = _mesh.DataTexH;
			var bytes = new byte[w * h * 4];
			float elevMin = ElevationMapMode.ElevationStops[0].Pos;
			float elevSpan = ElevationMapMode.ElevationStops[^1].Pos - elevMin;
			float tempMin = TemperatureMapMode.TemperatureStops[0].Pos;
			float tempSpan = TemperatureMapMode.TemperatureStops[^1].Pos - tempMin;
			var elevs = _vm.CellElevations;
			var plateIds = _vm.CellPlateIds;
			var temps = _vm.CellTemperatures;
			for (int i = 0; i < n; i++)
			{
				int o = i * 4;
				bytes[o] = (byte)Math.Round(Math.Clamp((elevs[i] - elevMin) / elevSpan, 0f, 1f) * 255f);
				bytes[o + 1] = (byte)Math.Round(plateIds[i] * 255f / _vm.PlateCount);   // 板号 → 色相归一（片元侧还原板色）
				bytes[o + 2] = _vm.IsLand(i) ? (byte)255 : (byte)0;                 // 统一判陆口（预留分区域材质位）
				bytes[o + 3] = (byte)Math.Round(Math.Clamp((temps[i] - tempMin) / tempSpan, 0f, 1f) * 255f);   // 温度 → 归一（片元侧查温度色带）
			}
			CopyChildTexels(bytes, writeRiver: false);
			return ImageTexture.CreateFromImage(Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes));
		}

		// 第二张逐格区域数据纹理（region_data A 通道已被温度占用；R=降水归一（陆地 min-max
		// 自适应域），G=河流档 0..3（仅走廊子格非零，shader 河流材质用），B/A 备用）。
		Texture2D BuildRegionData2Texture()
		{
			int n = _vm.CellCount, w = _mesh.DataTexW, h = _mesh.DataTexH;
			var bytes = new byte[w * h * 4];
			(float pMin, float pMax) = _vm.PrecipLandRange;
			var precip = _vm.CellPrecipMmYear;
			for (int i = 0; i < n; i++)
			{
				int o = i * 4;
				bytes[o] = (byte)Math.Round(PrecipitationMapMode.Normalize(precip[i], pMin, pMax) * 255f);
				bytes[o + 1] = 0;   // 河档只写走廊子格
				bytes[o + 2] = 0;
				bytes[o + 3] = 255;
			}
			CopyChildTexels(bytes, writeRiver: true);
			return ImageTexture.CreateFromImage(Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes));
		}

		// 走廊子格纹素 = 父格纹素复制（场值同源）；region_data2 的 G 通道写河档（0..3 → 0/85/170/255）。
		void CopyChildTexels(byte[] bytes, bool writeRiver)
		{
			int n = _vm.CellCount;
			var parents = _mesh.CorridorChildParent;
			var grades = _mesh.CorridorChildGrade;
			for (int e = 0; e < parents.Length; e++)
			{
				int child = (n + e) * 4, parent = parents[e] * 4;
				for (int k = 0; k < 4; k++) bytes[child + k] = bytes[parent + k];
				if (writeRiver) bytes[child + 1] = (byte)Math.Round(grades[e] * (255f / 3f));
			}
		}

		// 色带纹理烘焙（256×1，linear）：海拔/温度/降水三模式共用——与信息面板同一 RampSampleSmooth
		// 烘焙 → 画面与信息条取色天然同源（sRGB→linear）。采点 = 纹素中心。
		Texture2D BakeRampTexture(ColorStop[] stops)
		{
			const int ramp = 256;
			float vMin = stops[0].Pos, span = stops[^1].Pos - vMin;
			var bytes = new byte[ramp * 4];
			for (int i = 0; i < ramp; i++)
			{
				Color c = RampSampleSmooth(stops, vMin + span * (i + 0.5f) / ramp).SrgbToLinear();
				int o = i * 4;
				bytes[o] = (byte)Math.Round(c.R * 255f);
				bytes[o + 1] = (byte)Math.Round(c.G * 255f);
				bytes[o + 2] = (byte)Math.Round(c.B * 255f);
				bytes[o + 3] = 255;
			}
			return ImageTexture.CreateFromImage(Image.CreateFromData(ramp, 1, false, Image.Format.Rgba8, bytes));
		}

	// ── 拾取 ──

		// 拾取：屏幕点 → 相机射线 → 变换进 mesh 局部系（星球不自转后该变换为恒等，
		// 保留以兼容未来节点级变换）→ 与球求最近正交点 → 交点方向 = 单位球坐标 → LatLngToCell。
		// 返回命中格 id；未命中（射线不交球 / 点在球后）返回 null。
		public ulong? PickCell(Vector2 screenPos, Camera3D camera)
		{
			var toLocal = _meshInstance.GlobalTransform.AffineInverse();   // 世界 → mesh 局部
			Vector3 o = toLocal * camera.ProjectRayOrigin(screenPos);      // 局部系射线原点
			Vector3 d = (toLocal.Basis * camera.ProjectRayNormal(screenPos)).Normalized();  // 局部系射线方向

			// 球心在局部原点、半径 R：|o + t·d|² = R² → t² + 2(o·d)t + (o·o−R²) = 0
			float b = o.Dot(d);
			float c = o.Dot(o) - _ball.Radius * _ball.Radius;
			float disc = b * b - c;
			if (disc < 0f) return null;               // 射线不交球
			float t = -b - Mathf.Sqrt(disc);          // 最近交点（正面）
			if (t < 0f) return null;                  // 球在相机背后

			Vector3 hit = o + d * t;
			Vector3 dir = hit / _ball.Radius;         // 单位球方向（数据层坐标系的经纬方向）
			double lat = Math.Asin(Mathf.Clamp(dir.Y, -1f, 1f));   // 弧度（门面 LatLng 与 h3api 一致）
			double lng = Math.Atan2(dir.Z, dir.X);
			return H3.LatLngToCell(new LatLng(lat, lng), _ball.Res);
		}
	}
}
