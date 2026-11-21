using System;
using System.Text;
using World.Data;
using World.Application.MapViewing;

namespace World.Application.Components;

/// <summary>
/// 格子信息卡的文本逻辑：把一次拾取结果与可选地壳详情格式化成摘要、地壳段落与边界列表。
/// 不依赖 Godot 控件，也不推导事实（海陆取 <see cref="PickedCell.IsLand"/>）。
/// </summary>
internal static class CellInfoCardFormatter
{
	static readonly string[] Headings = { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };

	internal static MapSelectionContent Describe(in PickedCell cell, string bandName, CrustCellDetails crust)
	{
		string lat = $"{Math.Abs(cell.LatDeg):F1}°{(cell.LatDeg >= 0f ? "N" : "S")}";
		string lng = $"{Math.Abs(cell.LngDeg):F1}°{(cell.LngDeg >= 0f ? "E" : "W")}";
		string elev = $"{(cell.ElevM >= 0f ? "+" : "")}{cell.ElevM:F0} m";
		string summary = $"格 id   {cell.CellId:X}\n" +
						 $"经纬    {lat}, {lng}\n" +
						 $"海拔    {elev}（{bandName}）\n" +
						 $"海陆    {(cell.IsLand ? "陆地" : "海洋")}（陆占比 {cell.LandFraction:P0}）\n" +
						 $"离海    {cell.DistToCoast} 格      离岸 {cell.DistToLand} 格";
		if (cell.Biome is BiomeType biome) summary += $"\n群系    {BiomeName(biome)}";

		CrustCellBoundary[] edges = crust?.Boundaries ?? Array.Empty<CrustCellBoundary>();
		bool showBoundaryToggle = edges.Length > 1;
		return new MapSelectionContent(cell.CellId, summary, CrustText(crust),
			showBoundaryToggle ? $"全部相邻边界（{edges.Length}）" : string.Empty,
			showBoundaryToggle ? BoundariesText(edges) : string.Empty);
	}

	static string CrustText(CrustCellDetails crust)
	{
		if (crust == null) return string.Empty;
		var text = new StringBuilder();
		if (crust.TimeMa is double time) text.AppendLine($"演化年代    {time:0.00####} Ma");
		text.Append($"表层板块 {crust.PlateId}  ·  表层{(crust.Type == CrustType.Continental ? "陆壳" : "洋壳")}");
		if (crust.OceanicCoverage is double coverage) text.Append($"  ·  格内洋壳占比 {coverage:P0}");
		if (crust.OceanAgeMa is double age) text.AppendLine().Append($"洋壳平均年龄    {age:F1} Ma");
		string direction = crust.HeadingDeg is double heading
			? $"{Headings[(int)((heading + 22.5) / 45) % Headings.Length]}（{heading:F0}°）" : "近乎静止";
		text.AppendLine().Append($"板块运动    {direction}");
		if (crust.SpeedKmPerMa is double speed) text.Append($"  {Rate(speed)} cm/年");
		if (crust.MaterialHeightM is float material && crust.ReliefM is double relief)
			text.AppendLine().Append($"材料海拔    {material:+0;-0;0} m\n剖面净修正  {relief:+0;-0;0} m");

		CrustCellBoundary[] edges = crust.Boundaries ?? Array.Empty<CrustCellBoundary>();
		text.AppendLine();
		if (edges.Length == 0) text.Append("无直接板块边界");
		else text.Append("边界摘要    ").Append(BoundaryText(edges[0]));
		return text.ToString();
	}

	static string BoundariesText(CrustCellBoundary[] edges)
	{
		var list = new StringBuilder();
		for (int i = 0; i < edges.Length; i++)
		{
			if (i > 0) list.AppendLine();
			list.Append(BoundaryText(edges[i]));
		}
		return list.ToString();
	}

	static string BoundaryText(CrustCellBoundary edge)
	{
		string motion = edge.Motion switch
		{
			BoundaryKind.Convergent => edge.Convergence == ConvergenceKind.Subduction ? "俯冲" : "陆陆碰撞",
			BoundaryKind.Divergent => "张裂",
			BoundaryKind.Transform => "走滑",
			_ => "不活动",
		};
		string side = edge.Role switch
		{
			BoundarySideRole.Overriding => "（本侧上盘）",
			BoundarySideRole.Underthrusting => "（本侧下盘）",
			_ => "",
		};
		string text = $"邻接板块 {edge.NeighborPlateId}  ·  {motion}{side}";
		if (edge.NormalKmPerMa is double normal && edge.ShearKmPerMa is double shear)
		{
			string sense = normal < 0 ? "闭合" : "张开";
			text += $"\n    法向{sense} {Rate(normal)}  ·  走滑 {Rate(shear)} cm/年";
		}
		return text;
	}

	static string Rate(double kmPerMa) => (Math.Abs(kmPerMa) * 0.1).ToString("F2");

	static string BiomeName(BiomeType biome) => biome switch
	{
		BiomeType.IceCap => "冰原",
		BiomeType.Tundra => "苔原",
		BiomeType.TropicalRainforest => "热带雨林",
		BiomeType.TropicalMonsoon => "热带季风林",
		BiomeType.TropicalSavanna => "热带稀树草原",
		BiomeType.HotDesert => "热沙漠",
		BiomeType.ColdDesertKoppen => "冷沙漠",
		BiomeType.HotSteppe => "热半干旱草原",
		BiomeType.ColdSteppe => "冷半干旱草原",
		BiomeType.HumidSubtropical => "湿润亚热带",
		BiomeType.Oceanic => "海洋性温带",
		BiomeType.MonsoonSubtropical => "冬干亚热带",
		BiomeType.MediterraneanHot => "热夏地中海",
		BiomeType.MediterraneanCool => "凉夏地中海",
		BiomeType.ContinentalHot => "热夏湿润大陆",
		BiomeType.ContinentalWarm => "暖夏湿润大陆",
		BiomeType.Subarctic => "亚寒带针叶林",
		BiomeType.ContinentalDry => "冬干大陆",
		_ => throw new ArgumentOutOfRangeException(nameof(biome)),
	};
}
