shader_type spatial;
render_mode unshaded, cull_back;

// 噪声地形 · 平光地图材质（2026-09-29）：颜色全部来自顶点色（海拔分档），
// 不受灯光影响（unshaded）——单半径/颜色表达拍板的配套材质。
void vertex() {
}

void fragment() {
	ALBEDO = COLOR.rgb;
}
