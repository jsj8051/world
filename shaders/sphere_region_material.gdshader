shader_type spatial;
render_mode unshaded;

// 全域材质（2026-09-09 v3 解析边距版，替旧"逐格顶点色重交"与"边距权重插值"两代）：一颗星球
// 一张材质——逐格区域数据烘进 region_data 纹理（每格 1 纹素，UV = 格纹素中心），片元按
// display_mode uniform 派生区域外观（海拔色带 / 板块色相），模式切换只换 uniform。
//
// 板块边界描边 = 片元解析边距（2026-09-09 v3，替"边距权重插值 + 角点帽"两代）：
// 每格常量属性（格心方向 UV2、边 0 法向方位角 COLOR.r、旗标字节 COLOR.g）块内插值恒精确，
// 片元把自身方向投影到格心切面，对【旗标边】逐边求到线段的真实距离取 min——带宽处处一致、
// 拐角天然圆角衔接、同板边绝不描边（修"五边贴异板格第 6 边误描"与"拐角缺口/深色圆珠"两代问题）。
// region_data（filter_nearest，纹素布局见 BallMesh.DataTexWidth）：R = 海拔归一、G = 板号色相归一、
// B = 陆 1 / 洋 0（Crust.IsLand 统一口）、A 备用。
uniform sampler2D region_data : filter_nearest, repeat_disable;
uniform sampler2D elevation_ramp : filter_nearest, repeat_disable;   // 256×1，C# 从 ElevationStops 烘（已转 linear）
uniform int display_mode = 0;      // 当前显示模式 = MapMode.Id（0 海拔 / 1 板块；加模式在此扩分支）
uniform float outline_enable : hint_range(0.0, 1.0) = 1.0;
uniform float outline_darken : hint_range(0.0, 1.0) = 0.0;    // 描边带压暗系数（用户已调 0 = 纯黑线）
uniform float outline_width : hint_range(0.0, 0.5) = 0.18;    // 带半宽（× 格内切半径）
uniform vec2 cell_metrics_hex = vec2(0.0071, 0.0041);         // 六边格（内切半径, 半边长），CPU 按球实测
uniform vec2 cell_metrics_pent = vec2(0.0081, 0.0047);        // 五边格

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

varying vec3 v_dir;   // 顶点内：模型空间单位球方向（格内片元位置基准）

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

	// ── 板块边界描边：解析边距 ──
	// 解码格常量（COLOR 8bit：G = 旗标字节，bit0..5 异板边 / bit6 格心 z 负 / bit7 五边格）
	float b = floor(COLOR.g * 255.0 + 0.5);
	bool is_pent = b >= 128.0;
	float zsign = mod(b, 128.0) >= 64.0 ? -1.0 : 1.0;
	float fb = mod(b, 64.0);
	vec3 cdir = normalize(vec3(UV2, zsign * sqrt(max(1.0 - dot(UV2, UV2), 0.0))));

	// 片元方向投影到格心切面（tu/tv 与 CPU TangentFrame 同式）
	vec3 up = abs(cdir.y) < 0.98 ? vec3(0, 1, 0) : vec3(1, 0, 0);
	vec3 tu = normalize(cross(up, cdir));
	vec3 tv = cross(cdir, tu);
	vec2 o = vec2(dot(normalize(v_dir), tu), dot(normalize(v_dir), tv));

	// 对旗标边逐边求到【线段】的真实距离（段端自动圆角衔接拐角），取 min = 到边界路径距离。
	// ⚠️ 边 k 法向方位 = θ + k·步长（θ 本身就是边 0 的法向方位，CPU 取角0/角1 中点方向），
	//    勿再 +0.5 步——错半步会把每条旗标边画到相邻边上（碎裂描边，2026-09-09 修）
	float step_ang = is_pent ? 1.2566371 : 1.0471976;              // 72° / 60°
	vec2 metrics = is_pent ? cell_metrics_pent : cell_metrics_hex; // (内切半径, 半边长)
	float dmin = 1e9;
	for (int k = 0; k < 6; k++)
	{
		if (is_pent && k == 5) break;
		if (mod(floor(fb / exp2(float(k))), 2.0) < 0.5) continue;  // bit k = 边 k 是否异板边
		float ang = COLOR.r * 6.2831855 - float(k) * step_ang;
		vec2 n = vec2(cos(ang), sin(ang));                         // 边外法向
		float dn = metrics.x - dot(n, o);                          // 到边线距离（格内为正）
		float dt = max(abs(dot(vec2(-n.y, n.x), o)) - metrics.y, 0.0);  // 段端外伸量
		dmin = min(dmin, length(vec2(dn, dt)));
	}

	float w = outline_width * metrics.x;
	float aa = fwidth(dmin);
	float band = (1.0 - smoothstep(w - aa, w + aa, dmin)) * outline_enable;
	ALBEDO = mix(base, base * outline_darken, band);
}
