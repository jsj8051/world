shader_type spatial;
render_mode unshaded;

// 全域材质（2026-09-10 v5）：一颗星球一张材质——逐格区域数据烘进 region_data 纹理（每格 1 纹素，
// UV = 格纹素中心），片元按 display_mode uniform 派生区域外观（海拔色带 / 板块色相），模式切换
// 只换 uniform。板块边界描边已改几何点串方案（BallMesh.BuildBoundaryLineMesh：异板共享边 → 球面
// 三角带 + 角点端帽圆盘，独立 MeshInstance 以不透明同色渲染——重叠不叠加变深、角点帽盘无缝，
// AA 交视口 MSAA）；本 shader 不再有解析边距/旗标/CUSTOM0（v4 及以前方案已删）。
// region_data（filter_nearest，纹素布局见 BallMesh.DataTexWidth）：R = 海拔归一、G = 板号色相归一、
// B = 陆 1 / 洋 0（Crust.IsLand 统一口）、A 备用。
uniform sampler2D region_data : filter_nearest, repeat_disable;
uniform sampler2D elevation_ramp : filter_nearest, repeat_disable;   // 256×1，C# 从 ElevationStops 烘（已转 linear）
uniform int display_mode = 0;      // 当前显示模式 = MapMode.Id（0 海拔 / 1 板块；加模式在此扩分支）

// sRGB→linear（与 Godot Color.SrgbToLinear 同公式——策略色为 sRGB 意图，进 ALBEDO 前转 linear，
// 否则整球观感被提亮；2026-09-07 实测色偏修复的同款纪律）
vec3 srgb_to_linear3(vec3 c)
{
	vec3 lo = c / 12.92;
	vec3 hi = pow((c + vec3(0.055)) / 1.055, vec3(2.4));
	return mix(lo, hi, step(vec3(0.04045), c));
}

// 板色 = Godot Color.FromHsv(hue, 0.8, 0.9) 等价实现（HSV 均布色相，PlateMapMode.PlateColor 同式）
vec3 plate_color(float hue)
{
	float h = fract(hue) * 6.0;
	int sector = int(h);
	float f = h - float(sector);
	float v = 0.9;
	float p = v * (1.0 - 0.8);
	float q = v * (1.0 - 0.8 * f);
	float t = v * (1.0 - 0.8 * (1.0 - f));
	vec3 rgb = vec3(v, p, q);
	if (sector == 0) rgb = vec3(v, t, p);
	else if (sector == 1) rgb = vec3(q, v, p);
	else if (sector == 2) rgb = vec3(p, v, t);
	else if (sector == 3) rgb = vec3(p, q, v);
	else if (sector == 4) rgb = vec3(t, p, v);
	else rgb = vec3(v, p, q);
	return srgb_to_linear3(rgb);
}

void fragment()
{
	vec4 region = texture(region_data, UV);
	vec3 base;
	if (display_mode == 0)
		base = texture(elevation_ramp, vec2(region.r, 0.5)).rgb;   // 海拔：色带纹理（已烘 linear）
	else
		base = plate_color(region.g);                              // 板块：板号 → 色相现算
	ALBEDO = base;
}
