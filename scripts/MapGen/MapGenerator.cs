using Godot;
using System;
using System.Threading.Tasks;
using World.Biome;
using World.Surface;
using World.Tectonics;

namespace World.MapGen;

/// <summary>
/// 地图生成器（生成阶段，离线）：海拔场（板块构造） + 气候场（温度/降水）→ 生物群系 →
/// 球面顶点存档（v3，无投影无平面中转）。
///
/// 生成与游玩解耦：生成可花费数十分钟，产出存档；游玩只读存档。
///
/// 2026-08-16：板块生成（SotE 模式）已推倒删除。
/// 2026-08-02：接入 tectonics.js 移植的球面板块模拟（M1-M3 全部完成），
///   海拔/气候/biome 全部计算在球面顶点上，直接存 v3 球面存档（无平面中转）。
/// </summary>
public partial class MapGenerator : Node
{
	[Export] public int Seed = 42;
	[Export] public float RadiusKm = 6330f;
	[Export] public string OutputPath = "user://maps/map1.mpa";
	[Export] public bool AutoQuit = false; // true=生成后退出；false=切到查看场景
	[Export] public bool ExportPreview = true; // 生成后导出海拔预览 PNG（headless 调参可视化）
	[Export] public int TectonicsGridN = 32;   // 板块模拟 Icosahedron 细分（32→10242 顶点）
	[Export] public float SimMegayears = 600f; // 板块模拟时长（百万年）
	[Export] public float SimStepMy = 2f;      // 模拟时间步（百万年）
	[Export] public int NumPlates = 8;         // 初始板块数

