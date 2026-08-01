shader_type spatial;
render_mode cull_disabled;

// Flat per-tile vertex color; COLOR.a encodes distance to the tile edge
// (1 = center, 0 = boundary). The outline is a DARKENED version of the tile
// color (not near-black), so cell borders read as crisp grid lines instead
// of looking like cracks between tiles.
void fragment()
{
	float edge = smoothstep(0.88, 0.98, COLOR.a);
	ALBEDO = mix(COLOR.rgb * 0.55, COLOR.rgb, edge);
	// 哑光：SPECULAR 默认 0.5 会让每个色块浮出镜面高光（塑料感），置零关闭。
	SPECULAR = 0.0;
	ROUGHNESS = 0.95;
}

// 分段色阶（cel shading）：漫反射按 NdotL 分 3 档，
// 暗面整块变暗而非连续渐变，贴合格子色块风。
// 边界用 smoothstep 软化（过渡带 0.12~0.35、0.35~0.55），对比温和：
// 亮面 0.9 / 中面 0.72 / 暗面 0.5 / 背光 0.5。
void light()
{
	float ndotl = dot(NORMAL, LIGHT);
	float band;
	if (ndotl > 0.55)      band = 0.9;
	else if (ndotl > 0.35) band = mix(0.72, 0.9, smoothstep(0.35, 0.55, ndotl));
	else if (ndotl > 0.12) band = mix(0.6, 0.72, smoothstep(0.12, 0.35, ndotl));
	else if (ndotl > 0.0)  band = mix(0.5, 0.6, smoothstep(0.0, 0.12, ndotl));
	else                   band = 0.5;
	DIFFUSE_LIGHT += band * LIGHT_COLOR * ATTENUATION * ALBEDO;
}
