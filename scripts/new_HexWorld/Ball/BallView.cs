using Godot;
using System;
using World.NewHexWorld.UI.ViewModels;
using World.Utils;
using World.Utils.H3;

namespace World.NewHexWorld
{
	// 球视图（View）：Ball 数据 + BallMesh 几何 + MeshInstance 渲染。
	// MVVM 接线（设计入口 §2.2/§2.4）：Bind(HexWorldViewModel) 订阅 Changed 信号 → 拉取
	// 逐格色投影缓存提交 GPU（板块边界描边权重随 COLOR.a 一并并入——描边是格面材质的一部分，
	// 无独立线几何）。View 只显示，不读/改 Model 的地壳场数组（颜色/边界角点集都经 VM 派生口）；
	// Ball 自身的静态网格几何（建面/拾取）属渲染基础设施，直读只读 Ball。
	// Model 构造权在组装器：Init(Ball) 下行注入，View 不自建数据。拾取 PickCell（屏幕点 → 球面
	// cell）。视角运动全交给 OrbitalCamera（拖转/缩放）——星球本身不自转（用户拍板 09-07）。
	public partial class BallView : Node3D
	{
		Ball _ball;                     // 数据层（组装器 Init 注入）
		BallMesh _mesh;                 // 几何层
		MeshInstance3D _meshInstance;   // 格色面（含描边）渲染产物
		float[] _outlineWeights;        // 逐显示顶点边界权重（1 内部 / 0 边界角点；建一次常驻）
		HexWorldViewModel _vm;          // 球视图 VM（Bind 注入；变更信号驱动刷新）

		// 组装器下行：注入 Model 并建几何/渲染节点。分辨率/半径以 Ball 为单一事实源
		// （_ball.Res / _ball.Radius），本类不再持参数导出。
		public void Init(Ball ball)
		{
			_ball = ball;
			_mesh = new BallMesh();
			_mesh.BuildTileMeshData(_ball, _ball.Radius);
			CreateMeshNode();
		}

		// ── MVVM 绑定：订阅 VM 变更信号 → 拉取刷新（首帧即渲染当前模式）──

		public void Bind(HexWorldViewModel vm)
		{
			_vm = vm;
			_vm.Changed += Refresh;
			Refresh();
		}

		// 拉取 VM 派生数据重提交：逐格色投影（全量重交 surface，描边开关随权重并入 alpha）。
		void Refresh()
		{
			if (_vm == null) return;
			SubmitColors(_vm.CellColors, _vm.ShowBoundaries);
		}

		// ── 渲染提交 ──

		// 建格色面节点（只建一次）：描边材质吃顶点色——rgb = 格色（linear）、a = 边界权重，
		// 片元按权重插值压暗格色成轮廓带（hex_tile_outline.gdshader；老树 planet_detail 同款方案）。
		void CreateMeshNode()
		{
			var mat = new ShaderMaterial
			{
				Shader = GD.Load<Shader>("res://shaders/hex_tile_outline.gdshader"),
			};
			_meshInstance = new MeshInstance3D { Mesh = new ArrayMesh(), MaterialOverride = mat };
			AddChild(_meshInstance);
		}

		// 按每格色投影重交格色面。ArrayMesh 提交后色数组不可局部改，只能 ClearSurfaces +
		// AddSurfaceFromArrays 全量重交（res3 一次 ~10ms 级；换模式低频，可接受）。顶点/索引复用不动。
		// showOutline = 当前模式叠加板块边界描边：权重懒构建一次后并入顶点色 alpha；
		// 关闭时 alpha 全 1（片元 smoothstep 恒 1 → 纯格色），同一几何零额外开销。
		// ⚠️ 3D 渲染 linear 工作空间：策略色为 sRGB 意图（色带/常量/2D swatch 同语义），提交前
		// 逐格转 linear——输出端 sRGB encode 还原，否则整球观感被提亮（2026-09-07 实测色偏修复）。
		void SubmitColors(Color[] perCellColors, bool showOutline)
		{
			if (showOutline && _outlineWeights == null)
				_outlineWeights = _mesh.BuildTileOutlineWeights(_ball, _vm.BoundaryCellVerts);
			float[] weights = showOutline ? _outlineWeights : null;
			var linear = new Color[perCellColors.Length];
			for (int i = 0; i < perCellColors.Length; i++) linear[i] = perCellColors[i].SrgbToLinear();
			Color[] colors = _mesh.BuildTileColors(_ball, linear, weights);
			var am = (ArrayMesh)_meshInstance.Mesh;
			am.ClearSurfaces();
			var arr = new Godot.Collections.Array();
			arr.Resize((int)Mesh.ArrayType.Max);
			arr[(int)Mesh.ArrayType.Vertex] = _mesh.DisplayVerts;
			arr[(int)Mesh.ArrayType.Color] = colors;
			arr[(int)Mesh.ArrayType.Index] = _mesh.DisplayIndices;
			am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
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
