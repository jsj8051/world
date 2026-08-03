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
	[Export] public float SimStepMy = 4f;      // 模拟时间步（百万年）——2026-08-03：2→4 已验证质量一致（板块 7 块后性能）
	[Export] public int NumPlates = 8;         // 初始板块数
	[Export] public bool ProgradeRotation = true; // 自转方向：true=顺转（地球式），false=逆转（金星式）
	[Export] public float AxialTilt = 23.4f;   // 轴向倾角（度）：0=无季节，23.4=地球，90=极端季节
	[Export] public float Insolation = 1.0f;    // 恒星辐照度（相对地球 1AU）：0.7=远、冷，1.3=近、热
	[Export] public float RotationSpeed = 1.0f; // 自转速度（相对地球 24h=1.0）：0.2=慢（金星式），5=快（木星式）

	// 洋流场（生成时算好，WriteSpherical 存档 + 气候修正用）
	private Vector3[] _curDirs;
	private float[] _curWarmth;
	private float[] _curStrength;

	// 河流（生成时算好，WriteSpherical 存档；MapViewer 河流图层用）
	private byte[] _riverLevel;   // 每顶点：0=无河，1-3=级别
	private int[] _riverFlow;     // 每顶点流向（MapViewer 重建路径用）
	private float[] _riverVolume; // 每顶点累积水量 mm（河流图层/断流判定用）
	private byte[] _lakeLevel;    // 每顶点湖泊标记（0/1）
	private byte[] _mineralLevel; // 每顶点矿藏（v3.5：(富度<<4)|矿种；0=无）

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
			else if (TryFloat("AxialTilt", t => AxialTilt = t)) { }
			else if (TryFloat("Insolation", i => Insolation = i)) { }
			else if (TryFloat("RotationSpeed", r => RotationSpeed = r)) { }
			else if (v == "AutoQuit" || v == "--AutoQuit" || v == "AutoQuit=true" || v == "--AutoQuit=true") AutoQuit = true;
			else if (v.StartsWith("ProgradeRotation", StringComparison.OrdinalIgnoreCase))
			{
				// 支持：--ProgradeRotation false/true/0/1（空格或 =）或裸参数（=顺转）
				// ⚠️ else if 链会短路：此分支必须单独处理，内部自己消费后续参数
				if (v.Contains("false") || v.Contains("=0")) ProgradeRotation = false;
				else if (v.Contains("true") || v.Contains("=1")) ProgradeRotation = true;
				else if (i + 1 < ua.Length)
				{
					string nv = ua[i + 1].ToLowerInvariant();
					if (nv == "false" || nv == "0") { ProgradeRotation = false; i++; }
					else if (nv == "true" || nv == "1") { ProgradeRotation = true; i++; }
					else ProgradeRotation = true;   // 后续参数不是 bool → 裸参数=顺转
				}
				else ProgradeRotation = true;
			}
		}
		GD.Print($"[MapGenerator] user args: {string.Join(" | ", ua)}  -> seed={Seed} n={TectonicsGridN} {NumPlates}plates {SimMegayears}My ProgradeRotation={ProgradeRotation}");
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

		// ── 阶段化管线（2026-08-03 重构：气候→水文→生态→资源→统计；同步/异步共用）──
		var pipe = new PlanetPipeline();
		pipe.Run(sim, new PlanetParams
		{
			Seed = Seed, AxialTilt = AxialTilt, Insolation = Insolation,
			ProgradeRotation = ProgradeRotation, RotationSpeed = RotationSpeed, RadiusKm = RadiusKm,
		});
		var simVerts = sim.GlobalGrid.Vertices;   // 单位方向
		int vn = simVerts.Length;
		_riverFlow = pipe.RiverFlow; _riverVolume = pipe.RiverVolume;
		_riverLevel = pipe.RiverLevel; _lakeLevel = pipe.LakeLevel; _mineralLevel = pipe.MineralLevel;
		_curDirs = pipe.CurrentDirs; _curWarmth = pipe.CurrentWarmth; _curStrength = pipe.CurrentStrength;

		GD.Print($"[MapGenerator] 河岸带 {pipe.RiparianCount} 格");
		int mineralCount = 0;
		var mdist = new int[9];
		for (int i = 0; i < vn; i++)
			if (_mineralLevel[i] != 0)
			{
				mineralCount++;
				mdist[MineralSystem.TypeOf(_mineralLevel[i])]++;
			}
		GD.Print($"[MapGenerator] 矿藏 {mineralCount} 格 ({mineralCount * 100f / vn:F1}%)" +
			$" 铁={mdist[1]} 铜={mdist[2]} 锡={mdist[3]} 金={mdist[4]} 煤={mdist[5]} 盐={mdist[6]} 石料={mdist[7]} 宝石={mdist[8]}");

		sw.Stop();
		GD.Print($"[MapGenerator] seed={Seed} 球面顶点 {vn} elev[{pipe.MinElev:F4},{pipe.MaxElev:F4}] " +
			 $"temp[{pipe.MinTemp:F1},{pipe.MaxTemp:F1}]°C precip[{pipe.MinPrecip:F0},{pipe.MaxPrecip:F0}]mm took {sw.ElapsedMilliseconds}ms");
		long total = vn;
		var sb = new System.Text.StringBuilder("[MapGenerator] biome dist: ");
		var dist = new int[14];   // biome 0..13（含 Riparian）
		foreach (var b in pipe.Biome) dist[b]++;
		for (int i = 0; i < dist.Length; i++)
		{
			var name = ((BiomeType)i).ToString();
			sb.Append($"{name}={dist[i]}({dist[i] * 100.0 / total:F1}%) ");
		}
		GD.Print(sb.ToString());

		MapArchive.WriteSpherical(OutputPath, Seed, simVerts, pipe.MinElev, pipe.MaxElev, pipe.Elev,
			pipe.Temp, pipe.Precip, pipe.Biome, pipe.MinTemp, pipe.MaxTemp, pipe.MinPrecip, pipe.MaxPrecip,
			prograde: ProgradeRotation, rotationSpeed: RotationSpeed,
			currentDirs: _curDirs, currentWarmth: _curWarmth, currentStrength: _curStrength,
			riverLevel: _riverLevel, riverFlow: _riverFlow, riverVolume: _riverVolume, lakeLevel: _lakeLevel,
			mineralLevel: _mineralLevel);

		if (ExportPreview)
			ExportSphericalPreview(simVerts, pipe.Elev, pipe.MinElev, pipe.MaxElev);
	}

	/// <summary>
	/// 后台生成（UI 用）：纯数据计算跑后台线程（模拟+气候+biome，不碰 Godot 对象），
	/// 完成后回调主线程写存档。进度 0..1：模拟 0-0.7，气候 0.7-1.0。
	/// </summary>
	/// <param name="onProgress">主线程进度回调（0..1）。</param>
	/// <param name="onDone">主线程完成回调（true=成功写出存档）。</param>
	public void GenerateAsync(Action<float> onProgress, Action<bool, string> onDone)
	{
		int seed = Seed, n = TectonicsGridN, plates = NumPlates;
		float my = SimMegayears, step = SimStepMy, radius = RadiusKm;
		string outPath = OutputPath;
		bool exportPreview = ExportPreview;

		Task.Run(() =>
		{
			// ── 板块模拟（纯数据）──
			var sim = new TectonicsSimulation(n);
			sim.GenerateInitialCrust(seed);
			sim.SplitIntoPlates(plates, seed);
			int totalSteps = (int)(my / step);
			sim.RunWithProgress(my, step, frac => onProgress(frac * 0.7f));
			var disp = sim.Displacement;
			float sea = sim.SeaLevel;

			var simVerts = sim.GlobalGrid.Vertices;
			int vn = simVerts.Length;
			var svElev = new float[vn];
			float minE = float.MaxValue, maxE = float.MinValue;
			for (int i = 0; i < vn; i++)
			{
				svElev[i] = disp[i] - sea;
				if (svElev[i] < minE) minE = svElev[i];
				if (svElev[i] > maxE) maxE = svElev[i];
			}

			// ── 阶段化管线（气候→水文→生态→资源；后台线程纯计算，共用同步逻辑）──
			var pipe = new PlanetPipeline();
			pipe.Run(sim, new PlanetParams
			{
				Seed = seed, AxialTilt = AxialTilt, Insolation = Insolation,
				ProgradeRotation = ProgradeRotation, RotationSpeed = RotationSpeed, RadiusKm = radius,
			}, frac => onProgress(0.7f + 0.3f * frac));
			_riverFlow = pipe.RiverFlow; _riverVolume = pipe.RiverVolume;
			_riverLevel = pipe.RiverLevel; _lakeLevel = pipe.LakeLevel; _mineralLevel = pipe.MineralLevel;
			_curDirs = pipe.CurrentDirs; _curWarmth = pipe.CurrentWarmth; _curStrength = pipe.CurrentStrength;

			bool ok = MapArchive.WriteSpherical(outPath, seed, simVerts, pipe.MinElev, pipe.MaxElev, pipe.Elev,
				pipe.Temp, pipe.Precip, pipe.Biome, pipe.MinTemp, pipe.MaxTemp, pipe.MinPrecip, pipe.MaxPrecip,
				prograde: ProgradeRotation, rotationSpeed: RotationSpeed,
				currentDirs: _curDirs, currentWarmth: _curWarmth, currentStrength: _curStrength,
				riverLevel: _riverLevel, riverFlow: _riverFlow, riverVolume: _riverVolume, lakeLevel: _lakeLevel,
				mineralLevel: _mineralLevel, log: false);   // 后台线程禁止 GD.Print
			if (exportPreview)
				ExportSphericalPreview(simVerts, pipe.Elev, pipe.MinElev, pipe.MaxElev);
			return (ok, outPath);
		}).ContinueWith(t =>
		{
			// 线程池回调：线程安全的事（打印错误）直接做，UI 更新必须主线程
			if (t.IsFaulted)
				GD.PrintErr($"[MapGenerator] async failed: {t.Exception?.GetBaseException().Message}");
			CallDeferred(nameof(FinishAsync), t.IsCompletedSuccessfully && t.Result.ok, t.IsCompletedSuccessfully ? t.Result.outPath : "");
		});
		// 注意：onProgress 在后台线程被调用（UI 进度条读写 volatile 由 Godot 主线程 _Process 驱动更安全，
		// 但 ProgressBar.Value 属性主线程写后台线程读会竞争——这里直接回调，Godot Control 属性非线程安全。
		// 稳妥做法：onProgress 只记录 volatile 字段，主线程 _Process 读。此处简化：回调里 QueueRedraw 有风险，
		// 由调用方（MapGenMenu）保证只更新 volatile float。见 MapGenMenu.cs 注释。
	}

	private void FinishAsync(bool ok, string path)
	{
		_asyncDone?.Invoke(ok, path);
	}
	private Action<bool, string> _asyncDone;

	/// <summary>后台生成完成回调（主线程）。</summary>
	public void SetAsyncDoneCallback(Action<bool, string> cb) => _asyncDone = cb;

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
		// ⚠️ 后台线程禁止 GD.Print——日志由调用方（主线程）打
		}
}