	public override void _Ready()
	{
		// headless 调参支持：-- --seed=7 / -- --seed 7 / --seed=7 覆盖 [Export]
		// 支持：seed/TectonicsGridN/SimMegayears/NumPlates/AutoQuit/OutputPath
		var ua = OS.GetCmdlineUserArgs();
		for (int i = 0; i < ua.Length; i++)
		{
			string a = ua[i];
			string v = a.StartsWith("--") ? a.Substring(2) : a; // 兼容 --seed=X 与 seed=X
			bool TryInt(string key, Action<int> set)
			{
				if (v.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
					&& int.TryParse(v.AsSpan(key.Length + 1), out int val)) { set(val); return true; }
				if ((v == "--" + key || v == key) && i + 1 < ua.Length
					&& int.TryParse(ua[i + 1], out int val2)) { set(val2); i++; return true; }
				return false;
			}
			bool TryFloat(string key, Action<float> set)
			{
				if (v.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)
					&& float.TryParse(v.AsSpan(key.Length + 1), out float val)) { set(val); return true; }
				if ((v == "--" + key || v == key) && i + 1 < ua.Length
					&& float.TryParse(ua[i + 1], out float val2)) { set(val2); i++; return true; }
				return false;
			}
			if (TryInt("seed", s => Seed = s)) { }
			else if (TryInt("TectonicsGridN", g => TectonicsGridN = g)) { }
			else if (TryFloat("SimMegayears", m => SimMegayears = m)) { }
			else if (TryInt("NumPlates", p => NumPlates = p)) { }
			else if (v == "AutoQuit" || v == "--AutoQuit" || v == "AutoQuit=true" || v == "--AutoQuit=true") AutoQuit = true;
		}
		GD.Print($"[MapGenerator] user args: {string.Join(" | ", ua)}  -> seed={Seed} n={TectonicsGridN} {NumPlates}plates {SimMegayears}My");
		Generate();
		if (AutoQuit)
			GetTree().Quit();
		else
			GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/MapViewer.tscn");
	}

	public void Generate()
	{
		var sw = System.Diagnostics.Stopwatch.StartNew();

		// ── 海拔生成：球面板块模拟（tectonics.js 移植，M1-M3）──
		GD.Print($"[MapGenerator] 板块模拟开始 seed={Seed} n={TectonicsGridN} {SimMegayears}My ...");
		var sim = new TectonicsSimulation(TectonicsGridN);
		sim.GenerateInitialCrust(Seed);
		sim.SplitIntoPlates(NumPlates, Seed);
		sim.Run(SimMegayears, SimStepMy);
		var disp = sim.Displacement;
		float sea = sim.SeaLevel;
		float minD = float.MaxValue, maxD = float.MinValue;
		foreach (var d in disp) { if (d < minD) minD = d; if (d > maxD) maxD = d; }
		GD.Print($"[MapGenerator] 板块模拟完成 disp[{minD:F0},{maxD:F0}]m sealevel={sea:F0}m land={sim.LandFractionAboveSea() * 100:F1}%");

		// ── 球面直通：模拟结果直接存档（无平面中转、无投影）──
		// 2026-08-02：用户决定去掉平面中转（等距柱状摊平→贴回会引入投影变形/南极拉伸）。
		// 海拔/气候/biome 全部计算在球面顶点上，存档 v3 = 顶点数组。
		var simVerts = sim.GlobalGrid.Vertices;   // 单位方向
		int vn = simVerts.Length;
		var svElev = new float[vn];
		float minE = float.MaxValue, maxE = float.MinValue;
		for (int i = 0; i < vn; i++)
		{
			svElev[i] = disp[i] - sea;   // 米，0=海平面
			if (svElev[i] < minE) minE = svElev[i];
			if (svElev[i] > maxE) maxE = svElev[i];
		}

		// 气候 + 生物群系（球面顶点上直接算）
		var climate = new ClimateGenerator(Seed);
		var svTemp = new float[vn];
		var svPrecip = new float[vn];
		var svBiome = new byte[vn];
		float span = Mathf.Max(-minE, maxE);
		Parallel.For(0, vn, i =>
		{
			Vector3 p = simVerts[i] * RadiusKm;   // 球面点（km）
			float e1 = span > 1e-6f ? svElev[i] / span : 0f; // -1..1，0=海平面
			float t = climate.ComputeTemperature(p, e1);
			float pp = climate.ComputePrecipitation(p, e1);
			svTemp[i] = t;
			svPrecip[i] = pp;
			svBiome[i] = (byte)BiomeClassifier.Classify(e1, t, pp);
		});

		// ── 统计 ──
		float minT = float.MaxValue, maxT = float.MinValue;
		float minP = float.MaxValue, maxP = float.MinValue;
		var dist = new int[13];
		foreach (var t in svTemp) { if (t < minT) minT = t; if (t > maxT) maxT = t; }
		foreach (var p in svPrecip) { if (p < minP) minP = p; if (p > maxP) maxP = p; }
		foreach (var b in svBiome) dist[b]++;
		sw.Stop();

		GD.Print($"[MapGenerator] seed={Seed} 球面顶点 {vn} elev[{minE:F4},{maxE:F4}] " +
				 $"temp[{minT:F1},{maxT:F1}]°C precip[{minP:F0},{maxP:F0}]mm took {sw.ElapsedMilliseconds}ms");
		long total = vn;
		var sb = new System.Text.StringBuilder("[MapGenerator] biome dist: ");
		for (int i = 0; i < dist.Length; i++)
		{
			var name = ((BiomeType)i).ToString();
			sb.Append($"{name}={dist[i]}({dist[i] * 100.0 / total:F1}%) ");
		}
		GD.Print(sb.ToString());

		MapArchive.WriteSpherical(OutputPath, Seed, simVerts, minE, maxE, svElev,
			svTemp, svPrecip, svBiome, minT, maxT, minP, maxP);

		if (ExportPreview)
			ExportSphericalPreview(simVerts, svElev, minE, maxE);
	}

	/// <summary>球面预览导出：等距柱状投影渲染（仅调试可视化，非存档格式）。</summary>
	private void ExportSphericalPreview(Vector3[] verts, float[] elev, float minE, float maxE)
	{
		const int w = 1024, h = 512;
		float range = maxE - minE;
		float hSea = -minE / range;
		var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
		for (int y = 0; y < h; y++)
		{
			float lat = 90f - 180f * y / (h - 1);
			float la = Mathf.DegToRad(lat);
			float sinLa = Mathf.Sin(la), cosLa = Mathf.Cos(la);
			for (int x = 0; x < w; x++)
			{
				float lon = -180f + 360f * x / (w - 1);
				float lo = Mathf.DegToRad(lon);
				var p = new Vector3(cosLa * Mathf.Cos(lo), sinLa, cosLa * Mathf.Sin(lo));
				// 最近顶点（预览用，够快）
				int best = -1; float bd = float.MaxValue;
				for (int i = 0; i < verts.Length; i++)
				{
					float d = (verts[i] - p).LengthSquared();
					if (d < bd) { bd = d; best = i; }
				}
				float e = (elev[best] - minE) / range;
				float e1 = (e - hSea) / (hSea > 0.5f ? hSea : 1f - hSea);
				img.SetPixel(x, y, PlanetColors.ElevationToColor(e1));
			}
		}
		img.SavePng("user://maps/elev_preview.png");
		GD.Print("[MapGenerator] elev preview saved: user://maps/elev_preview.png");
	}
}
