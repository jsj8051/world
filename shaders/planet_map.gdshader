shader_type spatial;
render_mode cull_back;

// 星球地图着色：等距柱状海拔纹理（R = 归一化海拔 0..1）→ 海拔色带。
// SphereMesh UV：u 沿经度 0..1（一圈），v 沿纬度 0..1（北极→南极），
// 与等距柱状纹理 (x=经度, y=北→南) 直接对应。
uniform sampler2D map_tex : filter_linear;
uniform bool show_grid = true;
uniform float grid_degrees = 5.0;

// 海拔色带：深海 → 浅海 → 海岸 → 平原 → 丘陵 → 山地 → 雪
vec3 elevation_color(float h)
{
	vec3 deep  = vec3(0.03, 0.09, 0.22);
	vec3 shall = vec3(0.06, 0.28, 0.48);
	vec3 coast = vec3(0.62, 0.55, 0.35);
	vec3 plain = vec3(0.30, 0.52, 0.18);
	vec3 hill  = vec3(0.42, 0.38, 0.22);
	vec3 mount = vec3(0.55, 0.50, 0.45);
	vec3 snow  = vec3(0.94, 0.96, 1.00);

	vec3 col;
	if (h < 0.45)      col = mix(deep, shall, h / 0.45);
	else if (h < 0.50) col = mix(shall, coast, (h - 0.45) / 0.05);
	else if (h < 0.70) col = mix(coast, plain, (h - 0.50) / 0.20);
	else if (h < 0.85) col = mix(plain, hill, (h - 0.70) / 0.15);
	else if (h < 0.94) col = mix(hill, mount, (h - 0.85) / 0.09);
	else               col = mix(mount, snow, (h - 0.94) / 0.06);
	return col;
}

void fragment()
{
	float h = texture(map_tex, UV).r;
	vec3 col = elevation_color(h);

	// 经纬网格线（策略地图常用，辅助定位/后续对齐 hex 格子）
	if (show_grid)
	{
		vec2 g = UV * vec2(360.0, 180.0) / grid_degrees;
		vec2 frac_part = abs(fract(g) - 0.5);
		float line = smoothstep(0.5, 0.48, max(frac_part.x, frac_part.y));
		col = mix(col * 0.55, col, line);
	}

	ALBEDO = col;
	// 哑光：关闭默认 0.5 的镜面高光，避免塑料感。
	SPECULAR = 0.0;
	ROUGHNESS = 0.95;
}
