shader_type spatial;
render_mode unshaded;

// 全域材质（2026-09-09 材质覆盖方案，替旧"逐格顶点色重交"）：一颗星球一张材质——逐格区域
// 数据烘进 region_data 纹理（每格 1 纹素，UV = 格纹素中心，格块内顶点同值 → 插值恒定），
// 片元按当前显示模式在材质侧派生区域外观：海拔模式采样色带纹理、板块模式按板号色相现算板色。
// 模式切换 = 改 uniform，零几何重交、零 CPU 逐格取色。"对应区域显示对应材质"由 region_data
// 按纹素（= 按格）寻址达成；将来真"分区域材质"（陆贴地表纹理 / 洋贴水面）按 B 通道陆位分支即可。
// region_data（filter_nearest，纹素布局见 BallMesh.DataTexWidth）：R = 海拔归一（色带域 clamp）、
// G = 板号色相归一（板号/板数）、B = 陆 1 / 洋 0（Crust.IsLand 统一口）、A 备用。
// 描边带沿用 2026-09-08 拍板方案，记账粒度 2026-09-09 v2 改【边】：UV2 = (x 边距权重, y 边界旗)——
// x：格心 1 → 边顶点 0（等值线平行格边）；y：本三角形所属格边的边界旗（边顶点按边复制，非边界
// 边恒 0）→ 带强 = 旗 × 边距带，同板边绝不整条描边（修"五边贴异板格第 6 边误描"）。
// outline_enable = 0 时整体失效（旗数据保留，开关零成本）。
uniform sampler2D region_data : filter_nearest, repeat_disable;
uniform sampler2D elevation_ramp : filter_nearest, repeat_disable;   // 256×1，C# 从 ElevationStops 烘（已转 linear）
uniform int display_mode = 0;      // 当前显示模式 = MapMode.Id（0 海拔 / 1 板块；加模式在此扩分支）
uniform float outline_enable : hint_range(0.0, 1.0) = 1.0;
uniform float outline_darken : hint_range(0.0, 1.0) = 0.0;    // 描边带压暗系数（用户已调 0 = 纯黑线）
uniform float outline_width : hint_range(0.0, 0.5) = 0.18;    // 带半宽（× 格心到边距离；两侧格各出一条）

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

	// 锐边描边（2026-09-09 拍板）：单阈值切实心带 + fwidth 屏幕空间导数 1px 抗锯齿；
	// 带强 = 边界旗 × 边距带
	float aa = fwidth(UV2.x);
	float band = (1.0 - smoothstep(outline_width - aa, outline_width + aa, UV2.x)) * UV2.y * outline_enable;
	ALBEDO = mix(base, base * outline_darken, band);
}
