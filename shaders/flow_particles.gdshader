shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, fog_disabled;

// 流场粒子层（洋流 / 风场共用）：流线几何是静态的，粒子沿 UV.x（行进时间）运动。
// 宽度是屏幕 px（UV2.x），在 vertex 里按投影后的横向方向展开；
// 深度层次靠 NDC 偏移，不把顶点抬离地表（掠射角下抬升会横向漂移）。
uniform vec2 u_viewport = vec2(1920.0, 1080.0);
uniform sampler2D u_comet_tex : filter_linear, repeat_enable;
uniform float u_cycle = 0.22;
uniform float u_rate = 0.55;
uniform float u_time = 0.0;
uniform float u_tail_scale = 1.0;
uniform float u_depth_bias = 0.00006;
uniform float u_core = 0.26;
uniform float u_edge = 1.0;
uniform float u_base = 0.10;
uniform vec3 u_head_color = vec3(1.0, 0.98, 0.92);

varying float v_cross;
varying float v_u;
varying float v_phase;
varying float v_end;
varying vec3 v_color;

float line_cycle(float phase) {
	// 每条流线 ±22% 的周期：打散"所有线等间距排点"的规则感；只依赖线相位，不随 zoom 变。
	return u_cycle * (0.78 + 0.44 * fract(phase * 17.17));
}

float comet_phase(float u, float phase, float cycle) {
	// p=0 是彗星头；p>0 落在头后方（u 更小），到 duty 后是暗间隙。
	return fract(phase - u / cycle + u_time * u_rate);
}

void vertex() {
	float cycle = line_cycle(UV2.y);
	float p = comet_phase(UV.x, UV2.y, cycle);
	float lutP = min(p * u_tail_scale, 0.999);
	float head = 1.0 - smoothstep(0.0, 0.06, lutP);
	float taper = 1.0 - 0.35 * clamp(lutP / 0.55, 0.0, 1.0);
	float target_px = UV2.x * taper * (1.0 + 0.35 * head);

	vec3 center = VERTEX;
	vec3 side = NORMAL;
	vec4 c0 = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(center, 1.0));
	vec4 c1 = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(center + side * 0.001, 1.0));
	float w0 = max(abs(c0.w), 1e-6);
	float w1 = max(abs(c1.w), 1e-6);
	vec2 s0 = (c0.xy / w0) * u_viewport;
	vec2 s1 = (c1.xy / w1) * u_viewport;
	vec2 d = s1 - s0;
	float dl = length(d);
	vec2 dir = dl > 1e-9 ? d / dl : vec2(1.0, 0.0);

	float cross_sign = UV.y;
	vec2 offset = dir * (target_px * 0.5) * cross_sign;
	c0.xy += (offset / max(u_viewport, vec2(1.0, 1.0))) * 2.0 * c0.w;
	POSITION = c0;
	POSITION.z += u_depth_bias * POSITION.w;

	v_cross = cross_sign;
	v_u = UV.x;
	v_phase = UV2.y;
	v_end = COLOR.a;
	v_color = COLOR.rgb;
}

void fragment() {
	float crossAbs = abs(v_cross);
	float body = 1.0 - smoothstep(u_core, u_edge, crossAbs);
	if (body <= 0.002) discard;

	float cycle = line_cycle(v_phase);
	float p = comet_phase(v_u, v_phase, cycle);
	float row = (clamp(floor(v_phase * 4.0), 0.0, 3.0) + 0.5) / 4.0;
	// 近景只压 LUT 的可见尾长（世界长度），彗星头相位不变 ⇒ 缩放不会“回放”。
	float lutP = min(p * u_tail_scale, 0.999);
	float profile = texture(u_comet_tex, vec2(lutP, row)).r;

	// 同一彗星槽位每圈亮度略有差异，避免整条线像循环播放同一段虚线。
	float slot = floor(v_phase - v_u / cycle + u_time * u_rate);
	float grain = fract(sin(slot * 12.9898 + v_phase * 78.233) * 43758.5453);
	float gain = 0.82 + 0.18 * grain;

	float head = 1.0 - smoothstep(0.0, 0.06, lutP);
	vec3 color = mix(v_color, u_head_color, head * 0.30);
	// u_base 让每条流线先成为一条连续淡带；彗星只是在带上跑的高亮段。
	// 这样同一路径上不会再出现"多段彼此断开的光带"，重复路径则已在 C# 侧合并。
	float comet = profile * (0.85 + 0.45 * head) * gain;
	ALBEDO = color * (u_base + comet) * body * v_end;
	ALPHA = 1.0;
}
