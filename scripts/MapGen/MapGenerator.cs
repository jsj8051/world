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
	private float[] _riverVolume; // 每顶点累积水量 mm（降水-蒸发沿流向累积 = 流量）
	private byte[] _lakeLevel;    // 每顶点：0=无湖，1=湖（陆地盆地 + 水量 ≥ 阈值）

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
		World.Biome.WindField.Prograde = ProgradeRotation;   // 自转方向 → 盛行风科里奥利偏转
		World.Biome.WindField.RotationSpeed = RotationSpeed; // 自转速度 → 科里奥利强度
		var climate = new ClimateGenerator(Seed, AxialTilt, Insolation);
		var svTemp = new float[vn];
		var svPrecip = new float[vn];
		var svBiome = new byte[vn];
		float span = Mathf.Max(-minE, maxE);
		// 盛行风降水回调：球面点 → 归一化海拔（最近顶点，桶查询）
		var grid = sim.GlobalGrid;
		System.Func<Vector3, float> elevSampler = p =>
		{
			Vector3 dir = p.Normalized();
			int id = grid.NearestId(dir);
			return span > 1e-6f ? svElev[id] / span : 0f;
		};

		// 洋流场（2026-08-02 v2：风应力旋度 + 流函数 → 闭合环流；替代"方向=风向"）
		{
			// 归一化海拔数组
			var eNorm = new float[vn];
			for (int i = 0; i < vn; i++) eNorm[i] = span > 1e-6f ? svElev[i] / span : 0f;
			World.Biome.OceanCurrent.Compute(simVerts, grid.Neighbors, eNorm,
				out _curDirs, out _curWarmth, out _curStrength);
		}
		// 沿岸采样：球面点 → 最近海洋顶点的冷暖+强度（陆地点查邻域最近海洋，距离衰减）
		//   强度（0.3~1.0）用于修正系数动态化：强流带影响大、开阔弱流影响小
		System.Func<Vector3, (float warm, float str)> warmthSampler = p =>
		{
			Vector3 dir = p.Normalized();
			int id = grid.NearestId(dir);
			if (_curWarmth[id] != 0f)
				return (_curWarmth[id], _curStrength != null ? _curStrength[id] : 1f);   // 海洋点直接用
			// 陆地点：查邻居找最近海洋冷暖（沿岸陆地受影响）
			float best = 0f, bestD = 1e9f, bestStr = 1f;
			foreach (var nb in grid.Neighbors[id])
			{
				if (_curWarmth[nb] != 0f)
				{
					float d = Mathf.Acos(Mathf.Clamp(simVerts[id].Dot(simVerts[nb]), -1f, 1f));
					if (d < bestD) { bestD = d; best = _curWarmth[nb]; bestStr = _curStrength != null ? _curStrength[nb] : 1f; }
				}
			}
			float decay = Mathf.Exp(-bestD / 0.08f);   // 距岸衰减（0.08rad ≈ 500km）
			return (best * decay, bestStr);
		};
		climate.SetOceanCurrent(warmthSampler);

		Parallel.For(0, vn, i =>
		{
			Vector3 p = simVerts[i] * RadiusKm;   // 球面点（km）
			float e1 = span > 1e-6f ? svElev[i] / span : 0f; // -1..1，0=海平面
			float t = climate.ComputeTemperature(p, e1);
			float pp = climate.ComputePrecipitation(p, e1, elevSampler);
			svTemp[i] = t;
			svPrecip[i] = pp;
			svBiome[i] = (byte)BiomeClassifier.Classify(e1, t, pp);
		});

		// ── 河流（2026-08-02 v2：迭代演化——动态流向 + 输沙侵蚀沉积）──
		{
			var eNorm = new float[vn];
			for (int i = 0; i < vn; i++) eNorm[i] = span > 1e-6f ? svElev[i] / span : 0f;
			RiverSystem.ComputeIterative(simVerts, grid.Neighbors, eNorm, svElev,
				svPrecip, svTemp, waterThreshold: 5000f, lakeThreshold: 200f,
				seaLevelM: 0f, elevSpan: span, rounds: 4,
				out _riverFlow, out _riverVolume, out _riverLevel, out _lakeLevel, out _);

			// 修正后更新 minE/maxE（存档范围；svElev 含河谷/三角洲）
			minE = float.MaxValue; maxE = float.MinValue;
			foreach (var e in svElev) { if (e < minE) minE = e; if (e > maxE) maxE = e; }

			// ── 河岸生态带（2026-08-02）：沿岸陆地格（邻居有河/湖）→ Riparian 翠绿。
			//   干旱区沙漠沿岸变绿洲线（真实：尼罗河/撒哈拉绿洲）；湿润区沿岸清晰河岸林。
			{
				int riparianCount = 0;
				for (int i = 0; i < vn; i++)
				{
					if (svElev[i] <= sea) continue;                    // 海洋不算
					if (_riverLevel[i] > 0 || _lakeLevel[i] > 0) continue;   // 水格本身不算
					bool wet = false;
					foreach (var nb in grid.Neighbors[i])
						if (_riverLevel[nb] > 0 || _lakeLevel[nb] > 0) { wet = true; break; }
					if (wet) { svBiome[i] = (byte)BiomeType.Riparian; riparianCount++; }
				}
				GD.Print($"[MapGenerator] 河岸带 {riparianCount} 格");
			}
		}

	// ── 统计 ──
	float minT = float.MaxValue, maxT = float.MinValue;
		float minP = float.MaxValue, maxP = float.MinValue;
		var dist = new int[14];   // biome 0..13（含 Riparian）
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
			svTemp, svPrecip, svBiome, minT, maxT, minP, maxP, prograde: ProgradeRotation, rotationSpeed: RotationSpeed,
			currentDirs: _curDirs, currentWarmth: _curWarmth, currentStrength: _curStrength,
			riverLevel: _riverLevel, riverFlow: _riverFlow, riverVolume: _riverVolume, lakeLevel: _lakeLevel);

		if (ExportPreview)
			ExportSphericalPreview(simVerts, svElev, minE, maxE);
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

			// ── 气候 + biome（纯数据，FastNoiseLite 只读线程安全）──
			World.Biome.WindField.Prograde = ProgradeRotation;   // 自转方向 → 盛行风科里奥利偏转
		World.Biome.WindField.RotationSpeed = RotationSpeed; // 自转速度 → 科里奥利强度
			var climate = new ClimateGenerator(seed, AxialTilt, Insolation);
			var svTemp = new float[vn];
			var svPrecip = new float[vn];
			var svBiome = new byte[vn];
			float span = Mathf.Max(-minE, maxE);
			// 盛行风降水回调：球面点 → 归一化海拔（最近顶点，桶查询）
			var grid = sim.GlobalGrid;
			System.Func<Vector3, float> elevSampler = p =>
			{
				Vector3 dir = p.Normalized();
				int id = grid.NearestId(dir);
				return span > 1e-6f ? svElev[id] / span : 0f;
			};
			// 洋流场（v2 流函数法，与同步路径一致）
			{
				var eNorm = new float[vn];
				for (int i = 0; i < vn; i++) eNorm[i] = span > 1e-6f ? svElev[i] / span : 0f;
				World.Biome.OceanCurrent.Compute(simVerts, grid.Neighbors, eNorm,
					out _curDirs, out _curWarmth, out _curStrength);
			}
			System.Func<Vector3, (float warm, float str)> warmthSampler = p =>
			{
				Vector3 dir = p.Normalized();
				int id = grid.NearestId(dir);
				if (_curWarmth[id] != 0f)
					return (_curWarmth[id], _curStrength != null ? _curStrength[id] : 1f);
				float best = 0f, bestD = 1e9f, bestStr = 1f;
				foreach (var nb in grid.Neighbors[id])
				{
					if (_curWarmth[nb] != 0f)
					{
						float d = Mathf.Acos(Mathf.Clamp(simVerts[id].Dot(simVerts[nb]), -1f, 1f));
						if (d < bestD) { bestD = d; best = _curWarmth[nb]; bestStr = _curStrength != null ? _curStrength[nb] : 1f; }
					}
				}
				return (best * Mathf.Exp(-bestD / 0.08f), bestStr);
			};
			climate.SetOceanCurrent(warmthSampler);
			Parallel.For(0, vn, i =>
			{
				Vector3 p = simVerts[i] * radius;
				float e1 = span > 1e-6f ? svElev[i] / span : 0f;
				float t = climate.ComputeTemperature(p, e1);
				float pp = climate.ComputePrecipitation(p, e1, elevSampler);
				svTemp[i] = t;
				svPrecip[i] = pp;
				svBiome[i] = (byte)BiomeClassifier.Classify(e1, t, pp);
				if ((i & 0xFF) == 0)
					onProgress(0.7f + 0.3f * i / vn);
			});

			float minT = float.MaxValue, maxT = float.MinValue;
			float minP = float.MaxValue, maxP = float.MinValue;
			for (int i = 0; i < vn; i++)
			{
				if (svTemp[i] < minT) minT = svTemp[i];
				if (svTemp[i] > maxT) maxT = svTemp[i];
				if (svPrecip[i] < minP) minP = svPrecip[i];
				if (svPrecip[i] > maxP) maxP = svPrecip[i];
			}

			// 河流（后台线程安全：纯计算；迭代演化 v2）
			{
				var eNorm = new float[vn];
				for (int i = 0; i < vn; i++) eNorm[i] = span > 1e-6f ? svElev[i] / span : 0f;
				RiverSystem.ComputeIterative(simVerts, grid.Neighbors, eNorm, svElev,
					svPrecip, svTemp, waterThreshold: 5000f, lakeThreshold: 200f,
					seaLevelM: 0f, elevSpan: span, rounds: 4,
					out _riverFlow, out _riverVolume, out _riverLevel, out _lakeLevel, out _);
				minE = float.MaxValue; maxE = float.MinValue;
				foreach (var e in svElev) { if (e < minE) minE = e; if (e > maxE) maxE = e; }

				// 河岸生态带（同同步路径；后台线程禁止 GD.Print——不打印）
				for (int i = 0; i < vn; i++)
				{
					if (svElev[i] <= sea) continue;
					if (_riverLevel[i] > 0 || _lakeLevel[i] > 0) continue;
					bool wet = false;
					foreach (var nb in grid.Neighbors[i])
						if (_riverLevel[nb] > 0 || _lakeLevel[nb] > 0) { wet = true; break; }
					if (wet) svBiome[i] = (byte)BiomeType.Riparian;
				}
			}

			bool ok = MapArchive.WriteSpherical(outPath, seed, simVerts, minE, maxE, svElev,
				svTemp, svPrecip, svBiome, minT, maxT, minP, maxP, prograde: ProgradeRotation, rotationSpeed: RotationSpeed,
				currentDirs: _curDirs, currentWarmth: _curWarmth, currentStrength: _curStrength,
				riverLevel: _riverLevel, riverFlow: _riverFlow, riverVolume: _riverVolume, lakeLevel: _lakeLevel, log: false);   // 后台线程禁止 GD.Print
			if (exportPreview)
				ExportSphericalPreview(simVerts, svElev, minE, maxE);
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
