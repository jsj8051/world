shader_type spatial;
render_mode unshaded, blend_mix, depth_draw_always, cull_disabled;

// 河流表面材质（River v2.0，2026-10-04）：
//   把 H3 河网当**控制数据**，表现层另生成连续样条（RiverPresentationSpline），
//   本 shader 负责"河流在屏幕上怎么表现"——**屏幕空间恒定宽度** + 横截面柔化。
//
// ★不是真实水体：没有反射/折射/波动，就是战略地图河流材质。
// ★取代 v1.x 的"两套共面 mesh"（SoftEdge + Water）⇒ 软边白块 / z-fighting 从根上消失。
//
// 顶点属性约定（由 RiverLineOverlay 写入）：
//   VERTEX    = 中心线点（同一采样点 emit 两个顶点，位置相同）
//   NORMAL    = 该点的**球面横向**单位向量（side）
//   UV.x      = 沿河弧长参数（备用；当前不参与着色）
//   UV.y      = 横截面坐标 cross ∈ {-1, +1} ⇒ 片元里插值为 [-1, 1]
//   COLOR.rgb = 水色
//   ★UV2.x    = **目标屏幕宽度（px）**
//   ★UV2.y    = **深度偏移（NDC）**——代替物理抬升，见下方"为什么不用 lift"
//
// ⚠️⚠️ 为什么宽度走 UV2.x 而不是 COLOR.a（2026-10-05 实测踩坑，勿改回去）：
//   顶点色 COLOR 是**颜色通道，值域被夹在 [0,1]**——把 0.9~3.8 px 塞进 COLOR.a 会被全部截断成 1.0，
//   实测"强制 8px"后回读 firstColorA 仍是 1.00，河流因此全部退化成 1px 细线（宽度分档彻底失效）。
//   UV2 是 vec2 **浮点**属性，无此限制。
//
// ★★为什么用 NDC 深度偏移，而不是把顶点物理抬离地表（2026-10-04 实测改，勿改回去）：
//   物理抬 lift 在**掠射角**下会换算成很大的**横向屏幕位移**（旧 lift=1.0040 实测：
//   近景中位 55 px、P90 101 px）⇒ 河流明显"浮在表面上"、偏离真实河道。
//   NDC z 偏移只改深度、**不改 x/y** ⇒ 屏幕位置零视差。
//   而且恒定 NDC 偏移对应的世界距离 ∝ z_view²，正好匹配深度精度的 1/z² 分布
//   ⇒ 全视距余量恒定（实测口径 R=2/Near=0.02R/Far=10R/24-bit：Δz_world / 精度 = 1.68e7 × bias）。
//
// uniform u_screen_space：1 = 恒 px（v2 正式路径）；0 = 世界空间宽度（v1.2 fallback）。

uniform vec2 u_viewport = vec2(1920.0, 1080.0);
uniform float u_screen_space = 1.0;   // 1 = screen-space（恒 px）；0 = world-space fallback
uniform float u_world_per_px = 0.0;   // 世界单位/像素（u_screen_space=0 时由 C# 每帧写入）
uniform float u_core = 0.62;          // |cross| < u_core ⇒ 主水面（不透明）
uniform float u_edge = 1.0;           // |cross| = u_edge ⇒ 完全淡出
uniform float u_edge_gain = 1.16;     // 边缘相对中心的提亮系数（★不是白边，只是同色系稍亮）

varying float v_cross;
varying vec3 v_color;

void vertex() {
	float cross_sign = UV.y;
	float target_px = UV2.x;   // ★px 宽度走 UV2（COLOR.a 会被夹到 [0,1]，会把 3.8px 截成 1px）

	vec3 p = VERTEX;
	vec3 side = NORMAL;

	if (u_screen_space > 0.5) {
		// ── 恒 px：中心线与"沿 side 偏移一点"的辅助点分别投影，
		//    取屏幕空间方向作为加宽方向 ⇒ 宽度**与相机距离无关**。
		vec4 c0 = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(p, 1.0));
		vec4 c1 = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(p + side * 0.001, 1.0));
		float w0 = max(abs(c0.w), 1e-6);
		float w1 = max(abs(c1.w), 1e-6);
		vec2 s0 = (c0.xy / w0) * u_viewport;
		vec2 s1 = (c1.xy / w1) * u_viewport;
		vec2 d = s1 - s0;
		float dl = length(d);
		vec2 dir = dl > 1e-9 ? d / dl : vec2(1.0, 0.0);
		// px → NDC：NDC 跨度 2 对应整个视口 ⇒ offset_ndc = px / viewport * 2
		vec2 offs = dir * (target_px * 0.5) * cross_sign;
		c0.xy += (offs / max(u_viewport, vec2(1.0, 1.0))) * 2.0 * c0.w;
		POSITION = c0;
	} else {
		// ── fallback：世界空间宽度（等价 v1.2 的 camera-aware）
		vec3 world_off = side * (target_px * 0.5 * max(u_world_per_px, 0.0)) * cross_sign;
		POSITION = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(p + world_off, 1.0));
	}

	// ★深度层次：把顶点在 NDC 深度上"拉近"一点（只改 z，不改 x/y ⇒ 零视差）。
	//   ⚠️ **方向由实测确定**（2026-10-04）：Godot 4 的 RenderingDevice 在 D3D12/Vulkan 下用
	//      **reversed-Z**（NDC z 近大远小）⇒ 必须用 `+=`。写成 `-=` 时实测墨水从 26523 掉到 7446
	//      （河流被地形遮住），可据此判别方向是否翻错。
	POSITION.z += UV2.y * POSITION.w;

	v_cross = cross_sign;
	v_color = COLOR.rgb;
}

void fragment() {
	float a = abs(v_cross);
	// 中心主水面 → 两侧柔化淡出（取代 v1.x 的第二套 SoftEdge 几何）
	float body = 1.0 - smoothstep(u_core, u_edge, a);
	if (body <= 0.002) discard;
	// 边缘只做**同色系轻微提亮**，绝不是白边
	vec3 c = mix(v_color * u_edge_gain, v_color, smoothstep(1.0, 0.35, a));
	ALBEDO = c;
	ALPHA = body;
}
