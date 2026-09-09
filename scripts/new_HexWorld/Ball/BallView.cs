using Godot;
using System;
using World.NewHexWorld.UI.Modes;            // ElevationMapMode（海拔色带烘焙源）
using World.NewHexWorld.UI.ViewModels;
using World.Utils;
using static World.Utils.ColorRamp;          // RampSampleSmooth（画面与信息条同一色带源）
using World.Utils.H3;

namespace World.NewHexWorld
{
	// 球视图（View）：Ball 数据 + BallMesh 几何 + 单张全域材质渲染（2026-09-09 材质覆盖方案）。
	// MVVM 接线（设计入口 §2.2/§2.4）：Bind(HexWorldViewModel) 时一次性提交静态表面（顶点/索引 +
	// UV=格纹素中心 + UV2=边距权重/边界旗），并把 VM 派生的逐格区域数据烘成 region_data 纹理；此后
	// 海拔/板块取色全在片元侧按数据纹理派生，模式切换只改材质 uniform（display_mode/outline_enable）
	// ——零几何重交、零 CPU 逐格取色（旧逐格顶点色方案作废）。View 只显示，不读/改 Model 的地壳场
	// 数组（区域数据/边界格边集都经 VM 派生口）；Ball 自身的静态网格几何（建面/拾取）属渲染基础
	// 设施，直读只读 Ball。拾取 PickCell（屏幕点 → 球面 cell）。视角运动全交 OrbitalCamera
	// （拖转/缩放）——星球本身不自转（用户拍板 09-07）。
	public partial class BallView : Node3D
	{
		Ball _ball;                     // 数据层（组装器 Init 注入）
		BallMesh _mesh;                 // 几何层
		MeshInstance3D _meshInstance;   // 格面渲染产物（单表面一次提交，之后只换 uniform）
		ShaderMaterial _material;       // 全域材质（sphere_region_material.gdshader）
		HexWorldViewModel _vm;          // 球视图 VM（Bind 注入；变更信号驱动 uniform 刷新）

		// 组装器下行：注入 Model 并建几何/渲染节点。分辨率/半径以 Ball 为单一事实源
		// （_ball.Res / _ball.Radius），本类不再持参数导出。
		public void Init(Ball ball)
		{
			_ball = ball;
			_mesh = new BallMesh();
			_mesh.BuildTileMeshData(_ball, _ball.Radius);
			CreateMeshNode();
		}

		// ── MVVM 绑定：一次性提交静态表面 + 烘区域数据纹理；此后订阅变更信号换 uniform ──

		public void Bind(HexWorldViewModel vm)
		{
			_vm = vm;
			// 描边旗（区域静态 → 建一次常驻）：每条异板共享边在两侧格名下记旗 1（UV2.y），
			// 边粒度记账——同板边即使两端角点都贴邻边界边也绝不被误描（2026-09-09 修）
			_mesh.BuildTileEdgeFlags(_ball, vm.BoundaryCellEdges);
			SubmitSurface();
			_material.SetShaderParameter("region_data", BuildRegionDataTexture());
			_material.SetShaderParameter("elevation_ramp", BuildElevationRampTexture());
			_vm.Changed += Refresh;
			Refresh();
		}

		// 拉取 VM 显示状态 → 材质 uniform（模式切换/描边开关只走这里；静态世界无重交）。
		void Refresh()
		{
			if (_vm == null) return;
			_material.SetShaderParameter("display_mode", _vm.DisplayMode);
			_material.SetShaderParameter("outline_enable", _vm.ShowBoundaries ? 1f : 0f);
		}

		// ── 渲染提交（一次性）──

		// 建格面节点（只建一次）：全域材质 = sphere_region_material + 逐格数据纹理区域查找。
		void CreateMeshNode()
		{
			_material = new ShaderMaterial
			{
				Shader = GD.Load<Shader>("res://shaders/sphere_region_material.gdshader"),
			};
			_meshInstance = new MeshInstance3D { Mesh = new ArrayMesh(), MaterialOverride = _material };
			AddChild(_meshInstance);
		}

		// 静态表面一次提交：顶点/索引 + UV（格纹素中心 = region_data 查找地址）+ UV2（边距/边旗）+
		// COLOR.r/g（角旗，拐角帽用）。
		// 几何常驻不再动；取色移到片元侧按数据纹理派生。
		void SubmitSurface()
		{
			var am = (ArrayMesh)_meshInstance.Mesh;
			var arr = new Godot.Collections.Array();
			arr.Resize((int)Mesh.ArrayType.Max);
			arr[(int)Mesh.ArrayType.Vertex] = _mesh.DisplayVerts;
			arr[(int)Mesh.ArrayType.TexUV] = _mesh.DisplayUv;
			arr[(int)Mesh.ArrayType.TexUV2] = _mesh.DisplayUv2;
			arr[(int)Mesh.ArrayType.Color] = _mesh.DisplayCornerFlags;
			arr[(int)Mesh.ArrayType.Index] = _mesh.DisplayIndices;
			am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
		}

		// 逐格区域数据纹理（每格 1 纹素；R=海拔归一 / G=板号色相 / B=陆1洋0 / A 备用；布局 =
		// BallMesh.DataTexWidth，与 UV 填充同源）。数据经 VM 派生口（Crust 场引用，静态生成后只读）；
		// 海拔归一域 = 色带停点首尾 Pos（与色带同源 → 改色带无需动这里）。8 bit/通道对本阶段两级
		// 常量绰绰有余；将来要素生成器加连续起伏后换 Rf/Rgb16f 通道即可（纹素布局不变）。
		Texture2D BuildRegionDataTexture()
		{
			int n = _vm.CellCount;
			int w = BallMesh.DataTexWidth(n), h = (n + w - 1) / w;
			float elevMin = ElevationMapMode.ElevationStops[0].Pos;
			float span = ElevationMapMode.ElevationStops[^1].Pos - elevMin;
			int plateCount = _vm.PlateCount;
			var elevs = _vm.CellElevations;
			var plateIds = _vm.CellPlateIds;
			var bytes = new byte[w * h * 4];
			for (int i = 0; i < n; i++)
			{
				int o = i * 4;
				bytes[o] = (byte)Math.Round(Math.Clamp((elevs[i] - elevMin) / span, 0f, 1f) * 255f);
				bytes[o + 1] = (byte)Math.Round(plateIds[i] * 255f / plateCount);   // 板号 → 色相归一（片元侧还原板色）
				bytes[o + 2] = _vm.IsLand(i) ? (byte)255 : (byte)0;                 // 统一判陆口（预留分区域材质位）
				bytes[o + 3] = 255;
			}
			return ImageTexture.CreateFromImage(Image.CreateFromData(w, h, false, Image.Format.Rgba8, bytes));
		}

		// 海拔色带纹理（256×1，linear）：与信息面板同一 RampSampleSmooth(ElevationStops) 烘焙 →
		// 画面与信息条取色天然同源（旧 CPU 投影同款 sRGB→linear 纪律）。采点 = 纹素中心——
		// 0m 硬台阶落最近纹素界（~55 m 量级偏差，两级常量数据下不可见）。
		Texture2D BuildElevationRampTexture()
		{
			const int ramp = 256;
			var stops = ElevationMapMode.ElevationStops;
			float elevMin = stops[0].Pos, span = stops[^1].Pos - elevMin;
			var bytes = new byte[ramp * 4];
			for (int i = 0; i < ramp; i++)
			{
				Color c = RampSampleSmooth(stops, elevMin + span * (i + 0.5f) / ramp).SrgbToLinear();
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
