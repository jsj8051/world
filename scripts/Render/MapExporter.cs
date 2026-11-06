using System;
using Godot;
using World.Logic;
using World.Utils.H3;

namespace World.Render;

/// <summary>
/// 2D 地形图导出：等距圆柱投影（经度 −180…180、纬度 90…−90），逐格多边形填充，输出 PNG。
/// 一格一色、无抗锯齿：海岸与色带边界就落在 H3 格边上。
/// 只读世界事实与地图模式取色；不画河流、标注、经纬网或图面装饰。
/// 默认 8192×4096（2:1），适合 A2 300 DPI 的世界图；一次导出在主线程完成。
/// 输出到游戏目录下的 <c>map_exports/</c>（编辑器 = 项目根，导出后 = exe 同级）。
/// </summary>
public static class MapExporter
{
	/// <summary>默认导出宽（像素；2:1 世界图）。</summary>
	public const int DefaultWidth = 8192;

	/// <summary>默认导出高（像素）。</summary>
	public const int DefaultHeight = 4096;

	/// <summary>每边外扩的像素数：盖住相邻格之间的扫描线缝。</summary>
	const double PaddingPixels = 0.75;

	const string EditorOutputDir = "res://map_exports";
	const string OutputFolder = "map_exports";

	/// <summary>导出当前世界的地形图，返回 PNG 的绝对路径。</summary>
	/// <param name="sim">已生成的世界（读 <c>Facts.Ball</c> 与格数据）。</param>
	/// <param name="mode">取色策略（地形模式传 <c>TerrainMode</c>）。</param>
	/// <param name="width">输出宽（像素；建议 2:1）。</param>
	/// <param name="height">输出高（像素）。</param>
	public static string ExportTerrain(WorldGenSimulation sim, MapMode mode,
		int width = DefaultWidth, int height = DefaultHeight)
	{
		if (sim == null) throw new ArgumentNullException(nameof(sim));
		if (mode == null) throw new ArgumentNullException(nameof(mode));
		if (width <= 0 || height <= 0)
			throw new ArgumentOutOfRangeException(nameof(width), "出图尺寸须为正");

		byte[] pixels = RenderPixels(sim, mode, width, height);
		var image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, pixels);
		string dir = EnsureDirectory();
		string file = System.IO.Path.Combine(dir, $"terrain_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
		Error err = image.SavePng(file);
		if (err != Error.Ok)
			throw new InvalidOperationException($"PNG 写入失败：{err}（{file}）");
		return file;
	}

	/// <summary>
	/// 只做光栅化：等距圆柱投影下逐格多边形填充，返回 RGBA8 像素。
	/// 不触碰 Godot 引擎资源（<see cref="Image"/>），供无引擎宿主（测试 / 批处理）复用。
	/// </summary>
	public static byte[] RenderPixels(WorldGenSimulation sim, MapMode mode, int width, int height)
	{
		if (sim == null) throw new ArgumentNullException(nameof(sim));
		if (mode == null) throw new ArgumentNullException(nameof(mode));
		if (width <= 0 || height <= 0)
			throw new ArgumentOutOfRangeException(nameof(width), "出图尺寸须为正");

		var facts = sim.Facts ?? throw new InvalidOperationException("世界尚未生成");
		var ball = facts.Ball;
		int n = ball.CellIds.Length;

		mode.BeginBake();
		var colors = new Color[n];
		for (int i = 0; i < n; i++) colors[i] = mode.CellColorAt(i);

		var pixels = new byte[width * height * 4];
		FillBackground(pixels, new Color(0.04f, 0.06f, 0.10f));

		for (int i = 0; i < n; i++)
		{
			var boundary = H3.CellToBoundary(ball.CellIds[i]);
			if (boundary.Length < 3) continue;
			DrawCell(pixels, width, height, boundary, colors[i]);
		}
		return pixels;
	}

