shader_type spatial;
render_mode unshaded;

// 全域材质（2026-09-09 v3 解析边距版，替"逐格顶点色重交"与"边距权重插值 + 角点帽"两代）：
// 一颗星球一张材质——逐格区域数据烘进 region_data 纹理（每格 1 纹素，UV = 格纹素中心），
// 片元按 display_mode uniform 派生区域外观（海拔色带 / 板块色相），模式切换只换 uniform。
//
// 板块边界描边 = 片元解析边距（2026-09-09 v3 定稿）：每个扇形三角的三个顶点独占属性
// （格心/角点的切面坐标 UV2、旗标 + 边法向 + 内切半径 COLOR），片元把自身方向投影到格心
// 切面，对【旗标边】求到线段的精确距离——带宽处处一致、拐角天然圆角衔接、同板边绝不描边
// （修"五边贴异板格第 6 边误描"与"拐角缺口/深色圆珠"两代问题）。正六边形模型弃用——
// H3 五边格与畸变格的真实边法向/内切半径逐边下发，处处严格正确。
// region_data（filter_nearest，纹素布局见 BallMesh.DataTexWidth）：R = 海拔归一、G = 板号色相归一、
// B = 陆 1 / 洋 0（Crust.IsLand 统一口）、A 备用。
uniform sampler2D region_data : filter_nearest, repeat_disable;
uniform sampler2D elevation_ramp : filter_nearest, repeat_disable;   // 256×1，C# 从 ElevationStops 烘（已转 linear）
uniform int display_mode = 0;      // 当前显示模式 = MapMode.Id（0 海拔 / 1 板块；加模式在此扩分支）
uniform float outline_enable : hint_range(0.0, 1.0) = 1.0;
uniform float outline_darken : hint_range(0.0, 1.0) = 0.0;    // 描边带压暗系数（用户已调 0 = 纯黑线）
uniform float outline_width : hint_range(0.0, 0.5) = 0.18;    // 带半宽（× 本边内切半径）
uniform float rho_scale = 0.02;        // COLOR.a 的内切半径归一化尺度（BallMesh.RhoScale，rad）
uniform float cell_half_len = 0.0088;  // 半边长（切面单位；段端圆角），CPU 按球实测

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

varying vec3 v_dir;   // 顶点内：模型空间单位球方向（片元在格心切面内投影的基准）

void vertex()
{
	v_dir = normalize(VERTEX);
}

void fragment()
{
	vec4 region = texture(region_data, UV);
	vec3 base;
	if (display_mode == 0)
		base = texture(elevation_ramp, vec2(region.r, 0.5)).rgb;   // 海拔：色带纹理（已烘 linear）
	else
		base = plate_color(region.g);                              // 板块：板号 → 色相现算

	// ── 板块边界描边：到旗标边【线段】的解析距离 ──
	// UV2 = 片元在格心切面内的坐标（插值恒精确）；COLOR.r = 本三角对应格边的异板旗，
	// COLOR.gb = 边外法向（归一，/2+0.5 打包），COLOR.a = 内切半径 ρ（/rho_scale 打包）。
	// ⚠️ 旗标门控必不可少：无旗（同板边）不描——否则全网格都出蜂巢描边
	vec2 n = COLOR.gb * 2.0 - 1.0;
	float rho = COLOR.a * rho_scale;
	float dn = rho - dot(n, UV2);                                  // 到边线的距离（格内为正）
	vec2 tang = vec2(-n.y, n.x);
	float over = max(abs(dot(tang, UV2)) - cell_half_len, 0.0);    // 段端外伸量（圆角）
	float dmin = length(vec2(max(dn, 0.0), over));

	float w = outline_width * rho;
	float aa = fwidth(dmin);
	float band = (1.0 - smoothstep(w - aa, w + aa, dmin)) * step(0.5, COLOR.r) * outline_enable;
	ALBEDO = mix(base, base * outline_darken, band);
}
