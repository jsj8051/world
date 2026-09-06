using Godot;
using System;
using World.Utils;
using World.Utils.H3;

namespace World.NewHexWorld
{
	// 球视图：Ball 数据 + BallMesh 几何 + MeshInstance 渲染，自转展示。
	// 换配色 ApplyColorScheme（地图模式/内容更新）；拾取 PickCell（屏幕点 → 球面 cell）。
	public partial class BallView : Node3D
	{
		[Export] public int ResLevel = 3;
		[Export] public float Radius = 1f;
		[Export] public float SpinSpeed = 0.3f;     // 自转速度（弧度/秒）

		Ball _ball;                     // 数据层（_Ready 构建）
		BallMesh _mesh;                 // 几何层
		MeshInstance3D _meshInstance;   // 渲染产物

		// 数据层访问器（内容层 H3Plate / 编排 Manager 拿网格数据用）。
		public Ball BallData => _ball;

		public override void _Ready()
		{
			_ball = new Ball(ResLevel, Radius);
			_mesh = new BallMesh();
			_mesh.BuildTileMeshData(_ball, Radius);
			CreateMeshNode();
			ApplyColorScheme(DebugBaseColorScheme);   // 初始调试配色：按基底格分色
		}

		public override void _Process(double delta)
		{
			_meshInstance?.RotateY((float)delta * SpinSpeed);   // 自转看全表面
		}

		// 建渲染节点（只建一次）：无光材质吃顶点色——球面格显示不需要光照。
		private void CreateMeshNode()
		{
			var mat = new StandardMaterial3D
			{
				ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
				VertexColorUseAsAlbedo = true,
			};
			_meshInstance = new MeshInstance3D { Mesh = new ArrayMesh(), MaterialOverride = mat };
			AddChild(_meshInstance);
		}

		/// <summary>应用一套每格配色方案：按 scheme(cell id) 生成颜色 → 清旧 surface → 重提交。
		/// ArrayMesh 提交后色数组不可局部改，只能 ClearSurfaces + AddSurfaceFromArrays 全量重交
		/// （res3 一次 ~10ms 级；换配色是低频操作，可接受）。顶点/索引数组复用不动。</summary>
		public void ApplyColorScheme(Func<ulong, Color> scheme)
		{
			Color[] colors = _mesh.BuildTileColors(_ball, scheme);
			var am = (ArrayMesh)_meshInstance.Mesh;
			am.ClearSurfaces();
			var arr = new Godot.Collections.Array();
			arr.Resize((int)Mesh.ArrayType.Max);
			arr[(int)Mesh.ArrayType.Vertex] = _mesh.DisplayVerts;
			arr[(int)Mesh.ArrayType.Color] = colors;
			arr[(int)Mesh.ArrayType.Index] = _mesh.DisplayIndices;
			am.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
		}

		// 默认调试配色：122 基底格族分色（看清格型与五边形位置）。
		private static Color DebugBaseColorScheme(ulong cell)
			=> Color.FromHsv(H3.GetBaseCellNumber(cell) / 122f, 0.8f, 0.9f);

		// 拾取：屏幕点 → 相机射线 → 变换进 mesh 局部系（自转在 mesh 上，必须消除旋转
		// 才能对上数据层坐标）→ 与球求最近正交点 → 交点方向 = 单位球坐标 → LatLngToCell。
		// 返回命中格 id；未命中（射线不交球 / 点在球后）返回 null。
		public ulong? PickCell(Vector2 screenPos, Camera3D camera)
		{
			var toLocal = _meshInstance.GlobalTransform.AffineInverse();   // 世界 → mesh 局部
			Vector3 o = toLocal * camera.ProjectRayOrigin(screenPos);      // 局部系射线原点
			Vector3 d = (toLocal.Basis * camera.ProjectRayNormal(screenPos)).Normalized();  // 局部系射线方向

			// 球心在局部原点、半径 Radius：|o + t·d|² = R² → t² + 2(o·d)t + (o·o−R²) = 0
			float b = o.Dot(d);
			float c = o.Dot(o) - Radius * Radius;
			float disc = b * b - c;
			if (disc < 0f) return null;               // 射线不交球
			float t = -b - Mathf.Sqrt(disc);          // 最近交点（正面）
			if (t < 0f) return null;                  // 球在相机背后

			Vector3 hit = o + d * t;
			Vector3 dir = hit / Radius;               // 单位球方向（数据层坐标系的经纬方向）
			double lat = Math.Asin(Mathf.Clamp(dir.Y, -1f, 1f));   // 弧度（门面 LatLng 与 h3api 一致）
			double lng = Math.Atan2(dir.Z, dir.X);
			return H3.LatLngToCell(new LatLng(lat, lng), ResLevel);
		}
	}
}