	static void DrawCell(byte[] pixels, int width, int height, LatLng[] boundary, Color color)
	{
		int n = boundary.Length;
		var xs = new double[n];
		var ys = new double[n];
		double lng0 = boundary[0].Lng;
		double latC = 0.0, lngC = 0.0;

		for (int k = 0; k < n; k++)
		{
			double lng = Unwrap(boundary[k].Lng, lng0);
			latC += boundary[k].Lat;
			lngC += lng;
		}
		latC /= n;
		lngC /= n;

		// 沿「格心 → 顶点」方向外扩固定像素数。等距圆柱下经度方向的 1 px 是 2π/(width·cos lat)，
		// 按比例外扩会让极区格（经度跨度可达几十度）把相邻格整片涂掉。
		double dLat = PaddingPixels * Math.PI / height;
		double cosLat = Math.Max(Math.Cos(latC), 0.02);
		double dLng = PaddingPixels * 2.0 * Math.PI / width / cosLat;

		for (int k = 0; k < n; k++)
		{
			double lat = boundary[k].Lat;
			double lng = Unwrap(boundary[k].Lng, lng0);

			double dy = lat - latC;
			double dx = (lng - lngC) * cosLat;
			double len = Math.Sqrt(dx * dx + dy * dy);
			if (len > 1e-12)
			{
				lat += dLat * (dy / len);
				lng += dLng * (dx / len);
			}

			xs[k] = (lng + Math.PI) / (2.0 * Math.PI) * width;
			ys[k] = (Math.PI / 2.0 - Math.Clamp(lat, -Math.PI / 2.0, Math.PI / 2.0)) / Math.PI * height;
		}

		double minX = xs[0], maxX = xs[0];
		for (int k = 1; k < n; k++)
		{
			if (xs[k] < minX) minX = xs[k];
			if (xs[k] > maxX) maxX = xs[k];
		}

		byte r = ToByte(color.R), g = ToByte(color.G), b = ToByte(color.B);
		FillPolygon(pixels, width, height, xs, ys, r, g, b, 0.0);
		if (minX < 0.0) FillPolygon(pixels, width, height, xs, ys, r, g, b, width);
		if (maxX > width) FillPolygon(pixels, width, height, xs, ys, r, g, b, -width);
	}

	/// <summary>经度按首顶点展开：跨 ±180° 的格不横跨整幅图。</summary>
	static double Unwrap(double lng, double lng0)
	{
		while (lng - lng0 > Math.PI) lng -= 2.0 * Math.PI;
		while (lng - lng0 < -Math.PI) lng += 2.0 * Math.PI;
		return lng;
	}

	/// <summary>扫描线填充（像素中心采样；x 偏移用于 ±180° 接缝的另一侧）。</summary>
	static void FillPolygon(byte[] pixels, int width, int height, double[] xs, double[] ys,
		byte r, byte g, byte b, double xOffset)
	{
		int n = xs.Length;
		double minY = ys[0], maxY = ys[0];
		for (int k = 1; k < n; k++)
		{
			if (ys[k] < minY) minY = ys[k];
			if (ys[k] > maxY) maxY = ys[k];
		}

		int y0 = Math.Max(0, (int)Math.Floor(minY));
		int y1 = Math.Min(height - 1, (int)Math.Ceiling(maxY));
		if (y0 > y1) return;

		Span<double> hits = stackalloc double[n + 1];
		for (int y = y0; y <= y1; y++)
		{
			double sy = y + 0.5;
			int count = 0;
			for (int k = 0; k < n; k++)
			{
				int j = (k + 1) % n;
				double ya = ys[k], yb = ys[j];
				if ((ya <= sy && yb > sy) || (yb <= sy && ya > sy))
				{
					if (count == hits.Length) { count = -1; break; }
					double t = (sy - ya) / (yb - ya);
					hits[count++] = xs[k] + xOffset + t * (xs[j] - xs[k]);
				}
			}
			if (count < 0) continue;   // 交点数超出顶点数：非简单多边形，跳过该行（不画错色）

			for (int a = 1; a < count; a++)
			{
				double v = hits[a];
				int c = a - 1;
				while (c >= 0 && hits[c] > v) { hits[c + 1] = hits[c]; c--; }
				hits[c + 1] = v;
			}

			for (int k = 0; k + 1 < count; k += 2)
			{
				int xa = Math.Max(0, (int)Math.Ceiling(hits[k] - 0.5));
				int xb = Math.Min(width - 1, (int)Math.Floor(hits[k + 1] - 0.5));
				for (int x = xa; x <= xb; x++)
				{
					int o = (y * width + x) * 4;
					pixels[o] = r;
					pixels[o + 1] = g;
					pixels[o + 2] = b;
					pixels[o + 3] = 255;
				}
			}
		}
	}

	static void FillBackground(byte[] pixels, Color color)
	{
		byte r = ToByte(color.R), g = ToByte(color.G), b = ToByte(color.B);
		for (int i = 0; i < pixels.Length; i += 4)
		{
			pixels[i] = r;
			pixels[i + 1] = g;
			pixels[i + 2] = b;
			pixels[i + 3] = 255;
		}
	}

	static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

	static string EnsureDirectory()
	{
		// 先试项目根（编辑器/源码运行可写）；导出后的 res:// 只读，落到 exe 同级。
		string projectDir = ProjectSettings.GlobalizePath(EditorOutputDir);
		Error err = DirAccess.MakeDirRecursiveAbsolute(projectDir);
		if (err == Error.Ok || err == Error.AlreadyExists) return projectDir;

		string exeDir = System.IO.Path.Combine(
			System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".", OutputFolder);
		err = DirAccess.MakeDirRecursiveAbsolute(exeDir);
		if (err != Error.Ok && err != Error.AlreadyExists)
			throw new InvalidOperationException($"创建导出目录失败：{err}（{exeDir}）");
		return exeDir;
	}
}
